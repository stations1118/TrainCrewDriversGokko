namespace Gokko.Client;

/// <summary>
/// 接続先のサーバー。exe に埋め込まれ、画面には表示しない。
/// 同じフォルダに ServerAddress.Local.cs（LocalUrl を定義したもの）を置くと、その接続先でビルドされる。
/// なければ、下の仮の接続先になる。
///
/// 注意: 埋め込みは「画面に出さない・普通には変えられない」ためのもので、暗号化ではない。
/// 逆コンパイラや通信の監視で、アドレスは調べられる。参加の可否は合言葉で守る。
/// </summary>
internal static partial class ServerAddress
{
    /// <summary>接続先（/hub は付けなくてよい）。</summary>
#if LOCAL_SERVER_ADDRESS
    private const string Url = LocalUrl;
#else
    private const string Url = "https://example.duckdns.org";
#endif

    /// <summary>
    /// 実際に使う接続先。開発中にローカルのサーバーで試すときだけ、環境変数 GOKKO_SERVER で上書きできる
    /// （例: $env:GOKKO_SERVER='http://127.0.0.1:5000'）。
    /// </summary>
    public static string Current =>
        Environment.GetEnvironmentVariable("GOKKO_SERVER") is { Length: > 0 } overrideUrl ? overrideUrl : Url;
}
