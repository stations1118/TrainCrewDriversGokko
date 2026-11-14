using System.Collections.Concurrent;
using System.Text.Json;
using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>配信（/hub）に参加している車掌1人分。在線としては数えない。</summary>
public sealed class ConductorInfo
{
    public required string ConnectionId { get; init; }
    public string TrainNumber { get; set; } = "";
    public bool InGame { get; set; }
    public float TraveledDistance { get; set; }
    public DateTime LastReportUtc { get; set; }
}

/// <summary>
/// 運転士と車掌の組み合わせ。
/// ・配信（/hub）に車掌として参加した人を覚える（<see cref="PlayerRegistry"/> には入れないので、在線・進路・信号には関わらない）
/// ・中継（/crew）: 運転士の操作を同じ列番の車掌へ、車掌の操作を運転士へそのまま送る
/// ・運転士の操作は、あとから再生できるように logs/crew/ に記録する（1回の乗務ごとに1ファイル）
/// ・記録の再生: 車掌のゲームを、記録した運転士の操作で動かす（1台で中継の流れを確かめるため）
/// </summary>
public sealed class CrewRelay(IHubContext<CrewHub> hub, ILogger<CrewRelay> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- 配信（/hub）の車掌 ----
    private readonly ConcurrentDictionary<string, ConductorInfo> _conductors = new();

    public void AddConductor(string connectionId) =>
        _conductors[connectionId] = new ConductorInfo { ConnectionId = connectionId, LastReportUtc = DateTime.UtcNow };

    public bool IsConductor(string connectionId) => _conductors.ContainsKey(connectionId);

    public void UpdateConductor(string connectionId, ClientReport report)
    {
        if (!_conductors.TryGetValue(connectionId, out var c)) return;
        lock (c)
        {
            c.TrainNumber = (report.TrainNumber ?? "").Trim();
            if (c.TrainNumber.Length > 64) c.TrainNumber = c.TrainNumber[..64];
            c.InGame = report.InGame;
            c.TraveledDistance = report.TraveledDistance;
            c.LastReportUtc = DateTime.UtcNow;
        }
    }

    public void RemoveConductor(string connectionId) => _conductors.TryRemove(connectionId, out _);

    public List<ConductorInfo> Conductors()
    {
        var list = new List<ConductorInfo>();
        foreach (var c in _conductors.Values)
            lock (c)
                list.Add(new ConductorInfo
                {
                    ConnectionId = c.ConnectionId, TrainNumber = c.TrainNumber, InGame = c.InGame,
                    TraveledDistance = c.TraveledDistance, LastReportUtc = c.LastReportUtc
                });
        return list;
    }

    // ---- 中継（/crew） ----

    private sealed class Member
    {
        public required string ConnectionId { get; init; }
        public CrewRole Role { get; set; }
        public string TrainNumber { get; set; } = "";
        public long LastSeq { get; set; } = -1;

        /// <summary>運転士の操作の記録（乗務に入ってから、列番が変わるか抜けるまで）。</summary>
        public StreamWriter? Recording { get; set; }
        public long RecordingStartMs { get; set; }
    }

    private readonly ConcurrentDictionary<string, Member> _members = new();
    private readonly ConcurrentDictionary<string, Replay> _replays = new(); // 列番 → 再生中の記録

    private static string Group(string train, CrewRole role) => $"{(role == CrewRole.Driver ? "d" : "c")}:{train}";

    private static string RecordingDir => Path.Combine(AppContext.BaseDirectory, "logs", "crew");

    public async Task JoinAsync(string connectionId, CrewRole role, string train)
    {
        train = (train ?? "").Trim();
        if (train.Length > 64) train = train[..64];
        var m = _members.GetOrAdd(connectionId, id => new Member { ConnectionId = id });
        string oldTrain;
        lock (m)
        {
            oldTrain = m.TrainNumber;
            if (oldTrain == train && m.Role == role) return;
            CloseRecording(m);
            m.Role = role;
            m.TrainNumber = train;
            m.LastSeq = -1;
        }

        foreach (var r in new[] { CrewRole.Driver, CrewRole.Conductor })
            if (oldTrain.Length > 0) await hub.Groups.RemoveFromGroupAsync(connectionId, Group(oldTrain, r));
        if (train.Length > 0) await hub.Groups.AddToGroupAsync(connectionId, Group(train, role));
        logger.LogInformation("乗務 {Role} {Train}（{Id}）", role == CrewRole.Driver ? "運転士" : "車掌", train, connectionId[..Math.Min(6, connectionId.Length)]);
        if (oldTrain.Length > 0) await SendStatusAsync(oldTrain);
        if (train.Length > 0) await SendStatusAsync(train);
    }

    public async Task LeaveAsync(string connectionId)
    {
        if (!_members.TryRemove(connectionId, out var m)) return;
        string train;
        lock (m)
        {
            CloseRecording(m);
            train = m.TrainNumber;
        }

        if (train.Length == 0) return;
        if (m.Role == CrewRole.Conductor && !Members(train).Any(x => x.Role == CrewRole.Conductor)) StopReplay(train);
        await SendStatusAsync(train);
    }

    /// <summary>運転士の操作を、同じ列番の車掌へ送り、記録する。</summary>
    public Task FromDriverAsync(string connectionId, DriverInput input)
    {
        if (!_members.TryGetValue(connectionId, out var m)) return Task.CompletedTask;
        string train;
        lock (m)
        {
            if (m.Role != CrewRole.Driver || m.TrainNumber.Length == 0 || input.Seq <= m.LastSeq) return Task.CompletedTask;
            m.LastSeq = input.Seq;
            train = m.TrainNumber;
            Record(m, input);
        }

        if (_replays.ContainsKey(train)) return Task.CompletedTask; // 再生中は、記録の操作だけを送る
        return hub.Clients.Group(Group(train, CrewRole.Conductor)).SendAsync(CrewProtocol.ReceiveDriverInput, input);
    }

    /// <summary>車掌の操作を、同じ列番の運転士へ送る。</summary>
    public Task FromConductorAsync(string connectionId, ConductorInput input)
    {
        if (!_members.TryGetValue(connectionId, out var m)) return Task.CompletedTask;
        string train;
        lock (m)
        {
            if (m.Role != CrewRole.Conductor || m.TrainNumber.Length == 0) return Task.CompletedTask;
            train = m.TrainNumber;
        }

        if (!ConductorInput.Actions.Contains(input.Action ?? "-")) return Task.CompletedTask;
        return hub.Clients.Group(Group(train, CrewRole.Driver)).SendAsync(CrewProtocol.ReceiveConductorInput, input);
    }

    private List<Member> Members(string train) => _members.Values.Where(x => x.TrainNumber == train).ToList();

    public CrewStatus StatusOf(string train)
    {
        var list = Members(train);
        return new CrewStatus
        {
            TrainNumber = train,
            Drivers = list.Count(x => x.Role == CrewRole.Driver),
            Conductors = list.Count(x => x.Role == CrewRole.Conductor),
            Replaying = _replays.TryGetValue(train, out var r) ? r.Name : null
        };
    }

    private async Task SendStatusAsync(string train)
    {
        var status = StatusOf(train);
        try
        {
            await hub.Clients.Groups(Group(train, CrewRole.Driver), Group(train, CrewRole.Conductor)).SendAsync(CrewProtocol.ReceiveStatus, status);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "乗務の顔ぶれを送れなかった");
        }
    }

    // ---- 記録 ----

    private void Record(Member m, DriverInput input)
    {
        try
        {
            if (m.Recording == null)
            {
                Directory.CreateDirectory(RecordingDir);
                var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeName(m.TrainNumber)}.jsonl";
                m.Recording = new StreamWriter(Path.Combine(RecordingDir, name), append: false) { AutoFlush = true };
                m.RecordingStartMs = input.ClientMs;
            }

            m.Recording.WriteLine(JsonSerializer.Serialize(input, Json));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "運転士の操作を記録できなかった");
            CloseRecording(m);
        }
    }

    private static void CloseRecording(Member m)
    {
        try
        {
            m.Recording?.Dispose();
        }
        catch
        {
            // 閉じるときのエラーは無視する
        }

        m.Recording = null;
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }

    public List<CrewRecordingInfo> ListRecordings()
    {
        var list = new List<CrewRecordingInfo>();
        if (!Directory.Exists(RecordingDir)) return list;
        foreach (var file in new DirectoryInfo(RecordingDir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).Take(30))
        {
            try
            {
                var lines = File.ReadAllLines(file.FullName);
                if (lines.Length == 0) continue;
                var first = JsonSerializer.Deserialize<DriverInput>(lines[0], Json);
                var last = JsonSerializer.Deserialize<DriverInput>(lines[^1], Json);
                var name = Path.GetFileNameWithoutExtension(file.Name);
                list.Add(new CrewRecordingInfo
                {
                    Name = name,
                    TrainNumber = name.Contains('_') ? name[(name.IndexOf('_') + 1)..] : "",
                    StartedAt = file.CreationTime,
                    Seconds = first != null && last != null ? (last.ClientMs - first.ClientMs) / 1000.0 : 0,
                    Count = lines.Length
                });
            }
            catch
            {
                // 書きかけ・壊れた記録は飛ばす
            }
        }

        return list;
    }

    // ---- 再生 ----

    private sealed class Replay
    {
        public required string Name { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    /// <summary>車掌が、記録した運転士の操作で自分の列車を動かす。同じ列番の運転士がいるときは使えない。</summary>
    public async Task<string> StartReplayAsync(string connectionId, string name)
    {
        if (!_members.TryGetValue(connectionId, out var m) || m.Role != CrewRole.Conductor || m.TrainNumber.Length == 0)
            return "車掌として列番を決めてから再生してください";
        var train = m.TrainNumber;
        if (Members(train).Any(x => x.Role == CrewRole.Driver)) return $"列番 {train} には運転士がいるので、再生できません";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) return "記録の名前が正しくありません";
        var path = Path.Combine(RecordingDir, name + ".jsonl");
        if (!File.Exists(path)) return "記録が見つかりません";

        List<DriverInput> inputs;
        try
        {
            inputs = File.ReadAllLines(path).Select(l => JsonSerializer.Deserialize<DriverInput>(l, Json)).OfType<DriverInput>().ToList();
        }
        catch
        {
            return "記録を読めませんでした";
        }

        if (inputs.Count == 0) return "記録が空です";
        StopReplay(train);
        var replay = new Replay { Name = name, Cts = new CancellationTokenSource() };
        _replays[train] = replay;
        await SendStatusAsync(train);
        _ = Task.Run(() => RunReplayAsync(train, replay, inputs));
        logger.LogInformation("再生 {Name} → 列番 {Train}", name, train);
        return $"記録 {name} の再生を始めました（{inputs.Count}件・{(inputs[^1].ClientMs - inputs[0].ClientMs) / 1000.0:0}秒）";
    }

    public void StopReplay(string train)
    {
        if (_replays.TryRemove(train, out var r)) r.Cts.Cancel();
    }

    public string? TrainOf(string connectionId) => _members.TryGetValue(connectionId, out var m) ? m.TrainNumber : null;

    private async Task RunReplayAsync(string train, Replay replay, List<DriverInput> inputs)
    {
        var ct = replay.Cts.Token;
        var start = Environment.TickCount64;
        var t0 = inputs[0].ClientMs;
        var seq = 0L;
        try
        {
            foreach (var input in inputs)
            {
                var wait = input.ClientMs - t0 - (Environment.TickCount64 - start);
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                input.Seq = ++seq;
                await hub.Clients.Group(Group(train, CrewRole.Conductor)).SendAsync(CrewProtocol.ReceiveDriverInput, input, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 止めた
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "再生でエラー");
        }
        finally
        {
            if (_replays.TryGetValue(train, out var cur) && ReferenceEquals(cur, replay)) _replays.TryRemove(train, out _);
            await SendStatusAsync(train);
        }
    }
}
