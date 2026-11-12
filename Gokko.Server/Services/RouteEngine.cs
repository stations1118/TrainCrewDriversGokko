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

    private readonly RailData _data;
    private readonly RunTimeModel _model;
    private readonly CentralControl _central;
    private readonly PlayerRegistry _players;
    private readonly IncidentManager _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, TrainRun> _runs = new();
    private long _seq;

    public RouteEngine(RailData data, RunTimeModel model, CentralControl central, PlayerRegistry players, IncidentManager log)
    {
        _data = data;
        _model = model;
        _central = central;
        _players = players;
        _log = log;
    }

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
        public required string ConnId { get; init; }
        public required string Train { get; init; }
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
        public string? Message { get; set; }

        /// <summary>走った実績（ダイヤグラム用）。先頭のゾーンが変わるたびに記録し、30分より古いものは捨てる。</summary>
        public List<(DateTime Utc, double Y)> History { get; } = new();
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

    /// <summary>クライアントから届いた SetRoute の結果（"新野崎連動装置_10LD is OK" など）。</summary>
    public void ReportResult(string connId, string message)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return;
            var m = Regex.Match(message ?? "", @"^(.+?)_(\S+) is (.+)$");
            if (m.Success) run.Results[$"{m.Groups[1].Value}/{m.Groups[2].Value}"] = m.Groups[3].Value;
        }
    }

    // =========================================================================================
    // 毎周期の処理
    // =========================================================================================

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var snapshot = _players.Snapshot()
            .Where(p => p.Mode == ShareMode.Shared && p.InGame && p.TrainCrewConnected && p.TrainNumber.Length > 0)
            .ToList();

        lock (_lock)
        {
            // 列車の入れ替わり
            foreach (var id in _runs.Keys.Where(id => snapshot.All(p => p.ConnectionId != id)).ToList()) _runs.Remove(id);
            foreach (var p in snapshot)
            {
                if (_runs.TryGetValue(p.ConnectionId, out var existing) && existing.Train == p.TrainNumber)
                {
                    existing.Player = p;
                    continue;
                }

                if (TrainInfo(p.TrainNumber) is not { } info) continue;
                var dia = _data.DiaOf(p.TrainNumber);
                _runs[p.ConnectionId] = new TrainRun
                {
                    ConnId = p.ConnectionId, Train = p.TrainNumber, Type = dia?.Type ?? info.Type, Dir = info.Dir, Player = p,
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
                // 駅を抜けた列車は、その駅の出発順序と、手前の単線区間の走行順から外す
                for (var i = 0; i < run.Stations.Count; i++)
                {
                    if (!run.Stations[i].Done) continue;
                    _central.RemoveFromOrder(run.Stations[i].St.Id, run.Train);
                    if (i > 0) _central.RemoveFromSectionOrder(run.Stations[i - 1].St.Id, run.Stations[i].St.Id, run.Train);
                }
            }

            // 進路の要求（手前の駅を通過した順）
            var pending = _runs.Values
                .SelectMany(r => r.Stations.Where(s => !s.Done).Select(s => (Run: r, St: s)))
                .OrderBy(x => x.St.IsStart ? DateTime.MinValue : x.St.TriggeredUtc)
                .ToList();
            foreach (var (run, st) in pending) Process(run, st, occupied, now);

            BuildOutputs(now);
        }
    }

    private static (string Type, Dir Dir)? TrainInfo(string train)
    {
        var digits = Regex.Match(train, @"(\d+)(?!.*\d)");
        if (!digits.Success) return null;
        var dir = int.Parse(digits.Groups[1].Value[^1..]) % 2 == 1 ? Dir.Down : Dir.Up;
        string type;
        if (train.StartsWith("回")) type = "回送";
        else
            type = char.ToUpperInvariant(train[^1]) switch
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

            run.Stations.Add(sr);
        }
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

            // 手前の駅で行き止まりの番線に入る（打ち切り）なら、この駅は扱わない。開通済みの進路があれば取り消す
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
            ApplyHold(run, st, now);

            // 駅に着いた時刻（先頭が構内に入った。始発は認識した時刻）
            if (st.HereUtc == null && (st.IsStart || (run.Front - st.Zone) * step >= 0)) st.HereUtc = now;

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

            // 2つ手前の駅（すべての駅）に先頭が着いたか（構内に入ったか）。始発のすぐ先などで、もう過ぎていればすぐ
            if (!st.Triggered && !st.IsStart)
            {
                var triggerZone = st.Zone - 2 * TriggerAhead(run, st) * step;
                if ((run.Front - triggerZone) * step >= 0)
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
            AvoidTakenTrack(run, st, options);
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
        if (_central.Order(st.St.Id)?.Contains(run.Train) == true) return;
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
        if (!_data.PassingStations.TryGetValue(st.St.Id, out var ps) || st.Terminal) return null;
        if (_central.Order(st.St.Id) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        foreach (var name in order.Take(idx))
            if (RunByTrain(name) is { HasPosition: true } p && p.Dir == run.Dir && !HasPassed(p, st)
                && (p.Front - run.Front) * run.Dir.Step() < 0)
                return ps.TrackFor(run.Dir);
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
        if (_central.Order(st.St.Id) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        var mine = Circuits(path).ToHashSet();
        var waiting = new List<string>();
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || HasPassed(p, st)) continue;
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
    private string? LoopDwellHold(TrainRun run, StationRun st, DateTime now)
    {
        if (!IsLoopTrack(run, st)) return null;
        if (st.LoopArrivedUtc is not { } at) return $"待避線で停車（着いてから {_data.MinLoopDwellSeconds}秒）";
        var left = _data.MinLoopDwellSeconds - (now - at).TotalSeconds;
        return left > 0 ? $"待避線で停車（あと {Math.Ceiling(left):0}秒）" : null;
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
            if (!s.Done && IsTurnbackTrack(run, s)) return true;
        }
        return false;
    }

    private void Process(TrainRun run, StationRun st, Dictionary<string, TrainRun> occupied, DateTime now)
    {
        if (IsCutOff(run, st)) return;
        if (st.Done || !st.Locked && !st.IsStart && !_central.IsForced(run.Train, st.St.Id)) return;

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
                : TimeWait(run, st, now) ?? Conflict(run, st, path, occupied, departure: false);
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
                    : timeWait ?? SingleEntryHold(run, st) ?? (conflict = Conflict(run, st, st.ArrivePath, occupied, departure: false));
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
            else if (needArrive && arriveWait != null) wait = $"場内待ち（{arriveWait}）";
            else if (needArrive && LeadsToSingleTrack(run, st)) wait = "単線区間のため、場内の開通後に判断";
            else if (OrderHold(run, st, st.DepartPath) is { } ow) wait = ow;
            else wait = PassingHold(run, st, now)
                        ?? DepartStopHold(run, st, needArrive)
                        ?? LoopDwellHold(run, st, now)
                        ?? SingleTrackHold(run, st, now)
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
        if (!st.IsStart && !here && !approaching) return "交換の判断待ち（駅に近づくまで）";

        var nextZone = _data.Line.ZoneOfStation(next);
        var (lo, hi) = (Math.Min(st.Zone, nextZone), Math.Max(st.Zone, nextZone));

        // ---- 必ず待つ: 区間に対向列車がいる、相手の駅で対向列車の出発進路が出ている ----
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition && o.Dir != run.Dir))
        {
            if (other.Player.Zones.Any(z => z > lo && z < hi)) return $"単線区間に対向列車（{other.Train}）";
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && r.Departure && r.Station.St.Id == next))
                return $"対向列車（{other.Train}）が{nextName}を出発";
        }

        // ---- 走行順（集中扱い）: 区間ごとに決めた順で流す ----
        var order = _central.SectionOrder(st.St.Id, next);
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
            if (_data.PassingStations.ContainsKey(st.St.Id) && os.Track != null && os.Track != st.Track) continue; // 待避駅で別の番線
            if (!departure && os.Track != null && st.Track != null && os.Track != st.Track) continue; // 別の番線に入るだけならよい
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
                    TrainNumber = run.Train,
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
                Status = Status(run, s)
            }).ToList();

            var start = run.Stations.FirstOrDefault(s => s.IsStart && !s.Done);
            var shunting = start?.ShuntFrom != null;
            var canDepart = start != null && !start.Terminal && (shunting ? !start.ShuntRequested : !start.DepartureRequested);
            var label = shunting ? "入換要求" : "出発要求";
            var message = run.HasPosition ? null : "位置が分かりません（運転画面で列車を動かすと分かります）";
            _outputs[run.ConnId] = new Output(routes, deletes, plan, canDepart, label, message);
        }
    }

    private string Status(TrainRun run, StationRun s)
    {
        string Group(GroupState g, string wait, List<RouteDef> path, string label)
        {
            if (path.Count == 0) return "";
            if (g == GroupState.Waiting) return $"{label}待ち: {wait}";
            if (g != GroupState.Requested) return "";
            var reqs = run.Requests.Where(r => r.Station == s && path.Contains(r.Def)).ToList();
            if (reqs.Count > 0 && reqs.All(r => r.Seen)) return $"{label}開通";
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
        if (s.IsStart && !s.Locked) return s.Terminal ? "終着" : "出発要求待ち";
        var parts = new[]
        {
            Group(s.Depart, s.DepartWait, s.DepartPath, "出発"),
            Group(s.Arrive, s.ArriveWait, s.ArrivePath, s.Track == null ? "通過" : "場内")
        }.Where(x => x.Length > 0).ToList();
        if (s.HoldKept.Length > 0) parts.Add($"{s.HoldKept}抑止は列車が接近中のため取り消さず");
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
            return _runs.Values.Where(r => r.HasPosition && r.Player.Mode == ShareMode.Shared).OrderBy(r => r.Train).Select(r =>
            {
                var step = r.Dir.Step();
                var type = RunType(r);

                // この先の見込み: 今の位置から、終着駅（なければ路線の端）までの各駅
                var terminal = r.Player.Stops.Count > 0 ? _data.Line.StationIdByName(r.Player.Stops[^1].Name) : null;
                var terminalZone = terminal != null ? _data.Line.ZoneOfStation(terminal) : -1;
                // 行き止まりの番線に入る駅があれば、線はその駅で切る
                if (r.Stations.FirstOrDefault(s => !s.Done && IsTurnbackTrack(r, s)) is { } cut) terminalZone = cut.Zone;
                // 標準運転時分の見込みに、待避線での停車の延長（最低の停車時分）を足していく。停車は水平の線で描く
                var forecast = new List<object> { new { t = now, y = PlotY(r) } };
                var arrAt = new Dictionary<int, DateTime>(); // ゾーン → 着く（通過する）予想時刻
                double extra = 0;                            // ここまでに足した延長（秒）
                if (r.Front % 2 == 0 && r.Stations.FirstOrDefault(s => s.Zone == r.Front && !s.Done) is { } curSt && IsLoopTrack(r, curSt))
                {
                    // 待避線にいる: 最低の停車時分の残り（入りきる前なら全部）
                    var left = curSt.LoopArrivedUtc is { } la ? _data.MinLoopDwellSeconds - (now - la).TotalSeconds : _data.MinLoopDwellSeconds;
                    if (left > 0)
                    {
                        extra += left;
                        forecast.Add(new { t = now.AddSeconds(left), y = (double)r.Front });
                    }
                }

                for (var z = (r.Front % 2 == 0 ? r.Front : r.Front + step) + (r.Front % 2 == 0 ? 2 * step : 0);
                     _data.Line.StationAtZone(z) is { } sid; z += 2 * step)
                {
                    if (_model.SecondsTo(type, r.Dir, r.Front, r.Fraction, sid) is not { } sec) { if (z == terminalZone) break; continue; }
                    var arr = now.AddSeconds(sec + extra);
                    arrAt[z] = arr;
                    forecast.Add(new { t = arr, y = (double)z });
                    if (z == terminalZone) break;

                    // この駅の停車時分: 標準運転時分の値。待避線に入るなら最低の停車時分まで延ばし、その分を先の駅に足す
                    var modelDwell = _model.DwellAt(type, r.Dir, sid);
                    var dwell = modelDwell;
                    if (r.Stations.FirstOrDefault(s => s.Zone == z && !s.Done) is { } zs && IsLoopTrack(r, zs))
                        dwell = Math.Max(modelDwell, Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds));
                    extra += dwell - modelDwell;
                    if (dwell > 0) forecast.Add(new { t = arr.AddSeconds(dwell), y = (double)z });
                }

                // 駅の目印（◆）: 進路を扱う駅ごとに、その駅を通過する（停車駅は着く）予想時刻に置く（見込みの線の上）。
                // 開通の時刻（開通済みならその時刻、まだなら2つ手前の駅に着く見込み）は、説明に出す
                var markers = r.Stations.Where(s => !s.Done && !IsCutOff(r, s)).Select(s =>
                {
                    var openAt = UseOpenTime ? _central.OpenAt(r.Train, s.St.Id) : null;
                    var started = s.Arrive == GroupState.Requested || s.Depart == GroupState.Requested || s.Shunt == GroupState.Requested;

                    // 通過・到着の予想。今いる駅（始発・停車中）は今
                    var here = s.IsStart || r.Front == s.Zone;
                    DateTime? time = here ? now : arrAt.TryGetValue(s.Zone, out var at) ? at : null; // 待避線の延長を足した見込み
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
                        draggable = UseOpenTime && !started,
                        held = _central.IsHeld(r.Train, s.St.Id),
                        holdArrive = (_central.Hold(r.Train, s.St.Id) & HoldKind.Arrive) != 0,
                        holdDepart = (_central.Hold(r.Train, s.St.Id) & HoldKind.Depart) != 0,
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
                var order = _central.Order(station.Id)?.ToList() ?? new List<string>();
                var trains = _runs.Values
                    .Where(r => r.HasPosition)
                    .Select(r => (Run: r, St: r.Stations.FirstOrDefault(s => s.St.Id == station.Id && !s.Done)))
                    .Where(x => x.St != null && !IsCutOff(x.Run, x.St))
                    .Select(x =>
                    {
                        double? sec = x.St!.IsStart ? 0 : _model.SecondsTo(RunType(x.Run), x.Run.Dir, x.Run.Front, x.Run.Fraction, station.Id);
                        if (sec != null) sec += DwellSeconds(x.Run, x.St); // 待避線なら最低の停車時分
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
                            status = Status(x.Run, x.St)
                        };
                    })
                    .OrderBy(x => x.index < 0 ? int.MaxValue : x.index)
                    .ThenBy(x => x.eta ?? DateTime.MaxValue)
                    .ToList();
                list.Add(new
                {
                    station = station.Id,
                    name = station.Name,
                    loopTrack = _data.PassingStations.TryGetValue(station.Id, out var ps) ? $"下り{ps.DownTrack}・上り{ps.UpTrack}番線" : null,
                    // 指定中: 順序の列車が1本でもいま走っている（退出した列車だけの古い指定は数えない）
                    fixedOrder = trains.Any(t => t.index >= 0),
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
                    circuits = r.Player.TrackCircuits.ToList()
                }).ToList(),
                routes = runs.SelectMany(r => r.Requests
                    .Where(q => !q.Released && q.CancelledUtc == null)
                    .Select(q => new
                    {
                        train = r.Train,
                        station = q.Station.St.Id,
                        route = q.Def.Name,
                        open = q.Seen, // ゲームで開通を確認できたか（まだなら要求中）
                        circuits = LiveCircuits(r, q).ToList()
                    })).ToList()
            };
        }
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
