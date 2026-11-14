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

    public sealed record ClockRequest(string? Time, bool? Running, bool? Clear, double? Speed);

    /// <param name="Kind">抑止の種類（"arrive" 場内・"depart" 出発。省くと両方）。</param>
    public sealed record TrainStationRequest(string Train, string Station, int? Track, bool? Hold, string? Kind);

    public sealed record CrewLoginRequest(string? Name);

    public sealed record CrewAssignRequest(string Duty, string? User);

    public sealed record StationModeRequest(string Station, bool Central);

    public sealed record PassingRequest(int? MarginSeconds, int? ToleranceSeconds);

    /// <summary>駅の出発順序（Direction は使わない。上下まとめて1つの順序）。</summary>
    public sealed record OrderRequest(string Station, string? Direction, List<string>? Trains);

    public sealed record SectionOrderRequest(string A, string B, List<string>? Trains);

    /// <summary>駅扱い（強制進路）。Action は "arrive:1" "depart:2" "through" "shunt:3" など（◆・表のボタンが持っている値）。</summary>
    public sealed record ManualRequest(string Train, string Station, string Action, string? Label);

    public sealed record IdRequest(string Id);

    public sealed record ModeRequest(bool Dia);

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
        // できるのはダイヤグラムの表示と操作だけ（特発の作動・ロープレ時刻などは触れない。特発の解除はできる）
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

        // ダイヤグラムの設定画面からは、特発の解除だけできる（作動はできない）
        dia.MapGet("/incidents", (IncidentManager incidents) => Results.Ok(new
        {
            incidents = incidents.Snapshot().OrderBy(i => i.StartedUtc).Select(i => new { crossing = i.Crossing, reason = i.Reason, source = i.Source, started = i.StartedUtc,
                releaseInSeconds = i.ReleaseAtUtc is { } ra ? Math.Max(0, (int)Math.Ceiling((ra - DateTime.UtcNow).TotalSeconds)) : (int?)null })
        }));
        dia.MapPost("/release", (ReleaseRequest req, IncidentManager incidents) =>
        {
            if (string.IsNullOrWhiteSpace(req.Crossing)) return Results.BadRequest(new { ok = false, message = "踏切を指定してください" });
            var released = incidents.Release([req.Crossing.Trim()], "ダイヤグラムの設定画面");
            return Results.Ok(new { ok = true, released = released.Count });
        });

        // 在線位置の画面（tid.html）。見るだけで、操作はできない。合言葉なしで誰でも見られる
        var view = app.MapGroup("/api/tid");

        // 乗務タブレット（tablet.html）: スタフの並びの時刻表・次の駅・進路の状態・行路
        // user（ログインした乗務員）があれば、その人の行路を出す。train がなければ、その人の行路の、いま乗る列車
        view.MapGet("/tablet", (string? train, string? user, RouteEngine engine, DutyRoster roster, CrewRoster crew) =>
        {
            var name = crew.User(user);
            if (!string.IsNullOrEmpty(user) && name == null) return Results.NotFound(new { message = $"{user} というユーザーはいません", logout = true });
            var duties = name == null ? null : crew.DutiesOf(name).Select(roster.DutyById).OfType<Duty>().ToList();
            var t = string.IsNullOrEmpty(train) && duties != null ? engine.CurrentTrainOf(duties) : train;
            if (string.IsNullOrEmpty(t)) return Results.NotFound(new { message = name != null ? $"{name} さんには、まだ行路が割り当てられていません" : "列車番号がありません" });
            if (name != null) crew.Seen(name, t);
            return engine.TabletView(t, roster, duties, name) is { } v ? Results.Ok(v) : Results.NotFound(new { message = $"{t} のダイヤはありません" });
        });

        // 乗務タブレットのログイン（ユーザー名だけ。勤怠管理の画面で作ったユーザー）
        view.MapPost("/login", (CrewLoginRequest req, CrewRoster crew, DutyRoster roster) =>
        {
            if (crew.User(req.Name) is not { } name) return Results.NotFound(new { message = "そのユーザー名はありません（勤怠管理の画面で作ってください）" });
            return Results.Ok(new { name, duties = crew.DutiesOf(name) });
        });

        // 行路の一覧（乗務タブレットで、行路を選んで見るため）
        view.MapGet("/duties", (DutyRoster roster) => Results.Ok(new { duties = roster.Duties.Select(d => new { id = d.Id, trains = d.Items.Where(x => x.Kind != "休憩").Select(x => x.Train) }), problems = roster.Problems }));

        view.MapGet("/timetable", (string? train, RouteEngine engine, GameClock clock) =>
            engine.Timetable(train ?? "", clock.Now) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "この列車の時刻表はありません" }));

        // 閉塞信号機の番号と、守る軌道回路（軌道回路名つきの画面で、閉塞信号の位置に番号を出す）
        view.MapGet("/signals", (RailData rail) => Results.Ok(rail.Signals.Values
            .Where(s => !s.IsRepeater && s.Protects.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^(上り|下り)閉塞\d+$"))
            .Select(s => new { number = System.Text.RegularExpressions.Regex.Match(s.Name, @"\d+$").Value, direction = s.Name[..2], circuit = s.Protects[0] })));

        view.MapGet("/state", (RouteEngine engine, GameClock clock) => Results.Ok(new
        {
            gameTime = clock.NowText,
            tid = engine.TidView(),
            orders = engine.OrdersView(),
            sections = engine.SectionsView()
        }));

        // ---- 勤怠管理（kintai.html）: 乗務員（ユーザー名）と、行路への割り当て ----
        admin.MapGet("/crew", (CrewRoster crew, DutyRoster roster, RouteEngine engine) =>
        {
            var users = crew.Users().Select(u =>
            {
                var (online, train, last) = crew.Presence(u);
                return new { name = u, duties = crew.DutiesOf(u), online, train, last };
            }).ToList();
            var duties = roster.Duties.Select(d => new
            {
                view = engine.DutyView(d, null),
                user = crew.AssignedTo(d.Id)
            }).ToList();
            // 列車ごとの担当（この列車にはこの人が乗る）
            var trains = roster.Duties.SelectMany(d => d.Items.Where(x => x.Kind != "休憩").Select(x => new
            {
                train = x.Train, kind = x.Kind, duty = d.Id, user = crew.AssignedTo(d.Id), running = engine.IsRunning(x.Train)
            })).ToList();
            return Results.Ok(new { users, duties, trains, problems = roster.Problems, hasDuties = roster.Duties.Count > 0 });
        });
        admin.MapPost("/crew/users", (CrewLoginRequest req, CrewRoster crew) =>
            crew.AddUser(req.Name) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true }));
        admin.MapPost("/crew/users/delete", (CrewLoginRequest req, CrewRoster crew) =>
            crew.RemoveUser(req.Name) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "そのユーザーはいません" }));
        admin.MapPost("/crew/assign", (CrewAssignRequest req, CrewRoster crew, DutyRoster roster) =>
        {
            if (roster.DutyById(req.Duty) == null) return Results.NotFound(new { message = $"行路 {req.Duty} はありません" });
            return crew.Assign(req.Duty, req.User) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true });
        });

        admin.MapGet("/state", (GameData data, IncidentManager incidents, PlayerRegistry players, GameClock clock, RandomOptions random, ProtectionRadio radio) =>
        {
            var now = DateTime.UtcNow;
            var active = incidents.Snapshot().ToDictionary(i => i.Crossing);
            return Results.Ok(new
            {
                serverTime = DateTimeOffset.UtcNow,
                clock = new { gameTime = clock.NowText, running = clock.IsRunning },
                radio = radio.View(), // 防護無線の発報中の一覧
                random,
                reasons = data.Reasons,
                // 理由ごとの自動復帰の秒数と重み（ランダムで選ばれる確率は、重み ÷ 重みの合計）
                reasonSpecs = data.ReasonSpecs.Select(r => new { name = r.Name, seconds = r.MinSeconds >= 0 ? r.SecondsText : null, weight = r.Weight,
                    percent = data.ReasonSpecs.Sum(x => x.Weight) is var tw && tw > 0 ? Math.Round(r.Weight / tw * 100, 1) : 0 }),
                crossings = data.Crossings.Select(c => new
                {
                    name = c,
                    location = data.DescribeCrossing(c),
                    // 特発を戻せる軌道回路（信号・踏切道早見表から。なければ駅間で判定）
                    restoreCircuits = data.CrossingCircuits.TryGetValue(c, out var rc) ? rc.OrderBy(x => x, StringComparer.Ordinal).ToList() : null,
                    incident = active.TryGetValue(c, out var i)
                        ? new
                        {
                            reason = i.Reason,
                            source = i.Source,
                            startedGameTime = i.StartedGameTime,
                            elapsedSeconds = (int)(now - i.StartedUtc).TotalSeconds,
                            // 自動で復帰するまでの残り秒数（自動復帰しないなら null）
                            releaseInSeconds = i.ReleaseAtUtc is { } ra ? Math.Max(0, (int)Math.Ceiling((ra - now).TotalSeconds)) : (int?)null
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

        // 進路を取り始める位置（信号機）の一覧（確かめる用）
        admin.MapGet("/route/approach", (int? margin, RouteEngine engine, RailData rail) => Results.Ok(new
        {
            enabled = rail.ApproachTriggerEnabled,
            margin = margin ?? rail.ApproachTriggerMargin,
            rows = engine.ApproachTable(margin ?? rail.ApproachTriggerMargin)
        }));

        admin.MapPost("/clock", (ClockRequest req, GameClock clock, IncidentManager incidents) =>
        {
            if (req.Clear == true)
            {
                clock.Clear();
                incidents.Note("ロープレ時刻: 解除");
                return Results.Ok(new { ok = true, gameTime = (string?)null });
            }

            // 早回しのシミュレーション用: 進む速さ（1 = 実時間と同じ）
            if (req.Speed is { } sp)
            {
                clock.SetSpeed(sp);
                incidents.Note($"ロープレ時刻: 進む速さ {clock.Speed}倍");
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

            return Results.Ok(new { ok = true, gameTime = clock.NowText, running = clock.IsRunning, speed = clock.Speed });
        });
    }

    /// <summary>ダイヤグラムの表示と、そこからできる操作（管理画面と、ダイヤグラムの設定画面で共通）。</summary>
    private static void MapDiagramControls(RouteGroupBuilder g)
    {
        // 駅の扱い（列車扱い・集中扱い）と自動退避の数値（管理画面・ダイヤグラムの設定画面の両方から）
        g.MapPost("/route/station-mode", (StationModeRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            central.SetCentral(req.Station, req.Central);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/passing", (PassingRequest req, RailData rail, IncidentManager incidents) =>
        {
            if (req.MarginSeconds is { } m) rail.PassingMarginSeconds = Math.Clamp(m, 0, 900);
            if (req.ToleranceSeconds is { } t) rail.PassingToleranceSeconds = Math.Clamp(t, 0, 900);
            incidents.Note($"待避の数値: 余裕 {rail.PassingMarginSeconds}秒、許容の遅れ {rail.PassingToleranceSeconds}秒");
            return Results.Ok(new { ok = true });
        });

        // 手動進路構成（管理画面・ダイヤグラムの設定画面。設定画面は取消のたびに「通告は完了したか？」を確かめる）: 駅ごとの全進路の一覧と、進路名を直接指定した開通・取消
        g.MapGet("/route/catalog", (RailData rail) => Results.Ok(rail.ControlStations.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            interlock = s.Interlock,
            routes = rail.Routes.Values.Where(r => r.Interlock == s.Interlock)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new { name = r.Name, from = r.From, to = r.To, kind = r.Kind })
        })));

        g.MapPost("/route/manual-route", (ManualRouteRequest req, RouteEngine engine) =>
            Results.Ok(new { ok = true, message = engine.ManualRoute(req.Train ?? "", req.Interlock ?? "", req.Route ?? "", req.Set) }));

        // 運転モード（フリー運転・ダイヤ運転）の切り替え
        g.MapPost("/mode", (ModeRequest req, CentralControl central) =>
        {
            central.SetDiaMode(req.Dia);
            return Results.Ok(new { ok = true });
        });

        // つながっている参加者を強制的に切断する（次の報告のときに接続を切る）
        // 防護無線の発報を解除する（押したまま離れた参加者など）
        g.MapPost("/radio/clear", (IdRequest req, ProtectionRadio radio, StateBroadcaster broadcaster) =>
        {
            if (!radio.Clear(req.Id)) return Results.NotFound(new { message = "その発報はもうありません" });
            broadcaster.Trigger();
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/players/kick", (IdRequest req, PlayerRegistry players) =>
            players.Kick(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その参加者はもういません" }));

        // 継続乗務で残している在線を消す
        g.MapPost("/continue/delete", (IdRequest req, PlayerRegistry players) =>
            players.RemoveGhost(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その継続乗務申請はもうありません" }));

        g.MapGet("/diagram", (RouteEngine engine, RailData rail, PlayerRegistry players, CentralControl central) => Results.Ok(new
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
            sections = engine.SectionsView(),
            // これから進路を取る順番（取れずに待っている場内・出発）
            queue = engine.QueueView(),
            // 継続乗務申請（残している在線）と、出現待ちの列車
            continues = players.Ghosts().Select(x => new
            {
                id = x.Id,
                train = x.Info.TrainNumber,
                circuits = x.Info.TrackCircuits,
                since = x.SinceUtc,
                until = x.UntilUtc
            }),
            appear = engine.AppearView(),
            diaMode = central.DiaMode,
            // つながっている参加者（強制切断の一覧用）
            players = players.Snapshot().Where(p => !p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)).OrderBy(p => p.JoinedUtc).Select(p => new
            {
                id = p.ConnectionId,
                train = p.TrainNumber,
                typed = p.TrainNumberTyped,
                mode = p.Mode == Gokko.Common.ShareMode.Shared ? "在線共有" : "特発のみ",
                inGame = p.InGame,
                position = rail.Line.DescribeZones(p.Zones),
                appear = p.AppearReason
            }),
            // 駅の扱いと自動退避の数値
            stationModes = rail.ControlStations.Select(s => new { id = s.Id, name = s.Name, central = central.IsCentral(s.Id) }),
            passing = new { marginSeconds = rail.PassingMarginSeconds, toleranceSeconds = rail.PassingToleranceSeconds }
        }));

        // スジずらし: ◆を横にずらして、その駅の発車時刻を決める（time が null なら指定解除）
        g.MapPost("/route/open-at", (OpenAtRequest req, CentralControl central) =>
        {
            central.SetOpenAt(req.Train, req.Station, req.Time?.UtcDateTime);
            return Results.Ok(new { ok = true });
        });

        // 進路を要求した記録（新しい順）
        g.MapGet("/route/log", (RouteEngine engine) => Results.Ok(new { lines = engine.RouteLogView() }));

        // 列車別設定)（列車ごとの、この先の駅の番線・状態・駅扱いの選択肢）
        g.MapGet("/trains", (RouteEngine engine) => Results.Ok(new { trains = engine.AdminView() }));

        // 臨時停車・臨時通過・打ち切り（kind: "停車" / "通過" / "打ち切り"、null で元に戻す）
        g.MapPost("/route/stop", (TrainStationRequest req, CentralControl central) =>
        {
            central.SetStop(req.Train, req.Station, req.Kind);
            return Results.Ok(new { ok = true });
        });

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

        // 駅の出発順序（direction: "上り" / "下り"。trains が null なら、その向きを自動に戻す）。
        // direction がなければ、列車ごとの向きで振り分ける（trains も null なら上下とも自動に戻す）
        g.MapPost("/route/order", (OrderRequest req, CentralControl central, RailData rail, RouteEngine engine) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            var trains = req.Trains?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            Dir? dir = req.Direction switch { "上り" => Dir.Up, "下り" => Dir.Down, _ => null };
            if (dir is { } d) { central.SetOrder(req.Station, d, trains); return Results.Ok(new { ok = true }); }
            foreach (var x in new[] { Dir.Down, Dir.Up })
            {
                var mine = trains?.Where(t => engine.DirOfTrain(t) == x).ToList();
                if (trains == null || mine!.Count > 0) central.SetOrder(req.Station, x, trains == null ? null : mine);
            }

            return Results.Ok(new { ok = true });
        });
    }
}
