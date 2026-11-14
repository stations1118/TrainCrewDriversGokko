using System.Text;

namespace Gokko.Server.Services;

/// <summary>行路の1項目（乗務する列車、または休憩・便乗・入換）。時刻はダイヤから求める（休憩は前後の項目の間）。</summary>
public sealed record DutyItem(int Order, string Kind, string Train, string? From, string? To, string Note);

/// <summary>行路（運転士1人の乗務の並び）。</summary>
public sealed record Duty(string Id, List<DutyItem> Items);

/// <summary>
/// 行路表（Data/Duties.csv）。ない場合は空（行路を使わない運転会）。
/// 列: 行路番号,順,種別,列車番号,開始駅,終了駅,備考（1行目は見出し）。種別は 乗務・入換・便乗・休憩。
/// 開始駅・終了駅は、列車の途中から・途中まで乗るときだけ書く（空なら、その列車のダイヤの線区の中の最初・最後の駅）。
/// 運行番号（編成のつながり）とは別のもの。行路はユーザーが組んで CSV で渡す。
/// </summary>
public sealed class DutyRoster
{
    public const string FileName = "Duties.csv";

    public List<Duty> Duties { get; } = new();

    /// <summary>読み込みで見つけた注意（行がおかしい、列番がダイヤにない など）。</summary>
    public List<string> Problems { get; } = new();

    public static DutyRoster Load(string dataDir, RailData rail)
    {
        var roster = new DutyRoster();
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return roster;

        var byId = new Dictionary<string, Duty>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 4) { roster.Problems.Add($"{i + 1}行目: 列が足りません"); continue; }
            var id = f[0];
            if (!int.TryParse(f[1], out var order)) { roster.Problems.Add($"{i + 1}行目: 順が数字ではありません"); continue; }
            var kind = f[2].Length > 0 ? f[2] : "乗務";
            var train = RailData.NormalizeTrain(f[3]);
            if (kind != "休憩" && rail.DiaOf(train) == null) roster.Problems.Add($"{i + 1}行目: {train} はダイヤにありません");
            if (!byId.TryGetValue(id, out var duty)) byId[id] = duty = new Duty(id, new List<DutyItem>());
            duty.Items.Add(new DutyItem(order, kind, train, f.Length > 4 && f[4].Length > 0 ? f[4] : null, f.Length > 5 && f[5].Length > 0 ? f[5] : null,
                f.Length > 6 ? string.Join(",", f.Skip(6)) : ""));
        }

        foreach (var d in byId.Values)
        {
            d.Items.Sort((a, b) => a.Order.CompareTo(b.Order));
            roster.Duties.Add(d);
        }

        roster.Duties.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return roster;
    }

    /// <summary>その列車を受け持つ行路（最初に見つかったもの）。</summary>
    public Duty? DutyOfTrain(string train)
    {
        var key = RailData.NormalizeTrain(train);
        return Duties.FirstOrDefault(d => d.Items.Any(x => x.Kind != "休憩" && x.Train == key));
    }

    public Duty? DutyById(string id) => Duties.FirstOrDefault(d => d.Id == id);
}
