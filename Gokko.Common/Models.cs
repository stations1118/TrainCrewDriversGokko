namespace Gokko.Common;

/// <summary>接続のモード。</summary>
public enum ShareMode
{
    /// <summary>踏切の特発だけを共有する。他列車の表示・進路・信号はゲームのまま。</summary>
    CrossingOnly = 0,

    /// <summary>在線を共有する（集中モード）。他列車を消し、進路はサーバーが管理し、他の参加者の在線で信号を落とす。</summary>
    Shared = 1
}

/// <summary>参加の要求。</summary>
public sealed class JoinRequest
{
    public int ProtocolVersion { get; set; }

    /// <summary>サーバーに設定した合言葉。サーバー側が空なら何でもよい。</summary>
    public string Password { get; set; } = "";

    public ShareMode Mode { get; set; }
}

/// <summary>参加の結果。</summary>
public sealed class JoinResult
{
    public bool Ok { get; set; }

    /// <summary>拒否されたときの理由。</summary>
    public string Message { get; set; } = "";

    /// <summary>サーバーが扱う踏切名の一覧。クライアントは、ここに載る踏切の特発を同期する。</summary>
    public List<string> Crossings { get; set; } = new();
}

/// <summary>クライアントからサーバーへ、0.5秒ごとに送る自分の状態。</summary>
public sealed class ClientReport
{
    /// <summary>列車番号（入力欄の値。空ならゲームのダイヤ名）。</summary>
    public string TrainNumber { get; set; } = "";

    /// <summary>TRAIN CREW に接続できているか。</summary>
    public bool TrainCrewConnected { get; set; }

    /// <summary>運転画面（プレイ中・一時停止中）にいるか。</summary>
    public bool InGame { get; set; }

    /// <summary>自列車が踏んでいる軌道回路（他の列車の分は含めない）。</summary>
    public List<string> TrackCircuits { get; set; } = new();

    // ---- 以下は在線共有モードのときだけ使う ----

    /// <summary>自列車の停車駅の情報（ゲームの stationList）。時刻は送らない。</summary>
    public List<StopInfo> Stops { get; set; } = new();

    /// <summary>自列車が始発から走った距離（m。ゲームの myTrainData.TotalLength）。</summary>
    public float TraveledDistance { get; set; }

    /// <summary>次の駅までの残り距離（m。ゲームの nextStaDistance）。</summary>
    public float NextStationDistance { get; set; }

    public float Speed { get; set; }

    /// <summary>ゲームに今設定されている、自列車の進路。</summary>
    public List<RouteKey> GameRoutes { get; set; } = new();

    /// <summary>ユーザーが選んだ番線（駅ID → 番線）。</summary>
    public Dictionary<string, int> TrackChoices { get; set; } = new();

    /// <summary>
    /// 継続乗務ボタンが押されているか。押している間に運転画面を出ると、サーバーは最後の在線位置を5分間残す
    /// （同じ番線から次のシナリオを始めると、その列番の列車として続ける）。
    /// </summary>
    public bool ContinueDuty { get; set; }

    /// <summary>列車番号を入力欄で手入力したか（false ならシナリオの列番）。ダイヤ運転中は、手入力していない列車を出現させない。</summary>
    public bool TrainNumberTyped { get; set; }
}

/// <summary>停車駅1つ分（ゲームの stationList の要素から、時刻を除いたもの）。</summary>
public sealed class StopInfo
{
    public string Name { get; set; } = "";

    /// <summary>ゲームの stopType（StopForPassenger / StopForOperation / それ以外は通過）。</summary>
    public string StopType { get; set; } = "";

    /// <summary>始発からの距離（m）。</summary>
    public float Distance { get; set; }
}

/// <summary>連動装置名と進路名の組。</summary>
public sealed class RouteKey
{
    public string Interlocking { get; set; } = "";
    public string RouteName { get; set; } = "";

    public override string ToString() => $"{Interlocking}/{RouteName}";
}

/// <summary>サーバーからクライアントへ配信する状態。クライアントごとに中身が違う（理由の表示範囲など）。</summary>
public sealed class ClientState
{
    public DateTimeOffset ServerTime { get; set; }

    /// <summary>ダイヤ運転中か（false ならフリー運転）。ダイヤ運転中は列車番号の手入力が要り、継続乗務は常に入。</summary>
    public bool DiaMode { get; set; }

    /// <summary>ロープレ上の時刻（"HH:mm:ss"）。管理画面で設定するまでは null。</summary>
    public string? GameTime { get; set; }

    /// <summary>サーバーが判断した自分の現在地（"館浜〜駒野" など）。分からなければ null。</summary>
    public string? Position { get; set; }

    /// <summary>作動中の特発の踏切名。全員に同じものが届く。クライアントはこの通りにゲームへ反映する。</summary>
    public List<string> ActiveCrossings { get; set; } = new();

    /// <summary>作動中の特発のうち、自分のいる区間のもの（理由つき）。当該区間に入ったときだけ入る。</summary>
    public List<IncidentView> VisibleIncidents { get; set; } = new();

    /// <summary>参加している人数。</summary>
    public int PlayerCount { get; set; }

    // ---- 以下は在線共有モードのときだけ入る ----

    /// <summary>ゲームに開通させておく進路（送る順）。クライアントは、ゲームにないものを SetRoute で送る。</summary>
    public List<RouteCommand> Routes { get; set; } = new();

    /// <summary>取り消す進路。クライアントは、ゲームにあれば DeleteRoute2 で送る。</summary>
    public List<RouteKey> DeleteRoutes { get; set; } = new();

    /// <summary>他の参加者の在線で落とす信号の現示。ここにない信号は、ゲームの現示に戻す（None を送る）。</summary>
    public List<SignalCommand> Signals { get; set; } = new();

    /// <summary>自列車の、この先の駅ごとの番線と状態。</summary>
    public List<StationPlanView> Plan { get; set; } = new();

    /// <summary>出発要求ボタンを押せるか（駅に止まっていて、出発進路がまだのとき）。</summary>
    public bool CanRequestDeparture { get; set; }

    /// <summary>出発要求ボタンの表示（"出発要求"、引上線などにいるときは "入換要求"）。</summary>
    public string DepartureLabel { get; set; } = "出発要求";

    /// <summary>進路についての、ユーザーへのお知らせ。</summary>
    public string? RouteMessage { get; set; }
}

/// <summary>理由つきの特発の情報。</summary>
public sealed class IncidentView
{
    public string Crossing { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary>作動したロープレ時刻（設定されていれば）。</summary>
    public string? StartedGameTime { get; set; }
}

/// <summary>復帰ボタンの結果。</summary>
public sealed class RestoreResult
{
    public int Released { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 進路の開通指示。TRAIN CREW の SetRoute の引数にそのまま対応する
/// （route_manual モードで、[連動装置名, 進路名, LED表示, 列車番号, 停車/運転停車/通過]）。
/// </summary>
public sealed class RouteCommand
{
    /// <summary>
    /// 指示の番号（サーバーが振る）。クライアントは、一度ゲームで開通を確認した番号の指示は二度と送らない
    /// （ゲームが列車の通過で自動解除した進路を、送り直して開通させてしまわないため）。
    /// </summary>
    public long Id { get; set; }

    public string Interlocking { get; set; } = "";
    public string RouteName { get; set; } = "";

    /// <summary>進路表示器（LED）に出す値（場内・入換・誘導の進路）。</summary>
    public string Indicator { get; set; } = "";

    /// <summary>
    /// 出発信号機の進路なら true（列選）。Indicator が空でなければ、それ（ダイヤの種別表記から作った列選の文字）を送る。
    /// 空なら、クライアントがゲームの種別から作った列選の文字
    /// （普通・準急・区急・急行・快速急行、特急は D特 など）を送る。
    /// </summary>
    public bool UseTrainType { get; set; }

    public string TrainNumber { get; set; } = "";

    /// <summary>"停車"、"運転停車"、"通過" のどれか。</summary>
    public string StopType { get; set; } = "停車";
}

/// <summary>列車番号の確定の結果。</summary>
public sealed class TrainCheckResult
{
    /// <summary>ダイヤ（列車一覧）にある列車か。なければ、種別ごとの所定の番線で進路を作る。</summary>
    public bool Found { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>信号機の現示の指示（SetSignalPhases の1組）。Phase は R / YY / Y / YG / G。</summary>
public sealed class SignalCommand
{
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "R";
}

/// <summary>この先の駅1つ分の、番線と状態。</summary>
public sealed class StationPlanView
{
    public string StationId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"停車" / "運転停車" / "通過"。</summary>
    public string StopType { get; set; } = "";

    /// <summary>使う番線（通過なら null）。</summary>
    public int? Track { get; set; }

    /// <summary>選べる番線。</summary>
    public List<int> TrackOptions { get; set; } = new();

    /// <summary>番線の決め方（所定 / 選択 / 待避 / 集中）。</summary>
    public string TrackSource { get; set; } = "";

    /// <summary>番線が確定して、もう選べないか。</summary>
    public bool Locked { get; set; }

    /// <summary>運転士に見せる状態: 「出発要求待ち」（入換なら「入換要求待ち」）「進路確定」「進路未確定」の3段階だけ。</summary>
    public string Status { get; set; } = "";

    /// <summary>詳しい状態（待ちの理由など。管理・試験用。クライアントの画面には出さない）。</summary>
    public string Detail { get; set; } = "";
}
