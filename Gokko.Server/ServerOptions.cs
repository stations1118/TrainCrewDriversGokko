namespace Gokko.Server;

/// <summary>appsettings.json の "Gokko" の設定。環境変数（Gokko__AdminKey など）でも上書きできる。</summary>
public sealed class ServerOptions
{
    /// <summary>参加の合言葉。空なら誰でも参加できる。</summary>
    public string Password { get; set; } = "";

    /// <summary>管理画面の鍵。空なら管理機能は無効（安全のため）。</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>
    /// ダイヤグラムの設定画面（diasettings.html）の合言葉。他の参加者に公開する用で、管理キーとは別。
    /// できるのはダイヤグラムの操作（番線・発車順・進路取消・抑止解除・開通）だけ。空ならこの画面は無効。
    /// </summary>
    public string DiaPassword { get; set; } = "";

    /// <summary>データファイルのフォルダ。相対パスなら exe の場所から。</summary>
    public string DataDirectory { get; set; } = "Data";

    /// <summary>状態を配信する間隔（ミリ秒）。特発が変わったときは、これを待たずにすぐ配信する。</summary>
    public int BroadcastIntervalMs { get; set; } = 500;

    public RandomOptions Random { get; set; } = new();
}

/// <summary>
/// ランダム特発の設定。対象は「参加者の誰かの、現在の駅間と進行方向の先」の踏切。
/// 運転会の途中でも、管理画面から一部の値を変えられる。
/// </summary>
public sealed class RandomOptions
{
    /// <summary>起動時にランダム特発を動かすか。管理画面で切り替えられる。</summary>
    public bool Enabled { get; set; }

    public int RollIntervalSeconds { get; set; } = 47;
    public double Probability { get; set; } = 0.1;

    /// <summary>復帰されないとき、自動で解除するまでの秒数（手動で作動させたものにも効く）。0 なら自動で解除しない。</summary>
    public int AutoReleaseSeconds { get; set; } = 360;

    /// <summary>路線全体で、同時に作動させておく踏切の最大数（ランダム分のみ。手動は制限しない）。</summary>
    public int MaxConcurrent { get; set; } = 1;

    public bool AvoidRepeat { get; set; } = true;
    public int SectionsAhead { get; set; } = 1;
    public int SectionsBehind { get; set; }
    public bool FallbackToAllCrossings { get; set; }
}
