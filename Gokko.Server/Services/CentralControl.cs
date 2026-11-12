namespace Gokko.Server.Services;

/// <summary>抑止の種類（場内・出発。両方なら全部の進路を取らない）。</summary>
[Flags]
public enum HoldKind
{
    None = 0,
    Arrive = 1,
    Depart = 2,
    Both = Arrive | Depart
}

/// <summary>
/// 集中扱い（司令役の指示）。列車扱い（ユーザーの選択・ダイヤ・所定・待避）より優先される。
/// ・番線の指定: 列番＋駅 → 番線（列車扱いの選択や待避より優先）
/// ・抑止: 列番＋駅 → その駅の場内・出発（どちらか、または両方）の進路を開通させない
/// ・開通指示: 列番＋駅 → 手前の駅を通過していなくても、安全の確認ができしだい開通させる（集中扱いの駅では必須）
/// ・駅扱い（強制進路）: 列番＋駅＋「本線→1番線」などの進路 → 連動に問題がなければ、サーバーの抑止・待ちを無視して開通させる
/// ・駅の出発順序: 駅ごとの列番の並び（上下とも）。進路が競合する列車どうしは、先の列車が駅を抜けるまで出発させない
/// ・単線区間の走行順: 駅間ごとの列番の並び。先の列車が区間を抜けるまで、後の列車をその区間へ出発させない
/// ・駅の扱い: 駅ごとに「列車扱い」「集中扱い」。集中扱いの駅は、開通指示があるまで自動では開通させない
/// 列番で覚えるので、クライアントがつなぎ直しても引き継がれる。サーバーを再起動すると消える。
/// </summary>
public sealed class CentralControl
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Train, string Station), int> _tracks = new();
    private readonly Dictionary<(string Train, string Station), HoldKind> _holds = new();
    private readonly HashSet<(string Train, string Station)> _forces = new();
    private readonly HashSet<string> _centralStations = new();
    private readonly Dictionary<(string Train, string Station), DateTime> _openAt = new();
    private readonly Dictionary<string, List<string>> _orders = new();
    private readonly Dictionary<(string A, string B), List<string>> _sectionOrders = new();
    private readonly Dictionary<(string Train, string Station), string> _manual = new();

    // 順序を最後に変えた順番（出発順序と走行順のどちらが新しいかで、同期の向きを決める）
    private long _stamp;
    private readonly Dictionary<string, long> _orderStamps = new();
    private readonly Dictionary<(string A, string B), long> _sectionStamps = new();

    public long OrderStamp(string station) { lock (_lock) return _orderStamps.GetValueOrDefault(station); }
    public long SectionStamp(string a, string b) { lock (_lock) return _sectionStamps.GetValueOrDefault(SectionKey(a, b)); }

    public event Action<string>? Changed;

    // ---- 駅の出発順序 ----

    /// <summary>駅の出発順序（上下の列車をまとめた列番の並び）。</summary>
    public IReadOnlyList<string>? Order(string station)
    {
        lock (_lock) return _orders.TryGetValue(station, out var l) ? l.ToList() : null;
    }

    public void SetOrder(string station, IReadOnlyList<string>? trains)
    {
        lock (_lock)
        {
            if (trains is { Count: > 0 }) _orders[station] = trains.Distinct().ToList();
            else _orders.Remove(station);
            _orderStamps[station] = ++_stamp;
        }

        Changed?.Invoke($"集中扱い: {station} 出発順序 → {(trains is { Count: > 0 } ? string.Join(" → ", trains) : "自動に戻す")}");
    }

    /// <summary>走行順に合わせて出発順序を直す（同期。記録には出さず、走行順と同じ更新時刻にする）。</summary>
    public void SyncOrder(string station, IReadOnlyList<string> trains, long stamp)
    {
        lock (_lock)
        {
            _orders[station] = trains.Distinct().ToList();
            _orderStamps[station] = stamp;
        }
    }

    /// <summary>出発順序に合わせて走行順を直す（同期。記録には出さず、出発順序と同じ更新時刻にする）。</summary>
    public void SyncSectionOrder(string a, string b, IReadOnlyList<string> trains, long stamp)
    {
        lock (_lock)
        {
            _sectionOrders[SectionKey(a, b)] = trains.Distinct().ToList();
            _sectionStamps[SectionKey(a, b)] = stamp;
        }
    }

    /// <summary>その駅を抜けた列車を出発順序から外す（黙って。全部抜けたら順序を消す）。</summary>
    public void RemoveFromOrder(string station, string train)
    {
        lock (_lock)
        {
            if (!_orders.TryGetValue(station, out var l) || !l.Remove(train)) return;
            if (l.Count == 0) _orders.Remove(station);
        }
    }

    // ---- 単線区間の走行順 ----

    private static (string, string) SectionKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>単線区間（駅間）の走行順。列番の並び（上下とも）。</summary>
    public IReadOnlyList<string>? SectionOrder(string a, string b)
    {
        lock (_lock) return _sectionOrders.TryGetValue(SectionKey(a, b), out var l) ? l.ToList() : null;
    }

    public void SetSectionOrder(string a, string b, IReadOnlyList<string>? trains)
    {
        lock (_lock)
        {
            if (trains is { Count: > 0 }) _sectionOrders[SectionKey(a, b)] = trains.Distinct().ToList();
            else _sectionOrders.Remove(SectionKey(a, b));
            _sectionStamps[SectionKey(a, b)] = ++_stamp;
        }

        Changed?.Invoke($"集中扱い: {a}〜{b} 走行順 → {(trains is { Count: > 0 } ? string.Join(" → ", trains) : "自動に戻す")}");
    }

    /// <summary>区間を抜けた列車を走行順から外す（黙って）。</summary>
    public void RemoveFromSectionOrder(string a, string b, string train)
    {
        lock (_lock)
        {
            var k = SectionKey(a, b);
            if (!_sectionOrders.TryGetValue(k, out var l) || !l.Remove(train)) return;
            if (l.Count == 0) _sectionOrders.Remove(k);
        }
    }

    // ---- 発車時刻（ダイヤグラムのスジずらし） ----

    /// <summary>ダイヤグラムで指定した発車時刻（UTC）。この時刻の30秒前まで、その駅の出発進路を開通させない。</summary>
    public DateTime? OpenAt(string train, string station)
    {
        lock (_lock) return _openAt.TryGetValue((train, station), out var t) ? t : null;
    }

    public void SetOpenAt(string train, string station, DateTime? utc)
    {
        lock (_lock)
        {
            if (utc is { } t) _openAt[(train, station)] = t;
            else _openAt.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 発車時刻 → {(utc?.ToLocalTime().ToString("HH:mm:ss") ?? "指定解除")}");
    }

    // ---- 臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わらない） ----

    private readonly Dictionary<(string Train, string Station), string> _stops = new();

    /// <summary>"停車"（臨時停車）・"通過"（臨時通過）・"打ち切り"。指定がなければ null。</summary>
    public string? StopOverride(string train, string station)
    {
        lock (_lock) return _stops.GetValueOrDefault((train, station));
    }

    public void SetStop(string train, string station, string? kind)
    {
        lock (_lock)
        {
            if (kind is "停車" or "通過" or "打ち切り") _stops[(train, station)] = kind;
            else _stops.Remove((train, station));
        }

        var label = kind switch { "停車" => "臨時停車", "通過" => "臨時通過", "打ち切り" => "打ち切り", _ => "停車・通過を元に戻す" };
        Changed?.Invoke($"集中扱い: {train} {station} {label}");
    }

    // ---- 番線・抑止・開通指示 ----

    public int? TrackOverride(string train, string station)
    {
        lock (_lock) return _tracks.TryGetValue((train, station), out var t) ? t : null;
    }

    public HoldKind Hold(string train, string station)
    {
        lock (_lock) return _holds.GetValueOrDefault((train, station));
    }

    public bool IsHeld(string train, string station) => Hold(train, station) != HoldKind.None;

    public bool IsForced(string train, string station)
    {
        lock (_lock) return _forces.Contains((train, station));
    }

    public bool IsCentral(string station)
    {
        lock (_lock) return _centralStations.Contains(station);
    }

    public IReadOnlyCollection<string> CentralStations
    {
        get
        {
            lock (_lock) return _centralStations.ToList();
        }
    }

    public void SetTrack(string train, string station, int? track)
    {
        lock (_lock)
        {
            if (track is { } t) _tracks[(train, station)] = t;
            else _tracks.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 番線 → {(track?.ToString() ?? "指定解除")}");
    }

    /// <summary>抑止をかける・外す。kind を省くと場内・出発の両方。</summary>
    public void SetHold(string train, string station, bool hold, HoldKind kind = HoldKind.Both)
    {
        lock (_lock)
        {
            var cur = _holds.GetValueOrDefault((train, station));
            cur = hold ? cur | kind : cur & ~kind;
            if (cur == HoldKind.None) _holds.Remove((train, station));
            else _holds[(train, station)] = cur;
        }

        var what = kind switch { HoldKind.Arrive => "場内抑止", HoldKind.Depart => "出発抑止", _ => "抑止" };
        Changed?.Invoke($"集中扱い: {train} {station} {what}{(hold ? "" : "解除")}");
    }

    private readonly HashSet<(string Train, string Station)> _cancels = new();

    public void Force(string train, string station)
    {
        lock (_lock)
        {
            _forces.Add((train, station));
            _holds.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 開通指示");
    }

    // ---- 駅扱い（強制進路） ----

    /// <summary>
    /// 駅扱い: 「本線→1番線」（arrive:1）「1番線→本線」（depart:1）「通過」（through）「入換」（shunt:3）などの進路を、
    /// 連動に問題がなければ（進路に他の列車がいない・他の列車の進路と重ならない）、抑止・待ちを無視して開通させる。
    /// 実際の開通は進路の判断（RouteEngine）が次の周期で行う。
    /// </summary>
    public void RequestManual(string train, string station, string action, string label)
    {
        lock (_lock) _manual[(train, station)] = action;
        Changed?.Invoke($"駅扱い: {train} {station} {label}");
    }

    /// <summary>駅扱いの指示を受け取る（1回だけ）。</summary>
    public string? TakeManual(string train, string station)
    {
        lock (_lock) return _manual.Remove((train, station), out var a) ? a : null;
    }

    public void SetCentral(string station, bool central)
    {
        lock (_lock)
        {
            if (central) _centralStations.Add(station);
            else _centralStations.Remove(station);
        }

        Changed?.Invoke($"集中扱い: {station} を{(central ? "集中扱い" : "列車扱い")}に切り替え");
    }

    /// <summary>列車がいなくなったとき（列番が変わった・退出した）に、その列車の指示を消す。</summary>
    public void ForgetTrain(string train)
    {
        lock (_lock)
        {
            foreach (var k in _tracks.Keys.Where(k => k.Train == train).ToList()) _tracks.Remove(k);
            foreach (var k in _holds.Keys.Where(k => k.Train == train).ToList()) _holds.Remove(k);
            _forces.RemoveWhere(k => k.Train == train);
            _cancels.RemoveWhere(k => k.Train == train);
            foreach (var k in _manual.Keys.Where(k => k.Train == train).ToList()) _manual.Remove(k);
            foreach (var k in _openAt.Keys.Where(k => k.Train == train).ToList()) _openAt.Remove(k);
            foreach (var k in _stops.Keys.Where(k => k.Train == train).ToList()) _stops.Remove(k);
        }
    }
}
