using System.Security.Cryptography;
using System.Text;
using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>運転士と車掌の間の中継（/crew）。配信（/hub）とは別の接続。メソッド名は CrewProtocol と一致させる。</summary>
public sealed class CrewHub(CrewRelay relay, ServerOptions options) : Hub
{
    private const string JoinedKey = "joined";

    [HubMethodName(CrewProtocol.Join)]
    public async Task<JoinResult> Join(CrewJoinRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
            return new JoinResult { Message = $"クライアントの版が違います（サーバー: {Protocol.Version}、クライアント: {request.ProtocolVersion}）" };
        if (!PasswordMatches(request.Password)) return new JoinResult { Message = "合言葉が違います" };
        Context.Items[JoinedKey] = true;
        await relay.JoinAsync(Context.ConnectionId, request.Role, request.TrainNumber ?? "");
        // 入った本人にも今の顔ぶれを返す
        var train = relay.TrainOf(Context.ConnectionId) ?? "";
        await Clients.Caller.SendAsync(CrewProtocol.ReceiveStatus, relay.StatusOf(train));
        return new JoinResult { Ok = true };
    }

    [HubMethodName(CrewProtocol.SendDriverInput)]
    public Task SendDriverInput(DriverInput input) =>
        Joined && input != null ? relay.FromDriverAsync(Context.ConnectionId, input) : Task.CompletedTask;

    [HubMethodName(CrewProtocol.SendConductorInput)]
    public Task SendConductorInput(ConductorInput input) =>
        Joined && input != null ? relay.FromConductorAsync(Context.ConnectionId, input) : Task.CompletedTask;

    [HubMethodName(CrewProtocol.ListRecordings)]
    public List<CrewRecordingInfo> ListRecordings() => Joined ? relay.ListRecordings() : new();

    [HubMethodName(CrewProtocol.StartReplay)]
    public Task<string> StartReplay(string name) =>
        Joined ? relay.StartReplayAsync(Context.ConnectionId, name ?? "") : Task.FromResult("参加していません");

    [HubMethodName(CrewProtocol.StopReplay)]
    public void StopReplay()
    {
        if (Joined && relay.TrainOf(Context.ConnectionId) is { Length: > 0 } train) relay.StopReplay(train);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await relay.LeaveAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private bool Joined => Context.Items.ContainsKey(JoinedKey);

    private bool PasswordMatches(string? given)
    {
        if (string.IsNullOrEmpty(options.Password)) return true;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given ?? ""), Encoding.UTF8.GetBytes(options.Password));
    }
}
