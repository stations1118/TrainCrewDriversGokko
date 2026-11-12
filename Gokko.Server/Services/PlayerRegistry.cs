using System.Collections.Concurrent;
using System.Text;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>参加中のプレイヤー1人分の状態。読むときは <see cref="PlayerRegistry.Snapshot"/> の複製を使う。</summary>
public sealed class PlayerInfo
{
    public required string ConnectionId { get; init; }
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

    public void Remove(string connectionId) => _players.TryRemove(connectionId, out _);

    public PlayerInfo? Get(string connectionId)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return null;
        lock (p) return p.Clone();
    }

    public List<PlayerInfo> Snapshot()
    {
        var list = new List<PlayerInfo>();
        foreach (var p in _players.Values)
        {
            lock (p) list.Add(p.Clone());
        }

        return list;
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
            p.TrainNumber = Truncate(NormalizeName(report.TrainNumber));
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
