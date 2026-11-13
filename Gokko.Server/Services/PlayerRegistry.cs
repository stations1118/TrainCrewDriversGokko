using System.Collections.Concurrent;
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
public sealed class PlayerRegistry(GameData data)
{
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
        if (!_players.TryRemove(connectionId, out var p)) return;
        lock (p)
            if (p.ContinueDuty && p.Mode == ShareMode.Shared && p.InGame && p.TrackCircuits.Count > 0 && p.TrainNumber.Length > 0)
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
        var now = DateTime.UtcNow;
        foreach (var g in _ghosts.Values.Where(g => g.UntilUtc <= now).ToList())
        {
            _ghosts.Remove(g.Id);
            Changed?.Invoke($"継続乗務: {g.Info.TrainNumber} は5分たったので在線を消しました");
        }
    }

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
            _ghosts[id] = new Ghost(id, from.ConnectionId, info, now, now + GhostLife);
        }

        Changed?.Invoke($"継続乗務: {info.TrainNumber} の在線を5分間残します");
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
            if (wasIn && !nowIn && p.Mode == ShareMode.Shared && (p.ContinueDuty || report.ContinueDuty) && p.TrainNumber.Length > 0)
                AddGhost(p);
            p.ContinueDuty = report.ContinueDuty;
            var number = Truncate(NormalizeName(report.TrainNumber));
            if (!nowIn) p.AppearReason = null;
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
