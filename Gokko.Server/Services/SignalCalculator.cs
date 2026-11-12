using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 信号の連動。他の参加者の列車がいる軌道回路から、落とすべき信号の現示を求める。
/// 1. 防護区間（次の信号機までの軌道回路）に他の列車がいる信号機を R にする
/// 2. その手前の信号機を、灯式の表（次の現示 → 自分の現示）に従って決める（次の信号機が複数なら、いちばん高い現示を使う）
/// G より低いものだけを返す。ゲームは「今より低い現示」しか受け付けないので、ゲーム自身の判断（進路など）とは低い方が表示される。
/// </summary>
public sealed class SignalCalculator
{
    private static readonly string[] Order = ["R", "YY", "Y", "YG", "G"];
    private const int MaxDepth = 6;

    private readonly RailData _data;
    private readonly Dictionary<string, List<string>> _previous = new();

    public SignalCalculator(RailData data)
    {
        _data = data;
        foreach (var s in data.Signals.Values)
        foreach (var n in s.Next)
        {
            if (!_previous.TryGetValue(n, out var list)) _previous[n] = list = new List<string>();
            list.Add(s.Name);
        }
    }

    /// <summary>他の列車がいる軌道回路から、G より低くする信号機と現示を求める。</summary>
    public List<SignalCommand> Calculate(IReadOnlySet<string> occupiedByOthers)
    {
        if (occupiedByOthers.Count == 0) return new List<SignalCommand>();

        var red = _data.Signals.Values
            .Where(s => !s.IsRepeater && s.Protects.Count > 0 && s.Protects.Any(occupiedByOthers.Contains))
            .Select(s => s.Name)
            .ToHashSet();
        if (red.Count == 0) return new List<SignalCommand>();

        // 影響を受けるのは、R の信号機から手前へたどれる範囲だけ
        var affected = new HashSet<string>(red);
        var frontier = new List<string>(red);
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var name in frontier)
            foreach (var prev in _previous.GetValueOrDefault(name, []))
                if (affected.Add(prev)) next.Add(prev);
            frontier = next;
        }

        var memo = new Dictionary<string, string>();
        string PhaseOf(string name, int guard)
        {
            if (memo.TryGetValue(name, out var cached)) return cached;
            if (red.Contains(name)) return memo[name] = "R";
            if (guard > MaxDepth + 2 || !affected.Contains(name) || !_data.Signals.TryGetValue(name, out var s)) return "G";

            var nexts = s.Next.Where(n => s.IsRepeater || !n.Contains("中継")).ToList();
            if (nexts.Count == 0) return memo[name] = "G";
            var nextPhase = nexts.Select(n => PhaseOf(n, guard + 1)).OrderByDescending(p => Array.IndexOf(Order, p)).First();
            var table = s.Type != null && _data.SignalTypes.TryGetValue(s.Type, out var t) ? t : _data.SignalTypes.GetValueOrDefault("4灯YG式");
            var phase = table != null && table.TryGetValue(nextPhase, out var p) ? p : "G";
            return memo[name] = phase;
        }

        var result = new List<SignalCommand>();
        foreach (var name in affected)
        {
            var phase = PhaseOf(name, 0);
            if (phase != "G") result.Add(new SignalCommand { Name = name, Phase = phase });
        }

        return result.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }
}
