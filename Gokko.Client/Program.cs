using Gokko.Common;

namespace Gokko.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 接続のモードは起動時に選ぶ（起動後は変えられない）。
        // 在線共有なら、シナリオを始める前に他列車を消す必要があるため、TRAIN CREW につながった時点ですぐ設定する
        var config = ClientConfig.Load();
        ShareMode mode;
        var envMode = Environment.GetEnvironmentVariable("GOKKO_MODE"); // 開発用
        if (envMode is "shared" or "crossing")
        {
            mode = envMode == "shared" ? ShareMode.Shared : ShareMode.CrossingOnly;
        }
        else
        {
            using var dialog = new ModeDialog(config.Mode);
            if (dialog.ShowDialog() != DialogResult.OK || dialog.Selected is not { } selected) return; // 選ばずに閉じたら終了
            mode = selected;
        }

        config.Mode = mode;
        config.Save();
        Application.Run(new MainForm(config, mode));
    }
}
