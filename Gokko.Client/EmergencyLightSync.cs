namespace Gokko.Client;

/// <summary>
/// サーバーが決めた「作動中の特発」を、自分のゲームに反映する。
/// ・ゲームに送った値を覚えておき、サーバーの指示と違う踏切だけ SetEmergencyLight を送る
/// ・運転画面に入るたびに、全踏切を送り直す（シナリオの読み込みで、ゲーム側の状態が戻ることがあるため）
/// ・作動中の踏切は、念のため10秒ごとに送り直す
/// ・サーバーに接続できていない間（指示が null）は、送らずに今の状態を保つ
/// </summary>
public sealed class EmergencyLightSync
{
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, bool> _applied = new();
    private List<string> _all = new();
    private DateTime _lastResendUtc = DateTime.MinValue;
    private bool _wasInGame;

    /// <summary>ゲームで作動させている（と送った）踏切。</summary>
    public IEnumerable<string> AppliedOn => _applied.Where(kv => kv.Value).Select(kv => kv.Key);

    /// <summary>同期する踏切の一覧（サーバーから参加時に受け取る）。変わったら全部送り直す。</summary>
    public void SetCrossings(IEnumerable<string> crossings)
    {
        _all = crossings.ToList();
        _applied.Clear();
    }

    public async Task TickAsync(GameLink link, IReadOnlySet<string>? desired)
    {
        if (!link.IsConnected)
        {
            _applied.Clear();
            _wasInGame = false;
            return;
        }

        var inGame = link.Latest?.InGame == true;
        if (!inGame)
        {
            if (_wasInGame) _applied.Clear(); // 運転画面を出たら、次に入ったとき全部送り直す
            _wasInGame = false;
            return;
        }

        if (!_wasInGame)
        {
            _applied.Clear();
            _wasInGame = true;
        }

        if (desired == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastResendUtc >= ResendInterval)
        {
            _lastResendUtc = now;
            foreach (var name in _applied.Where(kv => kv.Value).Select(kv => kv.Key).ToList()) _applied.Remove(name);
        }

        foreach (var name in _all.Concat(desired).Distinct())
        {
            var want = desired.Contains(name);
            if (_applied.TryGetValue(name, out var current) && current == want) continue;

            try
            {
                await link.SetEmergencyLightAsync(name, want);
                _applied[name] = want;
            }
            catch
            {
                // 送れなかったものは、次の周期で送り直す
                return;
            }
        }
    }

    /// <summary>このクライアントが作動させた特発をすべて解除する（切断・終了のとき）。</summary>
    public async Task ClearAllAsync(GameLink link)
    {
        foreach (var name in AppliedOn.ToList())
        {
            try
            {
                if (link.IsConnected) await link.SetEmergencyLightAsync(name, false);
            }
            catch
            {
                // 接続が切れているときは送れない
            }

            _applied[name] = false;
        }
    }
}
