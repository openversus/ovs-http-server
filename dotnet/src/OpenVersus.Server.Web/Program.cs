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
