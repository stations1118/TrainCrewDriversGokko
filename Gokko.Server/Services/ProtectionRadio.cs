namespace Gokko.Server.Services;

/// <summary>
/// 防護無線。発報した地点で受信範囲を固定し、範囲にいる列車（上下・共有のモードを問わない）に警報を鳴らさせる。
/// 範囲: 駅間で発報したら前後2駅まで（B〜C駅間なら A〜D駅の構内と、その間の駅間）。駅構内で発報したら、その駅から前後それぞれ2駅まで。路線の端より先には広げない。
/// 発報者の切断・運転画面の外・TRAIN CREW の切断・列番の変更・報告が5秒以上止まったら、その発報は解除する。管理画面からも解除できる。
/// （他の開発者の提案「防護無線追加版」を、今のサーバーに移したもの）
/// </summary>
public sealed class ProtectionRadio(GameData data, PlayerRegistry players, IncidentManager log)
{
    private sealed record Transmission(string Id, int First, int Last, string Train, int Center, DateTime StartedUtc);

    private readonly Dictionary<string, Transmission> _active = new();
    private readonly object _lock = new();
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

    /// <summary>位置（ゾーン）。報告が新しく、運転画面にいるときだけ。古い Zones は使わず、最新の軌道回路から求める。</summary>
    private List<int> CurrentZones(PlayerInfo p, DateTime now) =>
        p.InGame && p.TrainCrewConnected && now - p.LastReportUtc <= Freshness ? data.Line.ZonesOf(p.TrackCircuits) : [];

    /// <summary>発報・解除の切り替え。自分の発報があれば解除（位置が分からなくても解除できる）、なければ今の位置で発報。受け付けたら true。</summary>
    public bool Toggle(string connectionId)
    {
        lock (_lock)
        {
            if (_active.Remove(connectionId, out var old))
            {
                log.Note($"防護無線: {old.Train} が解除");
                return true;
            }

            var p = players.Get(connectionId);
            if (p == null) return false;
            var zones = CurrentZones(p, DateTime.UtcNow);
            if (zones.Count == 0) return false; // 位置が分からない（路線の外・運転画面の外）

            // 境目をまたいでいるときは、進む向きの先頭側。向きが分からなければ駅間を優先
            var center = p.Direction > 0 ? zones.Max() : p.Direction < 0 ? zones.Min() : zones.FirstOrDefault(z => z % 2 == 1, zones[0]);
            var radius = center % 2 == 1 ? 3 : 4; // 駅間(3)なら 0〜6（A〜D）、駅(4)なら前後2駅
            var tx = new Transmission(connectionId, Math.Max(0, center - radius), Math.Min((data.Line.StationIds.Count - 1) * 2, center + radius),
                p.TrainNumber, center, DateTime.UtcNow);
            _active[connectionId] = tx;
            log.Note($"防護無線: {tx.Train} が {data.Line.ZoneLabel(center)} で発報（受信範囲 {RangeLabel(tx)}）");
            return true;
        }
    }

    /// <summary>発報者が退出した（その発報を解除）。</summary>
    public void Remove(string connectionId)
    {
        lock (_lock)
            if (_active.Remove(connectionId, out var tx)) log.Note($"防護無線: {tx.Train} の発報を解除（退出）");
    }

    /// <summary>管理画面からの解除。</summary>
    public bool Clear(string connectionId)
    {
        lock (_lock)
        {
            if (!_active.Remove(connectionId, out var tx)) return false;
            log.Note($"防護無線: {tx.Train} の発報を管理画面から解除");
            return true;
        }
    }

    /// <summary>
    /// 受信している参加者（接続ID）と、発報している参加者。配信のたびに呼ぶ。
    /// 発報者が切断・運転画面の外・列番を変えた・報告が止まった発報は、ここで解除する。
    /// </summary>
    public (HashSet<string> Receivers, HashSet<string> Senders) Evaluate(IReadOnlyList<PlayerInfo> snapshot, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        lock (_lock)
        {
            var byId = snapshot.GroupBy(p => p.ConnectionId).ToDictionary(g => g.Key, g => g.First());
            foreach (var (id, tx) in _active.ToArray())
                if (!byId.TryGetValue(id, out var p) || !p.InGame || !p.TrainCrewConnected || now - p.LastReportUtc > Freshness || p.TrainNumber != tx.Train)
                {
                    _active.Remove(id);
                    log.Note($"防護無線: {tx.Train} の発報を解除（切断・運転画面の外・列番の変更・報告の停止）");
                }

            if (_active.Count == 0) return (new HashSet<string>(), new HashSet<string>());
            var receivers = snapshot.Where(p => !p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)
                                                && CurrentZones(p, now).Any(z => _active.Values.Any(tx => z >= tx.First && z <= tx.Last)))
                .Select(p => p.ConnectionId).ToHashSet();
            return (receivers, _active.Keys.ToHashSet());
        }
    }

    /// <summary>発報中の一覧（管理画面用）。</summary>
    public List<object> View()
    {
        lock (_lock)
            return _active.Values.OrderBy(t => t.StartedUtc).Select(t => (object)new
            {
                id = t.Id,
                train = t.Train,
                at = data.Line.ZoneLabel(t.Center),
                range = RangeLabel(t),
                started = t.StartedUtc
            }).ToList();
    }

    private string RangeLabel(Transmission t) => $"{data.Line.ZoneLabel(t.First)}〜{data.Line.ZoneLabel(t.Last)}";
}
