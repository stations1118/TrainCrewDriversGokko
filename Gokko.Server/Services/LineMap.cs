using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// LineData.json（駅、路線の並び、駅間・駅構内ごとの軌道回路）。
/// 軌道回路 → 路線上の位置（ゾーン）を引けるようにする。
/// ゾーン: 駅は 0,2,4...、駅と駅の間（駅間）は 1,3,5...。路線は、line に書いた順に1本の鎖として並べる。
/// </summary>
public sealed class LineMap
{
    private readonly Dictionary<string, string> _stationName = new();
    private readonly List<string> _chain = new();
    private readonly Dictionary<string, int> _stationZone = new();
    private readonly Dictionary<string, int> _sectionZone = new();
    private readonly Dictionary<string, int> _circuitZone = new();

    internal static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static LineMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("LineData.json は { \"stations\": ..., \"line\": ..., \"sections\": ... } の形で書いてください");

        var map = new LineMap();

        foreach (var p in Required(root, "stations", JsonValueKind.Object).EnumerateObject())
        {
            map._stationName[p.Name.Trim()] = Str(p.Value);
        }

        foreach (var item in Required(root, "line", JsonValueKind.Array).EnumerateArray())
        {
            map._chain.Add(Str(item).Trim());
        }

        map.BuildZones();

        if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in sections.EnumerateObject())
            {
                var parts = p.Name.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !map._sectionZone.TryGetValue(SectionKey(parts[0], parts[1]), out var zone))
                    throw new InvalidDataException(
                        $"LineData.json の sections「{p.Name}」は、line で隣り合う駅の組ではありません（例: TH64-TH65）");

                map.AddCircuits(p.Value, zone, $"sections「{p.Name}」");
            }
        }

        if (root.TryGetProperty("yards", out var yards) && yards.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in yards.EnumerateObject())
            {
                if (!map._stationZone.TryGetValue(p.Name.Trim(), out var zone))
                    throw new InvalidDataException($"LineData.json の yards「{p.Name}」は、line にない駅です");

                map.AddCircuits(p.Value, zone, $"yards「{p.Name}」");
            }
        }

        // unassigned（割り当てなしの回路）は、位置の判定には使わないので読み込まない。
        return map;
    }

    /// <summary>踏んでいる軌道回路が属するゾーン（駅間または駅構内）。データにない回路は無視する。</summary>
    public List<int> ZonesOf(IEnumerable<string> circuits)
    {
        var zones = new SortedSet<int>();
        foreach (var circuit in circuits)
        {
            if (_circuitZone.TryGetValue(circuit, out var zone)) zones.Add(zone);
        }

        return zones.ToList();
    }

    /// <summary>路線の駅の並び（下り＝館浜方向の順）。</summary>
    public IReadOnlyList<string> StationIds => _chain;

    /// <summary>駅のゾーン（駅ID から）。なければ -1。</summary>
    public int ZoneOfStation(string stationId) => _stationZone.TryGetValue(stationId, out var z) ? z : -1;

    /// <summary>ゾーンが駅なら、その駅ID。</summary>
    public string? StationAtZone(int zone) => zone >= 0 && zone % 2 == 0 && zone / 2 < _chain.Count ? _chain[zone / 2] : null;

    public string StationName(string stationId) => _stationName.TryGetValue(stationId, out var n) ? n : stationId;

    /// <summary>駅名から駅ID（「江原信」は江ノ原信号場）。</summary>
    public string? StationIdByName(string name)
    {
        if (name == "江原信") name = "江ノ原信号場";
        foreach (var (id, n) in _stationName)
            if (n == name) return id;
        return null;
    }

    /// <summary>そのゾーン（駅構内・駅間）に属する軌道回路。</summary>
    public IEnumerable<string> CircuitsOfZone(int zone) => _circuitZone.Where(kv => kv.Value == zone).Select(kv => kv.Key);

    /// <summary>軌道回路のゾーン。なければ null。</summary>
    public int? ZoneOfCircuit(string circuit) => _circuitZone.TryGetValue(circuit, out var z) ? z : null;

    /// <summary>ゾーンの名前。駅なら「館浜」、駅間なら「館浜〜駒野」。</summary>
    public string ZoneLabel(int zone)
    {
        if (zone < 0 || zone > (_chain.Count - 1) * 2) return "?";
        if (zone % 2 == 0) return StationLabel(_chain[zone / 2]);
        return $"{StationLabel(_chain[(zone - 1) / 2])}〜{StationLabel(_chain[(zone + 1) / 2])}";
    }

    /// <summary>複数のゾーンをまとめた名前。空なら null。</summary>
    public string? DescribeZones(IReadOnlyCollection<int> zones)
    {
        return zones.Count == 0 ? null : string.Join("・", zones.OrderBy(z => z).Select(ZoneLabel));
    }

    private string StationLabel(string id)
    {
        return _stationName.TryGetValue(id, out var name) && name.Length > 0 ? name : id;
    }

    /// <summary>
    /// 「TH76-TH75」「TH76-75」「76-75」のような駅間、または「TH76」のような駅（構内）を、ゾーンに変換する。
    /// </summary>
    public bool TryParseLocation(string text, out int zone, out string error)
    {
        zone = -1;
        error = "";

        var normalized = Regex.Replace((text ?? "").ToUpperInvariant(), @"\s+", "");
        normalized = Regex.Replace(normalized, "[－ー−―‐–]", "-");
        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            error = $"書き方が正しくありません: {text}（例: TH76-TH75 や TH76）";
            return false;
        }

        var ids = new List<string>();
        foreach (var part in parts)
        {
            var m = Regex.Match(part, @"^(?:TH)?(\d+)(S?)$");
            if (!m.Success)
            {
                error = $"駅IDの書き方が正しくありません: {part}（例: TH76）";
                return false;
            }

            ids.Add("TH" + m.Groups[1].Value.PadLeft(2, '0') + m.Groups[2].Value);
        }

        if (ids.Count == 1)
        {
            if (_stationZone.TryGetValue(ids[0], out var stationZone))
            {
                zone = stationZone;
                return true;
            }

            error = $"路線データにない駅です: {ids[0]}";
            return false;
        }

        if (_sectionZone.TryGetValue(SectionKey(ids[0], ids[1]), out var sectionZone))
        {
            zone = sectionZone;
            return true;
        }

        error = $"その2駅の間は、路線データにありません: {ids[0]}-{ids[1]}（隣り合う駅を書いてください）";
        return false;
    }

    private static string SectionKey(string a, string b)
    {
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private void BuildZones()
    {
        if (_chain.Count < 2) throw new InvalidDataException("LineData.json の line には、2駅以上を書いてください");

        var seen = new HashSet<string>();
        for (var i = 0; i < _chain.Count; i++)
        {
            var id = _chain[i];
            if (!_stationName.ContainsKey(id))
                throw new InvalidDataException($"LineData.json の line の駅「{id}」が、stations にありません");
            if (!seen.Add(id))
                throw new InvalidDataException($"LineData.json の line に、同じ駅が2回あります: {id}");

            _stationZone[id] = i * 2;
            if (i + 1 < _chain.Count) _sectionZone[SectionKey(id, _chain[i + 1])] = i * 2 + 1;
        }
    }

    private void AddCircuits(JsonElement list, int zone, string where)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"LineData.json の {where} は、軌道回路名の配列で書いてください");

        foreach (var item in list.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n)
                ? Str(n)
                : Str(item);
            name = name.Trim();
            if (name.Length == 0) continue;

            if (_circuitZone.TryGetValue(name, out var existing) && existing != zone)
                throw new InvalidDataException($"LineData.json で、軌道回路「{name}」が複数の場所に書かれています（{where} ほか）");

            _circuitZone[name] = zone;
        }
    }

    private static JsonElement Required(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != kind)
            throw new InvalidDataException($"LineData.json に「{name}」がないか、書き方が正しくありません");
        return element;
    }

    internal static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
}
