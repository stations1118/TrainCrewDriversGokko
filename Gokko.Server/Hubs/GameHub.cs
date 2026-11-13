using System.Security.Cryptography;
using System.Text;
using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>ユーザークライアントとの接続口（/hub）。メソッド名は Protocol と一致させる。</summary>
public sealed class GameHub(
    PlayerRegistry players,
    IncidentManager incidents,
    GameData data,
    StateBroadcaster broadcaster,
    RouteEngine routes,
    ServerOptions options,
    ILogger<GameHub> logger) : Hub
{
    [HubMethodName(Protocol.Join)]
    public JoinResult Join(JoinRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
        {
            return new JoinResult
            {
                Message = $"クライアントの版が違います（サーバー: {Protocol.Version}、クライアント: {request.ProtocolVersion}）。最新のクライアントを使ってください"
            };
        }

        if (!PasswordMatches(request.Password))
            return new JoinResult { Message = "合言葉が違います" };

        var first = !players.IsJoined(Context.ConnectionId);
        if (first)
        {
            players.Add(Context.ConnectionId, request.Mode);
            logger.LogInformation("参加 {ConnectionId}（{Mode}・{Count}人）", Context.ConnectionId, request.Mode, players.Count);
        }

        broadcaster.Trigger();
        return new JoinResult { Ok = true, Crossings = data.Crossings.ToList() };
    }

    [HubMethodName(Protocol.Report)]
    public bool Report(ClientReport report)
    {
        // 管理画面・ダイヤグラムの設定画面から強制切断された参加者は、ここで接続を切る
        if (players.IsKicked(Context.ConnectionId))
        {
            logger.LogInformation("強制切断 {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return false;
        }

        return players.ApplyReport(Context.ConnectionId, report);
    }

    /// <summary>復帰ボタン。自分のいる区間で作動中の特発（理由が表示されているもの）だけを解除する。</summary>
    [HubMethodName(Protocol.Restore)]
    public RestoreResult Restore()
    {
        var p = players.Get(Context.ConnectionId);
        if (p == null) return new RestoreResult { Message = "サーバーに参加していません" };

        var targets = incidents.Snapshot().Where(i => data.IsNear(i.Crossing, p.TrackCircuits, p.Zones)).Select(i => i.Crossing).ToList();
        if (targets.Count == 0) return new RestoreResult { Message = "この区間に、作動中の特発はありません" };

        var who = string.IsNullOrEmpty(p.TrainNumber) ? "列番未設定" : $"列番 {p.TrainNumber}";
        var released = incidents.Release(targets, who);
        return new RestoreResult
        {
            Released = released.Count,
            Message = released.Count > 0 ? $"復帰しました: {string.Join("、", released.Select(r => r.Crossing))}" : "すでに復帰されています"
        };
    }

    /// <summary>出発要求ボタン（在線共有モード）。</summary>
    [HubMethodName(Protocol.RequestDeparture)]
    public string RequestDeparture()
    {
        var message = routes.RequestDeparture(Context.ConnectionId);
        broadcaster.Trigger();
        return message;
    }

    /// <summary>列車番号の確定（在線共有モード）。ダイヤにある列車か調べる。</summary>
    [HubMethodName(Protocol.CheckTrain)]
    public TrainCheckResult CheckTrain(string train) => routes.CheckTrain(train ?? "");

    /// <summary>TRAIN CREW の SetRoute の結果（在線共有モード）。</summary>
    [HubMethodName(Protocol.RouteResult)]
    public void RouteResult(string message)
    {
        if (!string.IsNullOrEmpty(message) && message.Length < 200) routes.ReportResult(Context.ConnectionId, message);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (players.IsJoined(Context.ConnectionId))
        {
            players.Remove(Context.ConnectionId);
            logger.LogInformation("退出 {ConnectionId}（{Count}人）", Context.ConnectionId, players.Count);
        }

        return base.OnDisconnectedAsync(exception);
    }

    private bool PasswordMatches(string? given)
    {
        if (string.IsNullOrEmpty(options.Password)) return true;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given ?? ""), Encoding.UTF8.GetBytes(options.Password));
    }
}
