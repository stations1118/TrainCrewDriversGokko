using System.Text.RegularExpressions;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 進路の判断（在線共有モード・フリーモード・列車扱い＋集中扱い）。
/// 0.5秒ごとに <see cref="Tick"/> を呼び、参加者ごとの「ゲームに開通させておく進路」「取り消す進路」「この先の駅の状態」を作る。
///
/// ・方向は列番の偶数・奇数（奇数＝下り）、種別は列番の末尾（なし普通・C準急・D区急・B急行・K快急・A特急）
/// ・ひとつ手前の駅（すべての駅で数える）を先頭が通過したら、その駅の番線を確定し、進路を要求する
/// ・出発進路は、前方の防護区間に他の列車がいなければ開通。単線区間は、さらにその区間に列車がいないことと、相手の駅で対向の出発進路が出ていないこと
/// ・場内・出発とも、進路の軌道回路に他の列車がいる、または他の列車の進路と重なるときは待たせる（手前の駅を通過した順）
/// ・待避: 後ろの「自分より強い種別」に追いつかれる見込みなら待避の番線に入れ（最大3本待つ）、待つ列車ごとに通過・追いつかれない見込み・許容の遅れで外していく
/// ・発車順（集中扱い）: 待避駅の通過・発車の順番を指定できる。先の列車が抜けるまで出発させず、抜かれる列車は待避の番線に入れる
/// ・在線共有モードの列車だけを扱う（特発のみ共有の列車は、位置を進路・信号・ダイヤグラムに使わない）
/// ・取消: ふだんはゲームの自動解除。列車が駅を抜けたのに残っている進路だけ、取り消しを指示する
/// </summary>
public sealed class RouteEngine
{
    private static readonly TimeSpan LeftoverDelay = TimeSpan.FromSeconds(20);

    /// <summary>要求した進路がこの時間たっても開通しなければ、ゲームに残っている「要求していない進路」を消す。</summary>
    private static readonly TimeSpan StuckDelay = TimeSpan.FromSeconds(5);

    /// <summary>「残っていた進路を取消」を記録に出したもの（同じ進路を何度も記録しないため）。</summary>
    private readonly HashSet<(string Conn, string Interlock, string Route)> _orphanLogged = new();

    /// <summary>「接近しているため取り消しません」を記録に出したもの（同じことを何度も記録しないため）。</summary>
    private readonly HashSet<(string Conn, string Station, bool Departure)> _holdLogged = new();

    /// <summary>
    /// 列番の読み替え（接続 → ゲームから届く列番・サーバーで使う列番）。途中駅で列番が変わる列車に使う。
    /// </summary>
    private readonly Dictionary<string, (string From, string To)> _renumber = new();

    /// <summary>単線区間の方向てこ（区間 → 向き・鎖錠されているか）。</summary>
    private readonly Dictionary<(string A, string B), (Dir Dir, bool Locked)> _levers = new();

    private readonly RailData _data;
    private readonly RunTimeModel _model;
    private readonly CentralControl _central;
    private readonly PlayerRegistry _players;
    private readonly IncidentManager _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, TrainRun> _runs = new();
    private long _seq;

    public RouteEngine(RailData data, RunTimeModel model, CentralControl central, PlayerRegistry players, IncidentManager log, GameClock clock)
    {
        _clock = clock;
        _data = data;
        _model = model;
        _central = central;
        _players = players;
        _log = log;
        _approach = new ApproachTrigger(data);
    }

    private readonly ApproachTrigger _approach;
    private readonly GameClock _clock;

    // =========================================================================================
    // ダイヤ運転（ロープレ時刻とダイヤの時刻）
    // =========================================================================================

    private static readonly TimeZoneInfo Japan = FindJapan();

    private static TimeZoneInfo FindJapan()
    {
        foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }

    private bool DiaMode => _central.DiaMode;

    /// <summary>
    /// 今のロープレ時刻（運転日の秒。ロープレ時刻がなければ日本時間の今）。ダイヤの「24:15」のような日またぎに合わせ、4時前は 24時以降として数える。
    /// </summary>
    private double NowSec()
    {
        var t = _clock.Now ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Japan).TimeOfDay;
        var sec = t.TotalSeconds;
        return sec < 4 * 3600 ? sec + 86400 : sec;
    }

    /// <summary>"HH:mm:ss"（24時以降も可）→ 運転日の秒。</summary>
    private static double? ParseDiaTime(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        var ss = p.Length > 2 && int.TryParse(p[2], out var x) ? x : 0;
        return hh * 3600 + mm * 60 + ss;
    }

    private static string FormatDiaTime(double sec)
    {
        var t = (int)Math.Round(sec);
        return $"{t / 3600 % 24:00}:{t / 60 % 60:00}:{t % 60:00}";
    }

    /// <summary>ダイヤのその駅の時刻（運転日の秒）。depart なら発（通過）時刻、なければ着。arrive なら着時刻、なければ発。</summary>
    private static double? DiaSec(TrainRun run, string stationId, bool depart)
    {
        var stop = run.Dia?.Stops.FirstOrDefault(x => x.StationId == stationId);
        if (stop == null) return null;
        return depart ? ParseDiaTime(stop.Dep) ?? ParseDiaTime(stop.Arr) : ParseDiaTime(stop.Arr) ?? ParseDiaTime(stop.Dep);
    }

    /// <summary>
    /// 入換で始まる列車の入換時刻（列番 → 運転日の秒）。前の列車のスタフの「折返|881C 08:42入換」から。
    /// </summary>
    private Dictionary<string, double> DiaShuntTimes => _diaShuntTimes ??= _data.Dia.Values
        .Where(d => d.Turn is { Kind: "折返", How: "入換", Number: not null, Time: not null })
        .Select(d => (d.Turn!.Number!, T: ParseDiaTime(d.Turn.Time)))
        .Where(x => x.T != null)
        .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().T!.Value);
    private Dictionary<string, double>? _diaShuntTimes;

    /// <summary>
    /// ダイヤ運転中の始発の入換（検車区・引上線からホームへ）: スタフに入換時刻があれば、その30秒前まで開けない（遅れていれば、空きしだい）。
    /// 引き上げてすぐ入れ換えて、ホームを早くからふさがないように。時刻が書かれていなければ待たない。
    /// </summary>
    private string? DiaShuntHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || _central.OpenAt(run.Train, st.St.Id) != null) return null;
        if (!DiaShuntTimes.TryGetValue(run.Dia.Number, out var t)) return null;
        return NowSec() < t - DepartLeadSeconds ? $"入換時刻待ち（{FormatDiaTime(t)} 入換。{DepartLeadSeconds}秒前に開通）" : null;
    }

    /// <summary>出発まで開けてから場内を開ける駅（ダイヤ運転中。浜園）。</summary>
    private bool DepartBeforeArrive(StationRun st) => DiaMode && _data.DepartBeforeArrive.Contains(st.St.Id);

    /// <summary>
    /// ダイヤ運転中の発車時刻: 停車列車・始発は、ダイヤの発車時刻の30秒前まで出発を開けない（遅れていれば、前が空きしだい）。
    /// 通過列車と、出発まで開けてから入る駅（浜園）は時刻で待たない。スジずらし・発車時刻の手打ちがあれば、そちら（DepartTimeHold）を使う
    /// </summary>
    private string? DiaDepartHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || _central.OpenAt(run.Train, st.St.Id) != null) return null;
        if (!st.IsStart && st.StopType == "通過" && !st.DepartStop) return null;
        if (DepartBeforeArrive(st)) return null;
        if (DiaSec(run, st.St.Id, depart: true) is not { } dep) return null;
        return NowSec() < dep - DepartLeadSeconds ? $"発車時刻待ち（{FormatDiaTime(dep)} 発。{DepartLeadSeconds}秒前に開通）" : null;
    }

    /// <summary>列車（ダイヤの列番）・駅 → その駅の出発（通過）信号機を踏んだ時刻（運転日の秒）。列車がいなくなっても残す。</summary>
    private readonly Dictionary<(string Train, string Station), double> _departedLog = new();
    private double? _diaSince;
    private double _lastTickSec;

    /// <summary>
    /// ダイヤ運転中の単線区間: ダイヤでこの列車より先に、相手の駅（next）から区間へ入る対向列車のうち、
    /// いま参加していなくて（出現前・遅れ・切断）、まだ相手の駅を出ていないもの。いれば列番を「・」でつなぐ。
    /// 見るのは、この列車が区間に入るダイヤの時刻の30分前までに入るはずの列車。ダイヤ運転を始める（時計を動かす）より前の時刻の列車は、
    /// 記録がないので見ない（途中から始めても、もう通り終えた列車を待ち続けない）。
    /// </summary>
    private string? DiaAbsentOpposing(TrainRun run, StationRun st, string next)
    {
        if (run.Dia == null || DiaSec(run, st.St.Id, depart: true) is not { } mine) return null;
        var present = new HashSet<string>();
        foreach (var r in _runs.Values.Where(r => r.Dia != null))
        {
            present.Add(r.Dia!.Number);
            if (r.Dia.Next is { } nx) present.Add(nx.Number); // 途中で列番が変わる列車（1053 → 回1153）
        }

        var waiting = new List<string>();
        foreach (var d in _data.Dia.Values)
        {
            if (d.Number == run.Dia.Number || present.Contains(d.Number)) continue;
            var iNext = d.Stops.FindIndex(s => s.StationId == next);
            var iHere = d.Stops.FindIndex(s => s.StationId == st.St.Id);
            if (iNext < 0 || iHere < 0 || iNext > iHere) continue; // 相手の駅から入って、こちらの駅へ来る列車だけ
            var s0 = d.Stops[iNext];
            if (ParseDiaTime(s0.Dep ?? s0.Arr) is not { } t || t >= mine || t < mine - 1800 || t < (_diaSince ?? double.MaxValue)) continue;
            lock (_departedLog)
                if (_departedLog.TryGetValue((d.Number, next), out var at) && at >= t - 3600) continue; // もう相手の駅を出た
            waiting.Add(d.Number);
        }

        return waiting.Count > 0 ? string.Join("・", waiting) : null;
    }

    /// <summary>
    /// ダイヤ運転中: この駅から出る始発列車のダイヤの発車が、この列車の着（通過）より前で、その出発の進路と場内の進路が重なるなら、始発列車が先
    /// （江ノ原検車区13番線から出る回送より先に、12番線に入る列車が場内を取ってしまわないように）。
    /// </summary>
    private string? DiaStartFirstHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || st.ArrivePath.Count == 0) return null;
        if (DiaSec(run, st.St.Id, depart: false) is not { } mine) return null;
        var path = Circuits(st.ArrivePath).ToHashSet();
        foreach (var other in _runs.Values.Where(o => o != run && o.Dia != null && o.HasPosition))
        {
            var os = other.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && s.IsStart && !s.Done);
            if (os == null || os.DepartPath.Count == 0) continue;
            if (DiaSec(other, st.St.Id, depart: true) is { } theirs && theirs < mine && Circuits(os.DepartPath).Any(path.Contains))
                return $"始発列車の発車待ち（{other.Train} が先）";
        }
        return null;
    }

    /// <summary>
    /// ダイヤ運転中の駅の出発順序（手で決めていなければ、ダイヤの発車・通過時刻の順）。ダイヤにない列車は入れない（待たない・待たせない）。
    /// </summary>
    private IReadOnlyList<string>? EffectiveOrder(string stationId, Dir dir)
    {
        if (_central.Order(stationId, dir) is { } manual) return manual;
        if (!DiaMode) return null;
        var list = _runs.Values.Where(r => r.Dir == dir && r.Dia != null && r.Stations.Any(s => s.St.Id == stationId && !s.Done))
            .Select(r => (r.Train, T: DiaSec(r, stationId, depart: true))).Where(x => x.T != null).OrderBy(x => x.T).Select(x => x.Train).ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>ダイヤ運転中の単線区間の走行順（手で決めていなければ、区間に入る駅のダイヤの発車・通過時刻の順）。</summary>
    private IReadOnlyList<string>? EffectiveSectionOrder(string a, string b)
    {
        if (_central.SectionOrder(a, b) is { } manual) return manual;
        if (!DiaMode) return null;
        var list = new List<(string Train, double T)>();
        foreach (var r in _runs.Values.Where(r => r.Dia != null))
        {
            var entry = r.Stations.FirstOrDefault(s => (s.St.Id == a || s.St.Id == b) && NextControlStation(r, s) is { } nx && (nx == a || nx == b) && nx != s.St.Id);
            if (entry == null || SectionCleared(r, a, b)) continue;
            if (DiaSec(r, entry.St.Id, depart: true) is { } t) list.Add((r.Train, t));
        }

        return list.Count > 0 ? list.OrderBy(x => x.T).Select(x => x.Train).ToList() : null;
    }

    /// <summary>その駅の場内（通過なら出発も）を取り始める位置（信号機）。設定で切ってあれば・計算できなければ null。</summary>
    private ApproachTrigger.Result? ApproachOf(TrainRun run, StationRun st)
    {
        if (!_data.ApproachTriggerEnabled || st.IsStart) return null;
        var passing = st.StopType == "通過" && !st.DepartStop && !st.Terminal && !IsTurnbackTrack(run, st);
        return _approach.For(st.St.Id, run.Dir, st.St.Routes[run.Dir], st.Track, passing, _data.ApproachTriggerMargin);
    }

    /// <summary>駅・向き・番線ごとの、進路を取り始める位置の一覧（確かめる用）。margin は限界点からの余裕（閉塞の数）。</summary>
    public List<object> ApproachTable(int margin)
    {
        var list = new List<object>();
        foreach (var st in _data.ControlStations)
        foreach (var dir in new[] { Dir.Down, Dir.Up })
        {
            var routes = st.Routes[dir];
            var tracks = routes.Arrive.Keys.OrderBy(t => t).Select(t => (int?)t).ToList();
            if (routes.Through.Count > 0) tracks.Add(null);
            foreach (var track in tracks)
            foreach (var passing in track == null ? new[] { true } : routes.Depart.ContainsKey(track.Value) ? new[] { false, true } : new[] { false })
            {
                var r = _approach.For(st.Id, dir, routes, track, passing, margin);
                list.Add(new
                {
                    station = st.Name, direction = dir.Label(), track, kind = passing ? "通過" : "停車",
                    home = r?.Home, changed = r?.Changed, limits = r?.Limits, triggers = r?.Triggers
                });
            }
        }

        return list;
    }

    /// <summary>列車の先頭が、取り始める信号機を過ぎたか（その信号機から場内信号機までの回路にいる、または駅に着いている）。</summary>
    private static bool PassedApproach(TrainRun run, StationRun st, ApproachTrigger.Result a) =>
        (run.Front - st.Zone) * run.Dir.Step() >= 0 || run.Player.TrackCircuits.Any(a.Near.Contains);

    /// <summary>
    /// 進路を取り始める位置（ApproachTrigger）をまだ過ぎていなければ、場内を要求しない（通過列車は出発も、場内を待つので一緒に待つ）。
    /// 現示が変わるところを運転士に見せない、いちばん遅い位置で取る。
    /// </summary>
    private string? ApproachHold(TrainRun run, StationRun st) =>
        ApproachOf(run, st) is { } a && !PassedApproach(run, st, a) ? $"取り始め待ち（{string.Join("・", a.Triggers)} を過ぎたら）" : null;

    // =========================================================================================
    // 列車・駅ごとの状態
    // =========================================================================================

    private enum GroupState
    {
        None,
        Waiting,
        Requested
    }

    private sealed class RouteReq
    {
        public required RouteDef Def { get; init; }
        public required long Id { get; init; }
        public required StationRun Station { get; init; }
        public required bool Departure { get; init; }
        public DateTime RequestedUtc { get; init; } = DateTime.UtcNow;
        public bool Seen { get; set; }
        public bool Released { get; set; }

        /// <summary>列車がこの進路の軌道回路に入ったことがある。</summary>
        public bool Entered { get; set; }

        /// <summary>番線の変更などで取り消した。ゲームにあれば取り消しを指示し、なくなったら解除済みにする。</summary>
        public DateTime? CancelledUtc { get; set; }

        /// <summary>管理画面の手動進路（進路名を直接指定したもの）。</summary>
        public bool Manual { get; init; }
    }

    /// <summary>何駅手前に着いたら開通させるか（すべての駅で数える。2 = 2つ手前の駅に着いたら）。</summary>
    private const int TriggerStationsAhead = 2;

    /// <summary>何駅手前に着いたら開通させるか。単線区間を通って入る駅は1つ手前（遠くから単線区間の駅をふさがない）、それ以外は2つ手前。</summary>
    private int TriggerAhead(TrainRun run, StationRun st) =>
        run.Stations.TakeWhile(s => s != st).LastOrDefault() is { } prev && _data.IsSingleTrack(prev.St.Id, st.St.Id) ? 1 : TriggerStationsAhead;

    /// <summary>ダイヤグラムの◆で指定した開通時刻を使うか。フリーモードでは時刻に依らず開通させるので使わない（ダイヤモードで使う）。</summary>
    public static bool UseOpenTime => false;

    private sealed class StationRun
    {
        /// <summary>出発停止の番線に入る（場内進路が解除されるまで出発を開通させない。停車種別は「停車」で送る）。</summary>
        public bool DepartStop { get; set; }

        /// <summary>待避線（待避駅の待避の番線）に着いた時刻。最低の停車時分を数える。</summary>
        public DateTime? LoopArrivedUtc { get; set; }

        /// <summary>列車が番線に入りきった時刻（番線ごとの「入ってから何秒後に出発を開通」を数える）。</summary>
        public DateTime? InTrackUtc { get; set; }

        /// <summary>番線に入りきって、ゲームの速度が 0km/h になった（止まった）時刻。出発・入換の開通の合図に使う。</summary>
        public DateTime? StoppedUtc { get; set; }

        /// <summary>先頭がこの駅に着いた時刻（始発は認識した時刻）。単線区間で、同時に待ち合ったときの順番に使う。</summary>
        public DateTime? HereUtc { get; set; }

        /// <summary>単線区間で、先に通すと決めた対向列車（最大2本。決めたら、その列車が来るまで待つ）。</summary>
        public List<string> OpposingWaits { get; } = new();

        /// <summary>この駅で先に通すと決めた対向列車の延べ本数（最大2本）。</summary>
        public int WaitedTotal { get; set; }

        /// <summary>場内・出発の待ちが、進路の競合（在線・重なり・順番待ち）によるものか。順番待ちの相手にするのはこれだけ。</summary>
        public bool ArriveBlocked { get; set; }
        public bool DepartBlocked { get; set; }

        /// <summary>駅扱い（強制進路）。連動の確認だけで開通させる（抑止・待ちを無視）。</summary>
        public bool ForceArrive { get; set; }
        public bool ForceDepart { get; set; }
        public bool ForceShunt { get; set; }

        /// <summary>抑止をかけたが、列車が接近していて取り消さなかった進路（"場内"・"出発"・"場内出発"。なければ空）。</summary>
        public string HoldKept { get; set; } = "";

        /// <summary>駅扱いで開通させた進路がある（後続に譲るための自動の取消をしない）。</summary>
        public bool ManualUsed { get; set; }

        public required StationDef St { get; init; }
        public int Zone { get; init; }
        public string StopType { get; set; } = "通過";
        public bool Terminal { get; set; }

        /// <summary>ダイヤ・ゲームどおりの停車種別と終着（臨時停車・臨時通過・打ち切りを外したときに戻す）。</summary>
        public string BaseStopType { get; set; } = "通過";
        public bool BaseTerminal { get; set; }
        public bool IsStart { get; set; }
        public int? Track { get; set; }
        public string TrackSource { get; set; } = "";
        public bool Locked { get; set; }
        public bool Triggered { get; set; }
        public DateTime TriggeredUtc { get; set; }
        public bool Done { get; set; }
        public DateTime DoneUtc { get; set; }
        public bool DepartureRequested { get; set; }
        public List<RouteDef> ArrivePath { get; set; } = new();
        public List<RouteDef> DepartPath { get; set; } = new();
        public GroupState Arrive { get; set; }
        public GroupState Depart { get; set; }
        public string ArriveWait { get; set; } = "";
        public string DepartWait { get; set; } = "";
        /// <summary>始発で、引上線などの入換が必要な番線にいるなら、その番線。</summary>
        public string? ShuntFrom { get; set; }
        public bool ShuntRequested { get; set; }
        public GroupState Shunt { get; set; }
        public string ShuntWait { get; set; } = "";

        /// <summary>終着の列車の入換（引上げ）: 要求された入換（その番線の shuntOut の何番目か）。要求がなければ null。</summary>
        public int? ShuntOutChoice { get; set; }
        public GroupState ShuntOut { get; set; }
        public string ShuntOutWait { get; set; } = "";
        public bool ForceShuntOut { get; set; }

        /// <summary>入換で経由する線路（"E線" など。直接なら null）。</summary>
        public string? ShuntVia { get; set; }
        /// <summary>自動の待避で待っている列車（強い種別の後続列車。最大 MaxPassingWaits 本）。</summary>
        public List<Partner> Partners { get; } = new();

        public bool Passing => Partners.Count > 0;

        /// <summary>発車順（集中扱い）で、先の列車を待っているところ。</summary>
        public bool OrderWaiting { get; set; }

        public List<int> Options { get; set; } = new();
    }

    private sealed class Partner
    {
        public required string Conn { get; init; }
        public required string Train { get; init; }
        public DateTime ExpectedPassUtc { get; init; }
    }

    private sealed class TrainRun
    {
        public required string ConnId { get; set; }
        public required string Train { get; init; }

        /// <summary>
        /// ゲームの列番（同じ列番の列車がほかにいて、サーバーでは Train に接尾辞 X・Y・Z を付けて呼び分けているときの、元の列番）。
        /// ゲームへ送る進路の列番はこちら（そのプレイヤーのゲームには、その列車しかいないため）
        /// </summary>
        public string GameTrain { get; init; } = "";
        public required string Type { get; init; }
        public required Dir Dir { get; init; }

        /// <summary>ダイヤにある列車なら、その列車（番線・停車・終着・列選の基本）。なければ種別ごとの所定で作る。</summary>
        public DiaTrain? Dia { get; init; }

        /// <summary>出発信号機の列選に出す文字（ダイヤの種別表記から）。空ならクライアントがゲームの種別から作る。</summary>
        public string Indicator { get; init; } = "";

        public PlayerInfo Player { get; set; } = null!;
        public bool HasPosition { get; set; }
        public int Front { get; set; }
        public int Rear { get; set; }
        public double Fraction { get; set; } = 0.5;
        public bool Initialized { get; set; }
        public List<StationRun> Stations { get; } = new();
        public List<RouteReq> Requests { get; } = new();
        public Dictionary<string, string> Results { get; } = new();

        /// <summary>ゲームの返事（Results）が届いた時刻。</summary>
        public Dictionary<string, DateTime> ResultAt { get; } = new();
        public string? Message { get; set; }

        /// <summary>走った実績（ダイヤグラム用）。先頭のゾーンが変わるたびに記録し、30分より古いものは捨てる。</summary>
        public List<(DateTime Utc, double Y)> History { get; } = new();

        /// <summary>踏んだ軌道回路と、最後に踏んでいた時刻（在線位置の画面が、通ってきた枝で在線を塗るため。1分より古いものは捨てる）。</summary>
        public Dictionary<string, DateTime> SeenCircuits { get; } = new();

        /// <summary>駅ごとの実際の着時刻・発時刻（ロープレ時刻の運転日の秒）。着は停車なら止まった時、通過なら駅に入った時。発は出発信号機を踏んだ時。</summary>
        public Dictionary<string, double> ActualArr { get; } = new();
        public Dictionary<string, double> ActualDep { get; } = new();

        /// <summary>遅れ（秒）。出発信号機を踏んだときに、ダイヤの発車（通過）時刻との差で決める。ダイヤにない列車・まだ分からなければ null。</summary>
        public double? Delay { get; set; }
    }

    // =========================================================================================
    // 外から使うもの
    // =========================================================================================

    public sealed record Output(
        List<RouteCommand> Routes,
        List<RouteKey> Deletes,
        List<StationPlanView> Plan,
        bool CanRequestDeparture,
        string DepartureLabel,
        string? Message);

    private readonly Dictionary<string, Output> _outputs = new();

    public Output? GetOutput(string connId)
    {
        lock (_lock) return _outputs.GetValueOrDefault(connId);
    }

    /// <summary>出発要求ボタン。駅に止まっている自列車の、その駅の出発進路を要求する。</summary>
    public string RequestDeparture(string connId)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return "在線共有モードの列車として認識されていません（列車番号・位置を確認してください）";
            if (DiaMode && run.Dia != null) return "ダイヤ運転中は、出発要求・入換要求はできません（時刻になると自動で開通します）";

            // 終着の列車の入換（引上げ）: 番線に入りきっていて、その番線から出る入換があれば、それを要求する
            if (ShuntOutStation(run) is { } end)
            {
                end.ShuntOutChoice = 0;
                return $"{end.St.Name}: {end.Track}番線から{ShuntOutOptions(run, end)[0].Label}への入換進路を要求しました";
            }

            var st = run.Stations.FirstOrDefault(s => s.IsStart && !s.Done);
            if (st == null) return "駅に止まっている状態ではありません";
            if (st.ShuntFrom != null)
            {
                if (st.Track == null) return $"{st.St.Name}: 入換で入れる番線がありません";
                st.ShuntRequested = true;
                return $"{st.St.Name}: {st.ShuntFrom}から{st.Track}番線への入換進路を要求しました";
            }

            if (st.Terminal || st.DepartPath.Count == 0 && !ResolvePaths(run, st)) return $"{st.St.Name}: この列車はここから先へ出る進路がありません";
            st.DepartureRequested = true;
            return $"{st.St.Name}: 出発進路を要求しました";
        }
    }

    /// <summary>
    /// その駅で運転を終える列車（終着、または行き止まりの番線に入る）が、今いる番線から出られる入換（引上げ）の選択肢。
    /// 同じ行き先の候補（経由の違い）は、最初のものを代表にして1つにまとめる。実際の進路は <see cref="ShuntOutPath"/> で選ぶ。
    /// </summary>
    private static List<ShuntOutDef> ShuntOutOptions(TrainRun run, StationRun st) =>
        (st.Terminal || IsTurnbackTrack(run, st)) && st.Track is { } t
            ? st.St.Routes[run.Dir].ShuntOuts.Where(o => o.From == t).GroupBy(o => o.Label).Select(g => g.First()).ToList()
            : new List<ShuntOutDef>();

    /// <summary>入換（引上げ）の選択肢の、実際に使う候補: 書いた順に、RequireFree の回路に他の列車がいないもの（なければ最後の候補）。</summary>
    private static ShuntOutDef ShuntOutPath(TrainRun run, StationRun st, ShuntOutDef option, Dictionary<string, TrainRun> occupied)
    {
        var alts = st.St.Routes[run.Dir].ShuntOuts.Where(o => o.From == option.From && o.Label == option.Label).ToList();
        return alts.FirstOrDefault(a => a.RequireFree.All(c => !occupied.TryGetValue(c, out var o) || o == run)) ?? alts[^1];
    }

    /// <summary>入換（引上げ）を要求できる駅: 列車が番線に入りきっていて、まだ入換を要求していない。なければ null。</summary>
    private static StationRun? ShuntOutStation(TrainRun run) =>
        run.Stations.FirstOrDefault(s => !s.Done && s.ShuntOutChoice == null && ShuntOutOptions(run, s).Count > 0
                                         && (s.IsStart || FullyInTrack(run, s)));

    /// <summary>
    /// 取り消した進路の信号機。ゲームから進路が消えるまでのあいだ、すぐに停止を出させる
    /// （進路の取消はゲーム側で時間がかかったり、断られたりすることがあるため）。
    /// </summary>
    public List<string> StopSignals(string connId)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return new List<string>();
            return run.Requests.Where(r => !r.Released && r.CancelledUtc != null && r.Def.Signal.Length > 0)
                .Select(r => r.Def.Signal).Distinct().ToList();
        }
    }

    /// <summary>クライアントから届いた SetRoute の結果（"新野崎連動装置_10LD is OK" など）。</summary>
    public void ReportResult(string connId, string message)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return;
            var m = Regex.Match(message ?? "", @"^(.+?)_(\S+) is (.+)$");
            if (!m.Success) return;
            var key = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            run.Results[key] = m.Groups[3].Value;
            run.ResultAt[key] = DateTime.UtcNow;
        }
    }

    // =========================================================================================
    // 毎周期の処理
    // =========================================================================================

    public void Tick()
    {
        var now = DateTime.UtcNow;
        // ダイヤ運転を始めた時刻（運転日の秒）。時計を大きく動かしたら、そこから数え直す
        var nowSec = NowSec();
        if (!DiaMode) _diaSince = null;
        else if (_diaSince == null || Math.Abs(nowSec - _lastTickSec) > 120) _diaSince = nowSec;
        _lastTickSec = nowSec;
        var snapshot = _players.Snapshot()
            .Where(p => p.Mode == ShareMode.Shared && p.InGame && p.TrainCrewConnected && p.TrainNumber.Length > 0)
            .ToList();

        lock (_lock)
        {
            // 継続乗務: 運転画面を出て在線を残したら、その列車（進路・駅の状態）を残した在線へ移す。
            // 同じ番線から次のシナリオを始めたら、残した在線は消え、新しいシナリオの列番の列車として作り直す
            foreach (var g in snapshot.Where(p => p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)))
            {
                var owner = g.ConnectionId[PlayerRegistry.GhostPrefix.Length..g.ConnectionId.LastIndexOf('/')];
                if (!_runs.ContainsKey(g.ConnectionId) && snapshot.All(p => p.ConnectionId != owner) && _runs.TryGetValue(owner, out var r0)
                    && r0.Player.TrainNumber == g.TrainNumber)
                    MoveRun(owner, g.ConnectionId);
            }

            // 列車の入れ替わり
            foreach (var id in _runs.Keys.Where(id => snapshot.All(p => p.ConnectionId != id)).ToList()) _runs.Remove(id);
            // 列番の読み替え（途中駅で列番が変わる列車）は、運転画面を出た・ゲームの列番が変わったら忘れる
            foreach (var id in _renumber.Keys.Where(id => snapshot.All(p => p.ConnectionId != id || p.TrainNumber != _renumber[id].From)).ToList())
                _renumber.Remove(id);
            _appear.Clear();
            foreach (var id in _assigned.Keys.Where(id => snapshot.All(p => p.ConnectionId != id)).ToList()) _assigned.Remove(id);
            foreach (var p in snapshot)
            {
                // サーバーで使う列番: 読み替えていれば、読み替えた先（ゲームから届く列番は元のまま）。
                // 同じ列番の列車がほかにいれば、2本目以降に接尾辞 X・Y・Z を付けて呼び分ける
                var requested = _renumber.TryGetValue(p.ConnectionId, out var rn) ? rn.To : p.TrainNumber;
                var number = UniqueNumber(p.ConnectionId, requested);
                if (_runs.TryGetValue(p.ConnectionId, out var existing) && existing.Train == number)
                {
                    existing.Player = p;
                    continue;
                }

                // 出現待ち: 継続乗務の開始番線が違う、または他の列車がいる・他の列車の進路が開通している軌道回路に現れた列車は、
                // その回路が空く（先の列車が抜ける）まで、サーバーの列車として扱わない（進路を作らず、在線位置にも出さない）
                _runs.Remove(p.ConnectionId);
                if (p.AppearReason != null)
                {
                    _appear[p.ConnectionId] = (number, p.AppearReason);
                    continue;
                }

                if (AppearBlocker(p) is { } blocker)
                {
                    _appear[p.ConnectionId] = (number, $"{blocker.Train} がいる・{blocker.Train} の進路がある軌道回路に現れました（{blocker.Train} が抜けたら始まります）");
                    if (_appearLogged.Add((p.ConnectionId, number)))
                        RouteLogLine(now, $"出現待ち: {number}（{string.Join(" ", p.TrackCircuits)}）に {blocker.Train} の在線・進路（{string.Join(" ", blocker.Requests.Where(q => !q.Released && !q.Entered && q.CancelledUtc == null).Select(q => q.Def.Name))}）");
                    continue;
                }

                if (TrainInfo(number) is not { } info) continue;
                var dia = _data.DiaOf(number) ?? _data.DiaOf(requested); // 接尾辞を付けた列車は、元の列番のダイヤで走る
                _runs[p.ConnectionId] = new TrainRun
                {
                    ConnId = p.ConnectionId, Train = number, GameTrain = requested, Type = dia?.Type ?? info.Type, Dir = info.Dir, Player = p,
                    Dia = dia, Indicator = dia != null ? IndicatorOf(dia.TypeLabel) : ""
                };
            }

            foreach (var run in _runs.Values) UpdatePosition(run);

            // 他の列車がいる軌道回路（回路 → 列番）
            var occupied = new Dictionary<string, TrainRun>();
            foreach (var run in _runs.Values)
            foreach (var c in run.Player.TrackCircuits)
                occupied[c] = run;

            foreach (var run in _runs.Values.Where(r => r.HasPosition))
            {
                if (!run.Initialized) Initialize(run);
                UpdateStations(run, now);

                // 途中駅で列番が変わる列車（ダイヤの「次の列番」）: その駅の番線に入りきって止まったら、次の列番に読み替える。
                // 次の周期から、その列番の列車（その駅の始発）として、その列番のダイヤで進路を作る
                if (run.Dia?.Next is { } next && run.Player.Speed < 1
                    && run.Stations.FirstOrDefault(s => s.St.Id == next.StationId && !s.Done) is { } ns && FullyInTrack(run, ns)
                    && TrainInfo(next.Number) != null)
                {
                    _renumber[run.ConnId] = (run.Player.TrainNumber, next.Number);
                    _log.Note($"列番の読み替え: {run.Train} → {next.Number}（{ns.St.Name}に到着）");
                }
                // 駅を抜けた列車は、その駅の出発順序と、手前の単線区間の走行順から外す
                for (var i = 0; i < run.Stations.Count; i++)
                {
                    if (!run.Stations[i].Done) continue;
                    _central.RemoveFromOrder(run.Stations[i].St.Id, run.Train);
                    if (i > 0) _central.RemoveFromSectionOrder(run.Stations[i - 1].St.Id, run.Stations[i].St.Id, run.Train);
                }
            }

            SyncOrders();

            // 進路の要求（手前の駅を通過した順）
            var pending = _runs.Values
                .SelectMany(r => r.Stations.Where(s => !s.Done).Select(s => (Run: r, St: s)))
                // 急に現れた列車（始発）は、現れた時刻の順に並べる。すでに走っている列車の方が先
                .OrderBy(x => x.St.IsStart ? x.St.HereUtc ?? now : x.St.TriggeredUtc)
                .ToList();
            foreach (var (run, st) in pending) Process(run, st, occupied, now);

            // これから進路を取る順番（取れずに待っているもの。処理した順）
            _queue = pending.Where(x => !x.St.Done).SelectMany(x => new[]
                {
                    (x.Run, x.St, Kind: x.St.Track == null ? "通過" : "場内", State: x.St.Arrive, Wait: x.St.ArriveWait),
                    (x.Run, x.St, Kind: "出発", State: x.St.Depart, Wait: x.St.DepartWait)
                })
                .Where(q => q.State == GroupState.Waiting)
                .Select(q => (object)new { train = q.Run.Train, station = q.St.St.Name, kind = q.Kind, wait = q.Wait }).ToList();
            UpdateLevers();

            BuildOutputs(now);
        }
    }

    /// <summary>
    /// 単線区間の方向てこ（区間ごとの向き）。その区間に列車がいる、または区間へ出る出発進路を要求している列車がいれば、
    /// その列車の向きに鎖錠する（Locked）。誰も使っていなければ、最後の向きのまま、鎖錠なし。
    /// 鎖錠されている間、反対向きの出発は開通させない（SingleTrackHold が、区間の対向列車・相手の駅の対向の出発進路で待たせる）。
    /// </summary>
    /// <summary>
    /// 出発信号の進路が出ていく単線区間（SingleTrackSections の組）と、その向き。単線区間へ出ない進路・出発信号でない進路なら null。
    /// 向きは進路の種別（下り出発・上り出発）から決める。手動進路構成で出した出発信号も、これで方向てこ・対向の判断に入る
    /// </summary>
    private ((string, string) Section, Dir Dir)? DepartSection(RouteDef def, Dir fallback)
    {
        if (!def.IsDepartureSignal) return null;
        var dir = def.Category.StartsWith("上り") ? Dir.Up : def.Category.StartsWith("下り") ? Dir.Down : fallback;
        var z = _data.Line.ZoneOfStation(def.StationId);
        if (z < 0) return null;
        foreach (var sec in _data.SingleTrackSections)
        {
            var other = sec.Item1 == def.StationId ? sec.Item2 : sec.Item2 == def.StationId ? sec.Item1 : null;
            if (other == null) continue;
            if ((_data.Line.ZoneOfStation(other) - z) * dir.Step() > 0) return (sec, dir);
        }

        return null;
    }

    private void UpdateLevers()
    {
        foreach (var (a, b) in _data.SingleTrackSections)
        {
            var (za, zb) = (_data.Line.ZoneOfStation(a), _data.Line.ZoneOfStation(b));
            var (lo, hi) = (Math.Min(za, zb), Math.Max(za, zb));
            Dir? use = null;
            foreach (var run in _runs.Values.Where(r => r.HasPosition))
            {
                if (run.Player.Zones.Any(z => z > lo && z < hi)) { use = run.Dir; break; } // 区間の中にいる
                // 区間へ出る出発進路を要求している（自動・駅扱い・手動進路構成のどれでも）
                var leaving = run.Requests.Where(r => !r.Released && r.CancelledUtc == null)
                    .Select(r => DepartSection(r.Def, run.Dir)).FirstOrDefault(ds => ds is { } d && d.Section == (a, b));
                if (leaving is not { } go) continue;
                use = go.Dir;
                break;
            }

            if (use is { } dir) _levers[(a, b)] = (dir, true);
            else if (_levers.TryGetValue((a, b), out var cur)) _levers[(a, b)] = (cur.Dir, false);
        }
    }

    private List<object> _queue = new();

    /// <summary>出現待ちの列車（接続ID → 列番・理由）。毎周期作り直す。</summary>
    private readonly Dictionary<string, (string Train, string Reason)> _appear = new();
    private readonly HashSet<(string Conn, string Train)> _appearLogged = new();

    /// <summary>出現待ちの列車（管理画面・ダイヤグラムの設定画面用）。</summary>
    public List<object> AppearView()
    {
        lock (_lock) return _appear.Values.Select(a => (object)new { train = a.Train, reason = a.Reason }).ToList();
    }

    /// <summary>
    /// 現れた列車（まだサーバーの列車になっていない）の踏んでいる軌道回路に、他の列車がいる、
    /// または他の列車がまだ入っていない（これから通る）開通中・要求中の進路がかかっていれば、その列車。
    /// </summary>
    private TrainRun? AppearBlocker(PlayerInfo p)
    {
        var mine = p.TrackCircuits.ToHashSet();
        if (mine.Count == 0) return null;
        foreach (var r in _runs.Values)
        {
            if (r.ConnId == p.ConnectionId) continue;
            if (r.Player.TrackCircuits.Any(mine.Contains)) return r;
            if (r.Requests.Any(q => !q.Released && !q.Entered && q.CancelledUtc == null && q.Def.Circuits.Any(mine.Contains))) return r;
        }

        return null;
    }

    /// <summary>列車（進路・駅の状態）を別の接続IDへ移す（継続乗務）。</summary>
    private void MoveRun(string from, string to)
    {
        if (!_runs.Remove(from, out var run)) return;
        run.ConnId = to;
        _runs[to] = run;
        if (_renumber.Remove(from, out var rn)) _renumber[to] = rn;
        if (_assigned.Remove(from, out var asg)) _assigned[to] = asg;
    }

    /// <summary>接続ごとに割り振ったサーバーの列番（ゲームの列番 → サーバーの列番）。ゲームの列番が変わるまで同じものを使う。</summary>
    private readonly Dictionary<string, (string Game, string Server)> _assigned = new();

    /// <summary>
    /// 同じ列番の列車がほかにいるとき、2本目以降のサーバーの列番を決める（列車番号の付け方の「接尾辞」: 2本目 X、3本目 Y、4本目 Z）。
    /// 例: 3643K が4本 → 3643K・3643KX・3643KY・3643KZ。先に走っていた列車が元の列番のまま。
    /// ダイヤに別の列車としてある列番（1683CX など）と、ほかの接続が使っている列番は使わない。全部ふさがっていれば「#2」などを付ける。
    /// 進路・集中扱いの指示・順序はサーバーの列番で別々に扱い、ゲームへ送る進路の列番は元の列番のまま。
    /// </summary>
    private string UniqueNumber(string conn, string requested)
    {
        if (_assigned.TryGetValue(conn, out var a) && a.Game == requested) return a.Server;
        var used = _runs.Values.Where(r => r.ConnId != conn).Select(r => r.Train)
            .Concat(_assigned.Where(kv => kv.Key != conn).Select(kv => kv.Value.Server)).ToHashSet();
        var server = requested;
        if (used.Contains(requested))
        {
            var stem = Regex.Replace(requested, "[XYZ]$", "");
            server = new[] { "X", "Y", "Z" }.Select(x => stem + x)
                         .FirstOrDefault(c => c != requested && !used.Contains(c) && _data.DiaOf(c) == null)
                     ?? Enumerable.Range(2, 98).Select(i => $"{requested}#{i}").First(c => !used.Contains(c));
            _log.Note($"同じ列番: {requested} がすでに走っているため、サーバーでは {server} として扱います");
        }

        _assigned[conn] = (requested, server);
        return server;
    }
    private readonly List<string> _routeLog = new();

    /// <summary>これから進路を取る順番（取れずに待っている場内・出発。処理する順）。</summary>
    public List<object> QueueView() { lock (_lock) return _queue.ToList(); }

    /// <summary>進路を要求した記録（新しい順）。</summary>
    public List<string> RouteLogView() { lock (_lock) return Enumerable.Reverse(_routeLog).Take(300).ToList(); }

    /// <summary>進路を要求したことを記録する（画面用に直近500件。あとから調べられるよう、日ごとのファイル logs/routes-yyyyMMdd.log にも書く）。</summary>
    private void RouteLog(DateTime now, TrainRun run, StationRun st, string kind, List<RouteDef> path) =>
        RouteLogLine(now, $"{run.Train} {st.St.Name} {kind} {string.Join("→", Enumerable.Reverse(path).Select(r => r.Name))}");

    private void RouteLogLine(DateTime now, string text)
    {
        var line = $"{now.ToLocalTime():HH:mm:ss} {text}";
        _routeLog.Add(line);
        if (_routeLog.Count > 500) _routeLog.RemoveAt(0);
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"routes-{now.ToLocalTime():yyyyMMdd}.log"), $"{now.ToLocalTime():yyyy-MM-dd} {line}{Environment.NewLine}");
        }
        catch
        {
            // 書けなくても進路の判断は続ける
        }
    }

    /// <summary>
    /// 単線区間の走行順と、その区間へ出る駅の出発順序をそろえる（どちらかを変えたら、もう片方も変わる）。
    /// 新しく変えた方に合わせる。そろえるのは「その駅からその区間へ出る列車」どうしの並びだけ。
    /// </summary>
    private void SyncOrders()
    {
        foreach (var (a, b) in _data.SingleTrackSections)
        {
            if (_central.SectionOrder(a, b) is not { } sec) continue;
            foreach (var x in new[] { a, b })
            {
                var other = x == a ? b : a;
                var deps = sec.Where(t => RunByTrain(t) is { } r && r.Stations.FirstOrDefault(s => s.St.Id == x && !s.Done) is { } s
                                          && !IsCutOff(r, s) && !s.Terminal && NextControlStation(r, s) == other).ToList();
                if (deps.Count == 0) continue;
                var dir = RunByTrain(deps[0])!.Dir; // x から other へ出る列車は、みな同じ向き
                var order = _central.Order(x, dir)?.ToList();
                var secStamp = _central.SectionStamp(a, b);
                var stStamp = _central.OrderStamp(x, dir);
                if (order == null || secStamp > stStamp)
                {
                    var merged = Reorder(order ?? new List<string>(), deps);
                    if (order == null || !merged.SequenceEqual(order)) _central.SyncOrder(x, dir, merged, secStamp);
                }
                else if (stStamp > secStamp)
                {
                    var inStation = order.Where(deps.Contains).ToList();
                    if (inStation.Count < 2) continue;
                    var merged = Reorder(sec.ToList(), inStation);
                    if (!merged.SequenceEqual(sec)) _central.SyncSectionOrder(a, b, merged, stStamp);
                }
            }
        }
    }

    /// <summary>list の中で order の列車がいる位置はそのままに、その並びを order の順にする（list にない列車は末尾に足す）。</summary>
    private static List<string> Reorder(List<string> list, List<string> order)
    {
        var res = list.ToList();
        res.AddRange(order.Where(t => !res.Contains(t)));
        var idx = res.Select((t, i) => (t, i)).Where(p => order.Contains(p.t)).Select(p => p.i).ToList();
        for (var k = 0; k < idx.Count; k++) res[idx[k]] = order[k];
        return res;
    }

    /// <summary>単線区間の閉塞信号機（とその中継信号機）: どの区間の、どちら向きの信号機か。信号のデータの方向てこから作る。</summary>
    private List<((string A, string B) Section, Dir Dir, string Name)>? _leverSignals;

    private List<((string A, string B) Section, Dir Dir, string Name)> LeverSignals()
    {
        if (_leverSignals != null) return _leverSignals;
        var list = new List<((string A, string B) Section, Dir Dir, string Name)>();
        foreach (var sig in _data.Signals.Values.Where(s => s.DirectionRoutes.Count > 0 && s.Direction != null))
        {
            // 方向てこの名前（TH65_14F など）の駅ID から、単線区間を引く
            var ids = sig.DirectionRoutes.Select(n => n.Contains('_') ? n[..n.IndexOf('_')] : n).Distinct().ToList();
            foreach (var sec in _data.SingleTrackSections.Where(s => ids.Contains(s.Item1) && ids.Contains(s.Item2)))
                list.Add((sec, sig.Direction!.Value, sig.Name));
        }

        // 中継信号機は、主の信号機と同じ扱い
        foreach (var rep in _data.Signals.Values.Where(s => s.IsRepeater))
        foreach (var main in list.Where(x => rep.Next.Contains(x.Name)).ToList())
            if (list.All(x => x.Name != rep.Name)) list.Add((main.Section, main.Dir, rep.Name));
        return _leverSignals = list;
    }

    /// <summary>
    /// 方向てこによって停止に固定する閉塞信号機。単線区間の閉塞信号機は、方向てこがその信号機の向きのときだけ現示を出す。
    /// 逆向きのとき・まだ一度も向きが決まっていないときは、停止にする（ゲームの閉塞信号機は方向てこを見ないので、
    /// 固定しないと、対向列車が区間にいるのに進行を出してしまう）。全員のゲームに同じものを送る。
    /// </summary>
    public List<string> LeverStopSignals()
    {
        lock (_lock)
            return LeverSignals().Where(x => !_levers.TryGetValue(x.Section, out var lv) || lv.Dir != x.Dir).Select(x => x.Name).ToList();
    }

    /// <summary>列車の向き（走っていればその列車、いなければ列番から）。分からなければ null。</summary>
    public Dir? DirOfTrain(string train)
    {
        lock (_lock) return RunByTrain(train)?.Dir ?? TrainInfo(train)?.Dir;
    }

    /// <summary>
    /// 列番から種別と向きを読む（列車番号の付け方: 接頭辞 + 始発時刻2桁 + 運番下2桁 + 社局 + 種別 + 接尾辞）。
    /// ・呼び分けの「#2」など、接尾辞（X・Y・Z）は除く
    /// ・向き: 数字の最後の桁（奇数＝下り、偶数＝上り）
    /// ・種別: 接頭辞が「回」（定期回送）「試」（試運転）なら回送。それ以外（「臨」を含む）は、数字のあとの文字の最後（社局 T・S の後ろ）
    ///   A＝特急、B＝急行、C＝準急、D＝区急、K＝快急、なし（社局 T・S だけも）＝普通
    /// </summary>
    private static (string Type, Dir Dir)? TrainInfo(string train)
    {
        var body = Regex.Replace(train.Trim(), @"#\d+$", "");
        body = Regex.Replace(body, "[XYZ]$", "", RegexOptions.IgnoreCase);
        var digits = Regex.Match(body, @"(\d+)(?!.*\d)");
        if (!digits.Success) return null;
        var dir = int.Parse(digits.Groups[1].Value[^1..]) % 2 == 1 ? Dir.Down : Dir.Up;
        string type;
        if (body.StartsWith("回") || body.StartsWith("試")) type = "回送";
        else if (body.Length == 0) type = "普通";
        else
            type = char.ToUpperInvariant(body[^1]) switch
            {
                'A' => "特急", 'B' => "急行", 'C' => "準急", 'D' => "区急", 'K' => "快急", _ => "普通"
            };
        return (type, dir);
    }

    private static string RunType(TrainRun run) => run.Type == "回送" ? "普通" : run.Type;

    /// <summary>ダイヤの種別表記 → 出発信号機の列選の文字。</summary>
    private static string IndicatorOf(string label)
    {
        var name = label.Replace("臨時", "");
        if (Regex.IsMatch(name, @"^C特\d+$") && int.Parse(name[2..]) > 4) return "C特4";
        return name switch
        {
            "回送" or "A特" or "B特" or "C特1" or "C特2" or "C特3" or "C特4" or "D特" or "快速急行" or "急行" or "区急" or "準急" or "普通" => name,
            "試運転" => "回送",
            "C特" => "C特4",
            "特急" => "D特",
            "区間急行" => "区急",
            "快急" => "快速急行",
            _ => ""
        };
    }

    /// <summary>列車番号の確定（クライアントの「確定」ボタン）。ダイヤにある列車か。</summary>
    public TrainCheckResult CheckTrain(string train)
    {
        var name = RailData.NormalizeTrain(train);
        if (name.Length == 0) return new TrainCheckResult { Message = "列車番号が空です" };
        if (TrainInfo(name) == null) return new TrainCheckResult { Message = $"{name}: 列車番号の形が違います（数字が入っていません）" };
        if (_data.DiaOf(name) is { } dia)
            return new TrainCheckResult { Found = true, Message = $"{name}（{dia.TypeLabel}）: ダイヤの進路で構成します" };
        return new TrainCheckResult { Message = $"{name}: 進路情報がありません。種別進路で構成します" };
    }

    private void UpdatePosition(TrainRun run)
    {
        var seenAt = DateTime.UtcNow;
        foreach (var c in run.Player.TrackCircuits) run.SeenCircuits[c] = seenAt;
        foreach (var c in run.SeenCircuits.Where(kv => (seenAt - kv.Value).TotalSeconds > 60).Select(kv => kv.Key).ToList()) run.SeenCircuits.Remove(c);
        var zones = run.Player.Zones;
        run.HasPosition = zones.Count > 0;
        if (!run.HasPosition) return;
        run.Front = run.Dir == Dir.Down ? zones.Max() : zones.Min();
        run.Rear = run.Dir == Dir.Down ? zones.Min() : zones.Max();

        // 駅間のどこにいるか（停車駅の距離から）。分からなければ 0.5
        run.Fraction = 0.5;
        if (run.Front % 2 == 1)
        {
            var a = _data.Line.StationAtZone(run.Front - run.Dir.Step());
            var b = _data.Line.StationAtZone(run.Front + run.Dir.Step());
            var da = DistanceOf(run.Player, a);
            var db = DistanceOf(run.Player, b);
            if (da is { } x && db is { } y && y > x) run.Fraction = Math.Clamp((run.Player.TraveledDistance - x) / (y - x), 0, 1);
        }

        // ダイヤグラム用の実績（駅のゾーンを目盛りにした位置）
        var now = DateTime.UtcNow;
        var pos = PlotY(run);
        if (run.History.Count == 0 || Math.Abs(run.History[^1].Y - pos) > 0.05 || (now - run.History[^1].Utc).TotalSeconds > 20)
            run.History.Add((now, pos));
        run.History.RemoveAll(h => (now - h.Utc).TotalMinutes > 30);
    }

    /// <summary>ダイヤグラムの縦の位置（駅＝偶数のゾーン。駅間は、進んだ割合で按分）。</summary>
    private static double PlotY(TrainRun run)
    {
        if (run.Front % 2 == 0) return run.Front;
        var from = run.Front - run.Dir.Step();
        return from + 2 * run.Dir.Step() * run.Fraction;
    }

    private float? DistanceOf(PlayerInfo p, string? stationId)
    {
        if (stationId == null) return null;
        var name = _data.Line.StationName(stationId);
        return p.Stops.FirstOrDefault(s => s.Name == name)?.Distance;
    }

    private static string StopTypeLabel(string? gameStopType) => gameStopType switch
    {
        "StopForPassenger" => "停車",
        "StopForOperation" => "運転停車",
        _ => "通過"
    };

    /// <summary>位置が分かった最初の周期に、この先の駅の一覧を作る。</summary>
    private void Initialize(TrainRun run)
    {
        run.Initialized = true;
        var step = run.Dir.Step();
        var stops = run.Player.Stops;
        // 終着: ダイヤにあればダイヤ（江原検 → 江ノ原信号場 など、ゲームの停車駅にない入庫も分かる）、なければゲームの停車駅の最後
        var terminalId = run.Dia != null ? run.Dia.TerminalId
            : stops.Count > 0 ? _data.Line.StationIdByName(stops[^1].Name) : null;
        var terminalZone = terminalId != null ? _data.Line.ZoneOfStation(terminalId) : -1;
        // ダイヤの終着駅が、もう後ろ（今いる位置より手前）なら使わない
        if (terminalZone >= 0 && (terminalZone - run.Front) * step < 0 && !run.Player.Zones.Contains(terminalZone)) (terminalId, terminalZone) = (null, -1);

        var stations = _data.ControlStations.ToList();
        if (run.Dir == Dir.Up) stations.Reverse();
        foreach (var st in stations)
        {
            var zone = _data.Line.ZoneOfStation(st.Id);
            if (zone < 0) continue;
            // 終着駅より先は扱わない
            if (terminalZone >= 0 && (zone - terminalZone) * step > 0) continue;

            var sr = new StationRun
            {
                St = st,
                Zone = zone,
                // 停車・通過: ダイヤにあればダイヤ（手入力の列番がシナリオと違っても、その列番のとおりに）、なければゲーム
                StopType = run.Dia != null
                    ? run.Dia.Stations.TryGetValue(st.Id, out var ds) ? ds.StopType : "通過"
                    : StopTypeLabel(stops.FirstOrDefault(s => s.Name == st.Name)?.StopType),
                Terminal = st.Id == terminalId
            };

            if ((zone - run.Rear) * step < 0 && (zone - run.Front) * step < 0)
            {
                sr.Done = true; // もう過ぎた駅
                sr.DoneUtc = DateTime.UtcNow;
            }
            else if (run.Player.Zones.Contains(zone) || (zone - run.Front) * step <= 0)
            {
                sr.IsStart = true; // 今いる駅（出発は要求ボタンで）
                sr.StopType = sr.StopType == "通過" ? "停車" : sr.StopType;
            }

            sr.BaseStopType = sr.StopType;
            sr.BaseTerminal = sr.Terminal;
            run.Stations.Add(sr);
        }
    }

    /// <summary>
    /// 列車別設定の臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わらない）。
    /// 変わったら、まだ列車が入っていなければ、その駅の進路を取り消して作り直す。入っていれば変えない。
    /// </summary>
    private void ApplyStopOverride(TrainRun run, StationRun st, DateTime now)
    {
        var ov = _central.StopOverride(run.Train, st.St.Id);
        var stop = ov switch { "停車" or "打ち切り" => "停車", "通過" => "通過", _ => st.BaseStopType };
        if (st.IsStart && stop == "通過") stop = "停車";
        var terminal = ov == "打ち切り" || st.BaseTerminal;
        if (stop == st.StopType && terminal == st.Terminal) return;
        // 停車・通過が変わると番線も変わりうるので、場内から作り直す。終着だけが変わるなら、出発だけ作り直す
        // 場内にもう入っていれば、停車・通過は変えない（打ち切りの終着だけ反映する）
        if (stop != st.StopType && run.Requests.Any(r => r.Station == st && r.Entered && !r.Released && !r.Departure)) stop = st.StopType;
        var stopChanged = stop != st.StopType;
        if (!stopChanged && terminal == st.Terminal) return;
        if (run.Requests.Any(r => r.Station == st && r.Entered && !r.Released && (stopChanged || r.Departure))) return;
        foreach (var req in run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null && (stopChanged || r.Departure)))
            req.CancelledUtc = now;
        st.StopType = stop;
        st.Terminal = terminal;
        (st.Depart, st.DepartWait) = (GroupState.None, "");
        if (stopChanged)
        {
            st.Triggered = false;
            st.Locked = false;
            st.Partners.Clear();
            (st.Arrive, st.ArriveWait) = (GroupState.None, "");
            st.ArrivePath.Clear();
            st.DepartPath.Clear();
        }
        else ResolvePaths(run, st);
        _log.Note($"列車別設定: {run.Train} {st.St.Name} {(ov ?? "指定解除")}（{stop}{(terminal ? "・終着" : "")}）");
    }

    private void UpdateStations(TrainRun run, DateTime now)
    {
        var step = run.Dir.Step();
        foreach (var st in run.Stations.Where(s => !s.Done))
        {
            // 最後尾が駅を抜けたら終わり
            if ((run.Rear - st.Zone) * step > 0)
            {
                st.Done = true;
                st.DoneUtc = now;
                foreach (var req in run.Requests.Where(r => r.Station == st && !r.Seen)) req.Released = true;
                continue;
            }

            ApplyStopOverride(run, st, now);

            // 手前の駅で行き止まりの番線に入る・打ち切りの指定がある（打ち切り）なら、この駅は扱わない。開通済みの進路があれば取り消す
            if (IsCutOff(run, st))
            {
                foreach (var req in run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null)) req.CancelledUtc = now;
                st.Triggered = false;
                st.Locked = false;
                st.Partners.Clear();
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                (st.Depart, st.DepartWait) = (GroupState.None, "");
                continue;
            }

            if (_central.TakeManual(run.Train, st.St.Id) is { } manual) ApplyManual(run, st, manual, now);

            // ダイヤ運転中のダイヤの列車: 入換（引上線などからの始発・終着の引上げ）は、止まったら自動で要求する（運転士は要求できない）
            if (DiaMode && run.Dia != null && run.Player.Speed < StopSpeedKmh)
            {
                if (st.IsStart && st.ShuntFrom != null && !st.ShuntRequested) st.ShuntRequested = true;
                if (st.ShuntOutChoice == null && ShuntOutOptions(run, st).Count > 0 && (st.IsStart || FullyInTrack(run, st))) st.ShuntOutChoice = 0;
            }
            ApplyHold(run, st, now);

            // 駅に着いた時刻（先頭が構内に入った。始発は認識した時刻）
            if (st.HereUtc == null && (st.IsStart || (run.Front - st.Zone) * step >= 0))
            {
                st.HereUtc = now;
                if (!st.IsStart && st.StopType == "通過" && !st.DepartStop) run.ActualArr.TryAdd(st.St.Id, NowSec()); // 通過: 駅に入った時刻
            }

            if (!st.Locked) ChooseTrack(run, st);
            else if (CanRechoose(run, st))
            {
                // 単線区間の駅は、場内を開通させるまで番線を選び直す（対向列車が先に入った番線を避けて、空いている方へ）
                var old = st.Track;
                ChooseTrack(run, st);
                if (old != st.Track)
                {
                    ResolvePaths(run, st);
                    _log.Note($"番線変更: {run.Train} {st.St.Name} {old?.ToString() ?? "-"}番線 → {st.Track?.ToString() ?? "-"}番線（{st.TrackSource}）");
                }
            }

            // 待避線に着いた時刻（番線の回路に入った時点）
            if (st.LoopArrivedUtc == null && IsLoopTrack(run, st) && FullyInTrack(run, st))
                st.LoopArrivedUtc = now;
            if (st.InTrackUtc == null && FullyInTrack(run, st)) st.InTrackUtc = now;
            if (st.StoppedUtc == null && FullyInTrack(run, st) && run.Player.Speed < StopSpeedKmh)
            {
                st.StoppedUtc = now;
                if (!st.IsStart) run.ActualArr.TryAdd(st.St.Id, NowSec()); // 停車: 止まった時刻
            }

            // 2つ手前の駅（すべての駅）に先頭が着いたか（構内に入ったか）。始発のすぐ先などで、もう過ぎていればすぐ
            if (!st.Triggered && !st.IsStart)
            {
                var triggerZone = st.Zone - 2 * TriggerAhead(run, st) * step;
                // 取り始める信号機（ApproachTrigger）を先に過ぎたら、そこで番線を確定する（駅が近く、2つ手前の駅より手前になる所）
                if ((run.Front - triggerZone) * step >= 0 || ApproachOf(run, st) is { } ap && PassedApproach(run, st, ap))
                {
                    st.Triggered = true;
                    st.TriggeredUtc = now;
                    ChooseTrack(run, st);
                    DecidePassing(run, st, now);
                    st.Locked = true;
                    ResolvePaths(run, st);
                }
            }
        }

        // 集中扱いで、確定後に番線が変えられた駅（ダイヤグラム・管理画面から）
        foreach (var st in run.Stations.Where(s => !s.Done && s.Locked))
            ApplyTrackChange(run, st, now);

        // ゲームの進路一覧と突き合わせる（開通の確認・ゲームの自動解除・取り消した進路）
        foreach (var req in run.Requests.Where(r => !r.Released))
        {
            var inGame = run.Player.GameRoutes.Contains((req.Def.Interlock, req.Def.Name));

            // 列車がその進路に入り、通り抜けたら、その進路は使い終わり（ゲームの進路の一覧で確認できなかった進路も、ここで片付ける。
            // 残したままだと、使い終わった進路が、ほかの列車の進路と「重なる」と判定されて開通をふさぐ）
            if (req.Def.Circuits.Any(run.Player.TrackCircuits.Contains))
            {
                // 出発信号機（通過の進路なら、その進路）を踏んだ時刻を、その駅の発時刻とし、ダイヤとの差を遅れにする
                if (!req.Entered && (req.Def.IsDepartureSignal || req.Station.Track == null) && run.ActualDep.TryAdd(req.Station.St.Id, NowSec()))
                {
                    // 列車がいなくなっても残す記録（ダイヤ運転中の単線の交換で、対向列車がもう区間に入ったかを知るため）
                    lock (_departedLog) _departedLog[(run.Dia?.Number ?? run.Train, req.Station.St.Id)] = NowSec();
                    if (DiaSec(run, req.Station.St.Id, depart: true) is { } plan)
                        run.Delay = run.ActualDep[req.Station.St.Id] - plan;
                }
                req.Entered = true;
            }
            else if (req.Entered && req.CancelledUtc == null)
            {
                req.Released = true;
                continue;
            }

            if (req.CancelledUtc is { } c)
            {
                // 取り消した進路は、ゲームからなくなって（送信と行き違いにならないよう）5秒たてば解除済み
                if (!inGame && now - c > TimeSpan.FromSeconds(5)) req.Released = true;
                continue;
            }

            if (inGame) req.Seen = true;
            else if (req.Seen) req.Released = true;
        }

        run.Requests.RemoveAll(r => r.Released && (now - r.RequestedUtc).TotalMinutes > 5);
    }

    /// <summary>番線の決め方: 集中扱いの指定 ＞ 待避 ＞ ユーザーの選択 ＞ 所定。</summary>
    private void ChooseTrack(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];

        // 始発で、入換の元の番線（引上線など）にいるか。入換が終わってホームに入れば、ふつうの始発に戻る
        st.ShuntFrom = null;
        if (st.IsStart)
            foreach (var sh in routes.Shunts)
            {
                var fc = sh.FromCircuit ?? (sh.From is { } f && st.St.TrackCircuit.TryGetValue(f, out var c) ? c : null);
                // 入換の行き先の番線にもういれば、入換はいらない（江ノ原信号場の13番線の長い列車が、一直線の10番線側にはみ出していても、
                // 10番線にいるとはみなさない）
                if (st.St.TrackCircuit.TryGetValue(sh.To, out var toCircuit) && run.Player.TrackCircuits.Contains(toCircuit)) continue;
                if (fc != null && run.Player.TrackCircuits.Contains(fc))
                {
                    st.ShuntFrom = sh.FromLabel;
                    break;
                }
            }

        var options = new List<int>();
        if (st.ShuntFrom is { } from)
        {
            options.AddRange(routes.Shunts.Where(s => s.FromLabel == from && (st.Terminal || routes.Depart.ContainsKey(s.To))).Select(s => s.To).Distinct().OrderBy(t => t));
        }
        else
        {
            foreach (var t in routes.Arrive.Keys.Union(routes.Depart.Keys).OrderBy(t => t))
            {
                var canArrive = st.IsStart || routes.Arrive.ContainsKey(t);
                var canDepart = st.Terminal || routes.Depart.ContainsKey(t) || routes.Turnback.Contains(t); // 行き止まりは打ち切りとして選べる
                if (canArrive && canDepart) options.Add(t);
            }
        }

        st.Options = options;
        var throughOk = routes.Through.Count > 0 && !st.Terminal && !st.IsStart;

        if (_central.TrackOverride(run.Train, st.St.Id) is { } ct && options.Contains(ct))
        {
            (st.Track, st.TrackSource) = (ct, "集中");
            return;
        }

        if (OrderLoopTrack(run, st) is { } ot && options.Contains(ot))
        {
            (st.Track, st.TrackSource) = (ot, "待避");
            return;
        }

        if (st.Passing && st.Track is { } pt && options.Contains(pt))
        {
            st.TrackSource = "待避";
            return;
        }

        // 始発駅: 列車が実際にいる番線を使う
        if (st.IsStart)
        {
            foreach (var t in options)
                if (st.St.TrackCircuit.TryGetValue(t, out var tc) && run.Player.TrackCircuits.Contains(tc))
                {
                    (st.Track, st.TrackSource) = (t, "在線");
                    return;
                }
        }

        if (run.Player.TrackChoices.TryGetValue(st.St.Id, out var chosen) && options.Contains(chosen))
        {
            (st.Track, st.TrackSource) = (chosen, "選択");
            return;
        }

        // ダイヤの番線（手入力の列番、またはシナリオの列番のダイヤ）。単線区間の駅で、ほかの列車が使っていれば空いている番線へ
        if (run.Dia != null && run.Dia.Stations.TryGetValue(st.St.Id, out var ds) && ds.Track is { } dt && options.Contains(dt))
        {
            (st.Track, st.TrackSource) = (dt, "ダイヤ");
            // ダイヤ運転中は、ダイヤの番線を勝手に変えない（ふさがっていれば空くまで待つ。交換・順序はダイヤどおりに流すので、ずらすと行き違えなくなる）
            if (!DiaMode) AvoidTakenTrack(run, st, options);
            return;
        }

        // 通過できる駅（江ノ原信号場）は、止まらない列車なら番線を使わない
        if (throughOk)
        {
            (st.Track, st.TrackSource) = (null, "通過");
            return;
        }

        int? def = null;
        if (st.St.DefaultTracks.TryGetValue(run.Dir, out var rules))
        {
            var keys = new List<string>();
            if (st.Terminal) keys.AddRange([$"終着:{run.Type}", "終着"]);
            if (st.IsStart) keys.AddRange([$"始発:{run.Type}", "始発"]);
            keys.AddRange([run.Type, "*"]);
            foreach (var k in keys)
                if (rules.TryGetValue(k, out var t) && options.Contains(t)) { def = t; break; }
        }

        // 決まりがなければ、そのまま通れる番線の最初、それもなければ選べる番線の最初
        if (def == null)
        {
            var through = routes.ThroughTracks.Where(options.Contains).ToList();
            def = through.Count > 0 ? through[0] : options.Count > 0 ? options[0] : null;
        }

        (st.Track, st.TrackSource) = (def, "所定");
        AvoidTakenTrack(run, st, options);
    }

    /// <summary>
    /// 単線区間の駅で、決めた番線をほかの列車（上下とも）が使う・いるなら、空いている別の番線にする
    /// （単線の駅は2線あるので、片方がふさがっていてももう片方に入れる。行き違い・続行のため）。
    /// </summary>
    private void AvoidTakenTrack(TrainRun run, StationRun st, List<int> options)
    {
        if (st.Track is not { } cur || st.IsStart || !_data.TouchesSingleTrack(st.St.Id)) return;
        var routes = st.St.Routes[run.Dir];

        bool Taken(int t) =>
            _runs.Values.Any(o => o != run && o.Stations.Any(s => s.St.Id == st.St.Id && !s.Done && !IsCutOff(o, s) && s.Locked && s.Track == t))
            || st.St.TrackCircuit.TryGetValue(t, out var tc) && _runs.Values.Any(o => o != run && o.Player.TrackCircuits.Contains(tc));

        if (!Taken(cur)) return;
        // 代わりの番線: 通り抜ける列車は通れる番線（行き止まりは不可）、終着なら着ける番線
        var candidates = st.Terminal ? options : routes.ThroughTracks.Where(options.Contains).ToList();
        if (candidates.FirstOrDefault(t => t != cur && !Taken(t)) is var alt && alt != 0)
            (st.Track, st.TrackSource) = (alt, "空き番線");
    }

    /// <summary>番線の確定後も、選び直してよいか（単線区間の駅で、まだ進路を何も要求していない。指定・待避・駅扱いは除く）。</summary>
    private bool CanRechoose(TrainRun run, StationRun st) =>
        !st.IsStart && _data.TouchesSingleTrack(st.St.Id)
        && st.Arrive != GroupState.Requested && st.Depart != GroupState.Requested
        && !st.Passing && !st.ForceArrive && !st.ForceDepart
        && st.TrackSource is "ダイヤ" or "所定" or "空き番線"
        && !(DiaMode && st.TrackSource == "ダイヤ")
        && _central.TrackOverride(run.Train, st.St.Id) == null
        && !run.Requests.Any(r => r.Station == st && !r.Released && r.CancelledUtc == null);

    /// <summary>
    /// 確定後の番線の変更（集中扱い）。進路をまだ要求していなければそのまま変える。
    /// 要求済みでも、列車がまだその進路に入っていなければ、今の進路を取り消して新しい番線で開通し直す（急な番線変更）。
    /// </summary>
    private void ApplyTrackChange(TrainRun run, StationRun st, DateTime now)
    {
        var want = _central.TrackOverride(run.Train, st.St.Id);
        var source = "集中";
        if (want == null && OrderLoopTrack(run, st) is { } ot) (want, source) = (ot, "待避");
        if (want == null || want == st.Track || !st.Options.Contains(want.Value)) return;
        if (st.IsStart && st.ShuntFrom != null) return; // 入換の番線は、入換要求の前に ChooseTrack で決まる

        var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
        if (active.Any(r => r.Def.Circuits.Any(run.Player.TrackCircuits.Contains)))
            return; // もう進路に入っている

        foreach (var r in active) r.CancelledUtc = now;
        var old = st.Track;
        st.Track = want;
        st.LoopArrivedUtc = null;
        st.InTrackUtc = null;
        st.StoppedUtc = null;
        st.TrackSource = source;
        st.Partners.Clear();
        (st.Arrive, st.ArriveWait) = (GroupState.None, "");
        (st.Depart, st.DepartWait) = (GroupState.None, "");
        ResolvePaths(run, st);
        _log.Note($"番線変更: {run.Train} {st.St.Name} {old?.ToString() ?? "-"}番線 → {want}番線" + (active.Count > 0 ? "（開通済みの進路を取り消して取り直し）" : ""));
    }

    /// <summary>
    /// 抑止（場内・出発）: その進路を開通させない。すでに開通している（要求している）進路は、
    /// 運転士の目の前で信号が急に変わらない範囲なら取り消す。列車がもう接近していれば、取り消さずに残す。
    /// 駅扱い・手動進路で開通させた進路は、抑止では取り消さない。
    /// </summary>
    private void ApplyHold(TrainRun run, StationRun st, DateTime now)
    {
        st.HoldKept = "";
        var hold = _central.Hold(run.Train, st.St.Id);
        if (hold == HoldKind.None || st.ManualUsed) return;
        foreach (var departure in new[] { false, true })
        {
            if ((hold & (departure ? HoldKind.Depart : HoldKind.Arrive)) == 0) continue;
            var label = departure ? "出発" : "場内";
            var active = run.Requests
                .Where(r => r.Station == st && r.Departure == departure && !r.Manual && !r.Released && r.CancelledUtc == null).ToList();
            if (active.Count == 0) continue;
            var names = string.Join("・", active.Select(r => r.Def.Name));
            if (active.Any(r => !CanCancelQuietly(run, st, r.Def)))
            {
                st.HoldKept += label;
                if (_holdLogged.Add((run.ConnId, st.St.Id, departure)))
                    _log.Note($"{label}抑止: {run.Train} {st.St.Name} の {names} は、列車が接近しているため取り消しません");
                continue;
            }

            foreach (var r in active) r.CancelledUtc = now;
            if (departure) (st.Depart, st.DepartWait) = (GroupState.None, "");
            else
            {
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                if (st.IsStart && st.ShuntFrom != null) st.Shunt = GroupState.None;
            }

            _holdLogged.Remove((run.ConnId, st.St.Id, departure));
            _log.Note($"{label}抑止: {run.Train} {st.St.Name} の {names} を取消（列車はまだ接近していない）");
        }
    }

    /// <summary>
    /// その進路を、運転士に急な信号の変化を見せずに取り消せるか。
    /// 列車が進路の中にいる、または接近鎖錠の区間（連動表）にいれば、取り消せない。
    /// 接近鎖錠の区間が分からない進路は、列車が手前の駅より先に来ていたら取り消せないものとする。
    /// </summary>
    private static bool CanCancelQuietly(TrainRun run, StationRun st, RouteDef def)
    {
        var on = run.Player.TrackCircuits;
        if (def.Circuits.Any(on.Contains)) return false;
        if (def.Approach.Count > 0) return !def.Approach.Any(on.Contains);
        return (st.Zone - run.Front) * run.Dir.Step() >= 3;
    }

    // ---- 駅扱い（強制進路） ----

    /// <summary>駅扱いのボタン（その列車・駅で選べる進路）。action は CentralControl.RequestManual に渡す値。</summary>
    private static List<(string Action, string Label)> ManualOptions(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];
        var list = new List<(string, string)>();
        if (st.IsStart && st.ShuntFrom is { } from)
        {
            foreach (var to in routes.Shunts.Where(s => s.FromLabel == from).Select(s => s.To).Distinct().OrderBy(t => t))
                list.Add(($"shunt:{to}", $"入換 {from}→{to}番線"));
            return list;
        }

        if (!st.IsStart)
        {
            foreach (var t in routes.Arrive.Keys.OrderBy(t => t)) list.Add(($"arrive:{t}", $"本線→{t}番線"));
            if (routes.Through.Count > 0) list.Add(("through", "本線通過"));
        }

        if (!st.Terminal)
            foreach (var t in routes.Depart.Keys.OrderBy(t => t))
                if (!st.IsStart || st.Options.Contains(t) || st.Track == t)
                    list.Add(($"depart:{t}", $"{t}番線→本線"));

        // 終着の列車の入換（引上げ）
        var outs = ShuntOutOptions(run, st);
        for (var i = 0; i < outs.Count; i++) list.Add(($"shuntout:{i}", $"入換をする（{st.Track}番線→{outs[i].Label}）"));
        return list;
    }

    /// <summary>
    /// 駅扱いの指示を適用する。番線を変え（開通済みでまだ入っていない進路は取り消す）、その進路を
    /// 「連動の確認だけで開通させる」印を付ける（抑止・待避・単線の交換・順番待ちなどは無視）。
    /// </summary>
    private void ApplyManual(TrainRun run, StationRun st, string action, DateTime now)
    {
        var routes = st.St.Routes[run.Dir];
        var kind = action.Split(':')[0];
        int? track = action.Contains(':') && int.TryParse(action.Split(':')[1], out var t) ? t : null;

        void UseTrack(int? want)
        {
            if (want == st.Track) { st.TrackSource = "駅扱い"; return; }
            var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
            foreach (var r in active.Where(r => !r.Def.Circuits.Any(run.Player.TrackCircuits.Contains))) r.CancelledUtc = now;
            (st.Track, st.TrackSource) = (want, "駅扱い");
            st.Partners.Clear();
            st.LoopArrivedUtc = null;
            if (st.Arrive != GroupState.Requested || active.Any(r => !r.Departure && r.CancelledUtc != null)) (st.Arrive, st.ArriveWait) = (GroupState.None, "");
            if (st.Depart != GroupState.Requested || active.Any(r => r.Departure && r.CancelledUtc != null)) (st.Depart, st.DepartWait) = (GroupState.None, "");
            ResolvePaths(run, st);
        }

        switch (kind)
        {
            case "arrive" when track is { } a && routes.Arrive.ContainsKey(a) && !st.IsStart:
                _central.SetTrack(run.Train, st.St.Id, a);
                UseTrack(a);
                st.ForceArrive = true;
                break;
            case "through" when routes.Through.Count > 0 && !st.IsStart:
                _central.SetTrack(run.Train, st.St.Id, null);
                UseTrack(null);
                st.ForceArrive = true;
                break;
            case "depart" when track is { } d && routes.Depart.ContainsKey(d) && !st.Terminal:
                _central.SetTrack(run.Train, st.St.Id, d);
                UseTrack(d);
                st.ForceDepart = true;
                if (st.IsStart) st.DepartureRequested = true;
                break;
            case "shunt" when track is { } to && st.IsStart && st.ShuntFrom != null:
                _central.SetTrack(run.Train, st.St.Id, to);
                (st.Track, st.TrackSource) = (to, "駅扱い");
                st.ShuntRequested = true;
                st.ForceShunt = true;
                break;
            case "shuntout" when track is { } idx && idx >= 0 && idx < ShuntOutOptions(run, st).Count:
                // 入換をする: 連動の確認だけで開通させる（列車が番線に入りきる前でも要求できる）
                (st.ShuntOutChoice, st.ShuntOut, st.ForceShuntOut) = (idx, GroupState.None, true);
                return;
            default:
                _log.Note($"駅扱い: {run.Train} {st.St.Name} の「{action}」は、この列車では使えません");
                return;
        }

        st.Locked = true;
        if (!st.Triggered) (st.Triggered, st.TriggeredUtc) = (true, now);
        ResolvePaths(run, st);
    }

    /// <summary>
    /// 管理画面の手動進路（管理者用）: 進路名を直接指定して、その列車のゲームに開通・取消させる。連動の確認もしない。
    /// 列車がもう通り過ぎた駅の進路は扱えない。
    /// </summary>
    public string ManualRoute(string train, string interlock, string routeName, bool set)
    {
        lock (_lock)
        {
            var run = _runs.Values.FirstOrDefault(r => r.Train == train);
            if (run == null) return $"{train} は在線共有モードで走っていません";
            if (!_data.Routes.TryGetValue((interlock, routeName), out var def)) return $"{interlock} に進路 {routeName} はありません";
            var now = DateTime.UtcNow;
            var active = run.Requests.Where(r => r.Def == def && !r.Released && r.CancelledUtc == null).ToList();
            var st = run.Stations.FirstOrDefault(s => s.St.Id == def.StationId && !s.Done) ?? run.Stations.FirstOrDefault(s => !s.Done);
            if (set)
            {
                if (active.Count > 0) return $"{train}: {routeName} はもう要求しています";
                if (st == null) return $"{train}: 扱える駅がありません";
                if (DepartSection(def, run.Dir) is { } ds)
                {
                    var secName = $"{_data.Line.StationName(ds.Section.Item1)}〜{_data.Line.StationName(ds.Section.Item2)}";
                    if (_levers.TryGetValue(ds.Section, out var lv) && lv.Locked && lv.Dir != ds.Dir)
                        return $"{train}: {routeName} は開通させません（方向てこが{lv.Dir.Label()}向きに鎖錠中: {secName}）";
                    var (za, zb) = (_data.Line.ZoneOfStation(ds.Section.Item1), _data.Line.ZoneOfStation(ds.Section.Item2));
                    if (_runs.Values.FirstOrDefault(o => o != run && o.Player.Zones.Any(z => z > Math.Min(za, zb) && z < Math.Max(za, zb))) is { } inSec)
                        return $"{train}: {routeName} は開通させません（単線区間 {secName} に {inSec.Train} がいます）";
                    _levers[ds.Section] = (ds.Dir, true); // すぐに倒す（次の周期を待たずに、対向の出発を止める）
                }

                foreach (var old in run.Requests.Where(o => !o.Released && o.CancelledUtc != null && o.Def == def)) old.Released = true;
                run.Requests.Add(new RouteReq { Def = def, Id = ++_seq, Station = st, Departure = def.IsDepartureSignal, RequestedUtc = now, Manual = true });
                if (st.St.Id == def.StationId) st.ManualUsed = true;
                _log.Note($"手動進路: {train} {interlock} {routeName} を開通");
                return $"{train}: {routeName} を開通させます";
            }

            foreach (var r in active) r.CancelledUtc = now;
            if (active.Count == 0)
            {
                if (!run.Player.GameRoutes.Contains((interlock, routeName))) return $"{train}: {routeName} はゲームにありません";
                if (st == null) return $"{train}: 扱える駅がありません";
                run.Requests.Add(new RouteReq { Def = def, Id = ++_seq, Station = st, Departure = def.IsDepartureSignal, RequestedUtc = now, Manual = true, Seen = true, CancelledUtc = now });
            }

            // 取り消した進路の駅は、自動で開き直さないように抑止にする
            if (st != null && st.St.Id == def.StationId)
            {
                _central.SetHold(train, def.StationId, true, def.IsDepartureSignal ? HoldKind.Depart : HoldKind.Arrive);
                if (active.Any(r => !r.Departure)) (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                if (active.Any(r => r.Departure)) (st.Depart, st.DepartWait) = (GroupState.None, "");
            }

            _log.Note($"手動進路: {train} {interlock} {routeName} を取消");
            return $"{train}: {routeName} を取り消します";
        }
    }

    /// <summary>番線から、送る進路の並びを決める。進路がなければ false。</summary>
    private bool ResolvePaths(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];
        st.ArrivePath = new List<RouteDef>();
        st.DepartPath = new List<RouteDef>();
        if (st.Track is { } t)
        {
            if (!st.IsStart && routes.Arrive.TryGetValue(t, out var a)) st.ArrivePath = a;
            if (!st.Terminal && routes.Depart.TryGetValue(t, out var d)) st.DepartPath = d;
        }
        else if (routes.Through.Count > 0 && !st.IsStart && !st.Terminal)
        {
            st.ArrivePath = routes.Through; // 通過（江ノ原信号場）は、場内と同じ扱いで1回で送る
        }

        st.DepartStop = !st.IsStart && st.Track is { } dt && routes.DepartStop.Contains(dt);

        return st.ArrivePath.Count > 0 || st.DepartPath.Count > 0;
    }

    // =========================================================================================
    // 待避
    // =========================================================================================

    /// <summary>その列車がこの駅をもう抜けた（または先頭が駅より先にいて、最後尾も抜けた）。</summary>
    private static bool HasPassed(TrainRun t, StationRun st)
    {
        var ts = t.Stations.FirstOrDefault(s => s.St.Id == st.St.Id);
        return ts is { Done: true } || (t.HasPosition && (t.Rear - st.Zone) * t.Dir.Step() > 0);
    }

    private TrainRun? RunByTrain(string train) => _runs.Values.FirstOrDefault(r => r.Train == train);

    /// <summary>待避線を、ほかの列車（同じ方向）が待避に使っている・使う予定か。</summary>
    private TrainRun? LoopUser(TrainRun run, StationRun st, int loop) =>
        _runs.Values.FirstOrDefault(o => o != run && o.Dir == run.Dir
                                         && o.Stations.Any(s => s.St.Id == st.St.Id && !s.Done && s.Locked && s.Track == loop
                                                                && (s.Passing || s.TrackSource == "待避")));

    /// <summary>
    /// 自動の待避。番線の確定時に、後ろから来る「自分より強い種別」の列車を、この駅を通る見込みの早い順に見ていき、
    /// 次の駅までに追いつかれる見込みの列車を待つ（待つたびに自分の発車見込みを遅らせて判断を続ける。最大 MaxPassingWaits 本）。
    /// 集中扱いの番線指定・発車順があれば、自動では判断しない。その駅を通過する列車は、自動では待避させない。
    /// </summary>
    private void DecidePassing(TrainRun run, StationRun st, DateTime now)
    {
        if (!_data.PassingStations.TryGetValue(st.St.Id, out var ps) || st.Terminal) return;
        // 自動の待避は、その駅に停車する列車だけ（通過列車を待避のために止めない。通過列車は、次の停車する待避駅で待つ）
        if (st.StopType == "通過" && !st.IsStart) return;
        if (_central.TrackOverride(run.Train, st.St.Id) != null) return;
        if (_central.Order(st.St.Id, run.Dir)?.Contains(run.Train) == true) return;
        if (DiaMode && run.Dia != null) return; // ダイヤ運転中は、ダイヤの番線・順序どおり（勝手に待避させない）
        var loop = ps.TrackFor(run.Dir);
        if (!st.Options.Contains(loop) || LoopUser(run, st, loop) != null) return;
        var nextStation = _data.Line.StationAtZone(st.Zone + 2 * run.Dir.Step());
        if (nextStation == null) return;

        var type = RunType(run);
        var runTime = _model.SecondsBetween(type, run.Dir, st.St.Id, nextStation);
        if (runTime == null) return;
        var arrive = st.IsStart ? 0 : _model.SecondsTo(type, run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0;
        var depart = arrive + (st.StopType == "通過" && !st.IsStart ? 0 : _data.DefaultDwellSeconds);
        var rank = _data.RankOf(run.Type);

        var candidates = _runs.Values
            .Where(o => o != run && o.HasPosition && o.Dir == run.Dir && _data.RankOf(o.Type) > rank)
            .Where(o => (o.Front - run.Front) * run.Dir.Step() < 0) // 後ろにいる列車だけ
            .Select(o => (Run: o, St: o.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done && !s.Terminal)))
            .Where(x => x.St != null)
            .Select(x =>
            {
                var at = _model.SecondsTo(RunType(x.Run), x.Run.Dir, x.Run.Front, x.Run.Fraction, st.St.Id);
                var pass = at + (x.St!.StopType == "通過" ? 0 : _data.DefaultDwellSeconds);
                return (x.Run, Pass: pass);
            })
            .Where(x => x.Pass != null)
            .OrderBy(x => x.Pass)
            .ToList();

        var waits = new List<(TrainRun Run, double Pass, double Next)>();
        foreach (var (other, pass) in candidates)
        {
            if (waits.Count >= _data.MaxPassingWaits) break;
            var theirNext = _model.SecondsTo(RunType(other), other.Dir, other.Front, other.Fraction, nextStation);
            if (theirNext == null || theirNext >= depart + runTime + _data.PassingMarginSeconds) continue;
            waits.Add((other, pass!.Value, theirNext.Value));
            depart = Math.Max(depart, pass.Value); // 通過を待ってから発車する
            depart = Math.Max(depart, arrive + _data.MinLoopDwellSeconds); // 待避線では最低の停車時分を止まる
        }

        if (waits.Count == 0) return;
        foreach (var w in waits)
            st.Partners.Add(new Partner { Conn = w.Run.ConnId, Train = w.Run.Train, ExpectedPassUtc = now.AddSeconds(w.Pass) });
        st.Track = loop;
        st.TrackSource = "待避";
        _log.Note($"待避: {run.Train} は {st.St.Name} {loop}番線で " + string.Join("、", waits.Select(w => $"{w.Run.Train}（通過見込み {w.Pass:0}秒後）"))
                  + $" を待避。発車見込み {depart:0}秒後");
    }

    /// <summary>待避中なら、まだ発車させない理由。発車させてよければ null。待つ列車ごとに、通過・追いつかれない見込み・許容の遅れで外していく。</summary>
    private string? PassingHold(TrainRun run, StationRun st, DateTime now)
    {
        if (!st.Passing) return null;
        var nextStation = _data.Line.StationAtZone(st.Zone + 2 * run.Dir.Step());
        // 自分が次の駅に着く見込み: まだ駅に着いていなければ、着くまで＋停車時分を足す（待避線では必ず止まる）
        double? runTime = nextStation != null ? _model.SecondsBetween(RunType(run), run.Dir, st.St.Id, nextStation) : null;
        var atStation = run.Player.Zones.Contains(st.Zone) && (run.Front - st.Zone) * run.Dir.Step() <= 0;
        // 発車できる見込み（秒後）。まだ駅に着いていなければ、着くまで＋待避線の最低の停車時分。着いていれば、その残り
        var loopLeft = st.LoopArrivedUtc is { } la ? Math.Max(0, _data.MinLoopDwellSeconds - (now - la).TotalSeconds) : _data.MinLoopDwellSeconds;
        var depart = atStation ? loopLeft : (_model.SecondsTo(RunType(run), run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0) + Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds);

        // 通過の早い順に見る。先に待つ列車が残っていれば、その通過までは発車できないので、それを踏まえて判断する
        foreach (var p in st.Partners.OrderBy(p => p.ExpectedPassUtc).ToList())
        {
            string? why = null;
            if (!_runs.TryGetValue(p.Conn, out var partner) || partner.Train != p.Train) why = "いなくなった";
            else if (HasPassed(partner, st)) why = "通過";
            else if (now > p.ExpectedPassUtc.AddSeconds(_data.PassingToleranceSeconds)) why = $"見込みより {_data.PassingToleranceSeconds}秒以上遅れた";
            else if (runTime != null && nextStation != null
                     && _model.SecondsTo(RunType(partner), partner.Dir, partner.Front, partner.Fraction, nextStation) is { } theirs
                     && theirs >= depart + runTime + _data.PassingMarginSeconds)
                why = "今発車しても追いつかれない見込み";
            if (why == null)
            {
                // 残す列車: その通過を待ってから発車する
                var ps = partner!.Stations.FirstOrDefault(s => s.St.Id == st.St.Id);
                if (_model.SecondsTo(RunType(partner), partner.Dir, partner.Front, partner.Fraction, st.St.Id) is { } pass)
                    depart = Math.Max(depart, pass + (ps is { StopType: not "通過" } ? _data.DefaultDwellSeconds : 0));
                continue;
            }
            st.Partners.Remove(p);
            _log.Note($"待避: {run.Train} {st.St.Name} … {p.Train} {why}" + (st.Partners.Count == 0 ? "。待避終了" : ""));
        }

        return st.Passing ? $"待避中（{string.Join("・", st.Partners.Select(p => p.Train))} の通過待ち）" : null;
    }

    // ---- 発車順（集中扱い） ----

    /// <summary>出発順序で先の列車（同じ方向）が、まだ後ろ（この列車より手前）にいて駅を抜けていなければ、待避の番線（待避駅だけ）。</summary>
    private int? OrderLoopTrack(TrainRun run, StationRun st)
    {
        if (st.Terminal || st.IsStart) return null;
        if (_central.Order(st.St.Id, run.Dir) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        var routes = st.St.Routes[run.Dir];
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || p.Dir != run.Dir || HasPassed(p, st)
                || (p.Front - run.Front) * run.Dir.Step() >= 0) continue;
            // 待避駅: 待避の番線
            if (_data.PassingStations.TryGetValue(st.St.Id, out var ps)) return ps.TrackFor(run.Dir);
            // ほかの駅: 追い越す列車が使う番線と別の、そのまま通れる番線（なければ追い越せないので、番線は変えない）
            var theirs = p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done)?.Track;
            var alt = st.Options.Where(t => t != theirs && routes.Arrive.ContainsKey(t) && routes.Depart.ContainsKey(t)).ToList();
            if (alt.Count > 0) return st.Track is { } cur && alt.Contains(cur) ? cur : alt[0];
        }

        return null;
    }

    /// <summary>その列車がこの駅でこれから使う進路（場内はまだ入っていなければ、出発・通過）。番線が決まっていなければ、その時点の番線で。</summary>
    private static List<RouteDef> PendingPaths(TrainRun p, StationRun ps)
    {
        var routes = ps.St.Routes[p.Dir];
        var list = new List<RouteDef>();
        if (ps.Track is { } t)
        {
            if (!ps.IsStart && ps.Arrive != GroupState.Requested && routes.Arrive.TryGetValue(t, out var a)) list.AddRange(a);
            if (!ps.Terminal && routes.Depart.TryGetValue(t, out var d)) list.AddRange(d);
        }
        else if (!ps.IsStart && !ps.Terminal && ps.Arrive != GroupState.Requested) list.AddRange(routes.Through);
        return list;
    }

    /// <summary>
    /// 駅の出発順序。順序で先の列車が、まだ駅を抜けていなくて、自分の出発の進路と競合する（回路が重なる）なら待つ。
    /// 競合しない進路（別の番線の場内・反対方向の出発など）は待たない（1番線に先の列車が入れば、2番線の場内・出発は開けられる）。
    /// 同じ方向で先の列車が後ろにいるのに、自分が待避の番線にいない（追い越させられない）ときは待たない。
    /// </summary>
    private string? OrderHold(TrainRun run, StationRun st, List<RouteDef> path)
    {
        if (EffectiveOrder(st.St.Id, run.Dir) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        var mine = Circuits(path).ToHashSet();
        var waiting = new List<string>();
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || HasPassed(p, st)) continue;
            if ((_central.Hold(name, st.St.Id) & HoldKind.Depart) != 0) continue; // 出発抑止中の列車は待たない（ほかの列車を先に出す）
            if (p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is not { } ps || IsCutOff(p, ps)) continue; // その駅を通らない
            if (p.Dir == run.Dir && (p.Front - run.Front) * run.Dir.Step() < 0 && ps.Track == st.Track) continue; // 同じ番線の後ろの列車は追い越させられない
            if (!Circuits(PendingPaths(p, ps)).Any(mine.Contains)) continue; // 競合しない
            waiting.Add(name);
        }

        return waiting.Count > 0 ? $"出発順序待ち（{string.Join("・", waiting)} が先）" : null;
    }

    /// <summary>出発順序の画面に出す注意（競合する先の列車・追い越せない並び）。</summary>
    private string OrderWarning(TrainRun run, StationRun st, IReadOnlyList<string> order)
    {
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return "";
        var mine = Circuits(PendingPaths(run, st)).ToHashSet();
        var notes = new List<string>();
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || HasPassed(p, st)) continue;
            if (p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is not { } ps || IsCutOff(p, ps)) continue;
            if (p.Dir == run.Dir && (p.Front - run.Front) * run.Dir.Step() < 0 && ps.Track == st.Track)
                notes.Add($"{name} と同じ番線のため追い越せません（番線を変えてください）");
            else if (Circuits(PendingPaths(p, ps)).Any(mine.Contains))
                notes.Add($"{name} と進路が競合（{name} が先）");
        }

        return string.Join("、", notes);
    }

    // =========================================================================================
    // 進路の要求
    // =========================================================================================

    /// <summary>待避駅の待避の番線（津崎・新野崎の下り4・上り1）に入るか。始発・終着は除く。</summary>
    private bool IsLoopTrack(TrainRun run, StationRun st) =>
        !st.Terminal && !st.IsStart && st.Track is { } t && _data.PassingStations.TryGetValue(st.St.Id, out var ps) && ps.TrackFor(run.Dir) == t;

    /// <summary>この駅での停車時分の見込み（秒）。待避線は最低の停車時分、通過は 0。</summary>
    private int DwellSeconds(TrainRun run, StationRun st) =>
        IsLoopTrack(run, st) ? Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds)
        : st.StopType == "通過" && !st.IsStart && !st.DepartStop ? 0 : _data.DefaultDwellSeconds;

    /// <summary>待避線に着いてから、最低の停車時分がたっていなければ待つ理由。</summary>
    /// <summary>止まったとみなす速度（km/h 未満）。</summary>
    private const float StopSpeedKmh = 0.5f;

    /// <summary>
    /// 番線に入りきって 0km/h になる（止まる）まで開通させない。止まってから afterStopSeconds（と番線ごとの秒数）待つ。
    /// 秒数で待つと運転のしかたで長さが変わるので、ゲームの速度で止まったことを確かめる。
    /// </summary>
    private string? StopHold(StationRun st, DateTime now, string what, int extraSeconds = 0)
    {
        if (st.StoppedUtc is not { } at) return $"{what}（止まったら開通）";
        var sec = Math.Max(extraSeconds, _data.AfterStopSeconds);
        var left = sec - (now - at).TotalSeconds;
        return left > 0 ? $"{what}（止まってから{sec}秒・あと {Math.Ceiling(left):0}秒）" : null;
    }

    /// <summary>待避線に入った列車は、止まるまで出発を開通させない（すぐに次の信号を要求しないように）。</summary>
    private string? LoopDwellHold(TrainRun run, StationRun st, DateTime now) =>
        IsLoopTrack(run, st) ? StopHold(st, now, "待避線で停車") : null;

    /// <summary>ダイヤグラムのスジずらしで決めた発車時刻の30秒前まで、出発を開通させない。</summary>
    private string? DepartTimeHold(TrainRun run, StationRun st, DateTime now)
    {
        if (_central.OpenAt(run.Train, st.St.Id) is not { } t || now >= t.AddSeconds(-DepartLeadSeconds)) return null;
        return $"発車時刻待ち（{t.ToLocalTime():HH:mm:ss} 発。{DepartLeadSeconds}秒前に開通）";
    }

    /// <summary>スジずらしで決めた発車時刻の、何秒前に出発の進路を要求するか。</summary>
    private const int DepartLeadSeconds = 30;

    /// <summary>
    /// 番線に入りきって止まる（0km/h）まで、出発を開通させない。番線ごとの指定（大道寺の上り1番線など）がある番線は必ず、
    /// ない番線は、出発が開通しないまま入りきった（停止で入った）ときだけ。指定の秒数は「止まってから」の秒数。始発の列車には使わない。
    /// </summary>
    private string? TrackDelayHold(TrainRun run, StationRun st, DateTime now)
    {
        if (st.IsStart || st.Track is not { } t) return null;
        var configured = st.St.DepartDelay.TryGetValue((run.Dir, t), out var conf);
        if (!configured && st.InTrackUtc == null) return null; // 入りきる前に開通させるのはかまわない
        return StopHold(st, now, "停車", configured ? conf : 0);
    }

    /// <summary>行き止まり（折り返し）の番線に入るか。入ると、この駅で打ち切り。</summary>
    private static bool IsTurnbackTrack(TrainRun run, StationRun st) =>
        !st.Terminal && st.Track is { } t && st.St.Routes[run.Dir] is var routes && routes.Turnback.Contains(t) && !routes.Depart.ContainsKey(t);

    /// <summary>手前の駅で、行き止まりの番線に入って打ち切る（この駅には来ない）か。</summary>
    private static bool IsCutOff(TrainRun run, StationRun st)
    {
        foreach (var s in run.Stations)
        {
            if (s == st) return false;
            if (!s.Done && (IsTurnbackTrack(run, s) || s.Terminal)) return true;
        }
        return false;
    }

    private void Process(TrainRun run, StationRun st, Dictionary<string, TrainRun> occupied, DateTime now)
    {
        if (IsCutOff(run, st)) return;
        if (st.Done || !st.Locked && !st.IsStart && !_central.IsForced(run.Train, st.St.Id)) return;

        // 終着の列車の入換（引上げ）: 要求があれば、番線に入りきってから入換進路を開通させる
        if (st.ShuntOutChoice is { } outIndex && st.ShuntOut != GroupState.Requested)
        {
            var outOptions = ShuntOutOptions(run, st);
            if (outIndex >= outOptions.Count) st.ShuntOutChoice = null;
            else
            {
                var outDef = ShuntOutPath(run, st, outOptions[outIndex], occupied);
                var outPath = outDef.Routes;
                var outWait = !st.IsStart && !FullyInTrack(run, st) ? "列車が番線に入るまで"
                    : !st.IsStart && StopHold(st, now, "停車") is { } stopWait ? stopWait
                    : st.ForceShuntOut ? InterlockConflict(run, outPath, occupied)
                    : (_central.Hold(run.Train, st.St.Id) & HoldKind.Depart) != 0 ? "出発抑止（集中扱い）"
                    : Conflict(run, st, outPath, occupied, departure: true);
                if (outWait != null) (st.ShuntOut, st.ShuntOutWait) = (GroupState.Waiting, outWait);
                else
                {
                    foreach (var outRoute in Enumerable.Reverse(outPath))
                        run.Requests.Add(new RouteReq { Def = outRoute, Id = ++_seq, Station = st, Departure = true, RequestedUtc = now });
                    (st.ShuntOut, st.ShuntOutWait, st.ForceShuntOut) = (GroupState.Requested, "", false);
                    _log.Note($"入換: {run.Train} {st.St.Name} {st.Track}番線→{outOptions[outIndex].Label}{(outDef.Via != null ? $"（{outDef.Via}経由）" : "")}（{string.Join("・", outPath.Select(x => x.Name))}）");
                }
            }
        }

        // 前を走る同じ方向の列車がこの駅をまだ通っていないのに、後ろの自分が先に進路を取っていたら、譲る（まだ進路に入っていなければ取り消す）
        // 駅扱い（強制進路）で開通させた進路は譲らない
        if (!st.IsStart && !st.ManualUsed && (st.Arrive == GroupState.Requested || st.Depart == GroupState.Requested)
            && AheadHold(run, st, departure: st.Depart == GroupState.Requested) is { } aheadWhy)
        {
            var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
            if (!active.Any(r => r.Def.Circuits.Any(run.Player.TrackCircuits.Contains)))
            {
                foreach (var r in active) r.CancelledUtc = now;
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                (st.Depart, st.DepartWait) = (GroupState.None, "");
                _log.Note($"進路を譲る: {run.Train} {st.St.Name} … {aheadWhy}（{string.Join("・", active.Select(r => r.Def.Name))} を取消）");
            }
        }

        // 始発駅で集中扱いの「開通指示」があれば、入換要求・出発要求の代わりにする（司令役が列車を出発させる）
        if (st.IsStart && _central.IsForced(run.Train, st.St.Id))
        {
            if (st.ShuntFrom != null) st.ShuntRequested = true;
            else st.DepartureRequested = true;
        }

        // 始発の入換（引上線などからホームへ）: 入換要求があったら、入換進路を要求する
        if (st.IsStart && st.ShuntFrom is { } shuntFrom)
        {
            if (!st.ShuntRequested || st.Shunt == GroupState.Requested || st.Track is not { } to) return;

            // 同じ元・同じ番線への入換が複数あれば、書いた順に優先。RequireFree の回路に他の列車がいれば次の候補
            var choice = st.St.Routes[run.Dir].Shunts
                .Where(s => s.FromLabel == shuntFrom && s.To == to)
                .FirstOrDefault(s => s.RequireFree.All(c => !occupied.TryGetValue(c, out var o) || o == run));
            var path = choice?.Routes;
            if (path == null || path.Count == 0)
            {
                (st.Shunt, st.ShuntWait) = (GroupState.Waiting, "入換に使う線路がふさがっています");
                return;
            }

            st.ShuntVia = choice!.Via;
            var wait = st.ForceShunt ? InterlockConflict(run, path, occupied)
                : _central.IsHeld(run.Train, st.St.Id) ? "抑止（集中扱い）"
                : TimeWait(run, st, now) ?? DiaShuntHold(run, st) ?? Conflict(run, st, path, occupied, departure: false);
            if (wait != null)
            {
                (st.Shunt, st.ShuntWait) = (GroupState.Waiting, wait);
                return;
            }

            foreach (var r in Enumerable.Reverse(path))
                run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = false, RequestedUtc = now });
            (st.Shunt, st.ShuntWait) = (GroupState.Requested, "");
            if (st.ForceShunt) (st.ForceShunt, st.ManualUsed) = (false, true);
            return;
        }

        // 始発駅: 出発要求があったら、その時点の番線で確定する
        if (st.IsStart && !st.Locked)
        {
            if (DiaMode && run.Dia != null && !st.Terminal && st.ShuntFrom == null) st.DepartureRequested = true; // ダイヤ運転中は要求なしで（時刻で開ける）
            if (!st.DepartureRequested && !_central.IsForced(run.Train, st.St.Id))
            {
                if (!st.Terminal && st.Track != null) (st.Depart, st.DepartWait) = (GroupState.Waiting, "出発要求待ち");
                return;
            }

            st.Locked = true;
            ResolvePaths(run, st);
        }
        if (_central.IsForced(run.Train, st.St.Id) && !st.Locked)
        {
            st.Locked = true;
            if (!st.Triggered) { st.Triggered = true; st.TriggeredUtc = now; }
            ResolvePaths(run, st);
        }

        if (st.ArrivePath.Count == 0 && st.DepartPath.Count == 0 && st.Locked) ResolvePaths(run, st);

        var hold = _central.Hold(run.Train, st.St.Id);
        var arriveHeld = (hold & HoldKind.Arrive) != 0;
        var departHeld = (hold & HoldKind.Depart) != 0;
        var central = _central.IsCentral(st.St.Id) && !_central.IsForced(run.Train, st.St.Id);
        var timeWait = TimeWait(run, st, now);

        // 場内（通過を含む）を開通できるか。先に判定だけして、送るのは出発のあと
        string? arriveWait = null;
        var needArrive = st.ArrivePath.Count > 0 && st.Arrive != GroupState.Requested && !st.IsStart;
        st.ArriveBlocked = false;
        if (needArrive)
        {
            string? conflict = null;
            if (st.ForceArrive) arriveWait = conflict = InterlockConflict(run, st.ArrivePath, occupied); // 駅扱い: 連動の確認だけ
            else
                arriveWait = arriveHeld ? "場内抑止（集中扱い）"
                    : central ? "集中扱い（開通指示待ち）"
                    : timeWait ?? SingleEntryHold(run, st) ?? PrevDepartHold(run, st) ?? ApproachHold(run, st) ?? (st.Track == null ? DepartTimeHold(run, st, now) : null)
                      ?? (DepartBeforeArrive(st) && st.DepartPath.Count > 0 && st.Depart != GroupState.Requested ? "出発の開通待ち（出発まで開けてから場内）" : null)
                      ?? DiaStartFirstHold(run, st)
                      ?? (conflict = Conflict(run, st, st.ArrivePath, occupied, departure: false));
            st.ArriveBlocked = arriveWait != null && ReferenceEquals(arriveWait, conflict);
            if (arriveWait != null && !st.ForceArrive && st.TrackSource == "待避" && st.Track is { } lt && LoopUser(run, st, lt) is { } lu)
                arriveWait = $"待避線ふさがり（{lu.Train}）";
        }

        st.OrderWaiting = st.DepartPath.Count > 0 && st.Depart != GroupState.Requested && OrderHold(run, st, st.DepartPath) != null;

        // 出発（場内より先に送る）。場内を開通できない（先行列車が番線にいるなど）うちは、出発も要求しない
        st.DepartBlocked = false;
        if (st.DepartPath.Count > 0 && st.Depart != GroupState.Requested)
        {
            string? wait, conflict = null;
            if (st.ForceDepart) wait = conflict = InterlockConflict(run, st.DepartPath, occupied); // 駅扱い: 連動の確認だけ
            else if (departHeld) wait = "出発抑止（集中扱い）";
            else if (central) wait = "集中扱い（開通指示待ち）";
            else if (timeWait != null) wait = timeWait;
            else if (st.IsStart && !st.DepartureRequested) wait = "出発要求待ち";
            else if (DepartBeforeArrive(st) && !st.IsStart && ApproachHold(run, st) is { } aw) wait = aw;
            else if (needArrive && arriveWait != null && !DepartBeforeArrive(st)) wait = $"場内待ち（{arriveWait}）";
            else if (needArrive && LeadsToSingleTrack(run, st)) wait = "単線区間のため、場内の開通後に判断";
            else if (OrderHold(run, st, st.DepartPath) is { } ow) wait = ow;
            else wait = PassingHold(run, st, now)
                        ?? DepartStopHold(run, st, needArrive)
                        ?? LoopDwellHold(run, st, now)
                        ?? SingleTrackHold(run, st, now)
                        ?? TrackDelayHold(run, st, now)
                        ?? DepartTimeHold(run, st, now)
                        ?? DiaDepartHold(run, st)
                        ?? (conflict = Conflict(run, st, st.DepartPath, occupied, departure: true));
            st.DepartBlocked = wait != null && ReferenceEquals(wait, conflict);

            if (wait == null)
            {
                Request(run, st, st.DepartPath, departure: true, now);
                if (st.ForceDepart) (st.ForceDepart, st.ManualUsed) = (false, true);
            }
            else (st.Depart, st.DepartWait) = (GroupState.Waiting, wait);
        }

        if (needArrive)
        {
            if (arriveWait == null)
            {
                Request(run, st, st.ArrivePath, departure: false, now);
                if (st.ForceArrive) (st.ForceArrive, st.ManualUsed) = (false, true);
            }
            else (st.Arrive, st.ArriveWait) = (GroupState.Waiting, arriveWait);
        }
    }

    /// <summary>この駅からの出発が、単線区間へ出るか。</summary>
    private bool LeadsToSingleTrack(TrainRun run, StationRun st) => NextControlStation(run, st) is { } next && _data.IsSingleTrack(st.St.Id, next);

    private string? NextControlStation(TrainRun run, StationRun st)
    {
        var step = run.Dir.Step();
        for (var z = st.Zone + 2 * step; z >= 0 && _data.Line.StationAtZone(z) is { } id; z += 2 * step)
            if (_data.Stations.ContainsKey(id)) return id;
        return null;
    }

    /// <summary>
    /// 連動の確認（駅扱いの強制進路用）: 進路の回路に他の列車がいない、他の列車の進路と重ならない。
    /// サーバーの抑止・待避・単線の交換・順番待ちなどは見ない。
    /// </summary>
    private string? InterlockConflict(TrainRun run, List<RouteDef> path, Dictionary<string, TrainRun> occupied)
    {
        var circuits = Circuits(path).ToHashSet();
        foreach (var c in circuits)
            if (occupied.TryGetValue(c, out var o) && o != run) return $"在線（{o.Train}）";
        foreach (var other in _runs.Values.Where(o => o != run))
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && LiveCircuits(other, r).Any(circuits.Contains)))
                return $"進路が重なる（{other.Train}）";
        return null;
    }

    /// <summary>
    /// 他の列車の進路のうち、まだ使う回路。列車がその進路に入っていれば、列車より後ろの回路は使い終わっている
    /// （1番線に入りきった列車の場内進路が、2番線への場内とぶつかって見えないように）。回路は進路の順に並んでいる。
    /// </summary>
    private static IEnumerable<string> LiveCircuits(TrainRun owner, RouteReq r)
    {
        var c = r.Def.Circuits;
        for (var i = 0; i < c.Count; i++)
            if (owner.Player.TrackCircuits.Contains(c[i])) return c.Skip(i);
        return c;
    }

    /// <summary>
    /// 出発停止: 出発停止の番線に入る場内進路、または始発の入換進路が、まだ解除されていなければ（列車がその番線に入りきっていなければ）待つ理由。
    /// 連動で鎖錠されているので、同時に送ってもゲームに競合で断られる。解除はゲームの自動解除（列車が進路を抜けたとき）。
    /// </summary>
    private string? DepartStopHold(TrainRun run, StationRun st, bool arrivalNotYetRequested)
    {
        if (st.DepartStop && arrivalNotYetRequested) return "出発停止（場内の開通待ち）";
        if (!st.DepartStop && !st.IsStart) return null;
        var pending = run.Requests.Where(r => r.Station == st && !r.Departure && !r.Released && r.CancelledUtc == null).ToList();
        if (pending.Count == 0) return null;

        // ゲームは鎖錠を見ない。場内が残っていても、番線に入りきっていれば進める
        return FullyInTrack(run, st, pending.SelectMany(r => r.Def.Circuits)) ? null : "出発停止（列車が番線に入るまで）";
    }

    /// <summary>
    /// 列車が番線に入りきったか: 番線の回路に入り、それより手前の回路（場内・入換の進路の回路）をすべて抜けた。
    /// 手前の回路を渡さなければ、その駅の場内の進路の回路で判断する。
    /// </summary>
    private static bool FullyInTrack(TrainRun run, StationRun st, IEnumerable<string>? before = null)
    {
        if (st.Track is not { } t || !st.St.TrackCircuit.TryGetValue(t, out var platform)) return false;
        if (!run.Player.TrackCircuits.Contains(platform)) return false;
        return (before ?? st.ArrivePath.SelectMany(r => r.Circuits)).Where(c => c != platform).All(c => !run.Player.TrackCircuits.Contains(c));
    }

    /// <summary>ダイヤグラムで開通時刻が指定されていて、まだその時刻になっていなければ、待つ理由。開通指示があれば待たない。フリーモードでは使わない。</summary>
    private string? TimeWait(TrainRun run, StationRun st, DateTime now)
    {
        if (!UseOpenTime || _central.IsForced(run.Train, st.St.Id)) return null;
        return _central.OpenAt(run.Train, st.St.Id) is { } t && now < t ? $"開通時刻待ち（{t.ToLocalTime():HH:mm:ss}）" : null;
    }

    private void Request(TrainRun run, StationRun st, List<RouteDef> path, bool departure, DateTime now)
    {
        // 同じ進路を取り消した記録が残っていれば片付ける（残すと「消す」指示が、開き直した進路まで消してしまう）
        foreach (var old in run.Requests.Where(o => !o.Released && o.CancelledUtc != null && path.Contains(o.Def)))
            old.Released = true;

        // 遠い方から送る（信号機が、先の進路の開通を見てから現示を出せるように）
        foreach (var r in Enumerable.Reverse(path))
            run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = departure, RequestedUtc = now });
        RouteLog(now, run, st, departure ? "出発" : st.Track == null ? "通過" : "場内", path);
        if (departure) (st.Depart, st.DepartWait) = (GroupState.Requested, "");
        else (st.Arrive, st.ArriveWait) = (GroupState.Requested, "");
    }

    private IEnumerable<string> Circuits(IEnumerable<RouteDef> path) => path.SelectMany(r => r.Circuits);

    /// <summary>進路の範囲に他の列車がいる・他の列車の進路と重なる・先に手前の駅を通過した列車が待っている・前方に列車がいる。</summary>
    private string? Conflict(TrainRun run, StationRun st, List<RouteDef> path, Dictionary<string, TrainRun> occupied, bool departure)
    {
        var circuits = Circuits(path).ToHashSet();
        foreach (var c in circuits)
            if (occupied.TryGetValue(c, out var o) && o != run) return $"在線（{o.Train}）";

        if (AheadHold(run, st, departure) is { } ahead) return ahead;

        foreach (var other in _runs.Values.Where(o => o != run))
        {
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && LiveCircuits(other, r).Any(circuits.Contains)))
                return $"進路が重なる（{other.Train}）";

            // 同じ方向で後ろにいる列車は、順番待ちの相手にしない（前の列車が先。AheadHold で後ろの列車が待つ）
            if (other.HasPosition && other.Dir == run.Dir && (other.Front - run.Front) * run.Dir.Step() < 0) continue;

            // 先に開通の処理を始めて、同じ範囲を待っている列車。進路の競合（在線・重なり）で待っているものだけ
            // （単線の交換・待避・抑止・出発順序・出発要求待ちなどで自分から待っている列車を待つと、お互いに待ち合って止まる）
            foreach (var os in other.Stations.Where(s => s.St.Id == st.St.Id && !s.Done && s.Triggered && s.TriggeredUtc < st.TriggeredUtc))
            {
                // 相手が自分（の在線・進路）を待っているなら、自分が先（待ち合わない）
                var mention = $"（{run.Train}）";
                var theirs = (os.Arrive == GroupState.Waiting && os.ArriveBlocked && !os.ArriveWait.Contains(mention) ? os.ArrivePath : new List<RouteDef>())
                    .Concat(os.Depart == GroupState.Waiting && os.DepartBlocked && !os.DepartWait.Contains(mention) ? os.DepartPath : new List<RouteDef>());
                if (Circuits(theirs).Any(circuits.Contains)) return $"順番待ち（{other.Train}）";
            }
        }

        if (departure && path.Count > 0)
        {
            // 前方の防護区間（出発信号機から次の閉塞信号機まで）
            var last = path[^1];
            if (_data.Signals.TryGetValue(last.Signal, out var sig))
                foreach (var c in sig.Protects)
                    if (occupied.TryGetValue(c, out var o) && o != run) return $"前方在線（{o.Train}）";
        }

        return null;
    }

    /// <summary>
    /// 単線区間の出発（交換の判断）。駅間ごとに判断する。
    /// ・出発を決めるのは、列車がその駅に着いているか、その手前の駅間まで来てから（何駅も先の単線区間を先取りしない）。
    ///   これで、大道寺と水越に対向する列車がいれば藤江で、藤江と高見沢にいれば水越で行き違う
    /// ・次の駅までの区間に対向列車がいる、または相手の駅で対向列車の出発進路が出ていれば待つ。同じ方向の列車は続行してよい（間隔は信号と前方在線で守る）
    /// ・走行順（集中扱い）が決まっていれば、その順で流す
    /// ・なければダイヤグラムの見込みで、自分が相手の駅に着くより先に相手の駅に着く対向列車を、早い順に最大2本まで先に通す
    ///   （例: 1040 に対して 1051・903A・1041 が来るなら、903A まで待って出る）。待ち合いは、着くのが早い方が待つ（デッドロックしない）
    /// ・単線区間の駅は2線あるので、対向列車はもう片方の番線に入れる（AvoidTakenTrack）
    /// </summary>
    private string? SingleTrackHold(TrainRun run, StationRun st, DateTime now)
    {
        var step = run.Dir.Step();
        var next = NextControlStation(run, st);

        if (next == null || !_data.IsSingleTrack(st.St.Id, next)) return null;
        var nextName = _data.Line.StationName(next);

        // まだこの駅の手前の駅間まで来ていなければ、交換の判断をしない（場内が停止なら、判断は直前でよい）
        var here = (run.Front - st.Zone) * step >= 0;            // 駅に着いている（構内か、その先）
        var approaching = run.Front == st.Zone - step;             // 手前の駅間
        // 進路を取り始める位置（ApproachTrigger）が入っていれば、そこを過ぎたら判断する
        // （通過列車の場内・出発をまとめて開けられるように。水越の上りが、藤江を通過する前に出発まで開くように）
        var approachPassed = ApproachOf(run, st) is { } ap && PassedApproach(run, st, ap);
        if (!st.IsStart && !here && !approaching && !approachPassed) return "交換の判断待ち（駅に近づくまで）";

        var nextZone = _data.Line.ZoneOfStation(next);
        var (lo, hi) = (Math.Min(st.Zone, nextZone), Math.Max(st.Zone, nextZone));

        // ---- 必ず待つ: 区間に対向列車がいる、相手の駅で対向列車の出発進路が出ている ----
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition && o.Dir != run.Dir))
        {
            if (other.Player.Zones.Any(z => z > lo && z < hi)) return $"単線区間に対向列車（{other.Train}）";
            // 相手がもう区間を通り終えて（こちらの駅に入りきって）いれば、相手の出発進路の記録が残っていても待たない
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && r.Departure && r.Station.St.Id == next)
                && !SectionCleared(other, st.St.Id, next))
                return $"対向列車（{other.Train}）が{nextName}を出発";
        }

        var section = _data.SingleTrackSections.FirstOrDefault(s => s.Item1 == st.St.Id && s.Item2 == next || s.Item2 == st.St.Id && s.Item1 == next);
        // 区間へ逆向きに出る出発進路が出ている（手動進路構成で出したものを含む。進路の種別から向きを決める）
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition))
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && DepartSection(r.Def, other.Dir) is { } ds && ds.Section == section && ds.Dir != run.Dir)
                && !SectionCleared(other, st.St.Id, next))
                return $"対向列車（{other.Train}）の出発進路が{st.St.Name}〜{nextName}へ出ています";

        // ---- 方向てこ: 区間が逆向きに鎖錠されている間は、こちら向きの出発を開通させない ----
        if (_levers.TryGetValue(section, out var lever) && lever.Locked && lever.Dir != run.Dir)
            return $"方向てこが{lever.Dir.Label()}向き（{st.St.Name}〜{nextName}）";

        // ---- ダイヤ運転中: ダイヤで先に区間へ入る対向列車が、まだ現れていなくても（遅れ・未接続）、区間に入るまで待つ ----
        // 交換駅はダイヤどおり。相手の駅の場内が空いていても信号は取らない。走行順を手で決めた区間は、その順に従う
        if (DiaMode && run.Dia != null && _central.SectionOrder(st.St.Id, next) == null && DiaAbsentOpposing(run, st, next) is { } absent)
            return $"交換待ち（ダイヤ: {absent} が先に{st.St.Name}〜{nextName}を通る）";

        // ---- 走行順（集中扱い）: 区間ごとに決めた順で流す ----
        var order = EffectiveSectionOrder(st.St.Id, next); // ダイヤ運転中は、手で決めていなければダイヤの時刻順
        if (order != null && order.Contains(run.Train))
        {
            var before = order.TakeWhile(t => t != run.Train).Where(t => RunByTrain(t) is { } p && !SectionCleared(p, st.St.Id, next)).ToList();
            return before.Count > 0 ? $"走行順待ち（{st.St.Name}〜{nextName}: {string.Join("・", before)} が先）" : null;
        }

        // ---- 対向列車を先に通すか（ダイヤグラムの見込みで）。最大2本 ----
        // 自分が相手の駅に着くより、対向列車が相手の駅を出る見込みの方が十分早い（相手を駅で待たせることになる）なら、先に通す。
        // ほぼ同時なら相手の駅で行き違う（大道寺と水越の列車は藤江で）。お互いに相手を待つことになるとき（待ち合い）は、
        // 着くのが遅い方（走ってくる方）を先に通す。先に通すと決めたら、その列車が来るまで待つ（判断を変えない）
        var type = RunType(run);
        var runTime = _model.SecondsBetween(type, run.Dir, st.St.Id, next) ?? 180;
        double mineReady = here || st.IsStart ? 0 : _model.SecondsTo(type, run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0;
        var mineArrNext = mineReady + (here || st.IsStart ? 0 : DwellSeconds(run, st)) + runTime; // 自分が相手の駅に着く見込み
        st.OpposingWaits.RemoveAll(t => RunByTrain(t) is not { } p || !ComesThrough(p, next, st.St.Id) || SectionCleared(p, st.St.Id, next));

        var candidates = _runs.Values
            .Where(o => o != run && o.HasPosition && o.Dir != run.Dir && ComesThrough(o, next, st.St.Id))
            .Where(o => !(order?.Contains(o.Train) ?? false))
            // 出発要求をしていない始発列車（止まったまま）は待たない
            .Where(o => o.Stations.FirstOrDefault(s => s.St.Id == next) is not { IsStart: true } ns || ns.DepartureRequested || ns.ShuntRequested)
            .Select(o => (Run: o, Ready: ReadyAt(o, next)))
            .Where(x => x.Ready != null)
            .OrderBy(x => x.Ready)
            .ToList();
        foreach (var (o, ready) in candidates)
        {
            if (st.OpposingWaits.Contains(o.Train)) return $"対向列車（{o.Train}）を先に通す（{nextName}から）";
            if (st.OpposingWaits.Count + WaitedDone(st) >= MaxOpposingWaits) break;

            // 相手が自分を先に通すと決めているなら、待たない
            if (o.Stations.FirstOrDefault(s => s.St.Id == next && !s.Done) is { } os && os.OpposingWaits.Contains(run.Train)) continue;
            if (_central.IsHeld(o.Train, next)) continue; // 相手が抑止されていれば待たない

            // 相手が相手の駅を出られる見込み（着く＋停車時分）。自分がそこに着くより十分早ければ、相手を待たせることになる
            var theirDwell = o.Stations.FirstOrDefault(s => s.St.Id == next) is { } ons && ready!.Value > 0 ? DwellSeconds(o, ons) : 0;
            var theirLeaveNext = ready!.Value + theirDwell;
            if (theirLeaveNext + MeetToleranceSeconds >= mineArrNext) continue; // ほぼ同時か、自分が先に着く: 相手の駅で行き違う

            // 待ち合い: 相手から見ても、自分を待つことになる（自分がこの駅を出るのが、相手がこの駅に着くより十分早い）なら、着くのが早い方が待つ
            var theirRun = _model.SecondsBetween(RunType(o), o.Dir, next, st.St.Id) ?? 180;
            var mutual = mineReady + MeetToleranceSeconds < theirLeaveNext + theirRun;
            if (mutual && (ready.Value < mineReady || ready.Value == mineReady && string.CompareOrdinal(o.Train, run.Train) > 0)) continue;

            st.OpposingWaits.Add(o.Train);
            st.WaitedTotal++;
            _log.Note($"単線: {run.Train} は {st.St.Name} で対向の {o.Train} を先に通す（{nextName}に 自分が着く {mineArrNext:0}秒後・相手が出る {theirLeaveNext:0}秒後）");
            return $"対向列車（{o.Train}）を先に通す（{nextName}から）";
        }

        return null;
    }

    private const int MaxOpposingWaits = 2;

    /// <summary>行き違いで、相手を相手の駅で待たせてよい時間（秒）。これより長く待たせるなら、こちらが待つ。</summary>
    private const double MeetToleranceSeconds = 60;

    /// <summary>
    /// 単線区間を通って入る駅の場内は、手前の駅からその区間への出発が決まってから（行き違いの判断の前に、相手の駅の場内を取って
    /// 相手の出発をふさがないように）。駅を出たあと（区間の中）なら待たない。
    /// </summary>
    private string? SingleEntryHold(TrainRun run, StationRun st)
    {
        if (st.IsStart) return null;
        var prev = run.Stations.TakeWhile(s => s != st).LastOrDefault();
        if (prev == null || prev.Done || !_data.IsSingleTrack(prev.St.Id, st.St.Id)) return null;
        if ((run.Front - prev.Zone) * run.Dir.Step() > 0) return null; // もう手前の駅を出た
        if (prev.Depart == GroupState.Requested || prev.Terminal) return null;
        if (prev.Locked && prev.DepartPath.Count == 0) return null; // 手前の駅から出る進路がない（扱わない）
        return $"{prev.St.Name}の出発待ち（単線区間）";
    }

    /// <summary>
    /// 手前の駅（進路を扱う駅）の出発が、まだ開通していなければ待つ理由。出発抑止中や、出発が引けない（ほかの列車の進路と競合する・
    /// 待避中・単線の交換待ちなど）列車の、その先の駅の進路は取らない（取ると、ほかの列車の進路を無駄にふさぐため）。
    /// </summary>
    private string? PrevDepartHold(TrainRun run, StationRun st)
    {
        if (st.IsStart) return null;
        var prev = run.Stations.TakeWhile(s => s != st).LastOrDefault(s => !s.Done);
        if (prev == null || IsCutOff(run, prev) || prev.Terminal) return null;
        if ((run.Front - prev.Zone) * run.Dir.Step() > 0) return null; // もう手前の駅を出た
        // 始発の駅: 入換（引上線などからホーム）が終わり、出発進路を要求するまで、先の駅の進路は取らない
        if (prev.IsStart && prev.ShuntFrom != null) return $"{prev.St.Name}の入換待ち";
        if (prev.IsStart && prev.Depart != GroupState.Requested && !IsTurnbackTrack(run, prev)) return $"{prev.St.Name}の出発待ち";
        if (prev.Track == null) return prev.Arrive == GroupState.Requested ? null : $"{prev.St.Name}の通過待ち";
        if (prev.DepartPath.Count == 0) return null;
        return prev.Depart == GroupState.Requested ? null : $"{prev.St.Name}の出発待ち";
    }

    /// <summary>先に通し終わった対向列車の数（OpposingWaits から外したもの）。</summary>
    private static int WaitedDone(StationRun st) => st.WaitedTotal - st.OpposingWaits.Count;

    /// <summary>その列車が、駅 from から駅 to へ（区間を通って）これから走るか（from で打ち切り・終着しない）。</summary>
    private static bool ComesThrough(TrainRun o, string from, string to)
    {
        var f = o.Stations.FirstOrDefault(s => s.St.Id == from);
        var t = o.Stations.FirstOrDefault(s => s.St.Id == to);
        if (f == null || t == null || t.Done || IsCutOff(o, t)) return false;
        if (f.Terminal || IsTurnbackTrack(o, f)) return false;
        return !f.Done || (o.Front - f.Zone) * o.Dir.Step() >= 0; // from を出た後（区間内）も含む
    }

    /// <summary>その列車が駅に着く（出発できるようになる）までの見込み（秒）。駅にいれば 0。もう過ぎていれば null。</summary>
    private double? ReadyAt(TrainRun o, string station)
    {
        var z = _data.Line.ZoneOfStation(station);
        var d = (o.Front - z) * o.Dir.Step();
        if (d > 0) return null;
        if (d == 0 || o.Player.Zones.Contains(z)) return 0;
        return _model.SecondsTo(RunType(o), o.Dir, o.Front, o.Fraction, station);
    }

    /// <summary>その列車が、駅 a〜b の単線区間を通り終えたか（もう通らない列車も含む）。</summary>
    private bool SectionCleared(TrainRun p, string a, string b)
    {
        var za = _data.Line.ZoneOfStation(a);
        var zb = _data.Line.ZoneOfStation(b);
        var (lo, hi) = (Math.Min(za, zb), Math.Max(za, zb));
        if (p.Player.Zones.Any(z => z > lo && z < hi)) return false; // 区間内
        var exit = p.Dir == Dir.Down ? hi : lo;
        // 区間の出口の駅まで来ていれば通り終えた。区間をこれから通る（入口の駅より手前）なら、まだ
        if ((p.Front - exit) * p.Dir.Step() >= 0) return true;
        var entry = p.Dir == Dir.Down ? lo : hi;
        var entryId = _data.Line.StationAtZone(entry);
        var exitId = _data.Line.StationAtZone(exit);
        return entryId == null || exitId == null || !ComesThrough(p, entryId, exitId);
    }

    /// <summary>
    /// 同じ方向で前を走る列車が、まだこの駅を通っていなければ待つ（追い越しは待避駅で、別の番線に入るときだけ）。
    /// 江ノ原信号場のように1線しかない所で、後ろの列車に先に進路を渡さないため。出発要求前の始発列車は待たない。
    /// 場内は、前の列車と別の番線に入るならよい（単線区間の駅は2線ある）。
    /// </summary>
    private string? AheadHold(TrainRun run, StationRun st, bool departure)
    {
        var step = run.Dir.Step();
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition && o.Dir == run.Dir && (o.Front - run.Front) * step > 0))
        {
            if (other.Stations.FirstOrDefault(s => s.St.Id == st.St.Id) is not { Done: false } os || IsCutOff(other, os)) continue;
            if (os.Terminal || IsTurnbackTrack(other, os)) continue; // この駅で終わる列車は抜けないので待たない（番線のふさがりは在線で判断）
            if (os.IsStart && !os.DepartureRequested && !os.ShuntRequested) continue; // 止まったままの始発列車は追い越してよい
            // ダイヤ運転中: ダイヤでこちらが先に出る（通る）始発列車は待たない（江ノ原検車区から出る列車より先に、本線の列車を通す）
            if (os.IsStart && DiaMode && DiaSec(other, st.St.Id, depart: true) is { } od && DiaSec(run, st.St.Id, depart: true) is { } rd && rd < od) continue;
            // 出発抑止中で、別の番線にいる先行列車は追い越してよい（駒野1番線で抑止中の列車を残して、2番線の列車を先に出す）
            if ((_central.Hold(other.Train, st.St.Id) & HoldKind.Depart) != 0 && os.Track != null && os.Track != st.Track) continue;
            if (_data.PassingStations.ContainsKey(st.St.Id) && os.Track != null && os.Track != st.Track) continue; // 待避駅で別の番線
            // 別の番線に入るだけならよい。ただし、前の列車がまだ場内を取っていなくて、場内の進路が重なるなら、前の列車が先
            // （続行する2本が同じ駅の別の番線に入るとき、後ろの列車が先に場内を取ると、前の列車の場内がふさがるため）
            if (!departure && os.Track != null && st.Track != null && os.Track != st.Track
                && (os.Arrive == GroupState.Requested || os.IsStart || !Circuits(PendingPaths(other, os)).Any(Circuits(st.ArrivePath).ToHashSet().Contains))) continue;
            return $"先行列車（{other.Train}）が先";
        }

        return null;
    }

    // =========================================================================================
    // 出力
    // =========================================================================================

    private void BuildOutputs(DateTime now)
    {
        _outputs.Clear();
        foreach (var run in _runs.Values)
        {
            var routes = run.Requests.Where(r => !r.Released && r.CancelledUtc == null).OrderBy(r => r.Id)
                .Select(r => new RouteCommand
                {
                    Id = r.Id, Interlocking = r.Def.Interlock, RouteName = r.Def.Name,
                    Indicator = r.Def.IsDepartureSignal ? run.Indicator : r.Def.Indicator, // 出発は列選（ダイヤの種別表記。なければクライアントがゲームの種別から）
                    UseTrainType = r.Def.IsDepartureSignal,
                    TrainNumber = run.GameTrain.Length > 0 ? run.GameTrain : run.Train, // ゲームには元の列番で送る
                    StopType = r.Station.DepartStop && r.Station.StopType == "通過" ? "停車" : r.Station.StopType // 出発停止の番線では止める
                }).ToList();

            // 駅を抜けたのに残っている進路と、番線の変更で取り消した進路は、ゲームから消す
            var deletes = run.Requests
                .Where(r => !r.Released && (r.CancelledUtc != null || r.Seen && r.Station.Done && now - r.Station.DoneUtc > LeftoverDelay))
                .Where(r => run.Player.GameRoutes.Contains((r.Def.Interlock, r.Def.Name)))
                .Select(r => new RouteKey { Interlocking = r.Def.Interlock, RouteName = r.Def.Name })
                .ToList();

            // 要求した進路が5秒たっても開通しないとき、同じ連動装置に「サーバーが要求していない進路」がゲームに残っていれば消す
            // （列番の変更・つなぎ直し・サーバーの再起動の前に開通させた進路が残っていると、新しい進路が競合で開通しないため）。
            // 列車がもう入っている進路は消さない
            var wanted = run.Requests.Where(r => !r.Released).Select(r => (r.Def.Interlock, r.Def.Name)).ToHashSet();
            var stuck = run.Requests.Where(r => !r.Released && r.CancelledUtc == null && !r.Seen && now - r.RequestedUtc > StuckDelay)
                .Select(r => r.Def.Interlock).ToHashSet();
            foreach (var g in run.Player.GameRoutes.Where(g => stuck.Contains(g.Interlock) && !wanted.Contains(g)))
            {
                if (_data.Routes.TryGetValue(g, out var def) && def.Circuits.Any(run.Player.TrackCircuits.Contains)) continue;
                deletes.Add(new RouteKey { Interlocking = g.Interlock, RouteName = g.Route });
                if (_orphanLogged.Add((run.ConnId, g.Interlock, g.Route)))
                    _log.Note($"残っていた進路を取消: {run.Train} {g.Interlock} {g.Route}（要求していない進路が、新しい進路の開通をふさいでいたため）");
            }

            routes.RemoveAll(r => deletes.Any(d => d.Interlocking == r.Interlocking && d.RouteName == r.RouteName));

            var plan = run.Stations.Where(s => !s.Done).Select(s => new StationPlanView
            {
                StationId = s.St.Id,
                Name = s.St.Name,
                StopType = s.StopType,
                Track = s.Track,
                TrackOptions = s.Options,
                TrackSource = s.TrackSource,
                Locked = s.Locked,
                Status = ClientStatus(run, s), // 運転士には3段階だけ
                Detail = Status(run, s)        // 詳しい状態（画面には出さない）
            }).ToList();

            var start = run.Stations.FirstOrDefault(s => s.IsStart && !s.Done);
            var shunting = start?.ShuntFrom != null;
            var canDepart = start != null && !start.Terminal && (shunting ? !start.ShuntRequested : !start.DepartureRequested) && !(DiaMode && run.Dia != null);
            var label = shunting ? "入換要求" : "出発要求";
            // 終着の列車: 番線に入りきっていて、引上げの入換があれば「入換要求」
            if (ShuntOutStation(run) != null && !(DiaMode && run.Dia != null)) (canDepart, label) = (true, "入換要求");
            var message = !run.HasPosition ? "位置が分かりません（運転画面で列車を動かすと分かります）"
                : run.GameTrain.Length > 0 && run.Train != run.GameTrain ? $"同じ列番の列車が走っているため、サーバーでは {run.Train} として扱います"
                : null;
            _outputs[run.ConnId] = new Output(routes, deletes, plan, canDepart, label, message);
        }

        foreach (var (conn, a) in _appear)
            _outputs[conn] = new Output(new(), new(), new(), false, "出発要求", $"出現待ち: {a.Reason}");
    }

    /// <summary>
    /// クライアント（運転士）に出す状態。運転中は目の前の信号機しか見えないので、進路の中身（待ちの理由・抑止・順番など）は伏せ、
    /// 「出発要求待ち」（引上線からなら「入換要求待ち」）「進路確定」「進路未確定」の3段階だけにする。
    /// 進路確定: その駅でその列車に要る進路（場内、終着でなければ出発。通過なら両方）が全部開通した。管理画面には Status を出す
    /// </summary>
    private string ClientStatus(TrainRun run, StationRun s)
    {
        bool Opened(GroupState g, List<RouteDef> path)
        {
            if (path.Count == 0) return true;
            if (g != GroupState.Requested) return false;
            var reqs = run.Requests.Where(r => r.Station == s && path.Contains(r.Def) && !r.Released && r.CancelledUtc == null).ToList();
            return reqs.Count > 0 && reqs.All(r => r.Seen || Acknowledged(run, r));
        }

        if (IsCutOff(run, s)) return "進路未確定";
        if (s.IsStart && s.ShuntFrom != null)
            return !s.ShuntRequested ? "入換要求待ち"
                : s.Shunt == GroupState.Requested && run.Requests.Where(r => r.Station == s && !r.Departure && !r.Released).All(r => r.Seen || Acknowledged(run, r)) ? "進路確定" : "進路未確定";
        if (ShuntOutOptions(run, s).Count > 0 && (s.IsStart || FullyInTrack(run, s)))
            return s.ShuntOutChoice == null ? "入換要求待ち" : s.ShuntOut == GroupState.Requested ? "進路確定" : "進路未確定";
        if (s.IsStart && !s.Locked) return s.Terminal ? "進路確定" : "出発要求待ち";
        if (!s.Locked) return "進路未確定";
        var needDepart = !s.Terminal && !IsTurnbackTrack(run, s);
        var arriveOk = s.IsStart || Opened(s.Arrive, s.ArrivePath);
        var departOk = !needDepart || Opened(s.Depart, s.DepartPath);
        return arriveOk && departOk ? "進路確定" : "進路未確定";
    }

    private string Status(TrainRun run, StationRun s)
    {
        string Group(GroupState g, string wait, List<RouteDef> path, string label)
        {
            if (path.Count == 0) return "";
            if (g == GroupState.Waiting) return $"{label}待ち: {wait}";
            if (g != GroupState.Requested) return "";
            var reqs = run.Requests.Where(r => r.Station == s && path.Contains(r.Def)).ToList();
            if (reqs.Count > 0 && reqs.All(r => r.Seen || Acknowledged(run, r))) return $"{label}開通";
            var failed = path.Select(p => run.Results.GetValueOrDefault($"{p.Interlock}/{p.Name}")).FirstOrDefault(x => x != null && x != "OK");
            return failed != null ? $"{label}: {failed}（再試行中）" : $"{label}要求中";
        }

        if (IsCutOff(run, s)) return "扱わない（手前の駅で打ち切り）";
        if (!s.Locked && !s.IsStart) return IsTurnbackTrack(run, s) ? "未確定（この駅で打ち切り）" : "未確定";
        if (s.IsStart && s.ShuntFrom != null)
            return !s.ShuntRequested ? $"入換要求待ち（{s.ShuntFrom}→{s.Track}番線）"
                : s.Shunt == GroupState.Waiting ? $"入換待ち: {s.ShuntWait}"
                : (run.Requests.Any(r => r.Station == s && !r.Departure && r.Seen) ? "入換開通" : "入換要求中")
                  + (s.ShuntVia != null ? $"（{s.ShuntVia}経由）" : "");
        if (s.IsStart && !s.Locked)
            return !s.Terminal ? "出発要求待ち"
                : s.ShuntOutChoice == null ? "終着"
                : "終着／" + (s.ShuntOut == GroupState.Waiting ? $"入換待ち: {s.ShuntOutWait}" : s.ShuntOut == GroupState.Requested ? "入換（引上げ）要求済み" : "入換要求");
        var parts = new[]
        {
            Group(s.Depart, s.DepartWait, s.DepartPath, "出発"),
            Group(s.Arrive, s.ArriveWait, s.ArrivePath, s.Track == null ? "通過" : "場内")
        }.Where(x => x.Length > 0).ToList();
        if (s.HoldKept.Length > 0) parts.Add($"{s.HoldKept}抑止は列車が接近中のため取り消さず");
        if (s.ShuntOutChoice != null)
            parts.Add(s.ShuntOut == GroupState.Waiting ? $"入換待ち: {s.ShuntOutWait}" : s.ShuntOut == GroupState.Requested ? "入換（引上げ）要求済み" : "入換要求");
        if (s.Terminal) parts.Add("終着");
        else if (IsTurnbackTrack(run, s)) parts.Add("打ち切り（折り返し）");
        return parts.Count > 0 ? string.Join("／", parts) : "待機";
    }

    // =========================================================================================
    // 管理画面
    // =========================================================================================

    /// <summary>
    /// ダイヤグラム用のデータ。列車ごとに、走った実績・この先の見込み（標準運転時分）・駅ごとの開通の目印を返す。
    /// 縦の位置はゾーン（駅＝偶数）、時刻は UTC。
    /// </summary>
    public object DiagramView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // 在線を共有している列車だけ（特発のみ共有の列車は _runs に入らないが、念のためここでも絞る）
            var runs = _runs.Values.Where(r => r.HasPosition && r.Player.Mode == ShareMode.Shared).OrderBy(r => r.Train).ToList();

            // 1回目: スジずらし（発車時刻）だけを入れた見込み。2回目で、走行順・出発順序の先の列車を待つ時間を足す
            var basePlans = runs.ToDictionary(r => r, r => Plan(r, now, z => SlideDepart(r, z)));
            DateTime? MinDepart(TrainRun r, int z)
            {
                DateTime? m = SlideDepart(r, z);
                void Max(DateTime? t) { if (t != null && (m == null || t > m)) m = t; }
                if (_data.Line.StationAtZone(z) is not { } sid || r.Stations.FirstOrDefault(s => s.St.Id == sid && !s.Done) is not { } st) return m;

                // 単線区間の走行順: 先の列車が区間を通り終えるまで（同じ向きなら先の列車がこの駅を出たあと、逆向きなら先の列車がこの駅に着いたあと）
                if (NextControlStation(r, st) is { } nx && _data.IsSingleTrack(sid, nx) && _central.SectionOrder(sid, nx) is { } so && so.Contains(r.Train))
                    foreach (var name in so.TakeWhile(t => t != r.Train))
                        if (RunByTrain(name) is { } p && basePlans.TryGetValue(p, out var pp) && !SectionCleared(p, sid, nx))
                            Max(p.Dir == r.Dir ? pp.DepAt.TryGetValue(z, out var d1) ? d1 : null : pp.ArrAt.TryGetValue(z, out var a1) ? a1 : null);

                // 駅の出発順序: 同じ向きの先の列車がこの駅を出たあと
                if (EffectiveOrder(sid, r.Dir) is { } order && order.Contains(r.Train))
                    foreach (var name in order.TakeWhile(t => t != r.Train))
                        if (RunByTrain(name) is { } p && p.Dir == r.Dir && (_central.Hold(name, sid) & HoldKind.Depart) == 0
                            && basePlans.TryGetValue(p, out var pp) && pp.DepAt.TryGetValue(z, out var d2))
                            Max(d2);
                return m;
            }

            return runs.Select(r =>
            {
                var step = r.Dir.Step();
                var type = RunType(r);
                var plan = Plan(r, now, z => MinDepart(r, z));
                var forecast = plan.Points;
                var arrAt = plan.ArrAt;

                // 駅の目印（◆）: 進路を扱う駅ごとに、その駅を通過する（停車駅は着く）予想時刻に置く（見込みの線の上）。
                // 開通の時刻（開通済みならその時刻、まだなら2つ手前の駅に着く見込み）は、説明に出す
                var markers = r.Stations.Where(s => !s.Done && !IsCutOff(r, s)).Select(s =>
                {
                    var openAt = UseOpenTime ? _central.OpenAt(r.Train, s.St.Id) : null;
                    var started = s.Arrive == GroupState.Requested || s.Depart == GroupState.Requested || s.Shunt == GroupState.Requested;

                    // 通過・到着の予想。今いる駅（始発・停車中）は今
                    var here = s.IsStart || r.Front == s.Zone;
                    DateTime? time = here ? now : arrAt.TryGetValue(s.Zone, out var at) ? at : null; // 待避線の延長を足した見込み
                    // スジずらしで発車時刻を決めた駅は、◆を発車時刻に置く（ずらした位置に残る）
                    var departAt = _central.OpenAt(r.Train, s.St.Id);
                    if (departAt != null && plan.DepAt.TryGetValue(s.Zone, out var dt)) time = dt;
                    var stops = s.StopType != "通過" || s.DepartStop || s.Passing || IsTurnbackTrack(r, s) || s.Terminal;
                    var source = here ? "在線" : stops ? "到着予想" : "通過予想";

                    // 開通の時刻
                    DateTime? open = openAt;
                    var openSource = openAt != null ? "指定" : "見込み";
                    if (started) (open, openSource) = (s.TriggeredUtc != default ? s.TriggeredUtc : open, "開通済み");
                    else if (openAt == null)
                    {
                        if (s.Triggered) (open, openSource) = (s.TriggeredUtc, "到着済み");
                        else if (_data.Line.StationAtZone(s.Zone - 2 * TriggerAhead(r, s) * step) is { } prev)
                            open = _model.SecondsTo(type, r.Dir, r.Front, r.Fraction, prev) is { } sec ? now.AddSeconds(sec) : null;
                    }

                    // 番線を変えられるか: 選べる番線が2つ以上あり、列車がまだその駅の進路に入っていない
                    // 入換を出したあとの始発駅は、番線を変えられない
                    var entered = r.Requests.Any(q => q.Station == s && !q.Released && q.CancelledUtc == null
                                                      && q.Def.Circuits.Any(r.Player.TrackCircuits.Contains))
                                  || (s.IsStart && s.ShuntFrom != null && s.Shunt == GroupState.Requested);
                    return new
                    {
                        stationId = s.St.Id,
                        name = s.St.Name,
                        passingStation = _data.PassingStations.ContainsKey(s.St.Id),
                        y = (double)s.Zone,
                        t = time,
                        source,
                        openAt = open,
                        openSource,
                        editable = !started,
                        // ◆を横にずらすと、その駅の発車時刻を決められる（出発の進路をまだ取っていない、その駅で終わらない列車）
                        draggable = !s.Terminal && !IsTurnbackTrack(r, s) && s.Depart != GroupState.Requested,
                        departAt,
                        held = _central.IsHeld(r.Train, s.St.Id),
                        holdArrive = (_central.Hold(r.Train, s.St.Id) & HoldKind.Arrive) != 0,
                        holdDepart = (_central.Hold(r.Train, s.St.Id) & HoldKind.Depart) != 0,
                        // 臨時停車・臨時通過・打ち切りの指定（◆の窓で打ち切りを選べる）
                        stopOverride = _central.StopOverride(r.Train, s.St.Id),
                        terminal = s.Terminal,
                        manual = ManualOptions(r, s).Select(o => new { action = o.Action, label = o.Label }),
                        singleNext = NextControlStation(r, s) is { } nx && _data.IsSingleTrack(s.St.Id, nx) ? nx : null,
                        status = Status(r, s),
                        track = s.Track,
                        trackSource = s.TrackSource,
                        options = s.Options,
                        changeable = s.Options.Count > 1 && !entered
                    };
                }).Where(m => m.t != null).ToList();

                return new
                {
                    train = r.Train,
                    type = r.Type,
                    direction = r.Dir.Label(),
                    history = r.History.Select(h => new { t = h.Utc, y = h.Y }),
                    forecast,
                    markers
                };
            }).ToList();
        }
    }

    private sealed record PlanResult(List<object> Points, Dictionary<int, DateTime> ArrAt, Dictionary<int, DateTime> DepAt);

    /// <summary>スジずらしで決めた発車時刻（そのゾーンが進路を扱う駅なら）。</summary>
    private DateTime? SlideDepart(TrainRun r, int zone) =>
        _data.Line.StationAtZone(zone) is { } sid ? _central.OpenAt(r.Train, sid) : null;

    /// <summary>
    /// 列車の見込み（ダイヤグラムの点線）。今の位置から終着駅（なければ路線の端）まで、標準運転時分で各駅に着く・出る時刻を出す。
    /// 待避線では最低の停車時分まで止め、minDepart（スジずらし・走行順・出発順序で決まる、その駅を出られる最も早い時刻）より前には出ない。
    /// 延びた分は、その先の駅にも足す。停車は水平の線で描く。
    /// </summary>
    private PlanResult Plan(TrainRun r, DateTime now, Func<int, DateTime?> minDepart)
    {
        var step = r.Dir.Step();
        var type = RunType(r);
        var terminal = r.Player.Stops.Count > 0 ? _data.Line.StationIdByName(r.Player.Stops[^1].Name) : null;
        var terminalZone = terminal != null ? _data.Line.ZoneOfStation(terminal) : -1;
        // 行き止まりの番線に入る駅・終着（列車別設定の打ち切りを含む）があれば、線はその駅で切る
        if (r.Stations.FirstOrDefault(s => !s.Done && (IsTurnbackTrack(r, s) || s.Terminal)) is { } cut) terminalZone = cut.Zone;

        var points = new List<object> { new { t = now, y = PlotY(r) } };
        var arrAt = new Dictionary<int, DateTime>();
        var depAt = new Dictionary<int, DateTime>();
        double extra = 0;
        if (r.Front % 2 == 0 && r.Stations.FirstOrDefault(s => s.Zone == r.Front && !s.Done) is { } curSt)
        {
            // 今いる駅: 待避線の最低の停車時分の残り（入りきる前なら全部）と、決められた発車時刻
            var leave = now;
            if (IsLoopTrack(r, curSt))
            {
                var left = curSt.LoopArrivedUtc is { } la ? _data.MinLoopDwellSeconds - (now - la).TotalSeconds : _data.MinLoopDwellSeconds;
                if (left > 0) leave = now.AddSeconds(left);
            }

            if (minDepart(r.Front) is { } md && md > leave) leave = md;
            arrAt[r.Front] = now;
            depAt[r.Front] = leave;
            if (leave > now)
            {
                extra += (leave - now).TotalSeconds;
                points.Add(new { t = leave, y = (double)r.Front });
            }
        }

        for (var z = (r.Front % 2 == 0 ? r.Front : r.Front + step) + (r.Front % 2 == 0 ? 2 * step : 0);
             _data.Line.StationAtZone(z) is { } sid; z += 2 * step)
        {
            if (_model.SecondsTo(type, r.Dir, r.Front, r.Fraction, sid) is not { } sec) { if (z == terminalZone) break; continue; }
            var arr = now.AddSeconds(sec + extra);
            arrAt[z] = arr;
            points.Add(new { t = arr, y = (double)z });
            if (z == terminalZone) break;

            // この駅の停車時分: 標準運転時分の値。待避線なら最低の停車時分まで延ばし、決められた発車時刻より前には出ない
            var modelDwell = _model.DwellAt(type, r.Dir, sid);
            var dwell = modelDwell;
            if (r.Stations.FirstOrDefault(s => s.Zone == z && !s.Done) is { } zs && IsLoopTrack(r, zs))
                dwell = Math.Max(modelDwell, Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds));
            var dep = arr.AddSeconds(dwell);
            if (minDepart(z) is { } md2 && md2 > dep) dep = md2;
            depAt[z] = dep;
            extra += (dep - arr).TotalSeconds - modelDwell;
            if (dep > arr) points.Add(new { t = dep, y = (double)z });
        }

        return new PlanResult(points, arrAt, depAt);
    }

    /// <summary>
    /// 駅ごとの出発順序（ダイヤグラム・管理画面の編集用）。その駅をこれから通る列車（上下とも）を、順序の指定があればその順、
    /// なければ発車（通過）の見込みの順に並べる。番線を変えられるか・競合の注意も付ける。
    /// </summary>
    public object OrdersView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var list = new List<object>();
            foreach (var station in _data.ControlStations)
            {
                // 出発順序は上り・下りで別々（片方だけ自動に戻せる）
                var orders = new Dictionary<Dir, List<string>>
                {
                    [Dir.Down] = _central.Order(station.Id, Dir.Down)?.ToList() ?? new List<string>(),
                    [Dir.Up] = _central.Order(station.Id, Dir.Up)?.ToList() ?? new List<string>()
                };
                var here = _runs.Values
                    .Where(r => r.HasPosition)
                    .Select(r => (Run: r, St: r.Stations.FirstOrDefault(s => s.St.Id == station.Id && !s.Done)))
                    .Where(x => x.St != null && !IsCutOff(x.Run, x.St))
                    .ToList();

                // 表示の並び（在線位置の画面の出発・到着の枠）: 実際に進路を取る順。
                // 出発: もう出発進路を取った列車（取った順）→ 進路を取る順序（手で決めた順、ダイヤ運転中はダイヤの順）→ 抑止中は後ろ → 時刻
                //       （ダイヤ運転中はダイヤの発車時刻、そうでなければ見込み）
                // 到着: もう場内を取った列車（取った順）→ 着く見込みの時刻
                var nowSec = NowSec();
                double Eta(TrainRun r, StationRun s, bool dep)
                {
                    double? sec = s.IsStart ? 0 : _model.SecondsTo(RunType(r), r.Dir, r.Front, r.Fraction, station.Id);
                    if (sec != null && dep) sec += DwellSeconds(r, s);
                    return sec is { } v ? nowSec + v : double.MaxValue;
                }
                long Taken(TrainRun r, StationRun s, bool dep) =>
                    r.Requests.Where(q => q.Station == s && q.Departure == dep && q.CancelledUtc == null).Select(q => q.Id).DefaultIfEmpty(long.MaxValue).Min();
                var departRank = new Dictionary<string, int>();
                var arriveRank = new Dictionary<string, int>();
                foreach (var dir in new[] { Dir.Down, Dir.Up })
                {
                    var eff = EffectiveOrder(station.Id, dir)?.ToList() ?? new List<string>();
                    var mine = here.Where(x => x.Run.Dir == dir).ToList();
                    var dep = mine
                        .OrderBy(x => x.St!.Depart == GroupState.Requested ? 0 : 1)
                        .ThenBy(x => x.St!.Depart == GroupState.Requested ? Taken(x.Run, x.St, true) : 0)
                        .ThenBy(x => eff.IndexOf(x.Run.Train) is var i && i >= 0 ? i : int.MaxValue)
                        .ThenBy(x => (_central.Hold(x.Run.Train, station.Id) & HoldKind.Depart) != 0)
                        .ThenBy(x => DiaMode && DiaSec(x.Run, station.Id, depart: true) is { } d ? d : Eta(x.Run, x.St!, true))
                        .ToList();
                    for (var i = 0; i < dep.Count; i++) departRank[dep[i].Run.Train] = i;
                    var arr = mine
                        .OrderBy(x => x.St!.Arrive == GroupState.Requested ? 0 : 1)
                        .ThenBy(x => x.St!.Arrive == GroupState.Requested ? Taken(x.Run, x.St, false) : 0)
                        .ThenBy(x => Eta(x.Run, x.St!, false))
                        .ToList();
                    for (var i = 0; i < arr.Count; i++) arriveRank[arr[i].Run.Train] = i;
                }

                var trains = here
                    .Select(x =>
                    {
                        double? sec = x.St!.IsStart ? 0 : _model.SecondsTo(RunType(x.Run), x.Run.Dir, x.Run.Front, x.Run.Fraction, station.Id);
                        var arriveSec = sec;
                        if (sec != null) sec += DwellSeconds(x.Run, x.St); // 待避線なら最低の停車時分
                        var order = orders[x.Run.Dir];
                        var entered = x.Run.Requests.Any(q => q.Station == x.St && !q.Released && q.CancelledUtc == null
                                                              && q.Def.Circuits.Any(x.Run.Player.TrackCircuits.Contains));
                        return new
                        {
                            train = x.Run.Train,
                            type = x.Run.Type,
                            direction = x.Run.Dir.Label(),
                            stop = x.St.StopType,
                            eta = sec is { } s ? now.AddSeconds(s) : (DateTime?)null,
                            index = order.IndexOf(x.Run.Train),
                            track = x.St.Track,
                            trackSource = x.St.TrackSource,
                            options = x.St.Options,
                            changeable = x.St.Options.Count > 1 && !entered && !(x.St.IsStart && x.St.ShuntFrom != null && x.St.Shunt == GroupState.Requested),
                            warning = order.Count > 0 ? OrderWarning(x.Run, x.St, order) : "",
                            // 指定した発車時刻（ダイヤグラムのスジずらし・出発順序の手打ち）。この30秒前まで出発を開通させない
                            departAt = _central.OpenAt(x.Run.Train, x.St.St.Id),
                            departable = !x.St.Terminal && !IsTurnbackTrack(x.Run, x.St) && x.St.Depart != GroupState.Requested,
                            // その駅で運転を終える列車（終着・行き止まりの番線）。出発順序には出さず、到着順序にだけ出す
                            ends = x.St.Terminal || IsTurnbackTrack(x.Run, x.St),
                            // もうこの駅に着いている（始発・停車中・通過中）。到着順序には出さない
                            arrived = x.St.IsStart || (x.Run.Front - x.St.Zone) * x.Run.Dir.Step() >= 0,
                            // この駅に着く（通過する）見込みの時刻。到着順序はこの順
                            arrive = arriveSec is { } a ? now.AddSeconds(a) : (DateTime?)null,
                            // 出発抑止中（順序に入っていても、ほかの列車を先に出す）
                            held = (_central.Hold(x.Run.Train, station.Id) & HoldKind.Depart) != 0,
                            // 在線位置の画面の並び（出発・到着）。小さいほど先
                            departRank = departRank.GetValueOrDefault(x.Run.Train, int.MaxValue),
                            arriveRank = arriveRank.GetValueOrDefault(x.Run.Train, int.MaxValue),
                            status = Status(x.Run, x.St)
                        };
                    })
                    .OrderBy(x => x.index < 0 ? int.MaxValue : x.index)
                    .ThenBy(x => x.held)
                    .ThenBy(x => x.eta ?? DateTime.MaxValue)
                    .ToList();
                list.Add(new
                {
                    station = station.Id,
                    name = station.Name,
                    loopTrack = _data.PassingStations.TryGetValue(station.Id, out var ps) ? $"下り{ps.DownTrack}・上り{ps.UpTrack}番線" : null,
                    // 指定中: 順序の列車が1本でもいま走っている（退出した列車だけの古い指定は数えない）。上り・下り別にも出す
                    fixedOrder = trains.Any(t => t.index >= 0),
                    fixedUp = trains.Any(t => t.index >= 0 && t.direction == "上り"),
                    fixedDown = trains.Any(t => t.index >= 0 && t.direction == "下り"),
                    trains
                });
            }

            return list;
        }
    }

    /// <summary>
    /// 単線区間（駅間）ごとの走行順（編集用）。これからその区間を通る列車（上下とも）を、走行順の指定があればその順、
    /// なければ区間に入る見込みの順に並べる。
    /// </summary>
    public object SectionsView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var list = new List<object>();
            foreach (var (a, b) in _data.SingleTrackSections)
            {
                var order = _central.SectionOrder(a, b)?.ToList() ?? new List<string>();
                var trains = _runs.Values.Where(r => r.HasPosition && !SectionCleared(r, a, b))
                    .Select(r =>
                    {
                        var entry = r.Dir == Dir.Down
                            ? (_data.Line.ZoneOfStation(a) < _data.Line.ZoneOfStation(b) ? a : b)
                            : (_data.Line.ZoneOfStation(a) < _data.Line.ZoneOfStation(b) ? b : a);
                        var es = r.Stations.FirstOrDefault(s => s.St.Id == entry);
                        double? sec = es == null || es.Done ? 0 : es.IsStart ? 0 : _model.SecondsTo(RunType(r), r.Dir, r.Front, r.Fraction, entry);
                        if (sec != null && es is { Done: false }) sec += DwellSeconds(r, es);
                        return new
                        {
                            train = r.Train,
                            type = r.Type,
                            direction = r.Dir.Label(),
                            from = _data.Line.StationName(entry),
                            eta = sec is { } s ? now.AddSeconds(s) : (DateTime?)null,
                            index = order.IndexOf(r.Train),
                            status = es is { Done: false } ? Status(r, es) : "区間内"
                        };
                    })
                    .OrderBy(x => x.index < 0 ? int.MaxValue : x.index)
                    .ThenBy(x => x.eta ?? DateTime.MaxValue)
                    .ToList();
                list.Add(new
                {
                    a, b,
                    name = $"{_data.Line.StationName(a)}〜{_data.Line.StationName(b)}",
                    fixedOrder = trains.Any(t => t.index >= 0),
                    trains
                });
            }

            return list;
        }
    }

    /// <summary>
    /// 在線位置の画面（tid.html）用。列車が踏んでいる軌道回路と、開通させている進路のうち、まだ使う軌道回路を返す。
    /// </summary>
    public object TidView()
    {
        lock (_lock)
        {
            var runs = _runs.Values.Where(r => r.HasPosition).ToList();
            return new
            {
                trains = runs.Select(r => new
                {
                    train = r.Train,
                    type = r.Type,
                    direction = r.Dir.Label(),
                    circuits = r.Player.TrackCircuits.ToList(),
                    // 少し前（1分以内）に踏んでいた回路。在線をどの枝で塗るかの手がかり（通ってきた側の枝を選ぶ）
                    recent = r.SeenCircuits.Keys.Where(c => !r.Player.TrackCircuits.Contains(c)).ToList(),
                    // 遅れ（分）。1分以上のときだけ
                    delay = r.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : (int?)null
                }).ToList(),
                routes = runs.SelectMany(r => r.Requests
                    .Where(q => !q.Released && q.CancelledUtc == null)
                    .Select(q => new
                    {
                        train = r.Train,
                        station = q.Station.St.Id,
                        route = q.Def.Name,
                        // 開通を確認できたか: ゲームの進路の一覧に出た、または要求のあとでゲームが受け付けた返事をした
                        // （OK、または「同一進路への進入」＝もうその進路が取られている）。まだなら要求中
                        open = q.Seen || Acknowledged(r, q),
                        reply = r.Results.GetValueOrDefault($"{q.Def.Interlock}/{q.Def.Name}") ?? "",
                        circuits = LiveCircuits(r, q).ToList(),
                        // 接近鎖錠の区間（出発なら出る番線の回路）。在線位置の画面が、進路の線をどちらの枝へ引くかの手がかりにする
                        approach = q.Def.Approach
                    })).ToList(),
                // 単線区間の方向てこ: direction は "下り" / "上り"（まだ一度も使われていなければ null）。locked は、いま使われているか
                levers = _data.SingleTrackSections.Select(s => new
                {
                    a = s.Item1,
                    b = s.Item2,
                    direction = _levers.TryGetValue(s, out var lv) ? lv.Dir.Label() : null,
                    locked = _levers.TryGetValue(s, out var lk) && lk.Locked
                }).ToList()
            };
        }
    }

    /// <summary>
    /// 列車の時刻表（在線位置の画面で、列番を押したときに出す）。全駅（進路を扱わない駅も含む）の着・発（通過）時刻。
    /// ダイヤにある列車はダイヤの時刻。ない列車は、ロープレ時刻（なければ今の時刻）に始発駅を発車したとして、標準運転時分で見込む。
    /// </summary>
    public object? Timetable(string train, TimeSpan? clock)
    {
        lock (_lock)
        {
            var key = RailData.NormalizeTrain(train);
            var run = _runs.Values.FirstOrDefault(r => r.Train == key);
            var dia = run?.Dia ?? _data.DiaOf(key);
            bool Control(string? id) => id != null && _data.Stations.TryGetValue(id, out var s) && s.RouteControl;

            // 列車別設定の臨時停車・臨時通過・打ち切り（進路を扱う駅だけ）。stop は「臨停」「臨通」「打切」になる
            string? Override(string? id) => id != null ? _central.StopOverride(key, id) : null;

            if (dia != null && dia.Stops.Count > 0)
            {
                var rows = new List<(string Name, string? Sid, string Stop, string? Arr, string? Dep)>();
                foreach (var s in dia.Stops)
                {
                    rows.Add(Override(s.StationId) switch
                    {
                        "停車" when s.Stop == "通" => (s.Name, s.StationId, "臨停", s.Arr ?? s.Dep, s.Dep ?? s.Arr),
                        "通過" when s.Stop != "通" => (s.Name, s.StationId, "臨通", null, s.Dep ?? s.Arr),
                        "打ち切り" => (s.Name, s.StationId, "打切", s.Arr ?? s.Dep, null),
                        _ => (s.Name, s.StationId, s.Stop, s.Arr, s.Dep)
                    });
                    if (Override(s.StationId) == "打ち切り") break; // 打ち切った先の駅は出さない
                }

                // 計画（ダイヤの時刻）と実（着いた・出た駅は実際の時刻、これからの駅は計画に今の遅れを足した見込み）。
                // 着いた・出た駅は、計画の欄を「-」にする
                var stepD = run?.Dir.Step() ?? 0;
                var lastPassed = -1; // ここまでの駅はもう出た（通過した）
                if (run is { HasPosition: true })
                    for (var i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].Sid is not { } sid) continue;
                        var z = _data.Line.ZoneOfStation(sid);
                        if (run.ActualDep.ContainsKey(sid) || z >= 0 && (run.Front - z) * stepD > 0 && (run.Rear - z) * stepD > 0) lastPassed = i;
                    }

                var delay = Math.Max(0, run?.Delay ?? 0);
                string? Shift(string? t) => run != null && ParseDiaTime(t) is { } v ? FormatDiaTime(v + delay) : null;
                var stops = rows.Select((r, i) =>
                {
                    var arrDone = i <= lastPassed || r.Sid != null && run != null && run.ActualArr.ContainsKey(r.Sid);
                    var depDone = i <= lastPassed || r.Sid != null && run != null && run.ActualDep.ContainsKey(r.Sid);
                    var actArr = r.Sid != null && run != null && run.ActualArr.TryGetValue(r.Sid, out var aa) ? FormatDiaTime(aa) : Shift(r.Arr);
                    var actDep = r.Sid != null && run != null && run.ActualDep.TryGetValue(r.Sid, out var ad) ? FormatDiaTime(ad) : Shift(r.Dep);
                    return (object)new
                    {
                        name = r.Name, control = Control(r.Sid), stop = r.Stop,
                        planArr = r.Arr == null ? null : arrDone ? "-" : r.Arr,
                        actArr = r.Arr == null ? null : actArr,
                        planDep = r.Dep == null ? null : depDone ? "-" : r.Dep,
                        actDep = r.Dep == null ? null : actDep
                    };
                }).ToList();

                return new { train = key, label = dia.TypeLabel, source = "ダイヤ", delay = run?.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : (int?)null, stops };
            }

            if (run == null || !run.HasPosition) return null;

            // ダイヤにない列車: 始発駅（ゲームの停車駅の最初、なければ今いる所）から、終着（ゲームの停車駅の最後、なければ路線の端）まで
            var type = RunType(run);
            var step = run.Dir.Step();
            var ids = _data.Line.StationIds;
            int ZoneOfName(string name) => _data.Line.StationIdByName(name) is { } id ? _data.Line.ZoneOfStation(id) : -1;
            var stopZones = run.Player.Stops.Select(s => (Zone: ZoneOfName(s.Name), s.StopType)).Where(x => x.Zone >= 0).ToList();
            var startZone = stopZones.Count > 0 ? stopZones[0].Zone : run.Front;
            var endZone = run.Dia?.TerminalId is { } tid ? _data.Line.ZoneOfStation(tid)
                : stopZones.Count > 0 ? stopZones[^1].Zone
                : step > 0 ? ids.Max(_data.Line.ZoneOfStation) : ids.Min(_data.Line.ZoneOfStation);
            // 打ち切りの駅があれば、そこを終着にする
            foreach (var id in ids.OrderBy(i => _data.Line.ZoneOfStation(i) * step))
            {
                var z0 = _data.Line.ZoneOfStation(id);
                if ((z0 - startZone) * step <= 0 || (z0 - endZone) * step > 0) continue;
                if (Override(id) == "打ち切り") { endZone = z0; break; }
            }

            var start = clock ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Japan).TimeOfDay;
            string Fmt(double sec) => TimeSpan.FromTicks((start + TimeSpan.FromSeconds(sec)).Ticks % TimeSpan.TicksPerDay).ToString(@"hh\:mm\:ss");

            var list = new List<object>();
            foreach (var id in ids.OrderBy(i => _data.Line.ZoneOfStation(i) * step))
            {
                var z = _data.Line.ZoneOfStation(id);
                if ((z - startZone) * step < 0 || (z - endZone) * step > 0) continue;
                var stopInfo = stopZones.FirstOrDefault(x => x.Zone == z);
                var stops = stopInfo.StopType is "StopForPassenger" or "StopForOperation" || z == startZone || z == endZone;
                var mark = stopInfo.StopType == "StopForOperation" ? "運停" : stops ? "停" : "通";
                switch (Override(id))
                {
                    case "停車" when !stops: (stops, mark) = (true, "臨停"); break;
                    case "通過" when stops && z != startZone && z != endZone: (stops, mark) = (false, "臨通"); break;
                    case "打ち切り": mark = "打切"; break;
                }
                if (z == startZone)
                {
                    list.Add(new { name = _data.Line.StationName(id), control = Control(id), stop = mark, planArr = (string?)null, actArr = (string?)null, planDep = (string?)Fmt(0), actDep = (string?)null });
                    continue;
                }

                var sec = _model.SecondsTo(type, run.Dir, startZone, 0, id);
                if (sec == null) continue;
                var dwell = stops && z != endZone ? Math.Max(_model.DwellAt(type, run.Dir, id), _data.DefaultDwellSeconds) : 0;
                list.Add(new
                {
                    name = _data.Line.StationName(id), control = Control(id), stop = mark,
                    planArr = stops ? Fmt(sec.Value) : null,
                    actArr = (string?)null,
                    planDep = z == endZone ? null : Fmt(sec.Value + dwell),
                    actDep = (string?)null
                });
            }

            return new { train = key, label = run.Type, source = clock != null ? "標準運転時分（ロープレ時刻から）" : "標準運転時分（今の時刻から）", stops = list };
        }
    }

    /// <summary>要求のあとで、ゲームがその進路を受け付けた返事をしたか（OK・同一進路への進入）。表示のためだけに使う。</summary>
    private static bool Acknowledged(TrainRun run, RouteReq q)
    {
        var key = $"{q.Def.Interlock}/{q.Def.Name}";
        return run.ResultAt.TryGetValue(key, out var at) && at >= q.RequestedUtc
               && run.Results.TryGetValue(key, out var reply) && (reply == "OK" || reply.Contains("同一進路"));
    }

    public object AdminView()
    {
        lock (_lock)
        {
            return _runs.Values.OrderBy(r => r.Train).Select(r => new
            {
                train = r.Train,
                type = r.Type,
                direction = r.Dir.Label(),
                dia = r.Dia != null,
                position = r.HasPosition ? _data.Line.DescribeZones(r.Player.Zones) : "不明",
                // ゲームに今ある進路（手動進路の取消用）
                gameRoutes = r.Player.GameRoutes.Select(g => new { interlock = g.Item1, route = g.Item2 }),
                stations = r.Stations.Where(s => !s.Done).Select(s => new
                {
                    stationId = s.St.Id,
                    name = s.St.Name,
                    stopType = s.StopType,
                    stopOverride = _central.StopOverride(r.Train, s.St.Id),
                    track = s.Track,
                    options = s.Options,
                    source = s.TrackSource,
                    locked = s.Locked,
                    held = _central.IsHeld(r.Train, s.St.Id),
                    holdArrive = (_central.Hold(r.Train, s.St.Id) & HoldKind.Arrive) != 0,
                    holdDepart = (_central.Hold(r.Train, s.St.Id) & HoldKind.Depart) != 0,
                    manual = ManualOptions(r, s).Select(o => new { action = o.Action, label = o.Label }),
                    forced = _central.IsForced(r.Train, s.St.Id),
                    status = Status(r, s)
                })
            }).ToList();
        }
    }
}
