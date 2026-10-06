using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared;

// The game's login and sessions (routes owned by "access" in docs/routes.json): POST/DELETE /access, /sessions/*. The
// public port is ACCESS_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Access, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddAccess();
// The daily toast bonus's popup, when the game's websocket connects (the realtime gateway's connected events).
builder.AddDailyToastPopups();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
