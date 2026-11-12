using System.Text.Json;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 画面で入力した値。%APPDATA%\GokkoClient\client.json に保存し、次回の起動時に読み込む。
/// 合言葉もそのまま保存される（運転会の合言葉程度のものを想定）。
/// </summary>
public sealed class ClientConfig
{
    public string Password { get; set; } = "";
    public string TrainNumber { get; set; } = "";

    /// <summary>接続のモード。まだ選んでいなければ null（初めて接続するときに選ぶ）。</summary>
    public ShareMode? Mode { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GokkoClient", "client.json");

    public static ClientConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new ClientConfig();
        }
        catch
        {
            // 壊れていたら既定値で始める
        }

        return new ClientConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存できなくても動作には影響しない
        }
    }
}
