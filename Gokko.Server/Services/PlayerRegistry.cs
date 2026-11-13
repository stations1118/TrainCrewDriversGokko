using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>参加中のプレイヤー1人分の状態。読むときは <see cref="PlayerRegistry.Snapshot"/> の複製を使う。</summary>
public sealed class PlayerInfo
{
    public required string ConnectionId { get; set; }
    public DateTime JoinedUtc { get; init; }
    public string TrainNumber { get; set; } = "";
    public bool TrainCrewConnected { get; set; }
    public bool InGame { get; set; }
    public List<string> TrackCircuits { get; set; } = new();

    /// <summary>踏んでいる軌道回路から求めた、路線上の位置（駅=偶数、駅間=奇数）。</summary>
    public List<int> Zones { get; set; } = new();

    /// <summary>進行方向: 1=ゾーンの番号が大きくなる向き、-1=小さくなる向き、0=不明。</summary>
    public int Direction { get; set; }

    public double? LastCenter { get; set; }
    public DateTime LastReportUtc { get; set; }

    // ---- 在線共有モード ----
    public ShareMode Mode { get; set; }
    public List<StopInfo> Stops { get; set; } = new();
    public float TraveledDistance { get; set; }
    public float NextStationDistance { get; set; }
    public float Speed { get; set; }
    public HashSet<(string Interlock, string Route)> GameRoutes { get; set; } = new();
    public Dictionary<string, int> TrackChoices { get; set; } = new();

    // ---- 継続乗務 ----

    /// <summary>継続乗務ボタンが押されているか（最後の報告）。</summary>
    public bool ContinueDuty { get; set; }

    /// <summary>列車番号を手入力したか（最後の報告）。ダイヤ運転中は、手入力していない列車を出現させない。</summary>
    public bool TrainNumberTyped { get; set; }


    /// <summary>出現待ちの理由（継続乗務の開始番線が違う）。あれば、サーバーの列車として扱わない。</summary>
    public string? AppearReason { get; set; }

    /// <summary>位置（ゾーン）が分かっていた最後の時刻。データにない回路を踏んでいる間は、前の位置を保つ。</summary>
    public DateTime ZonesUpdatedUtc { get; set; }

    public PlayerInfo Clone()
    {
        var copy = (PlayerInfo)MemberwiseClone();
        copy.TrackCircuits = new List<string>(TrackCircuits);
        copy.Zones = new List<int>(Zones);
        copy.Stops = new List<StopInfo>(Stops);
        copy.GameRoutes = new HashSet<(string, string)>(GameRoutes);
        copy.TrackChoices = new Dictionary<string, int>(TrackChoices);
        return copy;
    }
}

/// <summary>参加中のプレイヤーの一覧。SignalR の接続ごとに1人。</summary>
public sealed class PlayerRegistry(GameData data, CentralControl central)
{
    /// <summary>ダイヤ運転中に列車番号を手入力していない列車の、出現待ちの理由。</summary>
    public const string NeedTypedNumber = "ダイヤ運転中は列車番号を手入力してください（シナリオの列番は使いません）";

    // ---- 強制切断（管理画面・ダイヤグラムの設定画面から） ----
    private readonly ConcurrentDictionary<string, bool> _kicked = new();

    /// <summary>参加者を強制的に切断する（次の報告のときに接続を切る）。参加していなければ false。</summary>
    public bool Kick(string connectionId)
    {
        if (!_players.ContainsKey(connectionId)) return false;
        _kicked[connectionId] = true;
        Changed?.Invoke($"強制切断: {(_players.TryGetValue(connectionId, out var p) ? p.TrainNumber : "")}（{connectionId[..Math.Min(6, connectionId.Length)]}）");
        return true;
    }

    public bool IsKicked(string connectionId) => _kicked.ContainsKey(connectionId);

    private const int MaxCircuits = 200;
    private const int MaxNameLength = 64;

    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();

    public int Count => _players.Count;

    public void Add(string connectionId, ShareMode mode)
    {
        var now = DateTime.UtcNow;
        _players[connectionId] = new PlayerInfo { ConnectionId = connectionId, JoinedUtc = now, LastReportUtc = now, Mode = mode };
    }

    public bool IsJoined(string connectionId) => _players.ContainsKey(connectionId);

    /// <summary>退出。継続乗務ボタンを押したまま運転中だったら、在線を残す（つなぎ直して同じ番線から始めれば引き継ぐ）。</summary>
    public void Remove(string connectionId)
    {
        var kicked = _kicked.TryRemove(connectionId, out _);
        if (!_players.TryRemove(connectionId, out var p)) return;
        lock (p)
            if (!kicked && (p.ContinueDuty || central.DiaMode) && p.Mode == ShareMode.Shared && p.InGame && p.TrackCircuits.Count > 0 && p.TrainNumber.Length > 0
                && p.AppearReason == null && !LeavesLine(p))
                AddGhost(p);
    }

    public PlayerInfo? Get(string connectionId)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return null;
        lock (p) return p.Clone();
    }

    /// <summary>参加中のプレイヤーと、継続乗務で残している在線（接続IDが <see cref="GhostPrefix"/> で始まる）。</summary>
    public List<PlayerInfo> Snapshot()
    {
        var list = new List<PlayerInfo>();
        foreach (var p in _players.Values)
        {
            lock (p) list.Add(p.Clone());
        }

        lock (_ghosts)
        {
            ExpireGhosts();
            list.AddRange(_ghosts.Values.Select(g => g.Info.Clone()));
        }

        return list;
    }

    // ---- 継続乗務: 運転画面を出たあとも、最後の在線位置を5分間残す ----

    public const string GhostPrefix = "継続/";
    public static readonly TimeSpan GhostLife = TimeSpan.FromMinutes(5);

    public sealed record Ghost(string Id, string Owner, PlayerInfo Info, DateTime SinceUtc, DateTime UntilUtc);

    private readonly Dictionary<string, Ghost> _ghosts = new();

    public event Action<string>? Changed;

    public List<Ghost> Ghosts()
    {
        lock (_ghosts)
        {
            ExpireGhosts();
            return _ghosts.Values.OrderBy(g => g.SinceUtc).ToList();
        }
    }

    /// <summary>継続乗務の在線を消す（管理画面・ダイヤグラムの設定画面から）。</summary>
    public bool RemoveGhost(string id)
    {
        Ghost? g;
        lock (_ghosts)
            if (!_ghosts.Remove(id, out g)) return false;
        Changed?.Invoke($"継続乗務: {g.Info.TrainNumber} の在線を消しました");
        return true;
    }

    private void ExpireGhosts()
    {
        if (central.DiaMode) return; // ダイヤ運転中は時間で消さない（管理画面・ダイヤグラムの設定画面から消す）
        var now = DateTime.UtcNow;
        foreach (var g in _ghosts.Values.Where(g => g.UntilUtc <= now).ToList())
        {
            _ghosts.Remove(g.Id);
            Changed?.Invoke($"継続乗務: {g.Info.TrainNumber} は5分たったので在線を消しました");
        }
    }

    /// <summary>
    /// 線区の外（赤山町の先、三郷方面）へ出ていく列車か。上り列車（列番の数字の最後の桁が偶数）が、赤山町の1番線・2番線、
    /// またはそれより三郷寄り（赤山町〜三郷の駅間・三郷・路線データにない回路）にだけいるとき。
    /// こういう列車がシナリオを終えたときは、継続乗務の在線を残さない（ダイヤ運転中も。その先へ出て行った扱い）。
    /// 2番線は下りの通過（新大路方から入ってくる列車）も使うので、上り列車に限る
    /// </summary>
    private bool LeavesLine(PlayerInfo p)
    {
        if (p.TrackCircuits.Count == 0) return false;
        var body = Regex.Replace(Regex.Replace(p.TrainNumber.Trim(), @"#\d+$", ""), "[XYZ]$", "", RegexOptions.IgnoreCase);
        var digits = Regex.Match(body, @"(\d+)(?!.*\d)");
        if (!digits.Success || int.Parse(digits.Groups[1].Value[^1..]) % 2 != 0) return false; // 上り（偶数）だけ

        var exitZone = data.Line.ZoneOfStation(ExitStation);
        if (exitZone < 0) return false;
        return p.TrackCircuits.All(c => ExitTrackCircuits.Contains(c) || (data.Line.ZoneOfCircuit(c) is not { } z || z < exitZone));
    }

    /// <summary>線区の外へ出る駅（赤山町）と、出ていく列車が使う番線の回路（1番線・2番線）。</summary>
    private const string ExitStation = "TH58";
    private static readonly HashSet<string> ExitTrackCircuits = ["TH58_2RAT", "TH58_2RBT"];

    private void AddGhost(PlayerInfo from)
    {
        var now = DateTime.UtcNow;
        var id = $"{GhostPrefix}{from.ConnectionId}/{now.Ticks}";
        var info = from.Clone();
        info.ConnectionId = id;
        info.InGame = true;
        info.TrainCrewConnected = true;
        info.Speed = 0;
        info.ContinueDuty = false;
        info.AppearReason = null;
        lock (_ghosts)
        {
            foreach (var old in _ghosts.Values.Where(g => g.Owner == from.ConnectionId).ToList()) _ghosts.Remove(old.Id);
            _ghosts[id] = new Ghost(id, from.ConnectionId, info, now, central.DiaMode ? DateTime.MaxValue : now + GhostLife);
        }

        Changed?.Invoke(central.DiaMode ? $"継続乗務: {info.TrainNumber} の在線を残します（ダイヤ運転中は消すまで残す）" : $"継続乗務: {info.TrainNumber} の在線を5分間残します");
    }

    /// <summary>
    /// 運転画面に入った列車が、継続乗務の在線を引き継ぐか。同じ番線（踏んでいる軌道回路が重なる）なら、残した在線を消して
    /// 新しいシナリオの列番の列車として始める（出現待ちにしない）。
    /// 自分が残した在線があるのに番線が違えば「開始番線が違う」として出現待ちにする。
    /// </summary>
    private void MatchGhost(PlayerInfo p, string number)
    {
        lock (_ghosts)
        {
            ExpireGhosts();
            var same = _ghosts.Values.Where(g => g.Info.TrackCircuits.Any(p.TrackCircuits.Contains))
                .OrderByDescending(g => g.Owner == p.ConnectionId).FirstOrDefault();
            if (same != null)
            {
                _ghosts.Remove(same.Id);
                p.AppearReason = null;
                Changed?.Invoke($"継続乗務: {same.Info.TrainNumber} の在線を {number} が引き継ぎました");
                return;
            }

            var mine = _ghosts.Values.FirstOrDefault(g => g.Owner == p.ConnectionId);
            p.AppearReason = mine != null ? $"開始番線が違う（継続乗務 {mine.Info.TrainNumber} の在線が残っています）" : null;
        }
    }

    /// <summary>クライアントの報告を反映する。参加していない接続なら false。</summary>
    public bool ApplyReport(string connectionId, ClientReport report)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return false;

        var circuits = (report.TrackCircuits ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Truncate(c.Trim()))
            .Distinct()
            .Take(MaxCircuits)
            .ToList();
        var zones = report.InGame ? data.Line.ZonesOf(circuits) : new List<int>();

        lock (p)
        {
            // 継続乗務: ボタンを押したまま運転画面を出たら、最後の在線を残す。運転画面に入ったら、残した在線を引き継ぐか確かめる
            var wasIn = p.InGame && p.TrackCircuits.Count > 0;
            var nowIn = report.InGame && circuits.Count > 0;
            // ダイヤ運転中は、継続乗務ボタンによらず残す（出現待ちだった列車は、在線がないので残さない）
            if (wasIn && !nowIn && p.Mode == ShareMode.Shared && (p.ContinueDuty || report.ContinueDuty || central.DiaMode) && p.TrainNumber.Length > 0
                && p.AppearReason == null && !LeavesLine(p))
                AddGhost(p);
            p.ContinueDuty = report.ContinueDuty;
            p.TrainNumberTyped = report.TrainNumberTyped;
            var number = Truncate(NormalizeName(report.TrainNumber));
            // ダイヤ運転中に列車番号を手入力していなければ、出現待ち（残した在線は引き継がず、消しも踏みつぶしもしない）
            var mustType = central.DiaMode && p.Mode == ShareMode.Shared && !report.TrainNumberTyped;
            if (!nowIn) p.AppearReason = null;
            else if (mustType) p.AppearReason = NeedTypedNumber;
            else if (p.Mode == ShareMode.Shared && (!wasIn || p.AppearReason != null))
            {
                p.TrackCircuits = circuits;
                MatchGhost(p, number);
            }

            p.TrainNumber = number;
            p.TrainCrewConnected = report.TrainCrewConnected;
            p.InGame = report.InGame;
            p.TrackCircuits = circuits;
            p.LastReportUtc = DateTime.UtcNow;

            // データにない回路だけを踏んでいるときは、前の位置を保つ（運転画面を出たら消す）
            if (zones.Count > 0 || !report.InGame || circuits.Count == 0)
            {
                p.Zones = zones;
                p.ZonesUpdatedUtc = DateTime.UtcNow;
            }

            if (p.Mode == ShareMode.Shared)
            {
                p.Stops = (report.Stops ?? new List<StopInfo>()).Take(100).ToList();
                p.TraveledDistance = report.TraveledDistance;
                p.NextStationDistance = report.NextStationDistance;
                p.Speed = report.Speed;
                p.GameRoutes = (report.GameRoutes ?? new List<RouteKey>()).Take(200)
                    .Select(r => (r.Interlocking, r.RouteName)).ToHashSet();
                p.TrackChoices = (report.TrackChoices ?? new Dictionary<string, int>())
                    .Take(50).ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            zones = p.Zones;

            // 位置が動いたら、その向きを進行方向とする。止まっている間は、直前の向きを保つ
            if (zones.Count > 0)
            {
                var center = zones.Average();
                if (p.LastCenter is { } last && Math.Abs(center - last) > 1e-9) p.Direction = center > last ? 1 : -1;
                p.LastCenter = center;
            }
        }

        return true;
    }

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Normalize(NormalizationForm.FormKC).Trim();
    }

    private static string Truncate(string s) => s.Length <= MaxNameLength ? s : s[..MaxNameLength];
}
