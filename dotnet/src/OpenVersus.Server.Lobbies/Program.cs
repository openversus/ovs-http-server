using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared;

// Parties, party and custom lobbies, the rift lobby, queueing and the game's /matches routes (routes owned by "lobbies"
// in docs/routes.json). The public port is LOBBIES_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Lobbies, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddPartyLobbies();
builder.AddCustomLobbies();
builder.AddRiftLobbies();
builder.AddMatchHistory();
builder.AddMatchmakingRequests();
// A player's equipped cosmetics are copied for their match when they join a lobby.
builder.AddCosmetics();
// Custom matches start here, and the rematches of custom and Casual matches (their votes and timers).
builder.AddMatchLauncher();
builder.AddRematches();
// A player whose game is gone leaves their party and custom lobby (the realtime gateway's disconnects).
builder.AddLobbyDisconnects();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
