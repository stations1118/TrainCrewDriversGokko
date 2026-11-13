using System.Text;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 踏切ごとの「特発を戻せる軌道回路」（Data/CrossingBlocks.csv ＝ 信号・踏切道早見表から作る）。
///
/// CSV は館浜（上）から赤山町（下）への並びで、列は「防護ゾーン, 下り線, 上り線, 防護ゾーン」。
/// 下り線・上り線の列には、駅（「TH70 浜園」）、閉塞信号機の番号（「８９」）、駅の信号機（「場内」「出発」「一場」「二場」「一出」「二出」）、
/// 踏切（「津１」＝津崎1号踏切、「虹渡」＝虹ケ浜渡り踏切、「道一」＝大道寺1号踏切）が書いてある。
///
/// 下りの列車は表の下から上へ、上りの列車は上から下へ進む。踏切ごとに、上下それぞれ
/// 「踏切がある閉塞（踏切の手前で最後に通る信号機の内方）」と「その一つ手前の閉塞（特発を見て止まる所）」の軌道回路を集める。
/// ・閉塞信号機: その信号機が守る軌道回路（下り89T など）
/// ・場内（一場・二場を含む）: その駅の構内の軌道回路全部と、場内信号機が守る軌道回路
/// ・出発（一出・二出を含む）: その駅の出発信号機が守る軌道回路
/// 駅の信号機がどの駅のものか: 下り線の場内は上の駅、出発は下の駅。上り線の場内は下の駅、出発は上の駅。
/// </summary>
public static class CrossingBlocks
{
    private static readonly Dictionary<char, string> StationOfMark = new()
    {
        ['館'] = "館浜", ['駒'] = "駒野", ['河'] = "河原崎", ['海'] = "海岸公園", ['虹'] = "虹ケ浜", ['津'] = "津崎", ['浜'] = "浜園",
        ['羽'] = "羽衣橋", ['井'] = "新井川", ['道'] = "大道寺", ['藤'] = "藤江", ['水'] = "水越", ['高'] = "高見沢", ['江'] = "江ノ原"
    };

    private sealed record Item(int Row, string Kind, string? Station, string Text); // Kind: station / block / home / start / crossing

    /// <summary>結果: 踏切名 → 戻せる軌道回路。CSV がなければ空。読めなかった行は notes に書く。</summary>
    public static Dictionary<string, HashSet<string>> Load(string path, LineMap line, RailData rail, GameData data, List<string> notes)
    {
        var result = new Dictionary<string, HashSet<string>>();
        if (!File.Exists(path)) return result;

        var rows = File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Split(',')).ToList();
        var down = new List<Item>();
        var up = new List<Item>();
        var crossings = new List<(int Row, string Name)>();
        var stations = new List<(int Row, string Id)>();

        for (var r = 0; r < rows.Count; r++)
        {
            for (var col = 2; col <= 3 && col < rows[r].Length; col++)
            {
                var text = rows[r][col].Normalize(NormalizationForm.FormKC).Trim();
                if (text.Length == 0 || text is "下り" or "上り" or "下り線" or "上り線") continue;
                var m = Regex.Match(text, @"^(TH\d+S?)\s");
                if (m.Success) { stations.Add((r, m.Groups[1].Value)); continue; }
                if (Regex.IsMatch(text, @"^\d+$")) { (col == 2 ? down : up).Add(new Item(r, "block", null, text)); continue; }
                if (text is "場内" or "一場" or "二場") { (col == 2 ? down : up).Add(new Item(r, "home", null, text)); continue; }
                if (text is "出発" or "一出" or "二出") { (col == 2 ? down : up).Add(new Item(r, "start", null, text)); continue; }
                var c = Regex.Match(text, @"^(.)(\d+|渡|一)$");
                if (c.Success && StationOfMark.TryGetValue(c.Groups[1].Value[0], out var stName))
                {
                    var name = c.Groups[2].Value == "渡" ? $"{stName}渡り踏切" : $"{stName}{(c.Groups[2].Value == "一" ? "1" : c.Groups[2].Value)}号踏切";
                    if (data.IsKnownCrossing(name)) crossings.Add((r, name));
                    else notes.Add($"踏切の閉塞: {r + 1}行目「{text}」→ {name} は Crossings.json にありません");
                    continue;
                }

                notes.Add($"踏切の閉塞: {r + 1}行目「{text}」は読めませんでした");
            }
        }

        string? StationAbove(int row) => stations.LastOrDefault(s => s.Row < row).Id;
        string? StationBelow(int row) => stations.FirstOrDefault(s => s.Row > row).Id;

        // 駅の信号機の、駅と種類（その駅の場内・出発の信号機が守る回路）
        HashSet<string> StationSignalCircuits(string stationId, string dir, bool home)
        {
            var set = new HashSet<string>();
            var name = line.StationName(stationId);
            var kinds = home ? new[] { "場内", "第一場内", "第二場内" } : new[] { "出発", "第二出発" };
            foreach (var s in rail.Signals.Values.Where(s => !s.IsRepeater && kinds.Any(k => s.Name.StartsWith(name + dir + k))))
                set.UnionWith(s.Protects);
            if (home && line.ZoneOfStation(stationId) is var z && z >= 0) set.UnionWith(line.CircuitsOfZone(z));
            return set;
        }

        HashSet<string> Circuits(Item it, string dir)
        {
            if (it.Kind == "block")
                return rail.Signals.TryGetValue($"{dir}閉塞{it.Text}", out var sig) ? sig.Protects.ToHashSet() : new HashSet<string> { $"{dir}{it.Text}T" };
            // 下り線: 場内は上の駅、出発は下の駅。上り線: 場内は下の駅、出発は上の駅
            var home = it.Kind == "home";
            var st = dir == "下り" ? (home ? StationAbove(it.Row) : StationBelow(it.Row)) : (home ? StationBelow(it.Row) : StationAbove(it.Row));
            return st == null ? new HashSet<string>() : StationSignalCircuits(st, dir, home);
        }

        foreach (var (row, name) in crossings)
        {
            var set = new HashSet<string>();
            // 下り（下から上へ）: 踏切より下にある信号機のうち近い順に2つ（踏切の閉塞と、その手前の閉塞）
            foreach (var it in down.Where(i => i.Row > row).OrderBy(i => i.Row).Take(2)) set.UnionWith(Circuits(it, "下り"));
            // 上り（上から下へ）: 踏切より上にある信号機のうち近い順に2つ
            foreach (var it in up.Where(i => i.Row < row).OrderByDescending(i => i.Row).Take(2)) set.UnionWith(Circuits(it, "上り"));
            if (set.Count == 0) { notes.Add($"踏切の閉塞: {name} の閉塞が見つかりません"); continue; }
            if (!result.TryGetValue(name, out var cur)) result[name] = set;
            else cur.UnionWith(set);
        }

        return result;
    }
}
