namespace Gokko.Server.Services;

/// <summary>作動中の特発1件。</summary>
/// <param name="Source">"ランダム" や "管理画面" など、誰が作動させたか。</param>
public sealed record Incident(string Crossing, string Reason, string Source, DateTime StartedUtc, string? StartedGameTime);

/// <summary>管理画面に出す記録。</summary>
public sealed record LogEntry(DateTime Utc, string? GameTime, string Message);

/// <summary>
/// 特発の状態（サーバーが唯一の正）。作動・解除はすべてここを通し、変わったら <see cref="Changed"/> で知らせる
/// （配信役がすぐに全員へ送る）。サーバーを再起動すると、作動中の特発は消える。
/// </summary>
public sealed class IncidentManager(GameData data, GameClock clock, ILogger<IncidentManager> logger)
{
    private const int MaxLog = 100;

    private readonly object _lock = new();
    private readonly Dictionary<string, Incident> _active = new();
    private readonly LinkedList<LogEntry> _log = new();

    /// <summary>特発が作動・解除されたとき。ロックの外で呼ばれる。</summary>
    public event Action? Changed;

    /// <summary>直前に作動させた踏切（ランダムで続けて同じ踏切を選ばないため）。</summary>
    public string? LastCrossing { get; private set; }

    public int ActiveCount
    {
        get
        {
            lock (_lock) return _active.Count;
        }
    }

    public bool TryTrigger(string crossing, string reason, string source, out string error)
    {
        error = "";
        crossing = crossing.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? "（理由なし）" : reason.Trim();
        if (reason.Length > 200) reason = reason[..200];

        if (!data.IsKnownCrossing(crossing))
        {
            error = $"踏切名が一覧にありません: {crossing}";
            return false;
        }

        lock (_lock)
        {
            if (_active.ContainsKey(crossing))
            {
                error = $"{crossing} は、すでに作動中です";
                return false;
            }

            _active[crossing] = new Incident(crossing, reason, source, DateTime.UtcNow, clock.NowText);
            LastCrossing = crossing;
            AddLog($"作動: {crossing}（{source}）理由: {reason}");
        }

        logger.LogInformation("特発 作動: {Crossing} ({Source}) {Reason}", crossing, source, reason);
        Changed?.Invoke();
        return true;
    }

    /// <summary>指定した踏切を解除する。解除できたものを返す。</summary>
    public List<Incident> Release(IEnumerable<string> crossings, string by)
    {
        var released = new List<Incident>();
        lock (_lock)
        {
            foreach (var c in crossings)
            {
                if (_active.Remove(c, out var incident))
                {
                    released.Add(incident);
                    AddLog($"復帰: {c}（{by}）");
                }
            }
        }

        if (released.Count > 0)
        {
            logger.LogInformation("特発 復帰: {Crossings} ({By})", string.Join(",", released.Select(r => r.Crossing)), by);
            Changed?.Invoke();
        }

        return released;
    }

    public List<Incident> ReleaseAll(string by)
    {
        List<string> names;
        lock (_lock) names = _active.Keys.ToList();
        return Release(names, by);
    }

    /// <summary>作動から一定時間たったものを解除する。</summary>
    public List<Incident> ReleaseExpired(TimeSpan age)
    {
        var now = DateTime.UtcNow;
        List<string> names;
        lock (_lock) names = _active.Values.Where(i => now - i.StartedUtc >= age).Select(i => i.Crossing).ToList();
        return names.Count == 0 ? new List<Incident>() : Release(names, "自動復帰");
    }

    public List<Incident> Snapshot()
    {
        lock (_lock) return _active.Values.OrderBy(i => i.StartedUtc).ToList();
    }

    public List<LogEntry> RecentLog()
    {
        lock (_lock) return _log.ToList();
    }

    /// <summary>参加・退出などの記録だけを残す（特発の状態は変えない）。</summary>
    public void Note(string message)
    {
        lock (_lock) AddLog(message);
    }

    private void AddLog(string message)
    {
        _log.AddFirst(new LogEntry(DateTime.UtcNow, clock.NowText, message));
        while (_log.Count > MaxLog) _log.RemoveLast();
    }
}
