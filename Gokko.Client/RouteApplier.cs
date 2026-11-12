using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// サーバーが決めた進路（ClientState.Routes / DeleteRoutes）を、自分のゲームに反映する（在線共有モード）。
/// ・ゲームにまだない進路は SetRoute で送る（届いた順＝サーバーが決めた順。出発 → 場内）。届かなければ3秒おきに送り直す
/// ・一度ゲームで開通を確認した指示（Id）は、二度と送らない。
///   ゲームは列車が通過すると進路を自動で消すので、送り直すと通過後にもう一度開通してしまうため
/// ・取り消しの指示が来た進路は、ゲームにあれば DeleteRoute2 で消す（同じ連動装置へは1つずつ）
/// </summary>
public sealed class RouteApplier
{
    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(3);

    private readonly HashSet<long> _seen = new();
    private readonly Dictionary<long, DateTime> _sentAt = new();
    private readonly Dictionary<(string, string), DateTime> _deleteSentAt = new();

    public void Reset()
    {
        _seen.Clear();
        _sentAt.Clear();
        _deleteSentAt.Clear();
    }

    public async Task TickAsync(GameLink link, ClientState? state, string trainNumber)
    {
        var game = link.Latest;
        if (state == null || !link.IsConnected || game is not { InGame: true } || game.Routes == null) return;

        var present = game.Routes.Select(r => (r.Interlock, r.Route)).ToHashSet();
        var now = DateTime.UtcNow;

        foreach (var r in state.Routes)
        {
            if (_seen.Contains(r.Id)) continue;
            if (present.Contains((r.Interlocking, r.RouteName)))
            {
                _seen.Add(r.Id);
                continue;
            }

            if (_sentAt.TryGetValue(r.Id, out var at) && now - at < Resend) continue;
            try
            {
                var train = r.TrainNumber.Length > 0 ? r.TrainNumber : trainNumber;
                // 列選: サーバーがダイヤの種別表記から作った文字があればそれ（手入力の列番がシナリオと違っても正しく出す）、なければゲームの種別から
                var indicator = r.UseTrainType ? (r.Indicator.Length > 0 ? r.Indicator : TrainTypeIndicator(game.TrainClass)) : r.Indicator;
                await link.SendCommandAsync("SetRoute", [r.Interlocking, r.RouteName, indicator, train, r.StopType]);
                _sentAt[r.Id] = now;
            }
            catch
            {
                return;
            }
        }

        foreach (var d in state.DeleteRoutes.GroupBy(d => d.Interlocking).Select(g => g.First()))
        {
            var key = (d.Interlocking, d.RouteName);
            if (!present.Contains(key)) continue;
            if (_deleteSentAt.TryGetValue(key, out var at) && now - at < Resend) continue;
            try
            {
                await link.SendCommandAsync("DeleteRoute2", [d.Interlocking, d.RouteName]);
                _deleteSentAt[key] = now;
            }
            catch
            {
                return;
            }
        }

        // サーバーの指示からなくなった Id の記録は捨てる（サーバーは解除済みの指示を二度と出さないので安全）
        var live = state.Routes.Select(r => r.Id).ToHashSet();
        _seen.RemoveWhere(id => !live.Contains(id));
        foreach (var id in _sentAt.Keys.Where(id => !live.Contains(id)).ToList()) _sentAt.Remove(id);
    }

    /// <summary>
    /// 出発信号機の列車選別表示（列選）に出す種別の文字。
    /// ゲームの種別（myTrainData.Class）から作る。
    /// </summary>
    public static string TrainTypeIndicator(string gameClass)
    {
        var name = gameClass ?? "";
        if (name == "臨時") return "回送";
        if (name.Contains("臨時")) name = name.Replace("臨時", "");
        else if (name.Contains("だんじり")) name = name.Replace("だんじり", "");
        return name switch
        {
            "回送" or "A特" or "B特" or "C特1" or "C特2" or "C特3" or "C特4" or "D特" or "快速急行" or "急行" or "区急" or "準急" or "普通" => name,
            "試運転" => "回送",
            "C特" => "C特4",
            "特急" => "D特",
            "区間急行" => "区急",
            "快急" => "快速急行",
            _ => ""
        };
    }

    /// <summary>自列車の進路をすべて消す（在線共有モードをやめるとき・終了時）。</summary>
    public async Task ClearAllAsync(GameLink link, string trainNumber)
    {
        var game = link.Latest;
        if (!link.IsConnected || game?.Routes == null) return;
        var mine = game.Routes.Where(r => r.Train == trainNumber || trainNumber.Length == 0).ToList();
        foreach (var r in mine)
        {
            try
            {
                await link.SendCommandAsync("DeleteRoute2", [r.Interlock, r.Route]);
                await Task.Delay(100);
            }
            catch
            {
                return;
            }
        }

        Reset();
    }
}
