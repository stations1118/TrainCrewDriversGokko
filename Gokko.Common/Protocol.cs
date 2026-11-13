namespace Gokko.Common;

/// <summary>
/// サーバーとユーザークライアントの間の取り決め（SignalR のメソッド名など）。
/// 両方で同じものを参照する。中身を変えて互換性がなくなるときは Version を上げる。
/// </summary>
public static class Protocol
{
    /// <summary>通信の版。サーバーと違うクライアントは、接続時に拒否される。</summary>
    public const int Version = 5;

    /// <summary>SignalR の接続先のパス（https://example.duckdns.org/hub）。</summary>
    public const string HubPath = "/hub";

    // ---- クライアント → サーバー ----

    /// <summary>参加する。引数 <see cref="JoinRequest"/>、戻り値 <see cref="JoinResult"/>。最初に1回呼ぶ。</summary>
    public const string Join = "Join";

    /// <summary>自分の状態を知らせる。引数 <see cref="ClientReport"/>、戻り値 bool（false なら参加し直す）。0.5秒ごと。</summary>
    public const string Report = "Report";

    /// <summary>復帰ボタン。自分のいる区間で作動中の特発を解除する。戻り値 <see cref="RestoreResult"/>。</summary>
    public const string Restore = "Restore";

    /// <summary>出発要求ボタン（在線共有モード）。駅に止まっている自列車の出発進路を要求する。戻り値 string（結果のお知らせ）。</summary>
    public const string RequestDeparture = "RequestDeparture";

    /// <summary>TRAIN CREW の SetRoute の結果を知らせる（在線共有モード）。引数 string（"連動装置_進路 is OK" など）。</summary>
    public const string RouteResult = "RouteResult";

    /// <summary>列車番号の確定（在線共有モード）。引数 string（列車番号）、戻り値 <see cref="TrainCheckResult"/>。ダイヤにある列車か調べる。</summary>
    public const string CheckTrain = "CheckTrain";

    /// <summary>防護無線の発報・解除（トグル）。引数 <see cref="ClientReport"/>（押した時点の位置）、戻り値 bool（受け付けたか）。</summary>
    public const string ToggleProtectionRadio = "ToggleProtectionRadio";

    // ---- サーバー → クライアント ----

    /// <summary>状態の配信。引数 <see cref="ClientState"/>。0.5秒ごと、および特発が変わった瞬間。</summary>
    public const string ReceiveState = "ReceiveState";
}
