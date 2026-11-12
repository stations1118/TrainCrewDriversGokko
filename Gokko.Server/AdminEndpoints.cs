using System.Security.Cryptography;
using System.Text;
using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 管理画面（wwwroot/admin.html）が使う API。ヘッダー X-Admin-Key に AdminKey を付けて呼ぶ。
/// AdminKey が未設定なら、すべて 503 で断る。
/// </summary>
public static class AdminEndpoints
{
    public sealed record TriggerRequest(string Crossing, string? Reason);

    public sealed record ReleaseRequest(string? Crossing);

    public sealed record RandomRequest(bool? Enabled, int? RollIntervalSeconds, double? Probability, int? AutoReleaseSeconds, int? MaxConcurrent);

    public sealed record ClockRequest(string? Time, bool? Running, bool? Clear);

    /// <param name="Kind">抑止の種類（"arrive" 場内・"depart" 出発。省くと両方）。</param>
    public sealed record TrainStationRequest(string Train, string Station, int? Track, bool? Hold, string? Kind);

    public sealed record StationModeRequest(string Station, bool Central);

    public sealed record PassingRequest(int? MarginSeconds, int? ToleranceSeconds);

    /// <summary>駅の出発順序（Direction は使わない。上下まとめて1つの順序）。</summary>
    public sealed record OrderRequest(string Station, string? Direction, List<string>? Trains);

    public sealed record SectionOrderRequest(string A, string B, List<string>? Trains);

    /// <summary>駅扱い（強制進路）。Action は "arrive:1" "depart:2" "through" "shunt:3" など（◆・表のボタンが持っている値）。</summary>
    public sealed record ManualRequest(string Train, string Station, string Action, string? Label);

    /// <summary>手動進路（管理者用）。進路名を直接指定する。</summary>
    public sealed record ManualRouteRequest(string Train, string Interlock, string Route, bool Set);

    public sealed record OpenAtRequest(string Train, string Station, DateTimeOffset? Time);

    private static bool SameKey(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    public static void MapAdminEndpoints(this WebApplication app, ServerOptions options)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(options.AdminKey))
                return Results.Problem("AdminKey が設定されていないため、管理機能は無効です", statusCode: 503);

            var given = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (!SameKey(given, options.AdminKey))
                return Results.Problem("管理キーが違います", statusCode: 401);

            return await next(ctx);
        });

        // ダイヤグラムの設定画面（diasettings.html。他の参加者に公開する用）。ヘッダー X-Dia-Key に DiaPassword を付けて呼ぶ。
        // できるのはダイヤグラムの表示と操作だけ（特発・ロープレ時刻・駅の扱い・待避の数値などは触れない）
        var dia = app.MapGroup("/api/dia").AddEndpointFilter(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(options.DiaPassword))
                return Results.Problem("DiaPassword が設定されていないため、この画面は使えません", statusCode: 503);

            var given = ctx.HttpContext.Request.Headers["X-Dia-Key"].ToString();
            if (!SameKey(given, options.DiaPassword))
                return Results.Problem("合言葉が違います", statusCode: 401);

            return await next(ctx);
        });

        MapDiagramControls(admin);
        MapDiagramControls(dia);

        // 在線位置の画面（tid.html）。見るだけで、操作はできない。合言葉なしで誰でも見られる
        var view = app.MapGroup("/api/tid");

        view.MapGet("/state", (RouteEngine engine, GameClock clock) => Results.Ok(new
        {
            gameTime = clock.NowText,
            tid = engine.TidView(),
            orders = engine.OrdersView(),
            sections = engine.SectionsView()
        }));

        admin.MapGet("/state", (GameData data, IncidentManager incidents, PlayerRegistry players, GameClock clock, RandomOptions random) =>
        {
            var now = DateTime.UtcNow;
            var active = incidents.Snapshot().ToDictionary(i => i.Crossing);
            return Results.Ok(new
            {
                serverTime = DateTimeOffset.UtcNow,
                clock = new { gameTime = clock.NowText, running = clock.IsRunning },
                random,
                reasons = data.Reasons,
                crossings = data.Crossings.Select(c => new
                {
                    name = c,
                    location = data.DescribeCrossing(c),
                    incident = active.TryGetValue(c, out var i)
                        ? new
                        {
                            reason = i.Reason,
                            source = i.Source,
                            startedGameTime = i.StartedGameTime,
                            elapsedSeconds = (int)(now - i.StartedUtc).TotalSeconds
                        }
                        : null
                }),
                players = players.Snapshot().OrderBy(p => p.JoinedUtc).Select(p => new
                {
                    mode = p.Mode == Gokko.Common.ShareMode.Shared ? "在線共有" : "特発のみ",
                    trainNumber = p.TrainNumber,
                    trainCrewConnected = p.TrainCrewConnected,
                    inGame = p.InGame,
                    position = data.Line.DescribeZones(p.Zones),
                    direction = p.Direction,
                    lastReportSecondsAgo = (int)(now - p.LastReportUtc).TotalSeconds
                }),
                log = incidents.RecentLog().Select(l => new { utc = l.Utc, gameTime = l.GameTime, message = l.Message })
            });
        });

        admin.MapPost("/trigger", (TriggerRequest req, IncidentManager incidents) =>
            incidents.TryTrigger(req.Crossing ?? "", req.Reason ?? "", "管理画面", out var error)
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { ok = false, message = error }));

        admin.MapPost("/release", (ReleaseRequest req, IncidentManager incidents) =>
        {
            var released = string.IsNullOrWhiteSpace(req.Crossing)
                ? incidents.ReleaseAll("管理画面")
                : incidents.Release([req.Crossing.Trim()], "管理画面");
            return Results.Ok(new { ok = true, released = released.Count });
        });

        admin.MapPost("/random", (RandomRequest req, RandomOptions random, IncidentManager incidents) =>
        {
            if (req.RollIntervalSeconds is { } interval) random.RollIntervalSeconds = Math.Clamp(interval, 1, 3600);
            if (req.Probability is { } prob) random.Probability = Math.Clamp(prob, 0.0, 1.0);
            if (req.AutoReleaseSeconds is { } auto) random.AutoReleaseSeconds = Math.Clamp(auto, 0, 86400);
            if (req.MaxConcurrent is { } max) random.MaxConcurrent = Math.Clamp(max, 1, 50);
            if (req.Enabled is { } enabled && enabled != random.Enabled)
            {
                random.Enabled = enabled;
                incidents.Note(enabled ? "ランダム特発: 開始" : "ランダム特発: 停止");
            }

            return Results.Ok(new { ok = true, random });
        });

        // ---- 進路・集中扱い ----
        admin.MapGet("/routes", (RouteEngine engine, CentralControl central, RailData rail) => Results.Ok(new
        {
            trains = engine.AdminView(),
            stations = rail.ControlStations.Select(s => new { id = s.Id, name = s.Name, central = central.IsCentral(s.Id) }),
            passing = new { marginSeconds = rail.PassingMarginSeconds, toleranceSeconds = rail.PassingToleranceSeconds }
        }));

        admin.MapPost("/route/station-mode", (StationModeRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            central.SetCentral(req.Station, req.Central);
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/route/open-at", (OpenAtRequest req, CentralControl central) =>
        {
            central.SetOpenAt(req.Train, req.Station, req.Time?.UtcDateTime);
            return Results.Ok(new { ok = true });
        });

        // 手動進路（管理者用）: 駅ごとの全進路の一覧と、進路名を直接指定した開通・取消
        admin.MapGet("/route/catalog", (RailData rail) => Results.Ok(rail.ControlStations.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            interlock = s.Interlock,
            routes = rail.Routes.Values.Where(r => r.Interlock == s.Interlock)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new { name = r.Name, from = r.From, to = r.To, kind = r.Kind })
        })));

        admin.MapPost("/route/manual-route", (ManualRouteRequest req, RouteEngine engine) =>
            Results.Ok(new { ok = true, message = engine.ManualRoute(req.Train ?? "", req.Interlock ?? "", req.Route ?? "", req.Set) }));

        admin.MapPost("/route/passing", (PassingRequest req, RailData rail, IncidentManager incidents) =>
        {
            if (req.MarginSeconds is { } m) rail.PassingMarginSeconds = Math.Clamp(m, 0, 900);
            if (req.ToleranceSeconds is { } t) rail.PassingToleranceSeconds = Math.Clamp(t, 0, 900);
            incidents.Note($"待避の数値: 余裕 {rail.PassingMarginSeconds}秒、許容の遅れ {rail.PassingToleranceSeconds}秒");
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/clock", (ClockRequest req, GameClock clock, IncidentManager incidents) =>
        {
            if (req.Clear == true)
            {
                clock.Clear();
                incidents.Note("ロープレ時刻: 解除");
                return Results.Ok(new { ok = true, gameTime = (string?)null });
            }

            if (!string.IsNullOrWhiteSpace(req.Time))
            {
                if (!TimeSpan.TryParse(req.Time.Trim(), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
                    return Results.BadRequest(new { ok = false, message = "時刻は 11:00 や 11:00:30 の形で入力してください" });

                clock.Set(t, req.Running ?? true);
                incidents.Note($"ロープレ時刻: {t:hh\\:mm\\:ss} に設定");
            }
            else if (req.Running is { } running)
            {
                clock.SetRunning(running);
            }

            return Results.Ok(new { ok = true, gameTime = clock.NowText, running = clock.IsRunning });
        });
    }

    /// <summary>ダイヤグラムの表示と、そこからできる操作（管理画面と、ダイヤグラムの設定画面で共通）。</summary>
    private static void MapDiagramControls(RouteGroupBuilder g)
    {
        g.MapGet("/diagram", (RouteEngine engine, RailData rail) => Results.Ok(new
        {
            now = DateTimeOffset.UtcNow,
            stations = rail.Line.StationIds.Select(id => new
            {
                id,
                name = rail.Line.StationName(id),
                y = rail.Line.ZoneOfStation(id),
                control = rail.Stations.TryGetValue(id, out var s) && s.RouteControl
            }),
            trains = engine.DiagramView(),
            orders = engine.OrdersView(),
            sections = engine.SectionsView()
        }));

        // 列車別設定（列車ごとの、この先の駅の番線・状態・駅扱いの選択肢）
        g.MapGet("/trains", (RouteEngine engine) => Results.Ok(new { trains = engine.AdminView() }));

        g.MapPost("/route/track", (TrainStationRequest req, CentralControl central) =>
        {
            central.SetTrack(req.Train, req.Station, req.Track);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/hold", (TrainStationRequest req, CentralControl central) =>
        {
            var kind = req.Kind switch { "arrive" => HoldKind.Arrive, "depart" => HoldKind.Depart, _ => HoldKind.Both };
            central.SetHold(req.Train, req.Station, req.Hold ?? true, kind);
            return Results.Ok(new { ok = true });
        });

        // 駅扱い（強制進路）: 連動に問題がなければ、サーバーの抑止・待ちを無視して開通させる
        g.MapPost("/route/manual", (ManualRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            if (string.IsNullOrWhiteSpace(req.Action)) return Results.BadRequest(new { ok = false, message = "進路が指定されていません" });
            central.RequestManual(req.Train, req.Station, req.Action.Trim(), req.Label ?? req.Action);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/section-order", (SectionOrderRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.IsSingleTrack(req.A ?? "", req.B ?? "")) return Results.BadRequest(new { ok = false, message = "単線区間ではありません" });
            central.SetSectionOrder(req.A!, req.B!, req.Trains?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList());
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/force", (TrainStationRequest req, CentralControl central) =>
        {
            central.Force(req.Train, req.Station);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/order", (OrderRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            central.SetOrder(req.Station, req.Trains?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList());
            return Results.Ok(new { ok = true });
        });
    }
}
