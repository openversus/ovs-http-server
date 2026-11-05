using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;

// The matchmaker (the TS server's worker service, src/matchmaking-worker.ts): every Matchmaking:IntervalMs it makes
// matches from the queues (1v1, 2v2, Casual's, FFA) and launches them (Core/Matchmaking/MatchmakingWorker.cs). No public port: it
// only reads and writes Redis, and answers on its control listeners (ovsctl -s matchmaking, /health/*). Any number of
// replicas: each queue is worked under a lock.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Matchmaking, args);
builder.AddMatchLauncher();
builder.AddMatchmaking();

var app = builder.Build();
app.UseOpenVersus();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
