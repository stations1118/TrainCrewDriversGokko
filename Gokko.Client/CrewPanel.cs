using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 「乗務」タブ。人の運転士と人の車掌が、同じ列車に乗って遊ぶための中継（0.1秒ごと）。
/// ・運転士: 自分のノッチ・レバーサー・戸閉を読んで、サーバーの中継（/crew）へ送る（サーバーが記録する）。
///   車掌のドア・ブザーの操作が届いたら、自分のゲームで同じボタンを押す
/// ・車掌: 届いた運転士のノッチ・レバーサーを、自分のゲームへそのまま送る（TrainCrewInput）。
///   自分のゲームでドアが開いた・閉まったら、運転士へ送る。運転士の操作が3秒届かなくなったら非常ブレーキ
/// ・車掌は、サーバーに記録した運転士の操作を再生して、1台で流れを確かめられる
/// 車掌のときは、届いた操作と自分の列車の状態を %APPDATA%\GokkoClient\crew\ に CSV で残す（ずれを調べるため）。
/// </summary>
public sealed class CrewPanel : TabPage
{
    private const int StaleMs = 3000;
    private const int HeartbeatMs = 500;

    private readonly CrewRole _role;
    private readonly GameLink _game;
    private readonly TciLink _tci = new();
    private readonly CrewLink _crew = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    private readonly Label _statusLabel = new();
    private readonly Label _notchLabel = new();
    private readonly Label _gapLabel = new();
    private readonly Label _doorLabel = new();
    private readonly ComboBox _recordings = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _refreshButton = new() { Text = "一覧" };
    private readonly Button _replayButton = new() { Text = "再生" };
    private readonly Button _stopReplayButton = new() { Text = "停止" };

    // 運転士
    private long _seq = Environment.TickCount64;
    private DriverInput? _lastSent;
    private long _lastSentMs;
    private readonly ConcurrentQueue<ConductorInput> _fromConductor = new();

    // 車掌
    private volatile DriverInput? _latestInput;
    private long _latestInputMs;
    private long _lastSeq;
    private long _prevArrivalMs;
    private long _arrivalGapMs;
    private bool _haveDriver;
    private bool _emergency;
    private bool? _ownAllClose;
    private DriverInput? _appliedInput;
    private StreamWriter? _log;

    private string _doorText = "";
    private DateTime _doorUntil = DateTime.MinValue;

    public CrewPanel(CrewRole role, GameLink game)
    {
        _role = role;
        _game = game;
        Text = "乗務";
        Padding = new Padding(6);
        Controls.Add(BuildLayout());

        _crew.DriverInputReceived += OnDriverInput;
        _crew.ConductorInputReceived += i => _fromConductor.Enqueue(i);
        _timer.Tick += (_, _) => Tick();
    }

    public CrewRole Role => _role;

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        foreach (var l in new[] { _statusLabel, _gapLabel, _doorLabel })
        {
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
        }

        _notchLabel.Dock = DockStyle.Fill;
        _notchLabel.TextAlign = ContentAlignment.MiddleCenter;
        _notchLabel.Font = new Font("Yu Gothic UI", 20f, FontStyle.Bold);

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(_statusLabel);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(_notchLabel);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(_gapLabel);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(_doorLabel);

        // ドア・ブザー（車掌が使う。運転士のときは自分のゲームで試すだけ）
        var doors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++) doors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        Button DoorButton(string text, string action)
        {
            var b = new Button { Text = text, Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold) };
            b.Click += async (_, _) => await _tci.PressAsync(action);
            return b;
        }

        doors.Controls.Add(DoorButton("ドア開", "DoorOpn"), 0, 0);
        doors.Controls.Add(DoorButton("ドア閉", "DoorCls"), 1, 0);
        doors.Controls.Add(DoorButton("再開閉", "ReOpenSW"), 2, 0);
        var buzzer = new Button { Text = "ブザー", Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold) };
        buzzer.MouseDown += (_, _) => Buzzer(true);
        buzzer.MouseUp += (_, _) => Buzzer(false);
        doors.Controls.Add(buzzer, 3, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.Controls.Add(doors);

        // 記録の再生（車掌だけ）
        var replay = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        replay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        replay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        replay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        replay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        foreach (var c in new Control[] { _recordings, _refreshButton, _replayButton, _stopReplayButton }) c.Dock = DockStyle.Fill;
        replay.Controls.Add(_recordings, 0, 0);
        replay.Controls.Add(_refreshButton, 1, 0);
        replay.Controls.Add(_replayButton, 2, 0);
        replay.Controls.Add(_stopReplayButton, 3, 0);
        _refreshButton.Click += async (_, _) => await RefreshRecordingsAsync();
        _replayButton.Click += async (_, _) =>
        {
            if (_recordings.SelectedItem is not RecordingItem item) return;
            Message?.Invoke(await _crew.StartReplayAsync(item.Info.Name));
        };
        _stopReplayButton.Click += async (_, _) => await _crew.StopReplayAsync();
        var replayBox = new GroupBox { Text = "記録した運転士の操作を再生（車掌のとき・運転士がいないとき）", Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 4) };
        replayBox.Controls.Add(replay);
        replayBox.Enabled = _role == CrewRole.Conductor;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(replayBox);

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = _role == CrewRole.Conductor
                ? "車掌: ゲームは運転士モードで、運転士と同じシナリオ・同じ列番で始めてください。列車は運転士のノッチで動きます（自分のマスコンは触らない）。" +
                  "ドアはゲームのキーか上のボタンで扱い、開け閉めは運転士のゲームにも伝わります。"
                : "運転士: 同じ列番で車掌が乗ると、ノッチを車掌のゲームへ送ります。ドアは車掌が扱います。運転の操作はサーバーに記録されます。"
        });
        return layout;
    }

    /// <summary>画面の下のお知らせに出す。</summary>
    public event Action<string>? Message;

    public void Start(string url, string password)
    {
        _tci.Start();
        _crew.Start(url, password, _role);
        _timer.Start();
    }

    public async Task StopAsync()
    {
        _timer.Stop();
        await _crew.StopAsync();
        ReleaseControl();
        CloseLog();
    }

    /// <summary>乗る列番（毎周期）。</summary>
    public void SetTrain(string train) => _crew.SetTrain(train);

    public void Shutdown()
    {
        _timer.Stop();
        ReleaseControl();
        CloseLog();
        _tci.Dispose();
    }

    // =====================================================================================

    private void OnDriverInput(DriverInput input)
    {
        // 送り元が替わった（記録の再生に切り替わった・運転士がつなぎ直した）ら、番号を数え直す
        var last = Interlocked.Read(ref _lastSeq);
        if (input.Seq <= last && last - input.Seq < 100_000) return;
        Interlocked.Exchange(ref _lastSeq, input.Seq);
        var now = Environment.TickCount64;
        var prev = Interlocked.Exchange(ref _prevArrivalMs, now);
        if (prev > 0) Interlocked.Exchange(ref _arrivalGapMs, now - prev);
        Interlocked.Exchange(ref _latestInputMs, now);
        _latestInput = input;
    }

    private void Tick()
    {
        var s = _tci.Read();
        if (_role == CrewRole.Driver) DriverTick(s);
        else ConductorTick(s);
        UpdateView(s);
    }

    private void DriverTick(TciState? s)
    {
        while (_fromConductor.TryDequeue(out var c))
        {
            switch (c.Action)
            {
                case "DoorOpn" or "DoorCls" or "ReOpenSW":
                    _ = _tci.PressAsync(c.Action);
                    ShowDoor(c.Action switch { "DoorOpn" => "車掌: ドア開", "DoorCls" => "車掌: ドア閉", _ => "車掌: 再開閉" });
                    break;
                case "Buzzer":
                    _tci.SetButton("Buzzer", true);
                    ShowDoor("車掌: ブザー");
                    break;
                case "BuzzerOff":
                    _tci.SetButton("Buzzer", false);
                    break;
            }
        }

        if (s == null || !s.InGame || !_crew.IsJoined) return;
        var game = _game.Latest;
        var input = new DriverInput
        {
            Power = s.Power, Brake = s.Brake, Reverser = s.Reverser, Speed = s.Speed, AllClose = s.AllClose,
            TraveledDistance = game?.TraveledDistance ?? 0, NextStationDistance = s.NextStationDistance,
            ClientMs = Environment.TickCount64
        };
        var changed = _lastSent == null || _lastSent.Power != input.Power || _lastSent.Brake != input.Brake
                      || _lastSent.Reverser != input.Reverser || _lastSent.AllClose != input.AllClose;
        if (!changed && input.ClientMs - _lastSentMs < HeartbeatMs) return;
        input.Seq = ++_seq;
        _crew.SendDriverInput(input);
        _lastSent = input;
        _lastSentMs = input.ClientMs;
    }

    private void ConductorTick(TciState? s)
    {
        if (s == null || !s.InGame)
        {
            _ownAllClose = null;
            return;
        }

        // 自分のゲームのドアが開いた・閉まったら、運転士へ
        if (_ownAllClose is { } before && before != s.AllClose)
        {
            var action = s.AllClose ? "DoorCls" : "DoorOpn";
            _crew.SendConductorInput(new ConductorInput { Action = action, AllClose = s.AllClose, TraveledDistance = _game.Latest?.TraveledDistance ?? 0 });
            ShowDoor(s.AllClose ? "ドア閉 → 運転士へ" : "ドア開 → 運転士へ");
        }

        _ownAllClose = s.AllClose;

        var input = _latestInput;
        var age = Environment.TickCount64 - Interlocked.Read(ref _latestInputMs);
        if (input != null && age < StaleMs)
        {
            if (_emergency) _tci.ForgetSent();
            _emergency = false;
            _haveDriver = true;
            _tci.SetReverser(input.Reverser);
            _tci.SetNotch(input.Power, input.Brake);
            if (!ReferenceEquals(input, _appliedInput))
            {
                _appliedInput = input;
                WriteLog(input, s);
            }
        }
        else if (_haveDriver && !_emergency)
        {
            // 運転士の操作が届かない: 走り続けないように非常ブレーキ
            _tci.SetNotch(0, 8, force: true);
            _emergency = true;
            Message?.Invoke("運転士の操作が届かないので、非常ブレーキをかけました");
        }
    }

    private void Buzzer(bool on)
    {
        _tci.SetButton("Buzzer", on);
        if (_role == CrewRole.Conductor) _crew.SendConductorInput(new ConductorInput { Action = on ? "Buzzer" : "BuzzerOff" });
    }

    /// <summary>車掌をやめるとき: 送ったノッチを中立・ブレーキに戻しておく（自分のマスコンで動かせるように）。</summary>
    private void ReleaseControl()
    {
        if (_role != CrewRole.Conductor || !_haveDriver) return;
        _tci.SetNotch(0, 8, force: true);
        _haveDriver = false;
        _latestInput = null;
    }

    private void ShowDoor(string text)
    {
        _doorText = text;
        _doorUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private async Task RefreshRecordingsAsync()
    {
        var list = await _crew.ListRecordingsAsync();
        _recordings.Items.Clear();
        foreach (var r in list) _recordings.Items.Add(new RecordingItem(r));
        if (_recordings.Items.Count > 0) _recordings.SelectedIndex = 0;
        else Message?.Invoke("記録がありません");
    }

    private sealed record RecordingItem(CrewRecordingInfo Info)
    {
        public override string ToString() => $"{Info.StartedAt.LocalDateTime:MM/dd HH:mm} 列番 {Info.TrainNumber}（{Info.Seconds:0}秒）";
    }

    private static string Notch(int power, int brake, int reverser)
    {
        var r = reverser switch { 1 => "前", -1 => "後", _ => "切" };
        var n = brake > 0 ? $"B{brake}" : power > 0 ? $"P{power}" : "N";
        return $"{n}　（{r}）";
    }

    private void UpdateView(TciState? s)
    {
        var st = _crew.Status;
        var link = !_tci.Ready ? $"TrainCrewInput が使えません（{_tci.Error}）"
            : !_crew.IsJoined ? "中継: 未接続" + (_crew.Message.Length > 0 ? $"（{_crew.Message}）" : "")
            : st == null || st.TrainNumber.Length == 0 ? "中継: 列番を決めると乗務に入ります"
            : $"中継: 列番 {st.TrainNumber}　運転士 {st.Drivers}人・車掌 {st.Conductors}人" + (st.Replaying != null ? $"　◆記録を再生中（{st.Replaying}）" : "");
        _statusLabel.Text = (_role == CrewRole.Conductor ? "【車掌】" : "【運転士】") + link;
        if (s is { IsDriver: false }) _statusLabel.Text += "　※ゲームを運転士モードにしてください";

        var game = _game.Latest;
        if (_role == CrewRole.Driver)
        {
            _notchLabel.Text = s == null ? "―" : Notch(s.Power, s.Brake, s.Reverser);
            _notchLabel.ForeColor = SystemColors.ControlText;
            _gapLabel.Text = st?.Conductors > 0 ? "車掌のゲームへノッチを送っています" : "車掌はいません（操作はサーバーに記録します）";
        }
        else
        {
            var input = _latestInput;
            var age = Environment.TickCount64 - Interlocked.Read(ref _latestInputMs);
            if (input == null)
            {
                _notchLabel.Text = "運転士の操作を待っています";
                _notchLabel.ForeColor = SystemColors.GrayText;
                _gapLabel.Text = "";
            }
            else
            {
                _notchLabel.Text = (_emergency ? "非常（操作が届きません）" : "運転士 " + Notch(input.Power, input.Brake, input.Reverser));
                _notchLabel.ForeColor = _emergency ? Color.Firebrick : age < StaleMs ? Color.ForestGreen : Color.DarkOrange;
                var gap = (game?.TraveledDistance ?? 0) - input.TraveledDistance;
                _gapLabel.Text = string.Create(CultureInfo.InvariantCulture,
                    $"運転士とのずれ {gap:+0.0;-0.0} m　速度 {s?.Speed ?? 0:0.0} / {input.Speed:0.0} km/h　届く間隔 {Interlocked.Read(ref _arrivalGapMs)} ms");
            }
        }

        _doorLabel.Text = DateTime.UtcNow < _doorUntil ? _doorText : s == null ? "" : s.AllClose ? "戸閉" : "ドア開";
        _replayButton.Enabled = st?.Replaying == null && st?.Drivers == 0;
        _stopReplayButton.Enabled = st?.Replaying != null;
    }

    // ---- 車掌の記録（CSV） ----

    private void WriteLog(DriverInput input, TciState s)
    {
        try
        {
            if (_log == null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GokkoClient", "crew");
                Directory.CreateDirectory(dir);
                _log = new StreamWriter(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeName(s.DiaName)}.csv"), false, new UTF8Encoding(true)) { AutoFlush = true };
                _log.WriteLine("時刻,届いた番号,運転士P,運転士B,運転士R,運転士速度,運転士距離,運転士残り,運転士戸閉,車掌P,車掌B,車掌速度,車掌距離,車掌残り,車掌戸閉");
            }

            var game = _game.Latest;
            _log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff},{input.Seq},{input.Power},{input.Brake},{input.Reverser},{input.Speed:0.00},{input.TraveledDistance:0.00},{input.NextStationDistance:0.00},{(input.AllClose ? 1 : 0)}," +
                $"{s.Power},{s.Brake},{s.Speed:0.00},{game?.TraveledDistance ?? 0:0.00},{s.NextStationDistance:0.00},{(s.AllClose ? 1 : 0)}"));
        }
        catch
        {
            CloseLog();
        }
    }

    private void CloseLog()
    {
        try { _log?.Dispose(); } catch { }
        _log = null;
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }
}
