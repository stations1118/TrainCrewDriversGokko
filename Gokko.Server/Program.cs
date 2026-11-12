using Gokko.Common;
using Gokko.Server;
using Gokko.Server.Hubs;
using Gokko.Server.Services;

// 設定・データ・管理画面は、exe と同じ場所から読む（systemd などで作業フォルダが違っても動くように）
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var options = builder.Configuration.GetSection("Gokko").Get<ServerOptions>() ?? new ServerOptions();
var dataDir = Path.IsPathRooted(options.DataDirectory)
    ? options.DataDirectory
    : Path.Combine(AppContext.BaseDirectory, options.DataDirectory);

// データファイルの誤りは、起動時に止めて知らせる
var data = GameData.Load(dataDir);
var rail = RailData.Load(dataDir, data.Line);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Random); // 管理画面から書き換える（運転会中だけ有効。再起動で appsettings の値に戻る）
builder.Services.AddSingleton(data);
builder.Services.AddSingleton(rail);
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<GameClock>();
builder.Services.AddSingleton<IncidentManager>();
builder.Services.AddSingleton<RunTimeModel>();
builder.Services.AddSingleton<SignalCalculator>();
builder.Services.AddSingleton<CentralControl>();
builder.Services.AddSingleton<RouteEngine>();
builder.Services.AddSingleton<StateBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StateBroadcaster>());
builder.Services.AddHostedService<RandomTokuhatsuService>();

builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    o.MaximumReceiveMessageSize = 64 * 1024;
});

var app = builder.Build();

app.Logger.LogInformation("データ: 踏切 {Crossings}件、区間設定 {Sections}件、理由 {Reasons}件（{Dir}）",
    data.Crossings.Count, data.CrossingZones.Count, data.Reasons.Count, dataDir);
app.Logger.LogInformation("進路: 駅 {Stations}件（うち扱う駅 {Control}件）、進路 {Routes}件、信号機 {Signals}件、標準運転時分 {RunTimes}区間",
    rail.Stations.Count, rail.ControlStations.Count(), rail.Routes.Count, rail.Signals.Count, rail.RunTimes.Count);

// 集中扱いの操作は、記録に残す
var central = app.Services.GetRequiredService<CentralControl>();
var incidentLog = app.Services.GetRequiredService<IncidentManager>();
central.Changed += message => incidentLog.Note(message);
app.Services.GetRequiredService<PlayerRegistry>().Changed += message => incidentLog.Note(message);
if (string.IsNullOrEmpty(options.AdminKey))
    app.Logger.LogWarning("Gokko:AdminKey が未設定です。管理画面は使えません");
if (string.IsNullOrEmpty(options.DiaPassword))
    app.Logger.LogInformation("Gokko:DiaPassword が未設定です。ダイヤグラムの設定画面（diasettings.html）は使えません");
if (string.IsNullOrEmpty(options.Password))
    app.Logger.LogWarning("Gokko:Password が未設定です。URL を知っていれば誰でも参加できます");

// 管理画面・ダイヤグラムの画面は、差し替えたらすぐ反映されるように、毎回確かめさせる（古い diapanel.js が残らないように）
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapGet("/", () => Results.Redirect("/admin.html"));
app.MapGet("/healthz", (PlayerRegistry players, IncidentManager incidents) =>
    Results.Ok(new { ok = true, players = players.Count, activeIncidents = incidents.ActiveCount, protocol = Protocol.Version }));
app.MapHub<GameHub>(Protocol.HubPath);
app.MapAdminEndpoints(options);

app.Run();
