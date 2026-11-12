using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 参加者全員へ状態を配信する。一定間隔（既定0.5秒）ごとに送るほか、特発が変わった瞬間にも送る。
/// 毎回「その人に必要な状態の全体」を送るので、クライアントは届いたもので置き換えるだけでよい。
/// 在線共有モードの参加者には、進路の判断（RouteEngine）と信号の連動（SignalCalculator）の結果も入れる。
/// </summary>
public sealed class StateBroadcaster : BackgroundService
{
    private readonly IHubContext<GameHub> _hub;
    private readonly PlayerRegistry _players;
    private readonly IncidentManager _incidents;
    private readonly GameClock _clock;
    private readonly GameData _data;
    private readonly RouteEngine _routes;
    private readonly SignalCalculator _signals;
    private readonly ServerOptions _options;
    private readonly ILogger<StateBroadcaster> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public StateBroadcaster(IHubContext<GameHub> hub, PlayerRegistry players, IncidentManager incidents, GameClock clock,
        GameData data, RouteEngine routes, SignalCalculator signals, ServerOptions options, ILogger<StateBroadcaster> logger)
    {
        _hub = hub;
        _players = players;
        _incidents = incidents;
        _clock = clock;
        _data = data;
        _routes = routes;
        _signals = signals;
        _options = options;
        _logger = logger;
        _incidents.Changed += Trigger;
    }

    /// <summary>次の間隔を待たずに、すぐ配信する。</summary>
    public void Trigger()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // すでに配信待ち
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.BroadcastIntervalMs, 100, 5000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _routes.Tick();
                await BroadcastAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "配信でエラー");
            }
        }
    }

    private async Task BroadcastAsync()
    {
        var players = _players.Snapshot();
        if (players.Count == 0) return;

        var active = _incidents.Snapshot();
        var activeNames = active.Select(i => i.Crossing).ToList();
        var now = DateTimeOffset.UtcNow;
        var gameTime = _clock.NowText;
        var shared = players.Where(p => p.Mode == ShareMode.Shared && p.InGame).ToList();

        var sends = new List<Task>(players.Count);
        foreach (var p in players)
        {
            var state = new ClientState
            {
                ServerTime = now,
                GameTime = gameTime,
                Position = _data.Line.DescribeZones(p.Zones),
                ActiveCrossings = activeNames,
                VisibleIncidents = active
                    .Where(i => _data.IsNear(i.Crossing, p.Zones))
                    .Select(i => new IncidentView { Crossing = i.Crossing, Reason = i.Reason, StartedGameTime = i.StartedGameTime })
                    .ToList(),
                PlayerCount = players.Count
            };

            if (p.Mode == ShareMode.Shared)
            {
                // 信号: 自分以外の在線共有の列車がいる軌道回路から
                var others = shared.Where(o => o.ConnectionId != p.ConnectionId).SelectMany(o => o.TrackCircuits).ToHashSet();
                state.Signals = _signals.Calculate(others);

                if (_routes.GetOutput(p.ConnectionId) is { } output)
                {
                    state.Routes = output.Routes;
                    state.DeleteRoutes = output.Deletes;
                    state.Plan = output.Plan;
                    state.CanRequestDeparture = output.CanRequestDeparture;
                    state.DepartureLabel = output.DepartureLabel;
                    state.RouteMessage = output.Message;
                }
                else
                {
                    state.RouteMessage = p.TrainNumber.Length == 0 ? "列車番号が分かりません" : "運転画面に入ると、進路の管理が始まります";
                }
            }

            sends.Add(SendAsync(p.ConnectionId, state));
        }

        await Task.WhenAll(sends);
    }

    private async Task SendAsync(string connectionId, ClientState state)
    {
        try
        {
            await _hub.Clients.Client(connectionId).SendAsync(Protocol.ReceiveState, state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "送信失敗 {ConnectionId}", connectionId);
        }
    }
}
