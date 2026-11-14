namespace Gokko.Common;

/// <summary>乗務の役割。運転士も車掌も、ゲームは運転士モードで遊ぶ。</summary>
public enum CrewRole
{
    /// <summary>運転士（ふつうの在線共有の参加者）。</summary>
    Driver = 0,

    /// <summary>
    /// 車掌。同じ列番の運転士の列車に乗る。サーバーは在線として数えず、運転士と同じ進路・信号を届ける。
    /// 車掌のゲームの列車は、運転士のノッチで動かす（TrainCrewInput）。
    /// </summary>
    Conductor = 1
}

/// <summary>
/// 運転士と車掌の間の中継（/crew）。配信（/hub）とは別の接続にして、0.5秒ごとの配信の遅れの揺れを受けないようにする。
/// </summary>
public static class CrewProtocol
{
    /// <summary>接続先のパス（https://example.duckdns.org/crew）。</summary>
    public const string HubPath = "/crew";

    // ---- クライアント → サーバー ----

    /// <summary>乗務に入る（列番が変わったら呼び直す）。引数 <see cref="CrewJoinRequest"/>、戻り値 <see cref="JoinResult"/>。</summary>
    public const string Join = "CrewJoin";

    /// <summary>運転士の操作（変わったとき、および0.5秒ごと）。引数 <see cref="DriverInput"/>。同じ列番の車掌へ送る。</summary>
    public const string SendDriverInput = "SendDriverInput";

    /// <summary>車掌の操作（ドア・ブザー）。引数 <see cref="ConductorInput"/>。同じ列番の運転士へ送る。</summary>
    public const string SendConductorInput = "SendConductorInput";

    /// <summary>記録の一覧。戻り値 List&lt;<see cref="CrewRecordingInfo"/>&gt;（新しい順）。</summary>
    public const string ListRecordings = "ListRecordings";

    /// <summary>記録の再生を始める（車掌だけ）。引数 string（記録の名前）、戻り値 string（結果のお知らせ）。</summary>
    public const string StartReplay = "StartReplay";

    /// <summary>記録の再生を止める。</summary>
    public const string StopReplay = "StopReplay";

    // ---- サーバー → クライアント ----

    /// <summary>運転士の操作が届いた（車掌へ）。引数 <see cref="DriverInput"/>。</summary>
    public const string ReceiveDriverInput = "ReceiveDriverInput";

    /// <summary>車掌の操作が届いた（運転士へ）。引数 <see cref="ConductorInput"/>。</summary>
    public const string ReceiveConductorInput = "ReceiveConductorInput";

    /// <summary>相手の顔ぶれが変わった。引数 <see cref="CrewStatus"/>。</summary>
    public const string ReceiveStatus = "ReceiveCrewStatus";
}

public sealed class CrewJoinRequest
{
    public int ProtocolVersion { get; set; }
    public string Password { get; set; } = "";
    public CrewRole Role { get; set; }
    public string TrainNumber { get; set; } = "";
}

/// <summary>同じ列番の乗務の顔ぶれ。</summary>
public sealed class CrewStatus
{
    public string TrainNumber { get; set; } = "";
    public int Drivers { get; set; }
    public int Conductors { get; set; }

    /// <summary>記録を再生中なら、その名前。</summary>
    public string? Replaying { get; set; }
}

/// <summary>運転士の操作と位置（TrainCrewInput の GetTrainState と、ゲームの通信から）。</summary>
public sealed class DriverInput
{
    /// <summary>送った順の番号（古いものが後から届いたら捨てる）。</summary>
    public long Seq { get; set; }

    /// <summary>運転士のパソコンの時計（ミリ秒）。記録の再生で、間隔を再現するのに使う。</summary>
    public long ClientMs { get; set; }

    /// <summary>マスコン（ゲームの Pnotch）。</summary>
    public int Power { get; set; }

    /// <summary>ブレーキ（ゲームの Bnotch）。</summary>
    public int Brake { get; set; }

    /// <summary>レバーサー（1 前進、0 中立、-1 後進）。</summary>
    public int Reverser { get; set; }

    public float Speed { get; set; }

    /// <summary>始発から走った距離（m。ゲームの通信の値。0.5秒ごとに更新）。</summary>
    public float TraveledDistance { get; set; }

    public float NextStationDistance { get; set; }

    /// <summary>運転士のゲームで、全部のドアが閉まっているか。</summary>
    public bool AllClose { get; set; }

    /// <summary>警笛（空笛）を鳴らしているか。</summary>
    public bool Horn { get; set; }
}

/// <summary>車掌の操作。ドアは「開ける・閉める」の指示として送り、運転士のゲームでも同じ操作をする。</summary>
public sealed class ConductorInput
{
    /// <summary>"DoorOpn"、"DoorCls"、"ReOpenSW"、"Buzzer"（押した）、"BuzzerOff"（離した）のどれか。</summary>
    public string Action { get; set; } = "";

    /// <summary>車掌のゲームで、全部のドアが閉まっているか。</summary>
    public bool AllClose { get; set; }

    /// <summary>車掌のゲームの、始発から走った距離（運転士とのずれを見る）。</summary>
    public float TraveledDistance { get; set; }
}

/// <summary>保存した運転士の操作の記録1つ分。</summary>
public sealed class CrewRecordingInfo
{
    public string Name { get; set; } = "";
    public string TrainNumber { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public double Seconds { get; set; }
    public int Count { get; set; }
}
