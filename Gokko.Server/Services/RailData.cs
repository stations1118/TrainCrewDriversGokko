using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>方向。下り＝館浜方向（ゾーンの番号が増える向き）、上り＝新大路方向。</summary>
public enum Dir
{
    Down,
    Up
}

public static class DirExtensions
{
    public static string Label(this Dir d) => d == Dir.Down ? "下り" : "上り";

    /// <summary>進む向きのゾーンの増分（下り +1、上り -1）。</summary>
    public static int Step(this Dir d) => d == Dir.Down ? 1 : -1;
}

/// <summary>進路1本（操作対象名一覧＋連動表）。</summary>
public sealed record RouteDef(
    string StationId,
    string Interlock,
    string Name,
    string Category,
    string Signal,
    IReadOnlyList<string> Related,
    string From,
    string To,
    IReadOnlyList<string> Circuits,
    string Kind,
    string Indicator,
    IReadOnlyList<string> Approach)
{
    // Approach: 接近鎖錠の区間（連動表の「接近鎖錠」の欄の軌道回路）。列車がここにいる間は、進路を取り消すと
    // 運転士の目の前で信号が変わるので、抑止では取り消さない。書かれていない進路は空

    /// <summary>出発信号機の進路か。出発の進路の LED は、進路表示ではなく列車の種別（列選）を出す。</summary>
    public bool IsDepartureSignal => Kind.Contains("出発");
}

/// <summary>駅・方向ごとの、番線と進路の対応（TrackRoutes.json）。</summary>
public sealed class StationDirRoutes
{
    public Dictionary<int, List<RouteDef>> Arrive { get; } = new();
    public Dictionary<int, List<RouteDef>> Depart { get; } = new();
    public List<RouteDef> Through { get; } = new();

    /// <summary>始発の入換（引上線・検車区などからホームへ）。</summary>
    public List<ShuntDef> Shunts { get; } = new();

    /// <summary>
    /// 終着の列車の入換（ホームから引上線・検車区などへ。TrackRoutes.json の shuntOut）。
    /// 同じ番線に複数あれば、書いた順（最初が、列車の「入換要求」ボタンで使う既定）。
    /// </summary>
    public List<ShuntOutDef> ShuntOuts { get; } = new();

    /// <summary>出発停止の番線（その番線に入る場内進路を開通させている間は、出発を開通できない。TrackRoutes.json の arrive の departStop）。</summary>
    public HashSet<int> DepartStop { get; } = new();

    /// <summary>行き止まり（折り返し）として選べる番線。入れるが、同じ方向へは出られない（TrackRoutes.json の arrive の turnback）。
    /// この番線を選ぶと、列車はこの駅で運転を打ち切る扱いになり、先の駅の進路は扱わない。</summary>
    public HashSet<int> Turnback { get; } = new();

    /// <summary>そのまま同じ方向に通れる番線（着と発の両方がある）。</summary>
    public IEnumerable<int> ThroughTracks => Arrive.Keys.Where(Depart.ContainsKey).OrderBy(t => t);
}

/// <summary>
/// 始発の入換1つ。元は番線（From）か、番線のない線路の軌道回路（FromCircuit）で書く。
/// 同じ元・同じ番線への入換が複数あれば、書いた順に優先する。RequireFree の回路に他の列車がいれば、その入換は使わない
/// （例: 大道寺 Y線→E線→番線 を既定にし、E線がふさがっていれば Y線から直接）。
/// </summary>
public sealed record ShuntDef(string FromLabel, int? From, string? FromCircuit, int To, List<RouteDef> Routes, List<string> RequireFree, string? Via);

/// <summary>
/// 終着の列車の入換1つ（番線 From から、Label の線路へ）。同じ番線・同じ行き先（Label）が複数あれば、まとめて1つの選択肢とし、
/// 書いた順に優先する。RequireFree の回路に他の列車がいれば次の候補（大道寺 番線→E線→Y線 を既定にし、E線がふさがっていれば直接）。
/// </summary>
public sealed record ShuntOutDef(int From, string Label, List<RouteDef> Routes, List<string> RequireFree, string? Via);

public sealed class StationDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Interlock { get; init; }
    public required bool SingleTrackStation { get; init; }
    public required bool RouteControl { get; init; }
    public Dictionary<Dir, StationDirRoutes> Routes { get; } = new() { [Dir.Down] = new(), [Dir.Up] = new() };

    /// <summary>
    /// 番線に入りきってから、出発を開通させるまで待つ秒数（RouteRules.json の departDelaySeconds。方向・番線ごと）。
    /// 例: 大道寺の上り1番線は、入ってから120秒たつまで出発信号を出さない。
    /// </summary>
    public Dictionary<(Dir Dir, int Track), int> DepartDelay { get; } = new();

    /// <summary>番線 → その番線の軌道回路。</summary>
    public Dictionary<int, string> TrackCircuit { get; } = new();

    /// <summary>方向 → 所定の番線の決まり（キー → 番線）。</summary>
    public Dictionary<Dir, Dictionary<string, int>> DefaultTracks { get; } = new();
}

public sealed class SignalDef
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public Dir? Direction { get; init; }
    public bool IsRepeater { get; init; }
    public IReadOnlyList<string> Next { get; init; } = [];
    public IReadOnlyList<string> Protects { get; init; } = [];

    /// <summary>
    /// 単線区間の閉塞信号機が従う方向てこ（区間の両端の駅の方向てこ。例: TH65_14F・TH64_11F ＝ 大道寺〜藤江）。
    /// 方向てこがこの信号機の向き（Direction）でない間は、この信号機は停止のまま。
    /// </summary>
    public IReadOnlyList<string> DirectionRoutes { get; init; } = [];
}

/// <summary>待避する駅と、方向ごとの待避の番線。</summary>
public sealed record PassingStation(string StationId, int DownTrack, int UpTrack)
{
    public int TrackFor(Dir d) => d == Dir.Down ? DownTrack : UpTrack;
}

/// <summary>ダイヤ（Timetable.json）の1列車。進路を作るときの基本（番線・停車・終着・列選）。</summary>
public sealed class DiaTrain
{
    public required string Number { get; init; }

    /// <summary>種別（特急・急行・普通・回送 など。試運転は回送として扱う）。</summary>
    public required string Type { get; init; }

    /// <summary>種別表記（C特1・A特・急行 など）。出発信号機の列選に出す。</summary>
    public required string TypeLabel { get; init; }

    /// <summary>進路を扱う駅で終わるなら、その駅（江原検 → 江ノ原信号場）。路線の外で終わるなら null。</summary>
    public string? TerminalId { get; set; }

    /// <summary>
    /// 途中駅で列番が変わる列車の、次の列番と、変わる駅（例: 1651 は大道寺から 回1651）。なければ null。
    /// </summary>
    public (string Number, string StationId)? Next { get; set; }

    /// <summary>運行番号（列番の付け方から求めたもの。<see cref="Gokko.Common.Unyo"/>）。読めなければ null。</summary>
    public int? Unyo { get; set; }

    /// <summary>終点のあと（スタフの「折返」「引継」「滞泊」）。書かれていなければ null。</summary>
    public DiaTurn? Turn { get; set; }

    /// <summary>時刻表（路線の外の駅も含む、ダイヤに書かれた全駅）。時刻は "HH:mm:ss"、書かれていなければ null。</summary>
    public List<DiaStop> Stops { get; } = new();

    /// <summary>駅ID → 停車するか・番線（書かれていなければ null）。</summary>
    public Dictionary<string, (string StopType, int? Track)> Stations { get; } = new();
}

/// <summary>
/// 終点のあと。Kind は 折返（Number の列車として折り返す。Time はその発車・入換の時刻、How は「発」か「入換」）、
/// 引継（同じ列番のまま線区の外へ）、滞泊（その駅で夜を越す）。
/// </summary>
public sealed record DiaTurn(string Kind, string? Number, string? Time, string? How);

/// <summary>時刻表の1駅（StationId は路線の外の駅なら null。Stop は 停・通・運停）。</summary>
public sealed record DiaStop(string Name, string? StationId, string Stop, string? Arr, string? Dep);

/// <summary>標準運転時分の1区間。</summary>
public sealed record RunTimeRow(string Type, Dir Dir, string From, string To, bool FromStops, bool ToStops, int Seconds);

/// <summary>
/// 在線共有（集中モード）で使う、路線・進路・信号のデータ一式（Data フォルダ）。
/// Stations.json / TrackRoutes.json / RouteRules.json / Interlockings.json / Signals.json / SignalTypes.json / RunTimes.json / Timetable.json
/// </summary>
public sealed class RailData
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public required LineMap Line { get; init; }
    public Dictionary<string, StationDef> Stations { get; } = new();
    public Dictionary<(string Interlock, string Route), RouteDef> Routes { get; } = new();
    public Dictionary<string, SignalDef> Signals { get; } = new();

    /// <summary>灯式 → 次の現示 → 自分の現示。</summary>
    public Dictionary<string, Dictionary<string, string>> SignalTypes { get; } = new();

    public HashSet<(string, string)> SingleTrackSections { get; } = new();
    public Dictionary<string, PassingStation> PassingStations { get; } = new();

    /// <summary>種別 → 強さ（大きいほど強い）。</summary>
    public Dictionary<string, int> TypeRank { get; } = new();

    public int MaxPassingWaits { get; set; } = 3;

    public int RankOf(string type) => TypeRank.TryGetValue(type, out var r) ? r : 0;
    public List<RunTimeRow> RunTimes { get; } = new();
    public int PassingMarginSeconds { get; set; } = 120;
    public int PassingToleranceSeconds { get; set; } = 60;
    public int DefaultDwellSeconds { get; private set; } = 20;

    /// <summary>待避線に入ったときの最低の停車時分（秒）。</summary>
    public int MinLoopDwellSeconds { get; private set; } = 90;
    public int AfterStopSeconds { get; private set; } = 0;

    /// <summary>進路を信号機の位置から取り始めるか（ApproachTrigger）と、限界点からの余裕（閉塞の数）。切ってあれば「2つ手前の駅」で取る。</summary>
    public bool ApproachTriggerEnabled { get; set; }
    public int ApproachTriggerMargin { get; set; } = 1;

    /// <summary>ダイヤ運転中、出発まで開けてから場内を開ける駅（浜園など）。RouteRules.json の departBeforeArrive。</summary>
    public HashSet<string> DepartBeforeArrive { get; } = new();

    /// <summary>進路を扱う駅（路線の並び順）。</summary>
    public IEnumerable<StationDef> ControlStations =>
        Line.StationIds.Where(id => Stations.TryGetValue(id, out var s) && s.RouteControl).Select(id => Stations[id]);

    public bool IsSingleTrack(string a, string b) => SingleTrackSections.Contains((a, b)) || SingleTrackSections.Contains((b, a));

    /// <summary>単線区間に接する駅か（行き違いのために、空いている番線へ入れる駅）。</summary>
    public bool TouchesSingleTrack(string station) => SingleTrackSections.Any(p => p.Item1 == station || p.Item2 == station);

    /// <summary>ダイヤ。列車番号（正規化したもの）→ 列車。</summary>
    public Dictionary<string, DiaTrain> Dia { get; } = new();

    public static string NormalizeTrain(string? s) => (s ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();

    public DiaTrain? DiaOf(string train) => Dia.GetValueOrDefault(NormalizeTrain(train));

    public static RailData Load(string dir, LineMap line)
    {
        var data = new RailData { Line = line };
        JsonDocument Doc(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name), Encoding.UTF8), JsonOptions);
        string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        List<string> List(JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();

        // ---- 進路（Interlockings.json） ----
        using (var doc = Doc("Interlockings.json"))
        {
            foreach (var il in doc.RootElement.GetProperty("interlockings").EnumerateArray())
            {
                var sid = Str(il, "stationId");
                var name = Str(il, "interlock");
                foreach (var r in il.GetProperty("routes").EnumerateArray())
                {
                    var def = new RouteDef(sid, name, Str(r, "name"), Str(r, "category"), Str(r, "signal"),
                        List(r, "related"), Str(r, "from"), Str(r, "to"), List(r, "circuits"), Str(r, "kind"), Str(r, "indicator"), List(r, "approach"));
                    data.Routes[(name, def.Name)] = def;
                }
            }
        }

        // ---- 駅（Stations.json） ----
        using (var doc = Doc("Stations.json"))
        {
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var st = new StationDef
                {
                    Id = Str(s, "id"), Name = Str(s, "name"), Interlock = Str(s, "interlock"),
                    SingleTrackStation = Str(s, "track") == "単線",
                    RouteControl = s.TryGetProperty("routeControl", out var rc) && rc.GetBoolean()
                };
                data.Stations[st.Id] = st;
                // 番線の軌道回路: 「N番線へ」の進路の最後の回路のうち、いちばん多く出てくるもの
                // （新野崎の 10LC は 34T まで、4L が 4LT まで。どちらも「3番線へ」なので、多い方の 4LT を番線の回路とする）
                var lastByTrack = new Dictionary<int, List<string>>();
                foreach (var r in data.Routes.Values.Where(r => r.Interlock == st.Interlock && r.Circuits.Count > 0))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(r.To, @"^(\d+)番線$");
                    if (!m.Success) continue;
                    var t = int.Parse(m.Groups[1].Value);
                    if (!lastByTrack.TryGetValue(t, out var list)) lastByTrack[t] = list = new List<string>();
                    list.Add(r.Circuits[^1]);
                }

                foreach (var (t, list) in lastByTrack)
                    st.TrackCircuit[t] = list.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            }
        }

        // ---- 番線の表（TrackRoutes.json） ----
        using (var doc = Doc("TrackRoutes.json"))
        {
            foreach (var s in doc.RootElement.GetProperty("stations").EnumerateArray())
            {
                if (!data.Stations.TryGetValue(Str(s, "id"), out var st))
                    throw new InvalidDataException($"TrackRoutes.json: Stations.json にない駅 {Str(s, "id")}");
                foreach (var (key, d) in new[] { ("down", Dir.Down), ("up", Dir.Up) })
                {
                    var e = s.GetProperty(key);
                    var target = st.Routes[d];
                    // 進路の並び。操作対象名一覧の関連進路（第二場内・入換の続きなど）が書かれていなければ、主の進路のすぐあとに足す
                    // （例: 新野崎 25LC → 21L、25LD → 22L。SetRoute では関連進路は自動で設定されないため）
                    List<RouteDef> Resolve(IEnumerable<string> names)
                    {
                        var list = new List<RouteDef>();
                        foreach (var n in names)
                        {
                            if (!data.Routes.TryGetValue((st.Interlock, n), out var r))
                                throw new InvalidDataException($"TrackRoutes.json: {st.Name} の進路 {n} が Interlockings.json にない");
                            if (!list.Contains(r)) list.Add(r);
                        }

                        for (var i = 0; i < list.Count; i++)
                        foreach (var rel in list[i].Related)
                            if (data.Routes.TryGetValue((st.Interlock, rel), out var rr) && !list.Contains(rr))
                                list.Insert(i + 1, rr);
                        return list;
                    }
                    target.Through.AddRange(Resolve(List(e, "through")));
                    if (e.TryGetProperty("shunt", out var shunts))
                        foreach (var sh in shunts.EnumerateArray())
                        {
                            int? from = sh.TryGetProperty("from", out var f) ? f.GetInt32() : null;
                            var fromCircuit = Str(sh, "fromCircuit") is { Length: > 0 } fc ? fc : null;
                            var label = Str(sh, "fromLabel") is { Length: > 0 } fl ? fl : from != null ? $"{from}番線" : fromCircuit ?? "?";
                            var via = Str(sh, "via") is { Length: > 0 } v ? v : null;
                            target.Shunts.Add(new ShuntDef(label, from, fromCircuit, sh.GetProperty("to").GetInt32(), Resolve(List(sh, "routes")),
                                List(sh, "requireFree"), via));
                        }
                    if (e.TryGetProperty("shuntOut", out var outs))
                        foreach (var so in outs.EnumerateArray())
                            target.ShuntOuts.Add(new ShuntOutDef(so.GetProperty("from").GetInt32(), Str(so, "label"), Resolve(List(so, "routes")),
                                List(so, "requireFree"), Str(so, "via") is { Length: > 0 } sov ? sov : null));
                    foreach (var (prop, map) in new[] { ("arrive", target.Arrive), ("depart", target.Depart) })
                    {
                        if (!e.TryGetProperty(prop, out var arr)) continue;
                        foreach (var t in arr.EnumerateArray())
                        {
                            var track = t.GetProperty("track").GetInt32();
                            map[track] = Resolve(List(t, "routes"));
                            if (prop == "arrive" && t.TryGetProperty("departStop", out var ds) && ds.ValueKind == JsonValueKind.True)
                                target.DepartStop.Add(track);
                            if (prop == "arrive" && t.TryGetProperty("turnback", out var tb) && tb.ValueKind == JsonValueKind.True)
                                target.Turnback.Add(track);
                        }
                    }
                }
            }
        }

        // ---- 決まり（RouteRules.json） ----
        using (var doc = Doc("RouteRules.json"))
        {
            var root = doc.RootElement;
            foreach (var pair in root.GetProperty("singleTrackSections").EnumerateArray())
            {
                var p = pair.EnumerateArray().Select(x => x.GetString()!).ToArray();
                data.SingleTrackSections.Add((p[0], p[1]));
            }

            foreach (var st in root.GetProperty("defaultTracks").EnumerateObject())
            {
                if (!data.Stations.TryGetValue(st.Name, out var station)) continue;
                foreach (var (label, d) in new[] { ("下り", Dir.Down), ("上り", Dir.Up) })
                {
                    if (!st.Value.TryGetProperty(label, out var m)) continue;
                    station.DefaultTracks[d] = m.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
                }
            }

            var rank = 0;
            foreach (var t in root.GetProperty("typeRank").EnumerateArray()) data.TypeRank[t.GetString()!] = rank++;
            foreach (var p in root.GetProperty("passingStations").EnumerateArray())
            {
                var ps = new PassingStation(Str(p, "station"), p.GetProperty("下り").GetInt32(), p.GetProperty("上り").GetInt32());
                data.PassingStations[ps.StationId] = ps;
            }

            data.MaxPassingWaits = root.GetProperty("maxPassingWaits").GetInt32();
            data.PassingMarginSeconds = root.GetProperty("passingMarginSeconds").GetInt32();
            data.PassingToleranceSeconds = root.GetProperty("passingToleranceSeconds").GetInt32();
            data.DefaultDwellSeconds = root.GetProperty("defaultDwellSeconds").GetInt32();
            if (root.TryGetProperty("minLoopDwellSeconds", out var ml)) data.MinLoopDwellSeconds = ml.GetInt32();
            if (root.TryGetProperty("afterStopSeconds", out var ms)) data.AfterStopSeconds = ms.GetInt32();
            if (root.TryGetProperty("departBeforeArrive", out var dba) && dba.ValueKind == JsonValueKind.Array)
                foreach (var x in dba.EnumerateArray()) if (x.GetString() is { Length: > 0 } sid) data.DepartBeforeArrive.Add(sid);
            if (root.TryGetProperty("approachTrigger", out var at) && at.ValueKind == JsonValueKind.Object)
            {
                if (at.TryGetProperty("enabled", out var ae)) data.ApproachTriggerEnabled = ae.GetBoolean();
                if (at.TryGetProperty("margin", out var am)) data.ApproachTriggerMargin = Math.Max(0, am.GetInt32());
            }

            if (root.TryGetProperty("departDelaySeconds", out var dd))
                foreach (var st in dd.EnumerateObject())
                {
                    if (!data.Stations.TryGetValue(st.Name, out var station)) continue;
                    foreach (var (label, d) in new[] { ("下り", Dir.Down), ("上り", Dir.Up) })
                    {
                        if (!st.Value.TryGetProperty(label, out var m)) continue;
                        foreach (var t in m.EnumerateObject())
                            if (int.TryParse(t.Name, out var track)) station.DepartDelay[(d, track)] = t.Value.GetInt32();
                    }
                }
        }

        // ---- 信号（Signals.json / SignalTypes.json） ----
        using (var doc = Doc("Signals.json"))
        {
            foreach (var s in doc.RootElement.GetProperty("signals").EnumerateArray())
            {
                var dirText = Str(s, "direction");
                data.Signals[Str(s, "name")] = new SignalDef
                {
                    Name = Str(s, "name"),
                    Type = Str(s, "type") is { Length: > 0 } t ? t : null,
                    Direction = dirText == "下り" ? Dir.Down : dirText == "上り" ? Dir.Up : null,
                    IsRepeater = s.TryGetProperty("isRepeater", out var rep) && rep.GetBoolean(),
                    Next = List(s, "next"),
                    Protects = List(s, "protects"),
                    DirectionRoutes = List(s, "directionRoutes")
                };
            }
        }

        using (var doc = Doc("SignalTypes.json"))
        {
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateObject())
                data.SignalTypes[t.Name] = t.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
        }

        // ---- 標準運転時分（RunTimes.json）。種別・方向・区間ごとの秒。fromStop / toStop は発駅・着駅の扱い（停・通・運停） ----
        if (File.Exists(Path.Combine(dir, "RunTimes.json")))
        {
            using var doc = Doc("RunTimes.json");
            foreach (var r in doc.RootElement.GetProperty("runTimes").EnumerateArray())
            {
                data.RunTimes.Add(new RunTimeRow(Str(r, "type"), Str(r, "dir") == "下り" ? Dir.Down : Dir.Up, Str(r, "from"), Str(r, "to"),
                    Str(r, "fromStop") != "通", Str(r, "toStop") != "通", r.GetProperty("seconds").GetInt32()));
            }
        }

        // ---- ダイヤ（Timetable.json）。列車ごとに、駅の着発時刻（"HH:mm:ss"）・停車（停）／通過（通）／運転停車（運停）・番線 ----
        // st は LineData.json の駅ID（路線にない場所は null）。書かれていない欄は null
        if (File.Exists(Path.Combine(dir, "Timetable.json")))
        {
            using var doc = Doc("Timetable.json");
            foreach (var t in doc.RootElement.GetProperty("trains").EnumerateArray())
            {
                var number = NormalizeTrain(Str(t, "number"));
                if (number.Length == 0) continue;
                if (!data.Dia.TryGetValue(number, out var cur))
                {
                    var type = Str(t, "type") == "試運転" ? "回送" : Str(t, "type");
                    data.Dia[number] = cur = new DiaTrain { Number = number, Type = type, TypeLabel = Str(t, "label") };
                }

                if (t.TryGetProperty("next", out var nx) && nx.ValueKind == JsonValueKind.Object
                    && NormalizeTrain(Str(nx, "number")) is { Length: > 0 } nextNumber && Str(nx, "st") is { Length: > 0 } nextStation)
                    cur.Next = (nextNumber, nextStation);

                cur.Unyo ??= t.TryGetProperty("unyo", out var un) && un.ValueKind == JsonValueKind.Number ? un.GetInt32() : Gokko.Common.Unyo.FromTrain(number);
                if (t.TryGetProperty("turn", out var tn) && tn.ValueKind == JsonValueKind.Object && Str(tn, "kind") is { Length: > 0 } turnKind)
                    cur.Turn = new DiaTurn(turnKind, NormalizeTrain(Str(tn, "number")) is { Length: > 0 } tno ? tno : null,
                        Str(tn, "time") is { Length: > 0 } ttime ? ttime : null, Str(tn, "how") is { Length: > 0 } thow ? thow : null);

                if (cur.Stops.Count == 0)
                    foreach (var s in t.GetProperty("stops").EnumerateArray())
                        cur.Stops.Add(new DiaStop(Str(s, "name"), Str(s, "st") is { Length: > 0 } sid0 ? sid0 : null, Str(s, "stop"),
                            Str(s, "arr") is { Length: > 0 } a0 ? a0 : null, Str(s, "dep") is { Length: > 0 } d0 ? d0 : null));

                string? lastStation = null;
                foreach (var s in t.GetProperty("stops").EnumerateArray())
                {
                    var sid = Str(s, "st");
                    lastStation = sid.Length > 0 ? sid : null;
                    if (lastStation == null) continue;
                    var stop = Str(s, "stop") switch { "停" => "停車", "運停" => "運転停車", _ => "通過" };
                    cur.Stations[sid] = (stop, s.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : null);
                }

                // 最後の駅が進路を扱う駅なら、そこが終着
                if (lastStation != null && data.Stations.TryGetValue(lastStation, out var ts) && ts.RouteControl) cur.TerminalId = lastStation;
            }
        }

        return data;
    }
}
