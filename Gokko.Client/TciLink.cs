using TrainCrew;

namespace Gokko.Client;

/// <summary>運転操作の読み取り結果（TrainCrewInput の GetTrainState から、乗務の中継に要るものだけ）。</summary>
public sealed record TciState(
    bool InGame,
    bool IsDriver,
    int Power,
    int Brake,
    int Reverser,
    float Speed,
    bool AllClose,
    string DiaName,
    float NextStationDistance);

/// <summary>
/// TRAIN CREW の公式の入出力ライブラリ（TrainCrewInput.dll）。ノッチ・レバーサー・ボタンをゲームへ送り、運転操作を読む。
/// 資料のとおり、運転画面の操作だけに使う。DLL の呼び出しは UI のスレッドからだけ行う。
/// </summary>
public sealed class TciLink : IDisposable
{
    private bool _ready;
    private int _lastPower = int.MinValue, _lastBrake = int.MinValue, _lastReverser = int.MinValue;

    /// <summary>DLL が使えないときの理由（使えれば空）。</summary>
    public string Error { get; private set; } = "";

    public bool Ready => _ready;

    public void Start()
    {
        if (_ready) return;
        try
        {
            TrainCrewInput.Init();
            _ready = true;
            Error = "";
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name;
        }
    }

    public TciState? Read()
    {
        if (!_ready) return null;
        try
        {
            var gs = TrainCrewInput.gameState;
            var s = TrainCrewInput.GetTrainState();
            if (s == null) return null;
            var inGame = gs.gameScreen is GameScreen.MainGame or GameScreen.MainGame_Pause;
            return new TciState(inGame, gs.crewType == CrewType.Driver, s.Pnotch, s.Bnotch, s.Reverser, s.Speed, s.AllClose,
                s.diaName ?? "", s.nextStaDistance);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ノッチを送る（変わったときだけ。force なら同じ値でも送る）。力行・ブレーキとも正の値（ゲームの Pnotch・Bnotch と同じ）。</summary>
    public void SetNotch(int power, int brake, bool force = false)
    {
        if (!_ready || (!force && power == _lastPower && brake == _lastBrake)) return;
        try
        {
            TrainCrewInput.SetNotch(power, brake);
            _lastPower = power;
            _lastBrake = brake;
        }
        catch
        {
            // 次に変わったときに送り直す
        }
    }

    public void SetReverser(int dir, bool force = false)
    {
        if (!_ready || (!force && dir == _lastReverser)) return;
        try
        {
            TrainCrewInput.SetReverser(dir);
            _lastReverser = dir;
        }
        catch
        {
            // 次に変わったときに送り直す
        }
    }

    /// <summary>送ったノッチの記録を捨てる（次は同じ値でも送る）。</summary>
    public void ForgetSent() => _lastPower = _lastBrake = _lastReverser = int.MinValue;

    /// <summary>ボタン（"DoorOpn"、"DoorCls"、"Buzzer" など InputAction の名前）を押す・離す。</summary>
    public bool SetButton(string action, bool on)
    {
        if (!_ready || !Enum.TryParse<InputAction>(action, out var a)) return false;
        try
        {
            TrainCrewInput.SetButton(a, on);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ボタンを押して、少しあとに離す。</summary>
    public async Task PressAsync(string action, int holdMs = 200)
    {
        if (!SetButton(action, true)) return;
        await Task.Delay(holdMs);
        SetButton(action, false);
    }

    public void Dispose()
    {
        if (!_ready) return;
        _ready = false;
        try
        {
            TrainCrewInput.Dispose();
        }
        catch
        {
            // 終了時のエラーは無視する
        }
    }
}
