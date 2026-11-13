using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Gokko.Client;

/// <summary>ゲームから読み取った、このクライアントに必要な情報。</summary>
/// <param name="TrackCircuits">自列車が踏んでいる軌道回路だけ（他の列車の分は含まない）。</param>
/// <param name="Screen">gameScreen の名前（MainGame、Menu など）。項目がなければ空。</param>
/// <param name="Stops">自列車の停車駅（stationList から。時刻は読まない）。</param>
/// <param name="Routes">ゲームに今設定されている進路（連動装置名・進路名・列車名）。進路データが届いていなければ null。</param>
public sealed record GameState(
    List<string> TrackCircuits,
    bool InGame,
    string Screen,
    string DiaName,
    List<Gokko.Common.StopInfo> Stops,
    float TraveledDistance,
    float NextStationDistance,
    float Speed,
    List<(string Interlock, string Route, string Train)>? Routes,
    string TrainClass);

/// <summary>
/// 自分のPCの TRAIN CREW（ws://127.0.0.1:50300/）との接続。
/// ・0.5秒ごとにデータ要求を送り、自列車が踏んでいる軌道回路・画面・ダイヤ名を読み取る
/// ・SetEmergencyLight（特発）や mode_req（他列車非表示）などのコマンドを送る
/// 切断されたら自動で再接続する。
/// </summary>
public sealed class GameLink
{
    public const string GameUri = "ws://127.0.0.1:50300/";

    private static readonly string[] DataRequestArgs = ["tconlyontrain"];
    private static readonly string[] InterlockRequestArgs = ["interlock"];

    private static readonly string[] ScreenNames =
        ["MainGame", "MainGame_Pause", "MainGame_Loading", "Menu", "Result", "Title", "Other", "NotRunningGame"];

    private static readonly JsonSerializerOptions SendJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private volatile ClientWebSocket? _ws;
    private volatile GameState? _latest;
    private volatile string _playerTrainNumber = "";
    private string _prevPlayer = ""; // 自列車の回路が見つかった、いちばん新しいダイヤ名（列番が変わった直後の照合に使う）

    public GameState? Latest => _latest;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    /// <summary>ゲームからの返事（APIMessage の title と message）。別スレッドから呼ばれる。</summary>
    public event Action<string, string>? ApiMessage;

    /// <summary>
    /// 自列車の列車番号（確定した入力欄の値）。ゲームのダイヤ名が取れないときだけ、
    /// 軌道回路の「最後に踏んだ列車名」との照合に使う（ふだんはダイヤ名で照合する）。
    /// </summary>
    public string PlayerTrainNumber
    {
        get => _playerTrainNumber;
        set => _playerTrainNumber = value ?? "";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>手で切ったか（<see cref="Stop"/> のあと、<see cref="Start"/> するまで）。</summary>
    public bool Stopped => _cts == null;

    /// <summary>
    /// ゲームとの接続を張り直す（今の接続を切ると、3秒後に自動でつなぎ直す）。
    /// ゲーム側（外部入出力）に溜まった要求や、進路一覧の送り方の状態を、まっさらにするため
    /// </summary>
    public void Reconnect()
    {
        try { _ws?.Abort(); } catch { }
    }

    /// <summary>踏切の特発を作動（true）または解除（false）する。</summary>
    public Task SetEmergencyLightAsync(string crossingName, bool on)
    {
        return SendCommandAsync("SetEmergencyLight", [crossingName, on ? "true" : "false"]);
    }

    /// <summary>任意のコマンドを送る。未接続なら例外。</summary>
    public Task SendCommandAsync(string command, string[] args)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("TRAIN CREW に接続していません");

        Trace(command, args);
        return SendAsync(ws, command, args, CancellationToken.None);
    }

    // 開発用: 環境変数 GOKKO_TRACE にファイル名があれば、送ったコマンドを書き出す（データ要求は除く）
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("GOKKO_TRACE");

    private static void Trace(string command, string[] args)
    {
        if (string.IsNullOrEmpty(TracePath)) return;
        try
        {
            File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {command} [{string.Join(",", args)}]{Environment.NewLine}");
        }
        catch
        {
            // 書けなくても動作には影響しない
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(GameUri), ct);
                _ws = ws;
                await RunSessionAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 接続できない・切れたときは、少し待って再接続する
            }
            finally
            {
                _ws = null;
                _latest = null;
            }

            try
            {
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poll = PollAsync(ws, pollCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        finally
        {
            pollCts.Cancel();
            await poll;
        }
    }

    private async Task PollAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            var n = 0;
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await SendAsync(ws, "DataRequest", DataRequestArgs, ct);

                // 進路の一覧（連動装置のデータ）は、「interlock」だけを単独で要求しないと、ゲームが送ってこない
                // （ほかの項目と一緒に要求しても有効にならない。一度有効にすると、ゲームを起動し直すまで続く）。
                // 届かないと、サーバーはゲームの進路が見えず、開通の確認も取消もできない。
                // 接続の直後と、そのあと10秒ごとに要求する（シナリオの読み込みなどで止まっても、すぐ戻るように）
                if (n++ % 20 == 0) await SendAsync(ws, "DataRequest", InterlockRequestArgs, ct);
                await Task.Delay(500, ct);
            }
        }
        catch (Exception)
        {
            // 切断・取消は受信側で扱う
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            try
            {
                var state = Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                if (state != null) _latest = state;
            }
            catch (JsonException)
            {
                // 状態ではないメッセージは無視する
            }
        }
    }

    private async Task SendAsync(ClientWebSocket ws, string command, string[] args, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { command, args }, SendJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---- 受信データの解釈（項目名は大文字小文字を区別せず、足りない項目は空として扱う） ----

    private GameState? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var data = Prop(root, "data") is { ValueKind: JsonValueKind.Object } d ? d : root;

        // コマンドへの返事（SetRouteState など）
        if (Str(Prop(root, "type")) == "APIMessage")
        {
            ApiMessage?.Invoke(Str(Prop(data, "title")), Str(Prop(data, "message")));
            return null;
        }

        var my = Prop(data, "myTrainData");
        var circuitList = Prop(data, "trackCircuitList");
        if (my is not { ValueKind: JsonValueKind.Object } && circuitList is not { ValueKind: JsonValueKind.Array })
            return null;

        var diaName = my is { ValueKind: JsonValueKind.Object } m ? Str(Prop(m, "diaName")) : "";
        // 自列車の位置は、ゲームの列車名（ダイヤ名）で探す。列番を手入力していても、軌道回路の Last はゲームの列車名のため
        // （手入力の列番で探すと、位置が分からない）
        var player = NormalizeName(string.IsNullOrWhiteSpace(diaName) ? _playerTrainNumber : diaName);

        // 踏まれている軌道回路と、その回路を最後に踏んだ列車名（Last）。プレイヤー以外の列車の分も含まれる。
        var occupied = new List<(string Circuit, string Last)>();
        if (circuitList is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = Str(Prop(item, "Name"));
                var on = Prop(item, "On") is { ValueKind: JsonValueKind.True };
                if (on && name.Length > 0) occupied.Add((name, Str(Prop(item, "Last"))));
            }
        }

        // 自列車の名前と一致する列車が踏んでいる軌道回路だけを、プレイヤーの位置とする。
        // 途中駅で列番が変わる（ゲームが 1178 → 回1178 にダイヤ名を変える）と、回路の Last は列車が次の回路に動くまで前の名前のまま。
        // 新しい名前の回路が出てくるまでは、ひとつ前のダイヤ名でも探す（止まっている間に在線が消えて、進路が取れなくなるのを防ぐ）
        var identified = player.Length > 0 && occupied.Any(o => o.Last.Length > 0);
        var circuits = new List<string>();
        if (identified)
        {
            circuits = occupied.Where(o => NormalizeName(o.Last) == player).Select(o => o.Circuit).Distinct().ToList();
            if (circuits.Count == 0 && _prevPlayer.Length > 0 && _prevPlayer != player)
                circuits = occupied.Where(o => NormalizeName(o.Last) == _prevPlayer).Select(o => o.Circuit).Distinct().ToList();
            else if (circuits.Count > 0)
                _prevPlayer = player;
        }

        var screen = ScreenName(Prop(data, "gameScreen"));
        var inGame = screen.Length == 0 || screen is "MainGame" or "MainGame_Pause";

        // 停車駅（時刻は使わない）・距離・速度
        var stops = new List<Gokko.Common.StopInfo>();
        float traveled = 0, nextDist = 0, speed = 0;
        if (my is { ValueKind: JsonValueKind.Object } mt)
        {
            if (Prop(mt, "stationList") is { ValueKind: JsonValueKind.Array } sl)
                foreach (var s in sl.EnumerateArray())
                    stops.Add(new Gokko.Common.StopInfo { Name = Str(Prop(s, "Name")), StopType = Str(Prop(s, "stopType")), Distance = Num(Prop(s, "TotalLength")) });
            traveled = Num(Prop(mt, "TotalLength"));
            nextDist = Num(Prop(mt, "nextStaDistance"));
            speed = Num(Prop(mt, "Speed"));
        }

        // ゲームに今設定されている進路
        List<(string, string, string)>? routes = null;
        if (Prop(data, "interlockDataList") is { ValueKind: JsonValueKind.Array } il)
        {
            routes = new List<(string, string, string)>();
            foreach (var i in il.EnumerateArray())
            {
                var ilName = Str(Prop(i, "Name"));
                if (Prop(i, "routes") is not { ValueKind: JsonValueKind.Array } rs) continue;
                foreach (var r in rs.EnumerateArray()) routes.Add((ilName, Str(Prop(r, "Name")), Str(Prop(r, "trainName"))));
            }
        }

        var trainClass = my is { ValueKind: JsonValueKind.Object } mc ? Str(Prop(mc, "Class")) : "";
        return new GameState(circuits, inGame, screen, diaName, stops, traveled, nextDist, speed, routes, trainClass);
    }

    private static float Num(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetSingle(out var f) ? f : 0;

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
    }

    private static string ScreenName(JsonElement? screen)
    {
        if (screen is not { } s) return "";
        return s.ValueKind switch
        {
            JsonValueKind.String => s.GetString() ?? "",
            JsonValueKind.Number => s.TryGetInt32(out var n) && n >= 0 && n < ScreenNames.Length ? ScreenNames[n] : "",
            _ => ""
        };
    }

    private static JsonElement? Prop(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }

        return null;
    }

    private static string Str(JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.String } e ? e.GetString() ?? "" : "";
    }
}
