using Gokko.Common;

namespace Gokko.Client;

/// <summary>接続のモードを選ぶ画面（初めて接続するとき、または「モード変更」を押したとき）。</summary>
public sealed class ModeDialog : Form
{
    public ShareMode? Selected { get; private set; }

    public ModeDialog(ShareMode? current)
    {
        Text = "接続のモード";
        ClientSize = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        layout.Controls.Add(new Label
        {
            Text = "他の参加者と、何を共有しますか？（起動後は変えられません）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold)
        });

        Button Choice(string title, string body, ShareMode mode, Color color)
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
            b.FlatAppearance.BorderSize = current == mode ? 3 : 1;
            b.Click += (_, _) =>
            {
                Selected = mode;
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
        Controls.Add(layout);
    }
}
