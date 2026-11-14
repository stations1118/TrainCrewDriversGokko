using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Client;

/// <summary>
/// 運転士と車掌の間の中継（/crew）との接続。配信（/hub）とは別の接続にする。
/// 切れたら3秒おきにつなぎ直し、そのたびに乗務に入り直す。列番が変わったら入り直す。
/// 受け取ったものはイベントで知らせる（SignalR のスレッドから呼ばれる）。
/// </summary>
public sealed class CrewLink
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile HubConnection? _conn;
    private volatile bool _joined;
    private string _password = "";
    private CrewRole _role;
    private volatile string _train = "";
    private string _joinedTrain = "";
    private readonly SemaphoreSlim _joinLock = new(1, 1);

    public event Action<DriverInput>? DriverInputReceived;
    public event Action<ConductorInput>? ConductorInputReceived;

    public volatile CrewStatus? Status;

    public bool IsJoined => _joined && _conn?.State == HubConnectionState.Connected;

    public string Message { get; private set; } = "";

    public void Start(string url, string password, CrewRole role)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _password = password;
        _role = role;
        var ct = _cts.Token;
        var hubUrl = NormalizeUrl(url);
        _loop = Task.Run(() => RunAsync(hubUrl, ct));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
            try { await _loop; } catch { }
        Status = null;
    }

    /// <summary>乗る列番（空なら、まだ乗らない）。変わったら入り直す。</summary>
    public void SetTrain(string train)
    {
        train = (train ?? "").Trim();
        if (train == _train) return;
        _train = train;
        _ = JoinAsync();
    }

    public void SendDriverInput(DriverInput input)
    {
        var conn = _conn;
        if (conn == null || !IsJoined || _joinedTrain.Length == 0) return;
        _ = SendQuietAsync(conn, CrewProtocol.SendDriverInput, input);
    }

    public void SendConductorInput(ConductorInput input)
    {
        var conn = _conn;
        if (conn == null || !IsJoined || _joinedTrain.Length == 0) return;
        _ = SendQuietAsync(conn, CrewProtocol.SendConductorInput, input);
    }

    public async Task<List<CrewRecordingInfo>> ListRecordingsAsync()
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return new();
        try { return await conn.InvokeAsync<List<CrewRecordingInfo>>(CrewProtocol.ListRecordings); }
        catch { return new(); }
    }

    public async Task<string> StartReplayAsync(string name)
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return "中継につながっていません";
        try { return await conn.InvokeAsync<string>(CrewProtocol.StartReplay, name); }
        catch { return "再生を始められませんでした"; }
    }

    public async Task StopReplayAsync()
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return;
        try { await conn.InvokeAsync(CrewProtocol.StopReplay); } catch { }
    }

    private static async Task SendQuietAsync(HubConnection conn, string method, object arg)
    {
        try { await conn.SendAsync(method, arg); }
        catch { /* 切断は接続ループ側で扱う */ }
    }

    private async Task JoinAsync()
    {
        var conn = _conn;
        if (conn == null || conn.State != HubConnectionState.Connected) return;
        await _joinLock.WaitAsync();
        try
        {
            var train = _train;
            var result = await conn.InvokeAsync<JoinResult>(CrewProtocol.Join,
                new CrewJoinRequest { ProtocolVersion = Protocol.Version, Password = _password, Role = _role, TrainNumber = train });
            if (result.Ok)
            {
                _joinedTrain = train;
                _joined = true;
                Message = "";
            }
            else
            {
                Message = result.Message;
            }
        }
        catch
        {
            // 切断は接続ループ側で扱う
        }
        finally
        {
            _joinLock.Release();
        }
    }

    private async Task RunAsync(string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var conn = new HubConnectionBuilder().WithUrl(url).Build();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            conn.On<DriverInput>(CrewProtocol.ReceiveDriverInput, i => DriverInputReceived?.Invoke(i));
            conn.On<ConductorInput>(CrewProtocol.ReceiveConductorInput, i => ConductorInputReceived?.Invoke(i));
            conn.On<CrewStatus>(CrewProtocol.ReceiveStatus, s => Status = s);

            try
            {
                await conn.StartAsync(ct);
                _conn = conn;
                _joinedTrain = "";
                await JoinAsync();
                await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 切断ボタン
            }
            catch
            {
                Message = "中継につながりません";
            }
            finally
            {
                _joined = false;
                _conn = null;
                Status = null;
                try { await conn.DisposeAsync(); } catch { }
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>配信の接続先（…/hub）と同じサーバーの /crew にする。</summary>
    private static string NormalizeUrl(string url)
    {
        var hub = ServerLink.NormalizeUrl(url);
        return hub[..^Protocol.HubPath.Length] + CrewProtocol.HubPath;
    }
}
