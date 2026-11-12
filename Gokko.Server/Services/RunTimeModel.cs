namespace Gokko.Server.Services;

/// <summary>
/// 標準運転時分から、列車がある駅に着く（通過する）までの見込み時間を出す（待避の判定に使う）。
/// 種別・方向ごとに、標準運転時分の区間をつないで「始点からの時刻」の表を作る。
/// 表にない駅（通過駅で時刻の載っていない駅）は、前後の駅の時刻をゾーンの位置で按分する。
/// </summary>
public sealed class RunTimeModel
{
    private readonly RailData _data;

    /// <summary>(種別, 方向) → ゾーン → (着の時刻, 発の時刻)。秒。</summary>
    private readonly Dictionary<(string, Dir), Dictionary<int, (double Arr, double Dep)>> _tables = new();

    public RunTimeModel(RailData data)
    {
        _data = data;
        foreach (var g in data.RunTimes.GroupBy(r => (r.Type, r.Dir)))
            _tables[g.Key] = Build(g.Key.Dir, g.ToList());
    }

    private Dictionary<int, (double, double)> Build(Dir dir, List<RunTimeRow> rows)
    {
        var line = _data.Line;
        // 発駅ごとに、最初の区間（同じ発駅が複数あれば短い方）
        var next = new Dictionary<string, RunTimeRow>();
        foreach (var r in rows.OrderBy(r => r.Seconds))
        {
            var from = line.StationIdByName(r.From);
            if (from != null) next.TryAdd(from, r);
        }

        var times = new Dictionary<int, (double Arr, double Dep)>();
        // 進む向きに並べて、つながる区間をたどる
        var ordered = line.StationIds.Where(id => line.ZoneOfStation(id) >= 0).ToList();
        if (dir == Dir.Up) ordered.Reverse();
        string? current = ordered.FirstOrDefault(id => next.ContainsKey(id));
        if (current == null) return times;

        times[line.ZoneOfStation(current)] = (0, 0);
        var guard = 0;
        while (current != null && next.TryGetValue(current, out var row) && guard++ < 200)
        {
            var to = line.StationIdByName(row.To);
            if (to == null) break;
            var dep = times[line.ZoneOfStation(current)].Dep;
            var arr = dep + row.Seconds;
            times[line.ZoneOfStation(to)] = (arr, arr + (row.ToStops ? _data.DefaultDwellSeconds : 0));
            current = to;
        }

        return times;
    }

    /// <summary>
    /// 列車の今の位置から、目的の駅に着く（通過する）までの見込み秒数。見込めないときは null。
    /// </summary>
    /// <param name="frontZone">列車の先頭のゾーン</param>
    /// <param name="fraction">駅間にいるとき、その駅間をどれだけ進んだか（0〜1）。分からなければ 0.5</param>
    /// <param name="stopsAtCurrent">駅にいるとき、そこで停車しているか（停車中なら、今から発車するとみなす）</param>
    public double? SecondsTo(string type, Dir dir, int frontZone, double fraction, string targetStationId)
    {
        var table = _tables.GetValueOrDefault((type, dir)) ?? _tables.GetValueOrDefault(("普通", dir));
        if (table == null || table.Count < 2) return null;

        var target = _data.Line.ZoneOfStation(targetStationId);
        if (target < 0) return null;
        if ((target - frontZone) * dir.Step() < 0) return null; // もう過ぎている

        double? TimeAt(int zone, bool departure)
        {
            if (table.TryGetValue(zone, out var t)) return departure ? t.Dep : t.Arr;
            // 表にない駅・駅間は、前後の表の駅で按分する
            var before = table.Keys.Where(z => (z - zone) * dir.Step() < 0).OrderBy(z => Math.Abs(z - zone)).Cast<int?>().FirstOrDefault();
            var after = table.Keys.Where(z => (z - zone) * dir.Step() > 0).OrderBy(z => Math.Abs(z - zone)).Cast<int?>().FirstOrDefault();
            if (before is not { } b || after is not { } a) return null;
            var f = (double)(zone - b) / (a - b);
            return table[b].Dep + (table[a].Arr - table[b].Dep) * f;
        }

        double? now;
        if (frontZone % 2 == 0)
        {
            now = TimeAt(frontZone, departure: true);
        }
        else
        {
            var prev = TimeAt(frontZone - dir.Step(), departure: true);
            var nxt = TimeAt(frontZone + dir.Step(), departure: false);
            now = prev is { } p && nxt is { } n ? p + (n - p) * Math.Clamp(fraction, 0, 1) : null;
        }

        var arrive = TimeAt(target, departure: false);
        return now is { } a0 && arrive is { } b0 ? Math.Max(0, b0 - a0) : null;
    }

    /// <summary>標準運転時分での、その駅の停車時分（秒）。通過・表にない駅は 0。</summary>
    public double DwellAt(string type, Dir dir, string stationId)
    {
        var table = _tables.GetValueOrDefault((type, dir)) ?? _tables.GetValueOrDefault(("普通", dir));
        var zone = _data.Line.ZoneOfStation(stationId);
        return table != null && table.TryGetValue(zone, out var t) ? t.Dep - t.Arr : 0;
    }

    /// <summary>駅から駅まで（発車してから着くまで）の見込み秒数。</summary>
    public double? SecondsBetween(string type, Dir dir, string fromStationId, string toStationId)
    {
        var from = _data.Line.ZoneOfStation(fromStationId);
        return from < 0 ? null : SecondsTo(type, dir, from, 0, toStationId);
    }
}
