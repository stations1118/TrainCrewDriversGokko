using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Client;

public enum ServerStatus
{
    /// <summary>接続していない（切断ボタン、または起動直後）</summary>
    Stopped,

    /// <summary>接続を試している、または切れて再接続を待っている</summary>
    Connecting,

    /// <summary>参加できている</summary>
    Connected,

    /// <summary>合言葉違い・版違いで断られた（再試行しない）</summary>
    Rejected
}

/// <summary>
/// サーバー（SignalR）との接続。切れたら3秒おきに再接続し、そのたびに参加し直す。
/// サーバーから届いた最新の状態は <see cref="State"/> で読む（未接続の間は null）。
/// </summary>
public sealed class ServerLink
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile HubConnection? _conn;
    private volatile bool _joined;
    private volatile ClientState? _state;
    private int _reporting;

    public ServerStatus Status { get; private set; } = ServerStatus.Stopped;

    /// <summary>直近のエラーや拒否の理由。</summary>
    public string Message { get; private set; } = "";

    public ClientState? State => _state;

    /// <summary>参加時にサーバーから受け取った踏切名の一覧。参加し直すたびに新しいリストに置き換わる。</summary>
    public IReadOnlyList<string> Crossings { get; private set; } = Array.Empty<string>();

    public bool IsJoined => _joined && _conn?.State == HubConnectionState.Connected;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>接続中のモード。</summary>
    public ShareMode Mode { get; private set; }

    public void Start(string url, string password, ShareMode mode)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var hubUrl = NormalizeUrl(url);
        Mode = mode;
        Status = ServerStatus.Connecting;
        Message = "";
        _loop = Task.Run(() => RunAsync(hubUrl, password, ct));
    }

    /// <summary>出発要求ボタン。</summary>
    public async Task<string> RequestDepartureAsync()
    {
        var conn = _conn;
        if (conn == null || !_joined) return "サーバーに接続していません";
        try
        {
            return await conn.InvokeAsync<string>(Protocol.RequestDeparture);
        }
        catch (Exception)
        {
            return "出発要求を送れませんでした";
        }
    }

    /// <summary>列車番号の確定。ダイヤにある列車か、サーバーに調べてもらう。接続していなければ null。</summary>
    public async Task<TrainCheckResult?> CheckTrainAsync(string train)
    {
        var conn = _conn;
        if (conn == null || !_joined) return null;
        try
        {
            return await conn.InvokeAsync<TrainCheckResult>(Protocol.CheckTrain, train);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>ゲームの SetRoute の結果をサーバーへ知らせる（応答は待たない）。</summary>
    public void SendRouteResult(string message)
    {
        var conn = _conn;
        if (conn == null || !_joined) return;
        _ = conn.SendAsync(Protocol.RouteResult, message);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch
            {
                // 終了処理中のエラーは無視する
            }
        }

        Status = ServerStatus.Stopped;
        _state = null;
    }

    /// <summary>自分の状態を送る。前回の送信が終わっていなければ送らない。</summary>
    public async Task ReportAsync(ClientReport report)
    {
        var conn = _conn;
        if (conn == null || !_joined) return;
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return;
        try
        {
            var ok = await conn.InvokeAsync<bool>(Protocol.Report, report);
            if (!ok)
            {
                // サーバーが参加者として扱っていない（サーバー側で消えた）。切断して参加し直す
                await conn.StopAsync();
            }
        }
        catch
        {
            // 切断は接続ループ側で扱う
        }
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }

    public async Task<RestoreResult> RestoreAsync()
    {
        var conn = _conn;
        if (conn == null || !_joined) return new RestoreResult { Message = "サーバーに接続していません" };
        try
        {
            return await conn.InvokeAsync<RestoreResult>(Protocol.Restore);
        }
        catch (Exception)
        {
            return new RestoreResult { Message = "復帰を送れませんでした" };
        }
    }

    private async Task RunAsync(string url, string password, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Status = ServerStatus.Connecting;
            var conn = new HubConnectionBuilder().WithUrl(url).Build();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            conn.On<ClientState>(Protocol.ReceiveState, s => _state = s);

            try
            {
                await conn.StartAsync(ct);
                var result = await conn.InvokeAsync<JoinResult>(
                    Protocol.Join, new JoinRequest { ProtocolVersion = Protocol.Version, Password = password, Mode = Mode }, ct);
                if (!result.Ok)
                {
                    Status = ServerStatus.Rejected;
                    Message = result.Message;
                    return; // 接続の破棄は finally で行う
                }

                Crossings = result.Crossings;
                _conn = conn;
                _joined = true;
                Status = ServerStatus.Connected;
                Message = "";

                await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 切断ボタン
            }
            catch (Exception)
            {
                // 例外の文面には接続先のアドレスが入ることがあるので、画面には出さない
                Message = "サーバーに接続できません";
            }
            finally
            {
                _joined = false;
                _conn = null;
                _state = null;
                try
                {
                    await conn.DisposeAsync();
                }
                catch
                {
                    // 破棄中のエラーは無視する
                }
            }

            if (ct.IsCancellationRequested) break;
            Status = ServerStatus.Connecting;
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

    /// <summary>「https://example.duckdns.org」のように入力されたら、末尾に /hub を付ける。</summary>
    public static string NormalizeUrl(string url)
    {
        var u = (url ?? "").Trim().TrimEnd('/');
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u;
        if (!u.EndsWith(Protocol.HubPath, StringComparison.OrdinalIgnoreCase)) u += Protocol.HubPath;
        return u;
    }
}
