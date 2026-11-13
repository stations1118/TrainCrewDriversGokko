using System.Text.RegularExpressions;

namespace Gokko.Common;

/// <summary>
/// 運行番号（列車番号の付け方から逆算する）。
/// 列番 = [回・臨・試] ＋ 始発駅の時刻（時） ＋ 下2桁 ＋ [種別の記号 A・K・B・C など]。
/// 上り（赤山町方面）は運行番号の下2桁そのまま、下り（館浜方面）は下2桁＋1。
/// 運行番号が100番台なら数字に＋3000、200番台なら＋6000する。
/// 例: 3643K → 142 運行（3643−3000＝643 → 6時台・下2桁43 → 下りなので42 → 100番台）。折り返しの 3842B も 142 運行。
/// 1182C → 1283C → 1282C → 1383C は、どれも 82 運行。
/// </summary>
public static partial class Unyo
{
    [GeneratedRegex(@"^(?:回|臨|試)?(\d+)")]
    private static partial Regex NumberPart();

    /// <summary>列番から運行番号を求める。数字が読めなければ null。</summary>
    public static int? FromTrain(string? train)
    {
        if (string.IsNullOrWhiteSpace(train)) return null;
        var m = NumberPart().Match(train.Normalize(System.Text.NormalizationForm.FormKC).Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        var offset = 0;
        if (n >= 6000) { offset = 200; n -= 6000; }
        else if (n >= 3000) { offset = 100; n -= 3000; }
        var low = n % 100;
        return offset + low - low % 2;
    }

    /// <summary>列番の時（始発駅の時刻の時）。数字が読めなければ null。</summary>
    public static int? HourOf(string? train)
    {
        if (string.IsNullOrWhiteSpace(train)) return null;
        var m = NumberPart().Match(train.Normalize(System.Text.NormalizationForm.FormKC).Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        if (n >= 6000) n -= 6000;
        else if (n >= 3000) n -= 3000;
        return n / 100;
    }
}
