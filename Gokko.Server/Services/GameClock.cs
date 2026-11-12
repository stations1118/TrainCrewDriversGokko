namespace Gokko.Server.Services;

/// <summary>
/// ロープレ上の時刻。管理画面で「11:00」と設定すると、その時点から実時間と同じ速さで進む。
/// 今後、発車時刻による進路の開通（ダイヤ）の基準に使う。
/// </summary>
public sealed class GameClock
{
    private readonly object _lock = new();
    private TimeSpan? _base;
    private DateTime _setUtc;
    private bool _running;

    public void Set(TimeSpan time, bool running)
    {
        lock (_lock)
        {
            _base = time;
            _setUtc = DateTime.UtcNow;
            _running = running;
        }
    }

    /// <summary>時刻を保ったまま、進める・止めるを切り替える。</summary>
    public void SetRunning(bool running)
    {
        lock (_lock)
        {
            if (NowCore() is not { } now) return;
            _base = now;
            _setUtc = DateTime.UtcNow;
            _running = running;
        }
    }

    public void Clear()
    {
        lock (_lock) _base = null;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _base != null && _running;
        }
    }

    public TimeSpan? Now
    {
        get
        {
            lock (_lock) return NowCore();
        }
    }

    public string? NowText => Now?.ToString(@"hh\:mm\:ss");

    private TimeSpan? NowCore()
    {
        if (_base is not { } b) return null;
        var t = _running ? b + (DateTime.UtcNow - _setUtc) : b;
        var ticks = t.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}
