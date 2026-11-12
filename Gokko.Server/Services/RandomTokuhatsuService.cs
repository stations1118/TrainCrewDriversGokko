namespace Gokko.Server.Services;

/// <summary>
/// ランダム特発と自動復帰。1秒ごとに動く。
/// ・自動復帰: 作動から AutoReleaseSeconds たった特発を解除する
/// ・ランダム: RollIntervalSeconds ごとに抽選し、当たれば「参加者の誰かの、現在の駅間と進行方向の先」の踏切から1つを作動させる
/// </summary>
public sealed class RandomTokuhatsuService(
    RandomOptions settings,
    PlayerRegistry players,
    IncidentManager incidents,
    GameData data,
    ILogger<RandomTokuhatsuService> logger) : BackgroundService
{
    private readonly Random _rng = new();
    private DateTime _lastRollUtc = DateTime.UtcNow;
    private bool _wasEnabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ランダム特発の処理でエラー");
            }
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;

        if (settings.AutoReleaseSeconds > 0)
            incidents.ReleaseExpired(TimeSpan.FromSeconds(settings.AutoReleaseSeconds));

        // 有効にした瞬間から、最初の抽選までは間隔ぶん待つ
        if (settings.Enabled && !_wasEnabled) _lastRollUtc = now;
        _wasEnabled = settings.Enabled;
        if (!settings.Enabled) return;

        if ((now - _lastRollUtc).TotalSeconds < Math.Max(1, settings.RollIntervalSeconds)) return;
        _lastRollUtc = now;

        if (incidents.ActiveCount >= Math.Max(1, settings.MaxConcurrent)) return;
        if (_rng.NextDouble() >= Math.Clamp(settings.Probability, 0.0, 1.0)) return;

        var pool = PickCandidates();
        if (pool.Count == 0) return;

        var crossing = pool[_rng.Next(pool.Count)];
        var reason = data.Reasons.Count > 0 ? data.Reasons[_rng.Next(data.Reasons.Count)] : "";
        incidents.TryTrigger(crossing, reason, "ランダム", out _);
    }

    /// <summary>参加者それぞれの「現在の駅間と進行方向の先」にある踏切を合わせたもの。</summary>
    public List<string> PickCandidates()
    {
        var active = incidents.Snapshot().Select(i => i.Crossing).ToHashSet();
        var pool = new HashSet<string>();

        foreach (var p in players.Snapshot())
        {
            if (!p.InGame || p.Zones.Count == 0) continue;
            var (lo, hi) = Window(p.Zones, p.Direction);
            foreach (var (crossing, zones) in data.CrossingZones)
            {
                if (!active.Contains(crossing) && zones.Any(z => z >= lo && z <= hi)) pool.Add(crossing);
            }
        }

        if (pool.Count == 0 && settings.FallbackToAllCrossings && players.Snapshot().Any(p => p.InGame))
        {
            foreach (var c in data.Crossings.Where(c => !active.Contains(c))) pool.Add(c);
        }

        var list = pool.ToList();
        if (settings.AvoidRepeat && incidents.LastCrossing is { } last && list.Count > 1) list.Remove(last);
        return list;
    }

    // 駅は偶数、駅間は奇数のゾーンなので、1駅間ぶんは2ゾーン
    private (int Lo, int Hi) Window(List<int> zones, int direction)
    {
        var min = zones.Min();
        var max = zones.Max();
        var ahead = 2 * Math.Max(0, settings.SectionsAhead);
        var behind = 2 * Math.Max(0, settings.SectionsBehind);

        if (direction > 0) return (min - behind, max + ahead);
        if (direction < 0) return (min - ahead, max + behind);

        var both = Math.Max(ahead, behind);
        return (min - both, max + both);
    }
}
