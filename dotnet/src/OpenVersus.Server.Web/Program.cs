using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Web.Site;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Identity;
using OpenVersus.Server.Http.Shared;

// The website, the OpenVersus client's API and the admin pages (routes owned by "web" in docs/routes.json). The public
// port is WEB_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Web, args);
builder.AddGameHttp(typeof(Program).Assembly);
// The website's admin pages and asset sync, the leaderboard APIs, and the live matches snapshot (at most every 2 s, on request).
builder.AddSetting<AdminSettings>("Admin");
builder.AddSetting<FfaSettings>("Ffa");
builder.AddLeaderboards();
builder.Services.AddSingleton<LiveMatches>();
// /namechange checks names and bans; /ovs/notifications finds its player as the game routes do; /api/identify registers
// the client (Steam tickets, the client gate's settings).
builder.AddBans();
builder.AddAccountResolver();
builder.AddIdentify();
// /ovs/client-version offers the latest client release.
builder.AddClientReleases();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
