using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// サーバーが起動時に読むデータファイル一式（Data フォルダ）。
/// ・Crossings.json         踏切名の一覧
/// ・LineData.json          路線データ（軌道回路 → 駅間）
/// ・CrossingSections.json  踏切ごとの駅間（理由を表示する区間、ランダムの対象の判定に使う）
/// ・Reasons.json           特発の理由の候補（ランダムで使う。なければ既定の候補）
/// 書き方の誤りがあれば、起動時に例外で止める。
/// </summary>
public sealed class GameData
{
    private static readonly string[] DefaultReasons =
    [
        "踏切支障報知装置（非常ボタン）が扱われた",
        "踏切内で自動車が立ち往生している",
        "障害物検知装置が動作した"
    ];

    private readonly Dictionary<string, List<int>> _crossingZones = new();
    private readonly HashSet<string> _crossingSet = new();

    public IReadOnlyList<string> Crossings { get; private init; } = [];
    public IReadOnlyList<string> Reasons { get; private init; } = [];
    public LineMap Line { get; private init; } = null!;

    /// <summary>踏切ごとのゾーン（CrossingSections.json に書いたもの）。</summary>
    public IReadOnlyDictionary<string, List<int>> CrossingZones => _crossingZones;

    public bool IsKnownCrossing(string name) => _crossingSet.Contains(name);

    /// <summary>
    /// 踏切が、指定したゾーン（プレイヤーの現在地）の区間にあるか。
    /// CrossingSections.json に区間がない踏切は、現在地が分かる全員にとって「区間内」として扱う。
    /// </summary>
    public bool IsNear(string crossing, IReadOnlyCollection<int> zones)
    {
        if (zones.Count == 0) return false;
        return !_crossingZones.TryGetValue(crossing, out var cz) || cz.Any(zones.Contains);
    }

    // 踏切ごとの「特発を戻せる軌道回路」（CrossingBlocks.csv から。上下別に、踏切のある閉塞とその手前の閉塞）
    private Dictionary<string, HashSet<string>> _crossingCircuits = new();

    public IReadOnlyDictionary<string, HashSet<string>> CrossingCircuits => _crossingCircuits;

    public void SetCrossingCircuits(Dictionary<string, HashSet<string>> map) => _crossingCircuits = map;

    /// <summary>
    /// 列車（踏んでいる軌道回路と、その位置のゾーン）が、その踏切の特発を戻せる所にいるか。
    /// 閉塞が分かっている踏切は軌道回路で（上下別・駅の境目をまたいでも正しく）、分からない踏切は駅間のゾーンで判定する。
    /// </summary>
    public bool IsNear(string crossing, IReadOnlyCollection<string> circuits, IReadOnlyCollection<int> zones)
    {
        if (_crossingCircuits.TryGetValue(crossing, out var set)) return circuits.Any(set.Contains);
        return IsNear(crossing, zones);
    }

    public string DescribeCrossing(string crossing)
    {
        return _crossingZones.TryGetValue(crossing, out var zones) ? Line.DescribeZones(zones) ?? "" : "（区間未設定）";
    }

    public static GameData Load(string directory)
    {
        string Read(string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"データファイルがありません: {path}");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        var crossings = ParseStringArray(Read("Crossings.json"), "Crossings.json");
        if (crossings.Count == 0) throw new InvalidDataException("Crossings.json に踏切名がありません");

        var line = LineMap.Parse(Read("LineData.json"));

        var reasonsPath = Path.Combine(directory, "Reasons.json");
        var reasons = File.Exists(reasonsPath)
            ? ParseStringArray(File.ReadAllText(reasonsPath, Encoding.UTF8), "Reasons.json")
            : DefaultReasons.ToList();
        if (reasons.Count == 0) reasons = DefaultReasons.ToList();

        var data = new GameData { Crossings = crossings, Reasons = reasons, Line = line };
        foreach (var c in crossings) data._crossingSet.Add(c);
        data.LoadCrossingSections(Read("CrossingSections.json"));
        return data;
    }

    private static List<string> ParseStringArray(string json, string fileName)
    {
        using var doc = JsonDocument.Parse(json, LineMap.JsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{fileName} は [\"...\", \"...\"] の形で書いてください");

        return doc.RootElement.EnumerateArray()
            .Select(LineMap.Str)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct()
            .ToList();
    }

    // CrossingSections.json: { "館浜1号踏切": "TH76-TH75", "新井川渡り踏切": ["TH68", "TH68-TH67"] }
    private void LoadCrossingSections(string json)
    {
        using var doc = JsonDocument.Parse(json, LineMap.JsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("CrossingSections.json は { \"踏切名\": \"TH76-TH75\" } の形で書いてください");

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var crossing = prop.Name.Trim();
            var texts = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                texts.AddRange(SplitList(prop.Value.GetString()));
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) texts.AddRange(SplitList(item.GetString()));
                }
            }
            else
            {
                throw new InvalidDataException($"CrossingSections.json の {crossing}: 文字列か配列で書いてください");
            }

            var zones = new List<int>();
            foreach (var text in texts)
            {
                if (!Line.TryParseLocation(text, out var zone, out var error))
                    throw new InvalidDataException($"CrossingSections.json の {crossing}: {error}");
                if (!zones.Contains(zone)) zones.Add(zone);
            }

            if (!_crossingSet.Contains(crossing))
                throw new InvalidDataException($"CrossingSections.json の {crossing}: Crossings.json にない踏切名です");
            if (zones.Count > 0) _crossingZones[crossing] = zones;
        }
    }

    private static IEnumerable<string> SplitList(string? text)
    {
        return (text ?? "").Split(new[] { ',', '、', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
