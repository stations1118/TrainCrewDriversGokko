using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 信号の連動。他の参加者の列車がいる軌道回路から、停止にする信号機を求める。
/// 防護区間（次の信号機までの軌道回路）に他の列車がいる信号機だけを R にして送る。手前の信号機の Y・YG は送らない。
/// TRAIN CREW は、上限で R にした信号機の手前を、灯式に従って自分で下げる（実機で確認: 下り閉塞49 を R にすると、
/// 0.5秒以内に 55＝Y、59＝YG になり、None で戻すと 0.5秒以内に G に戻った）。
/// ゲームは開いている進路を知っているので、分岐の手前の信号機も、実際に開いている進路の先に合わせて下がる。
/// 手前の現示をサーバーで計算して送っていたころは、ほかの列車が動いたあと古い Y・YG の上限が残り、本来より低い現示が出ることがあった。
/// ゲームは「今より低い現示」しか受け付けないので、ゲーム自身の判断（自分の列車・進路）と低い方が表示される。
/// </summary>
public sealed class SignalCalculator(RailData data)
{
    /// <summary>他の列車がいる軌道回路から、停止（R）にする信号機を求める。</summary>
    public List<SignalCommand> Calculate(IReadOnlySet<string> occupiedByOthers)
    {
        if (occupiedByOthers.Count == 0) return new List<SignalCommand>();

        return data.Signals.Values
            .Where(s => !s.IsRepeater && s.Protects.Count > 0 && s.Protects.Any(occupiedByOthers.Contains))
            .Select(s => new SignalCommand { Name = s.Name, Phase = "R" })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }
}
