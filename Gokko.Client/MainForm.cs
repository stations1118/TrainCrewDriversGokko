using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// ユーザークライアントの画面。
/// ・合言葉と列車番号を入れて接続する。接続のモード（特発だけ／在線を共有＝集中モード）を選ぶ
/// ・サーバーと TRAIN CREW の接続状態、ロープレ時刻、現在地を表示する
/// ・「特発」タブ: 自分のいる区間で特発が作動しているときだけ、理由と復帰ボタンを表示する
/// ・「進路」タブ（在線共有のみ）: この先の駅ごとの番線（選べる）と状態、出発要求ボタン
/// 0.5秒ごとに「ゲームの状態をサーバーへ送る → サーバーの指示をゲームへ反映する」を繰り返す。
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Ok = Color.ForestGreen;
    private static readonly Color Wait = Color.DarkOrange;
    private static readonly Color Ng = Color.Gray;

    private readonly ClientConfig _config;

    /// <summary>起動時に選んだモード（起動後は変えられない）。</summary>
    private readonly ShareMode _mode;
    private readonly GameLink _game = new();
    private readonly ServerLink _server = new();
    private readonly EmergencyLightSync _lights = new();
    private readonly RouteApplier _routes = new();
    private readonly SignalApplier _signals = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly Dictionary<string, int> _trackChoices = new();

    /// <summary>番線を選んだときの列車番号（列車が替わったら、選んだ番線を捨てる）。</summary>
    private string _choicesTrain = "";

    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly TextBox _trainBox = new() { PlaceholderText = "空欄ならシナリオの列番" };
    private readonly Button _confirmButton = new() { Text = "確定" };
    private readonly Button _connectButton = new() { Text = "接続" };
    private readonly Button _gameButton = new() { Text = "ゲーム切断" };
    private readonly Button _tabletButton = new() { Text = "乗務タブレット" };

    /// <summary>確定した列車番号（空ならシナリオの列番）。入力欄を書き換えただけでは変わらない。</summary>
    private string _confirmedTrain = "";

    /// <summary>ダイヤにあるか、サーバーに確かめた列車番号（同じ番号は1回だけ確かめる）。</summary>
    private string _checkedTrain = "";
    private bool _checking;
    private readonly Label _modeLabel = new();
    private readonly Label _serverLabel = new();
    private readonly Label _gameLabel = new();
    private readonly Label _infoLabel = new();
    private readonly Label _incidentLabel = new();
    private readonly Button _restoreButton = new() { Text = "復帰" };
    private readonly Label _messageLabel = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _routeTab = new() { Text = "進路" };
    private readonly DataGridView _planGrid = new();
    private readonly Button _departButton = new() { Text = "出発要求" };

    // 継続乗務: 押している間に運転画面を出ると、サーバーが最後の在線位置を5分間残す（同じ番線から次のシナリオを始めると、その列番で続ける）
    private readonly CheckBox _continueButton = new() { Text = "継続乗務", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _routeMessage = new();

    private IReadOnlyList<string>? _syncedCrossings;
    private bool _busy;
    private bool _closing;
    private bool _wasGameConnected;
    private string _lastScreen = "";
    private ShareMode? _modeApplied; // ゲームに送ったモード（他列車の表示・進路の手動）
    private DateTime _messageUntil = DateTime.MinValue;
    private string _planKey = "";
    private bool _updatingPlan; // 画面の更新で番線を書き換えている間は、ユーザーの選択として扱わない

    public MainForm(ClientConfig config, ShareMode mode)
    {
        _config = config;
        _mode = mode;
        Text = mode == ShareMode.Shared ? "特発共有クライアント（在線共有）" : "特発共有クライアント（特発のみ）";
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(480, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10f);

        _passwordBox.Text = _config.Password;
        _trainBox.Text = _config.TrainNumber;
        _confirmedTrain = _config.TrainNumber.Trim();

        _connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        _gameButton.Click += async (_, _) => await ToggleGameAsync();
        _tabletButton.Click += (_, _) => OpenTablet();
        _confirmButton.Click += async (_, _) => await ConfirmTrainAsync();
        _trainBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ConfirmTrainAsync();
        };
        _trainBox.TextChanged += (_, _) => UpdateTrainBoxColor();

        _incidentLabel.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _incidentLabel.TextAlign = ContentAlignment.MiddleLeft;
        _incidentLabel.Dock = DockStyle.Fill;

        _restoreButton.Font = new Font(Font.FontFamily, 22f, FontStyle.Bold);
        _restoreButton.FlatStyle = FlatStyle.Flat;
        _restoreButton.Dock = DockStyle.Fill;
        _restoreButton.Click += async (_, _) => await RestoreAsync();

        _departButton.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _departButton.Click += async (_, _) =>
        {
            _departButton.Enabled = false;
            ShowMessage(await _server.RequestDepartureAsync());
        };

        Controls.Add(BuildLayout());

        // ゲームからの返事（SetRoute の結果）はサーバーへ知らせる
        _game.ApiMessage += (title, message) =>
        {
            if (title == "SetRouteState" && _server.Mode == ShareMode.Shared) _server.SendRouteResult(message);
        };

        _timer.Tick += OnTick;
        _timer.Start();
        StartProtectionRadio();
        _game.Start();
        UpdateView(null);

        // 開発用: 環境変数 GOKKO_AUTOCONNECT=1 なら、起動時にそのまま接続する（GOKKO_PASSWORD。モードは GOKKO_MODE）
        if (Environment.GetEnvironmentVariable("GOKKO_AUTOCONNECT") == "1")
        {
            if (Environment.GetEnvironmentVariable("GOKKO_PASSWORD") is { } pw) _passwordBox.Text = pw;
            Shown += async (_, _) => await ToggleConnectionAsync();
        }
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control, float height = 32)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control);
        }

        void AddWide(Control control, SizeType type, float height)
        {
            layout.RowStyles.Add(new RowStyle(type, height));
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control);
            layout.SetColumnSpan(control, 2);
        }

        AddRow("合言葉", _passwordBox);
        var trainRow = new TableLayoutPanel { ColumnCount = 2, Margin = Padding.Empty };
        trainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        trainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _trainBox.Dock = DockStyle.Fill;
        _confirmButton.Dock = DockStyle.Fill;
        _confirmButton.Margin = new Padding(4, 2, 0, 2);
        trainRow.Controls.Add(_trainBox, 0, 0);
        trainRow.Controls.Add(_confirmButton, 1, 0);
        AddRow("列車番号", trainRow, 34);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _connectButton.Size = new Size(100, 30);
        _modeLabel.AutoSize = true;
        _modeLabel.Margin = new Padding(10, 7, 0, 0);
        _gameButton.Size = new Size(100, 30);
        _gameButton.Margin = new Padding(8, 3, 0, 3);
        _tabletButton.Size = new Size(110, 30);
        _tabletButton.Margin = new Padding(8, 3, 0, 3);
        buttons.Controls.AddRange([_connectButton, _gameButton, _tabletButton, _modeLabel]);
        AddWide(buttons, SizeType.Absolute, 40);

        foreach (var l in new[] { _serverLabel, _gameLabel, _infoLabel, _messageLabel })
        {
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
        }

        AddWide(_serverLabel, SizeType.Absolute, 26);
        AddWide(_gameLabel, SizeType.Absolute, 26);
        AddWide(_infoLabel, SizeType.Absolute, 26);

        // 特発タブ
        var incidentPage = new TabPage { Text = "特発", Padding = new Padding(6) };
        var incidentLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        incidentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        incidentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var incidentBox = new GroupBox { Text = "特発（当該区間に入ると表示）", Padding = new Padding(8), Dock = DockStyle.Fill };
        incidentBox.Controls.Add(_incidentLabel);
        incidentLayout.Controls.Add(incidentBox);
        incidentLayout.Controls.Add(_restoreButton);
        incidentPage.Controls.Add(incidentLayout);

        // 進路タブ
        _routeTab.Padding = new Padding(6);
        var routeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        routeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        _routeMessage.Dock = DockStyle.Fill;
        _routeMessage.TextAlign = ContentAlignment.MiddleLeft;
        _routeMessage.AutoEllipsis = true;
        routeLayout.Controls.Add(_routeMessage);
        BuildPlanGrid();
        routeLayout.Controls.Add(_planGrid);
        _departButton.Dock = DockStyle.Fill;
        _continueButton.Dock = DockStyle.Fill;
        _continueButton.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _continueButton.CheckedChanged += (_, _) =>
        {
            _continueButton.BackColor = _continueButton.Checked ? Color.DarkOrange : SystemColors.Control;
            _continueButton.ForeColor = _continueButton.Checked ? Color.White : SystemColors.ControlText;
            ShowMessage(_continueButton.Checked
                ? "継続乗務: このまま運転画面を出ると、在線位置を5分間残します。同じ番線から次のシナリオを始めると、この列番で続けます"
                : "継続乗務を取り消しました");
        };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        bottom.Controls.Add(_departButton, 0, 0);
        bottom.Controls.Add(_continueButton, 1, 0);
        routeLayout.Controls.Add(bottom);
        _routeTab.Controls.Add(routeLayout);

        _tabs.TabPages.Add(incidentPage);
        _tabs.TabPages.Add(_routeTab);
        AddWide(_tabs, SizeType.Percent, 100);
        AddWide(_messageLabel, SizeType.Absolute, 26);
        return layout;
    }

    private void BuildPlanGrid()
    {
        _planGrid.Dock = DockStyle.Fill;
        _planGrid.AllowUserToAddRows = false;
        _planGrid.AllowUserToDeleteRows = false;
        _planGrid.AllowUserToResizeRows = false;
        _planGrid.RowHeadersVisible = false;
        _planGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _planGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _planGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Station", HeaderText = "駅", ReadOnly = true, FillWeight = 22 });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stop", HeaderText = "停車", ReadOnly = true, FillWeight = 14 });
        _planGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Track", HeaderText = "番線", FillWeight = 18, FlatStyle = FlatStyle.Flat });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "決め方", ReadOnly = true, FillWeight = 14 });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状態", ReadOnly = true, FillWeight = 50 });
        _planGrid.DataError += (_, e) => e.ThrowException = false;
        _planGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_planGrid.IsCurrentCellDirty) _planGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // 番線の欄の上でマウスのホイールを回しても、番線が変わらないようにする
        // （クリックして選択中になった欄は、ホイールで値が変わり、そのまま「選んだ番線」として送られてしまうため）
        _planGrid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is not ComboBox combo) return;
            combo.MouseWheel -= IgnoreWheel;
            combo.MouseWheel += IgnoreWheel;
        };
        _planGrid.CellValueChanged += (_, e) =>
        {
            if (_updatingPlan || e.RowIndex < 0 || _planGrid.Columns[e.ColumnIndex].Name != "Track") return;
            var row = _planGrid.Rows[e.RowIndex];
            if (row.Tag is not string stationId || row.Cells["Track"].Value is not string text) return;
            if (text.EndsWith("番線") && int.TryParse(text[..^2], out var t)) _trackChoices[stationId] = t;
        };
    }

    // =====================================================================================
    // 接続・モード
    // =====================================================================================

    private async Task ToggleConnectionAsync()
    {
        _connectButton.Enabled = false;
        try
        {
            if (_server.Status == ServerStatus.Stopped || _server.Status == ServerStatus.Rejected)
            {
                _config.Password = _passwordBox.Text;
                _confirmedTrain = _trainBox.Text.Trim(); // 接続するときは、入力欄の列番で確定する
                _config.TrainNumber = _confirmedTrain;
                _config.Save();
                _checkedTrain = "";
                UpdateTrainBoxColor();
                _routes.Reset();
                _trackChoices.Clear();
                _server.Start(ServerAddress.Current, _config.Password, _mode);
            }
            else
            {
                // 運転会から抜けたら、特発・進路・信号を戻す（他列車の非表示と進路の手動は、アプリを閉じるまで続ける）
                await _server.StopAsync();
                await ClearSharedStateAsync();
            }
        }
        finally
        {
            _connectButton.Enabled = true;
        }
    }

    /// <summary>
    /// 在線共有モードの間は、「他列車の非表示」と「進路の手動」を常に入れる
    /// （TRAIN CREW に接続したとき、メニュー・読み込み画面に入ったとき、読み込みが終わって運転画面に入ったときに送る）。
    /// </summary>
    private async Task ApplyGameModesAsync(GameState? game, bool connected)
    {
        // サーバーへの接続を待たない（シナリオを始める前に他列車を消すため）
        var shared = _mode == ShareMode.Shared;
        var screen = connected ? game?.Screen ?? "" : "";
        // メニュー・読み込み画面に入ったときと、そこから運転画面に入ったとき（シナリオの読み込みが終わったとき）に送る。
        // シナリオを読み込むと、ゲームが進路の自動に戻っていることがあるため。
        // 一時停止からの再開では送らない。route_manual を送り直すと、ゲームは設定済みの進路を消すため
        var screenChanged = screen != _lastScreen
                            && (screen is "Menu" or "MainGame_Loading"
                                || screen == "MainGame" && _lastScreen is not ("MainGame_Pause" or ""));
        var reconnected = connected && !_wasGameConnected;
        // シナリオを閉じた（運転画面からメニュー・タイトル・結果画面へ）: ゲームとの接続を張り直し、送った進路・信号の記録を捨てる。
        // ゲーム側に溜まった要求や古い進路の記録で、必要な進路が送られなくなるのを防ぐ（次のシナリオでは、まっさらな状態から始める）
        var closedScenario = _lastScreen is "MainGame" or "MainGame_Pause" && screen is "Menu" or "Title" or "Result";
        if (closedScenario) _game.Reconnect();
        if (closedScenario || reconnected)
        {
            _routes.Reset();
            _signals.Reset();
        }
        _wasGameConnected = connected;
        _lastScreen = screen;
        if (!connected)
        {
            _modeApplied = null;
            _onemanApplied = false;
            return;
        }

        try
        {
            // ワンマン運転の強制（共有のモードによらず）。ユーザーの設定漏れがあっても、ゲームをワンマンにする
            if (!_onemanApplied || screenChanged || reconnected)
            {
                await SendOnemanAsync(true);
                _onemanApplied = true;
            }

            if (shared && (_modeApplied != ShareMode.Shared || screenChanged || reconnected))
            {
                await _game.SendCommandAsync("mode_req", ["hide_other"]);
                await _game.SendCommandAsync("mode_req", ["route_manual"]);
                _modeApplied = ShareMode.Shared;
            }
        }
        catch
        {
            // 次の周期で送り直す
        }
    }

    /// <summary>切断したとき: 作動させた特発、自列車の進路、落とした信号を戻す。</summary>
    private async Task ClearSharedStateAsync()
    {
        await _lights.ClearAllAsync(_game);
        if (_mode != ShareMode.Shared) return;
        await _routes.ClearAllAsync(_game, EffectiveTrainNumber(_game.Latest));
        await _signals.ClearAllAsync(_game);
    }

    private bool _onemanApplied;

    /// <summary>
    /// ワンマン運転の強制指定（ゲームの API の mode 要求 forceoneman_on / forceoneman_off）。
    /// 資料（TrainCrewControlDesk）の書き方は mode_request だが、このソフトが使っている mode_req でも送る（どちらか片方が効けばよい）。
    /// </summary>
    private async Task SendOnemanAsync(bool on)
    {
        var arg = on ? "forceoneman_on" : "forceoneman_off";
        await _game.SendCommandAsync("mode_req", [arg]);
        await _game.SendCommandAsync("mode_request", [arg]);
    }

    /// <summary>在線共有モードで変えたゲームの設定を元に戻す（進路・信号・他列車の表示・進路の自動）。</summary>
    private async Task LeaveGameModesAsync()
    {
        await ClearSharedStateAsync();
        if (_modeApplied != ShareMode.Shared) return;
        try
        {
            if (_game.IsConnected)
            {
                await _game.SendCommandAsync("mode_req", ["route_auto"]);
                await _game.SendCommandAsync("mode_req", ["show_other"]);
            }
        }
        catch
        {
            // 接続が切れていれば送れない
        }

        _modeApplied = null;
    }

    /// <summary>
    /// TRAIN CREW との接続を手で切る・つなぎ直す。切るときは、このソフトが変えたゲームの設定（進路・信号・他列車の表示・進路の自動・ワンマン）を戻してから切る。
    /// つなぎ直すと、まっさらな状態から送り直す
    /// </summary>
    private async Task ToggleGameAsync()
    {
        _gameButton.Enabled = false;
        try
        {
            if (!_game.Stopped)
            {
                await LeaveGameModesAsync();
                if (_onemanApplied && _game.IsConnected)
                    try { await SendOnemanAsync(false); } catch { }
                _game.Stop();
                _onemanApplied = false;
                _routes.Reset();
                _signals.Reset();
                ShowMessage("TRAIN CREW との接続を切りました（ゲームの設定は元に戻しました）");
            }
            else
            {
                _routes.Reset();
                _signals.Reset();
                _game.Start();
                ShowMessage("TRAIN CREW につなぎ直します");
            }
        }
        finally
        {
            _gameButton.Text = _game.Stopped ? "ゲーム接続" : "ゲーム切断";
            _gameButton.Enabled = true;
        }
    }

    /// <summary>乗務タブレット（サーバーの tablet.html）を、自分の列番を付けてブラウザで開く。列番がなければ、列車を選ぶ画面を開く。</summary>
    private void OpenTablet()
    {
        var train = EffectiveTrainNumber(_game.Latest);
        var url = ServerAddress.Current.TrimEnd('/') + "/tablet.html" + (train.Length > 0 ? "?train=" + Uri.EscapeDataString(train) : "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowMessage("ブラウザを開けませんでした: " + url);
        }
    }

    private async Task RestoreAsync()
    {
        _restoreButton.Enabled = false;
        var result = await _server.RestoreAsync();
        ShowMessage(result.Message);
    }

    // =====================================================================================
    // 毎周期の処理
    // =====================================================================================

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        _busy = true;
        try
        {
            var game = _game.Latest;
            var gameConnected = _game.IsConnected;
            _game.PlayerTrainNumber = _confirmedTrain;
            var trainNumber = EffectiveTrainNumber(game);
            var shared = _mode == ShareMode.Shared;

            // 列車が替わった・運転画面を出た（シナリオを開き直した）ら、前の列車で選んだ番線は捨てる
            // （残っていると、次の列車の番線がダイヤや所定ではなく、前の選択になってしまう）
            var driving = gameConnected && game?.InGame == true;
            if (trainNumber != _choicesTrain || !driving)
            {
                _choicesTrain = trainNumber;
                _trackChoices.Clear();
            }

            // 使う列番（確定した列番、空ならシナリオの列番）が変わったら、ダイヤにあるか確かめる
            if (shared && _server.IsJoined && trainNumber.Length > 0 && trainNumber != _checkedTrain && !_checking)
                _ = CheckTrainAsync(trainNumber, quietWhenFound: true);

            await ApplyGameModesAsync(game, gameConnected);

            // ゲームの状態をサーバーへ（応答は待たない）
            if (_server.IsJoined)
            {
                _ = _server.ReportAsync(CreateReport(game, gameConnected, trainNumber));
            }

            // サーバーの指示をゲームへ
            var state = _server.State;
            if (!ReferenceEquals(_syncedCrossings, _server.Crossings))
            {
                _syncedCrossings = _server.Crossings;
                _lights.SetCrossings(_syncedCrossings);
            }

            await _lights.TickAsync(_game, state == null ? null : new HashSet<string>(state.ActiveCrossings));
            if (shared && _server.IsJoined && _modeApplied == ShareMode.Shared)
            {
                await _routes.TickAsync(_game, state, trainNumber);
                await _signals.TickAsync(_game, state);
            }

            UpdateView(state);
        }
        finally
        {
            _busy = false;
        }
    }

    private string EffectiveTrainNumber(GameState? game)
    {
        return _confirmedTrain.Length > 0 ? _confirmedTrain : game?.DiaName ?? "";
    }

    /// <summary>「確定」ボタン: 入力欄の列番を使う列番にする（空ならシナリオの列番）。ダイヤになければ知らせる。</summary>
    private async Task ConfirmTrainAsync()
    {
        _confirmedTrain = _trainBox.Text.Trim();
        _config.TrainNumber = _confirmedTrain;
        _config.Save();
        UpdateTrainBoxColor();
        var train = EffectiveTrainNumber(_game.Latest);
        if (train.Length == 0)
        {
            ShowMessage("列番を確定しました（シナリオの列番を使います。ゲームに接続すると分かります）");
            return;
        }

        if (_mode != ShareMode.Shared || !_server.IsJoined)
        {
            ShowMessage($"列番を {train} に確定しました" + (_mode == ShareMode.Shared ? "（サーバーに接続すると、ダイヤにあるか確かめます）" : ""));
            _checkedTrain = "";
            return;
        }

        await CheckTrainAsync(train, quietWhenFound: false);
    }

    /// <summary>ダイヤにある列番か、サーバーに確かめる。なければ「種別進路で構成します」と知らせる。</summary>
    private async Task CheckTrainAsync(string train, bool quietWhenFound)
    {
        _checking = true;
        try
        {
            var result = await _server.CheckTrainAsync(train);
            if (result == null) return; // 送れなければ、次の周期で確かめ直す
            _checkedTrain = train;
            if (result.Found)
            {
                if (!quietWhenFound) ShowMessage(result.Message);
                return;
            }

            ShowMessage(result.Message);
            BeginInvoke(() => MessageBox.Show(this, result.Message, "列車番号", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>入力欄を書き換えて、まだ確定していなければ色を付ける。</summary>
    private void UpdateTrainBoxColor()
    {
        var pending = _trainBox.Text.Trim() != _confirmedTrain;
        _trainBox.BackColor = pending ? Color.FromArgb(255, 245, 205) : SystemColors.Window;
        _confirmButton.BackColor = pending ? Color.Gold : SystemColors.Control;
    }

    // =====================================================================================
    // 表示
    // =====================================================================================

    private void UpdateView(ClientState? state)
    {
        var running = _server.Status is ServerStatus.Connecting or ServerStatus.Connected;
        _connectButton.Text = running ? "切断" : "接続";
        _passwordBox.Enabled = !running;
        (_modeLabel.Text, _modeLabel.ForeColor) = _mode == ShareMode.Shared
            ? ("モード: 在線を共有（集中モード）", Color.Firebrick)
            : ("モード: 特発だけを共有", Color.SteelBlue);

        (_serverLabel.Text, _serverLabel.ForeColor) = _server.Status switch
        {
            ServerStatus.Connected => ($"● サーバー: 接続済み（{state?.PlayerCount ?? 0}人）", Ok),
            ServerStatus.Connecting => ("● サーバー: 接続中…" + (_server.Message.Length > 0 ? $"（{_server.Message}）" : ""), Wait),
            ServerStatus.Rejected => ($"● サーバー: 拒否されました（{_server.Message}）", Color.Firebrick),
            _ => ("● サーバー: 未接続", Ng)
        };

        var game = _game.Latest;
        if (_game.Stopped)
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 切断中（「ゲーム接続」でつなぎ直します）", Ng);
        else if (!_game.IsConnected)
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 未接続", Ng);
        else if (game?.InGame == true)
            (_gameLabel.Text, _gameLabel.ForeColor) = ($"● TRAIN CREW: 接続済み（運転中 {EffectiveTrainNumber(game)}）", Ok);
        else
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 接続済み（運転画面の外）", Wait);

        _infoLabel.Text = $"ロープレ時刻 {state?.GameTime ?? "--:--:--"}　現在地 {state?.Position ?? "不明"}"
                          + (state?.ProtectionRadioSending == true ? "　◆防護無線 発報中（F5 で解除）" : "");

        // 特発
        var visible = state?.VisibleIncidents ?? new List<IncidentView>();
        if (visible.Count > 0)
        {
            _incidentLabel.ForeColor = Color.Firebrick;
            _incidentLabel.Text = string.Join("\n", visible.Select(v =>
                $"◆ {v.Crossing}\n　{v.Reason}" + (v.StartedGameTime != null ? $"（{v.StartedGameTime}）" : "")));
        }
        else
        {
            _incidentLabel.ForeColor = SystemColors.GrayText;
            _incidentLabel.Text = "この区間に、作動中の特発はありません";
        }

        var canRestore = visible.Count > 0 && _server.IsJoined;
        _restoreButton.Enabled = canRestore;
        _restoreButton.BackColor = canRestore ? Color.Firebrick : SystemColors.Control;
        _restoreButton.ForeColor = canRestore ? Color.White : SystemColors.GrayText;

        // 進路
        var shared = _server.IsJoined && _server.Mode == ShareMode.Shared;
        _routeTab.Enabled = shared;
        if (!shared)
        {
            _routeMessage.Text = "進路は、在線を共有するモードで接続したときに使えます";
            _planGrid.Rows.Clear();
            _planKey = "";
            _departButton.Enabled = false;
        }
        else
        {
            // ダイヤ運転中は、列車から進路に触れない（番線はダイヤどおり。選んだ番線も送らない）
            _diaLock = state?.DiaMode == true;
            if (_diaLock) _trackChoices.Clear();
            _routeMessage.Text = state?.RouteMessage ?? (_diaLock ? "ダイヤ運転中は、番線・進路はダイヤどおりに自動で扱います（列車からは変えられません）"
                : state?.Plan.Count > 0 ? "番線は、手前の駅を通過するまで選べます" : "この先に、進路を扱う駅はありません");
            UpdatePlan(state?.Plan ?? new List<StationPlanView>());
            _departButton.Enabled = state?.CanRequestDeparture == true;
            _departButton.Text = state?.DepartureLabel ?? "出発要求";
            _departButton.BackColor = _departButton.Enabled ? Color.SeaGreen : SystemColors.Control;
            _departButton.ForeColor = _departButton.Enabled ? Color.White : SystemColors.GrayText;
        }

        // ダイヤ運転中は継続乗務を常に入（ボタンは押せない）。フリー運転に戻ったら、元の入・切に戻す
        var dia = shared && state?.DiaMode == true;
        _ = SyncGameClockAsync(dia, state?.GameTime);
        if (dia != _diaShown)
        {
            _diaShown = dia;
            if (dia) { _continueBefore = _continueButton.Checked; _continueButton.Checked = true; }
            else _continueButton.Checked = _continueBefore;
            _continueButton.Enabled = !dia;
        }

        if (DateTime.UtcNow > _messageUntil) _messageLabel.Text = "";
    }

    private bool _diaShown;
    private bool _diaLock; // ダイヤ運転中（番線を選べない）
    private bool _continueBefore;
    private string _clockSyncKey = "";

    /// <summary>
    /// ダイヤ運転中は、ゲームの時計をロープレ時刻に合わせる（実時間モード ＋ 時刻の補正 = ロープレ時刻 − このPCの今の時刻）。
    /// 補正が10秒以上変わったとき・ゲームにつなぎ直したときに送り直す。フリー運転に戻ったら実時間モードを外す。
    /// コマンドの書き方は資料（TrainCrewControlDesk）の mode_request と、このソフトの mode_req の両方で送る
    /// </summary>
    private async Task SyncGameClockAsync(bool dia, string? gameTime)
    {
        if (!_game.IsConnected) { _clockSyncKey = ""; return; }
        string key;
        int h = 0, m = 0, sec = 0;
        if (dia && TimeSpan.TryParse(gameTime, out var roleplay))
        {
            var offset = roleplay - DateTime.Now.TimeOfDay;
            var total = (int)Math.Round(offset.TotalSeconds);
            if (total > 43200) total -= 86400;
            if (total < -43200) total += 86400;
            (h, m, sec) = (total / 3600, total / 60 % 60, total % 60);
            key = $"on:{Math.Round(total / 10.0)}";
        }
        else key = "off";
        if (key == _clockSyncKey) return;
        var wasOn = _clockSyncKey.StartsWith("on");
        _clockSyncKey = key;
        try
        {
            if (key == "off")
            {
                if (!wasOn) return;
                await _game.SendCommandAsync("mode_req", ["realtimemode_off"]);
                await _game.SendCommandAsync("mode_request", ["realtimemode_off"]);
                return;
            }

            await _game.SendCommandAsync("mode_req", ["realtimemode_on"]);
            await _game.SendCommandAsync("mode_request", ["realtimemode_on"]);
            await _game.SendCommandAsync("realtime_offset", [h.ToString(), m.ToString(), sec.ToString()]);
        }
        catch
        {
            _clockSyncKey = ""; // 次の周期で送り直す
        }
    }

    private static void IgnoreWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    private void UpdatePlan(List<StationPlanView> plan)
    {
        _updatingPlan = true;
        try
        {
            UpdatePlanCore(plan);
        }
        finally
        {
            _updatingPlan = false;
        }
    }

    private void UpdatePlanCore(List<StationPlanView> plan)
    {
        var key = (_diaLock ? "dia|" : "") + string.Join("|", plan.Select(p => $"{p.StationId}:{string.Join(",", p.TrackOptions)}:{p.Locked}:{p.Track == null}"));
        if (key != _planKey)
        {
            _planKey = key;
            _planGrid.Rows.Clear();
            foreach (var p in plan)
            {
                var row = new DataGridViewRow { Tag = p.StationId };
                row.CreateCells(_planGrid);
                var cell = (DataGridViewComboBoxCell)row.Cells[_planGrid.Columns["Track"]!.Index];
                if (p.Track == null) cell.Items.Add("通過");
                foreach (var t in p.TrackOptions) cell.Items.Add($"{t}番線");
                row.Cells[_planGrid.Columns["Track"]!.Index].ReadOnly = _diaLock || p.Locked || p.TrackOptions.Count == 0 || p.Track == null;
                _planGrid.Rows.Add(row);
            }

            // 作り直したあとは、どの欄も選択中にしない（選択中の欄が残ると、その欄にサーバーの番線を表示できず、
            // 空のままの欄から意図しない番線が送られることがある）
            _planGrid.CurrentCell = null;
        }

        for (var i = 0; i < plan.Count && i < _planGrid.Rows.Count; i++)
        {
            var p = plan[i];
            var row = _planGrid.Rows[i];
            row.Cells["Station"].Value = p.Name;
            row.Cells["Stop"].Value = p.StopType;
            row.Cells["Source"].Value = p.TrackSource + (p.Locked ? "・確定" : "");
            row.Cells["Status"].Value = p.Status;
            var trackText = p.Track == null ? "通過" : $"{p.Track}番線";
            var trackCell = row.Cells["Track"];
            if (!(_planGrid.CurrentCell == trackCell && _planGrid.IsCurrentCellInEditMode) && !Equals(trackCell.Value, trackText))
                trackCell.Value = trackText;
            row.DefaultCellStyle.BackColor = p.Status.Contains("待") ? Color.FromArgb(255, 245, 225)
                : p.Status.Contains("開通") || p.Status == "進路確定" ? Color.FromArgb(232, 245, 233) : SystemColors.Window;
        }
    }

    private void ShowMessage(string message)
    {
        _messageLabel.Text = message;
        _messageUntil = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>サーバーへ送る自分の状態（毎周期の報告と、防護無線の発報で使う）。</summary>
    private ClientReport CreateReport(GameState? game, bool gameConnected, string trainNumber)
    {
        var shared = _mode == ShareMode.Shared;
        var report = new ClientReport
        {
            TrainNumber = trainNumber,
            TrainCrewConnected = gameConnected,
            InGame = gameConnected && game?.InGame == true,
            TrackCircuits = gameConnected && game != null ? game.TrackCircuits : new List<string>(),
            ContinueDuty = shared && _continueButton.Checked,
            TrainNumberTyped = _confirmedTrain.Length > 0 // 入力欄の列番か（ダイヤ運転中は手入力が要る）
        };
        if (shared && gameConnected && game != null)
        {
            report.Stops = game.Stops;
            report.TraveledDistance = game.TraveledDistance;
            report.NextStationDistance = game.NextStationDistance;
            report.Speed = game.Speed;
            report.GameRoutes = (game.Routes ?? new()).Select(r => new RouteKey { Interlocking = r.Interlock, RouteName = r.Route }).ToList();
            report.TrackChoices = new Dictionary<string, int>(_trackChoices);
        }

        return report;
    }

    // =====================================================================================
    // 防護無線（F5 で発報・解除。受信範囲にいる間は警報音を繰り返し鳴らす）
    // =====================================================================================

    private ProtectionRadioAudio? _radioAudio;
    private readonly System.Windows.Forms.Timer _radioAudioTimer = new() { Interval = 100 };
    private bool _radioToggling;
    private bool _radioHotKeyRegistered;

    private void StartProtectionRadio()
    {
        try { _radioAudio = new ProtectionRadioAudio(); }
        catch { ShowMessage("防護無線の警報音を読み込めませんでした（警報は鳴りません）"); }
        _radioAudioTimer.Tick += (_, _) => UpdateRadioAudio();
        _radioAudioTimer.Start();
        Shown += (_, _) =>
        {
            if (!_radioHotKeyRegistered)
                ShowMessage("防護無線の F5 キーを登録できません。F5 を使う他のアプリ（別の共有クライアントなど）を閉じて起動し直してください");
        };
    }

    // 受信範囲にいて、サーバーとゲームにつながっていて運転画面にいる間だけ鳴らす（配信が5秒止まったら止める）
    private void UpdateRadioAudio() =>
        _radioAudio?.Update(!_closing && _server.IsJoined && _server.HasFreshState && _game.IsConnected
                            && _game.Latest?.InGame == true && _server.State?.ProtectionRadioReceiving == true);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _radioHotKeyRegistered = ProtectionRadioHotKey.Register(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_radioHotKeyRegistered) ProtectionRadioHotKey.Unregister(Handle);
        _radioHotKeyRegistered = false;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ProtectionRadioHotKey.Message && m.WParam.ToInt32() == ProtectionRadioHotKey.Id)
        {
            _ = ToggleProtectionRadioAsync();
            return;
        }

        base.WndProc(ref m);
    }

    private async Task ToggleProtectionRadioAsync()
    {
        if (_closing || _radioToggling || !_server.IsJoined) return;
        _radioToggling = true;
        try
        {
            // 押した時点の位置を一緒に送る。解除は、ゲームから外れていてもできる
            var game = _game.Latest;
            var report = CreateReport(game, _game.IsConnected, EffectiveTrainNumber(game));
            if (!await _server.ToggleProtectionRadioAsync(report))
            {
                System.Media.SystemSounds.Beep.Play();
                ShowMessage("防護無線を発報できませんでした（運転画面で、路線の上にいるときに押してください）");
            }
        }
        finally
        {
            _radioToggling = false;
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            _radioAudioTimer.Stop();
            _radioAudio?.Update(false);
            // 作動させた特発・進路・信号・他列車の表示を戻してから閉じる
            e.Cancel = true;
            _closing = true;
            _timer.Stop();
            _config.TrainNumber = _confirmedTrain;
            _config.Save();
            await _server.StopAsync();
            await LeaveGameModesAsync();
            // ワンマンの強制も外す（このソフトを使わずに遊ぶときは、ゲームの設定どおりに）
            if (_onemanApplied && _game.IsConnected)
                try { await SendOnemanAsync(false); } catch { }
            _game.Stop();
            Close();
            return;
        }

        base.OnFormClosing(e);
    }
}
