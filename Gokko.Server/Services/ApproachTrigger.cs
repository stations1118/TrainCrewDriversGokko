namespace Gokko.Server.Services;

/// <summary>
/// 進路を取り始める位置（信号機）。「現示が変わるところを運転士に見せない、いちばん遅い位置」から決める。
///
/// 駅の場内信号機（通過なら場内＋出発）を開けるとき、手前の信号機の現示がどこまで変わるかを、灯式の表（SignalTypes.json）で計算する。
/// 1. 場内信号機（いちばん手前の場内）を X とする。「開けないとき」は X＝停止、「開けたとき」は X＝先の現示から決まる現示
///    （停車なら出発は停止のまま、通過なら出発も開けて、その先は進行とみなす）
/// 2. X の手前の信号機を S1, S2, ... とたどり（中継信号機は数えない）、2通りの現示を比べる。変わる一番手前を Sd とする
/// 3. 運転士は Sd を、その一つ手前の信号機 S(d+1) を過ぎたあたりから見る。なので S(d+1) を過ぎる前に開いていれば、変わるところを見せない（限界点）
/// 4. 取り始めるのは S(d+1+余裕) を過ぎたとき（余裕 0 なら限界点ちょうど。要求から開通まで数秒かかる）
/// 手前の信号機が枝分かれしていれば、枝ごとに計算する（列車がいる枝の信号機で判定する）。
/// 前の駅の出発・場内信号機も、閉塞信号機と同じにたどる。
///
/// 第1場内を先に取る駅（RouteRules.json の approachTrigger.firstHome。大道寺の上り）は、決めた信号機を過ぎたら第1場内だけ開け、
/// 第2場内（通過なら出発も）は 1. の「開けないとき」を「第1場内だけ開いている」として計算した位置で開ける。
/// </summary>
public sealed class ApproachTrigger
{
    private static readonly string[] Order = ["R", "YY", "Y", "YG", "G"];
    private const int MaxDepth = 10;

    private readonly RailData _data;
    private readonly Dictionary<string, List<string>> _previous = new();
    private readonly Dictionary<(string Station, Dir Dir, int? Track, bool Passing, int Margin, bool FirstHomeOpen), Result?> _cache = new();

    /// <param name="Triggers">取り始める信号機（枝ごと）</param>
    /// <param name="Limits">限界点の信号機（これを過ぎたら、現示の変化が見える。枝ごと）</param>
    /// <param name="Near">取り始める信号機を過ぎたと判定する軌道回路（取り始める信号機から場内信号機までの各信号機が守る回路）</param>
    /// <param name="Changed">現示が変わる信号機（手前へ順に）</param>
    public sealed record Result(string Home, List<string> Triggers, List<string> Limits, HashSet<string> Near, List<string> Changed);

    public ApproachTrigger(RailData data)
    {
        _data = data;
        foreach (var s in data.Signals.Values)
        foreach (var n in s.Next)
        {
            if (!_previous.TryGetValue(n, out var list)) _previous[n] = list = new List<string>();
            list.Add(s.Name);
        }
    }

    private string Own(string sig, string nextPhase)
    {
        var type = _data.Signals.TryGetValue(sig, out var s) ? s.Type : null;
        var table = type != null && _data.SignalTypes.TryGetValue(type, out var t) ? t : _data.SignalTypes.GetValueOrDefault("4灯YG式");
        return table != null && table.TryGetValue(nextPhase, out var p) ? p : "G";
    }

    private List<string> Nexts(string sig) =>
        _data.Signals.TryGetValue(sig, out var s) ? s.Next.Where(n => s.IsRepeater || !n.Contains("中継")).ToList() : new List<string>();

    /// <summary>
    /// 現示。stationSignals（この駅のこの向きの場内・出発）のうち opened にないものは停止。駅の先（出発より先）は進行とみなす。
    /// </summary>
    private string Phase(string sig, HashSet<string> stationSignals, HashSet<string> opened, Dictionary<string, string> memo, int depth = 0)
    {
        if (memo.TryGetValue(sig, out var m)) return m;
        if (stationSignals.Contains(sig) && !opened.Contains(sig)) return memo[sig] = "R";
        var nexts = Nexts(sig);
        // 駅の先（この駅の信号機でも、開けた進路の信号機でもない）は進行とみなす
        if (nexts.Count == 0 || depth > 3 || !stationSignals.Contains(sig)) return memo[sig] = stationSignals.Contains(sig) ? Own(sig, "G") : "G";
        var next = nexts.Select(n => Phase(n, stationSignals, opened, memo, depth + 1)).OrderByDescending(p => Array.IndexOf(Order, p)).First();
        return memo[sig] = Own(sig, next);
    }

    /// <summary>手前の信号機の現示（S1 から順に）。X の現示から、手前へ灯式の表で決める。</summary>
    private static List<string> Upstream(Func<string, string, string> own, List<string> path, string xPhase)
    {
        var list = new List<string>();
        var cur = xPhase;
        foreach (var s in path) { cur = own(s, cur); list.Add(cur); }
        return list;
    }

    /// <summary>手前の信号機の並び（枝ごと。中継信号機は除く）。</summary>
    private List<List<string>> Paths(string from)
    {
        var result = new List<List<string>>();
        void Walk(string cur, List<string> path)
        {
            var prevs = _previous.GetValueOrDefault(cur, []).Where(p => _data.Signals.TryGetValue(p, out var s) && !s.IsRepeater && !path.Contains(p)).ToList();
            if (prevs.Count == 0 || path.Count >= MaxDepth) { result.Add(path); return; }
            foreach (var p in prevs) Walk(p, new List<string>(path) { p });
        }

        Walk(from, new List<string>());
        return result;
    }

    /// <summary>
    /// その駅・向き・番線（null は通過の進路）で、停車か通過かのときの取り始める位置。場内信号機が分からなければ null。
    /// </summary>
    /// <param name="firstHomeOpen">
    /// 第1場内を先に取る駅（RouteRules.json の approachTrigger.firstHome）の第2場内のとき true。
    /// 「開けないとき」を、全部停止ではなく「第1場内だけ開いている（第2場内から先は停止）」として比べる。
    /// </param>
    public Result? For(string stationId, Dir dir, StationDirRoutes routes, int? track, bool passing, int margin, bool firstHomeOpen = false)
    {
        var key = (stationId, dir, track, passing, margin, firstHomeOpen);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        if (Home(routes, track, passing) is not { } h) return Store(null);
        var (stationSignals, opened, home) = h;

        var openPhase = Phase(home, stationSignals, opened, new Dictionary<string, string>());
        var closedPhase = firstHomeOpen ? Phase(home, stationSignals, new HashSet<string> { home }, new Dictionary<string, string>()) : "R";
        var triggers = new List<string>();
        var limits = new List<string>();
        var changed = new List<string>();
        var near = new HashSet<string>();
        foreach (var path in Paths(home))
        {
            if (path.Count == 0) continue;
            var closed = Upstream(Own, path, closedPhase);
            var open = Upstream(Own, path, openPhase);
            var d = 0; // 現示が変わる一番手前（1 = S1）
            for (var i = 0; i < path.Count; i++) if (closed[i] != open[i]) d = i + 1;
            foreach (var s in path.Take(d)) if (!changed.Contains(s)) changed.Add(s);
            var limitIdx = Math.Min(d + 1, path.Count);          // 限界点 S(d+1)
            var trigIdx = Math.Min(d + 1 + margin, path.Count);  // 取り始める S(d+1+余裕)
            limits.Add(path[limitIdx - 1]);
            triggers.Add(path[trigIdx - 1]);
            foreach (var s in path.Take(trigIdx))
                if (_data.Signals.TryGetValue(s, out var sd)) near.UnionWith(sd.Protects);
        }

        if (triggers.Count == 0) return Store(null);
        return Store(new Result(home, triggers.Distinct().ToList(), limits.Distinct().ToList(), near, changed));

        Result? Store(Result? r)
        {
            _cache[key] = r;
            return r;
        }
    }

    /// <summary>
    /// 第1場内を先に取る駅で、第1場内だけを取り始める位置。trigger（決めた信号機）を過ぎたら取る。
    /// Near は trigger から第1場内までの各信号機が守る回路。trigger が手前の信号機の並びになければ null。
    /// </summary>
    public Result? FirstHome(string stationId, Dir dir, StationDirRoutes routes, int? track, bool passing, string trigger)
    {
        var key = (stationId, dir, track, passing, trigger);
        if (_firstCache.TryGetValue(key, out var cached)) return cached;
        Result? result = null;
        if (Home(routes, track, passing) is { } h)
        {
            var home = h.Home;
            var near = new HashSet<string>();
            foreach (var path in Paths(home))
            {
                var idx = path.IndexOf(trigger);
                if (idx < 0) continue;
                foreach (var s in path.Take(idx + 1))
                    if (_data.Signals.TryGetValue(s, out var sd)) near.UnionWith(sd.Protects);
            }

            if (near.Count > 0) result = new Result(home, [trigger], [], near, []);
        }

        _firstCache[key] = result;
        return result;
    }

    private readonly Dictionary<(string, Dir, int?, bool, string), Result?> _firstCache = new();

    /// <summary>
    /// この駅のこの向きの場内・出発信号機、開ける信号機（番線の場内。通過なら出発も。通過の進路はそれだけ）、
    /// いちばん手前の場内信号機（開ける信号機のうち、ほかの信号機の先に出てこないもの）。分からなければ null。
    /// </summary>
    private (HashSet<string> StationSignals, HashSet<string> Opened, string Home)? Home(StationDirRoutes routes, int? track, bool passing)
    {
        var stationSignals = routes.Arrive.Values.SelectMany(x => x).Concat(routes.Depart.Values.SelectMany(x => x)).Concat(routes.Through)
            .Select(r => r.Signal).Where(s => s.Length > 0).ToHashSet();
        var arrive = track is { } t ? routes.Arrive.GetValueOrDefault(t) : routes.Through;
        if (arrive == null || arrive.Count == 0) return null;
        var opened = arrive.Select(r => r.Signal).Where(s => s.Length > 0).ToHashSet();
        if (passing && track is { } t2 && routes.Depart.TryGetValue(t2, out var dep)) opened.UnionWith(dep.Select(r => r.Signal).Where(s => s.Length > 0));
        var home = opened.FirstOrDefault(s => arrive.Any(r => r.Signal == s) && !opened.Any(o => o != s && Nexts(o).Contains(s)));
        return home == null ? null : (stationSignals, opened, home);
    }
}
