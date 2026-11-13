using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// サーバーが求めた信号の現示（他の参加者の在線で落とす分）を、自分のゲームに反映する（在線共有モード）。
/// ・SetSignalPhases [信号名, 現示, ...] で、変わったものだけ送る。指示がなくなった信号は None（ゲームの現示に戻す）を送る
/// ・ゲームのシナリオの読み込みなどで現示が戻ることがあるので、3秒ごとに全部送り直す
/// ・ゲームは「今より低い現示」しか受け付けないので、ゲーム自身の判断（進路など）と低い方が表示される
/// </summary>
public sealed class SignalApplier
{
    private static readonly TimeSpan ResendAll = TimeSpan.FromSeconds(3);

    private Dictionary<string, string> _sent = new();
    private DateTime _lastFullUtc = DateTime.MinValue;

    /// <summary>送った現示の記録を捨てる（シナリオを閉じた・ゲームとつなぎ直したとき。次の周期で全部送り直す）。</summary>
    public void Reset()
    {
        _sent.Clear();
        _lastFullUtc = DateTime.MinValue;
    }

    public async Task TickAsync(GameLink link, ClientState? state)
    {
        if (!link.IsConnected || link.Latest is not { InGame: true })
        {
            _sent.Clear();
            return;
        }

        var desired = (state?.Signals ?? new List<SignalCommand>()).ToDictionary(s => s.Name, s => s.Phase);
        var now = DateTime.UtcNow;
        var full = now - _lastFullUtc >= ResendAll;

        var args = new List<string>();
        foreach (var (name, phase) in desired)
            if (full || !_sent.TryGetValue(name, out var p) || p != phase) args.AddRange([name, phase]);
        foreach (var name in _sent.Keys.Where(n => !desired.ContainsKey(n))) args.AddRange([name, "None"]);

        if (args.Count == 0)
        {
            if (full) _lastFullUtc = now;
            return;
        }

        try
        {
            await link.SendCommandAsync("SetSignalPhases", args.ToArray());
            _sent = desired;
            if (full) _lastFullUtc = now;
        }
        catch
        {
            // 次の周期で送り直す
        }
    }

    /// <summary>落としていた信号をすべてゲームの現示に戻す（在線共有モードをやめるとき・終了時）。</summary>
    public async Task ClearAllAsync(GameLink link)
    {
        if (_sent.Count == 0 || !link.IsConnected) return;
        try
        {
            await link.SendCommandAsync("SetSignalPhases", _sent.Keys.SelectMany(n => new[] { n, "None" }).ToArray());
        }
        catch
        {
            // 接続が切れていれば送れない
        }

        _sent.Clear();
    }
}
