using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared;

// The Arena from its start (routes owned by "arenas" in docs/routes.json): start_arena_direct and the Arena's own calls
// between its rounds. The public port is ARENAS_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Arenas, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddArenas();
// Each round's matches start here.
builder.AddMatchLauncher();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
