using OpenVersus.Server.Identity.Epic;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared;

// The game's login and sessions (routes owned by "access" in docs/routes.json): POST/DELETE /access, /sessions/*. The
// public port is ACCESS_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Access, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddAccess();
builder.AddEpicIdTokens();
// The daily toast bonus's popup, when the game's websocket connects (the realtime gateway's connected events).
builder.AddDailyToastPopups();
// Keeps the required paks current for every service and tells online players who must restart (Core/Clients/RequiredPaks.cs):
// the latest client release, read here as the ban sweep reads bans, one replica at a time.
builder.AddClientReleases();
builder.AddRequiredPaksRefresher();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
