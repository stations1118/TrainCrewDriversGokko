using Gokko.Common;

namespace Gokko.Client;

/// <summary>接続のモードを選ぶ画面（初めて接続するとき、または「モード変更」を押したとき）。</summary>
public sealed class ModeDialog : Form
{
    public ShareMode? Selected { get; private set; }

    /// <summary>選んだ乗務の役割（「車掌として乗る」なら車掌）。</summary>
    public CrewRole SelectedRole { get; private set; }

    public ModeDialog(ShareMode? current, CrewRole currentRole = CrewRole.Driver)
    {
        Text = "接続のモード";
        ClientSize = new Size(480, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));

        layout.Controls.Add(new Label
        {
            Text = "他の参加者と、何を共有しますか？（起動後は変えられません）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold)
        });

        Button Choice(string title, string body, ShareMode mode, Color color, CrewRole role = CrewRole.Driver)
        {
            var b = new Button
            {
                Text = $"{title}\n{body}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(0, 4, 0, 4),
                FlatStyle = FlatStyle.Flat
            };
            b.FlatAppearance.BorderColor = color;
            b.FlatAppearance.BorderSize = current == mode && currentRole == role ? 3 : 1;
            b.Click += (_, _) =>
            {
                Selected = mode;
                SelectedRole = role;
                DialogResult = DialogResult.OK;
                Close();
            };
            return b;
        }

        layout.Controls.Add(Choice("特発だけを共有する",
            "踏切の特発だけを全員で共有します。他列車の表示・進路・信号はゲームのままです。",
            ShareMode.CrossingOnly, Color.SteelBlue));
        layout.Controls.Add(Choice("在線を共有する（集中モード）",
            "他の参加者と在線を共有します。ゲームの他列車は消え、進路はサーバーが開通させ、" +
            "他の参加者の位置に合わせて信号が変わります。特発も共有します。シナリオを始める前に起動してください。",
            ShareMode.Shared, Color.Firebrick));
        layout.Controls.Add(Choice("車掌として乗る（在線共有）",
            "同じ列番の運転士の列車に、車掌として乗ります。ゲームは運転士モードで、運転士と同じシナリオを始めてください。" +
            "列車は運転士のノッチで動き、ドアは自分で扱います。",
            ShareMode.Shared, Color.DarkGreen, CrewRole.Conductor));
        Controls.Add(layout);
    }
}
