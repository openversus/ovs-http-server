using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared;

// The match flow: a match from its start to what it changes once played. Its routes (owned by "matchflow" in
// docs/routes.json: the game's check-ins, concedes, faceoff and match config, and the rollback server's callbacks) on
// MATCHFLOW_PORT, where the router sends them; and the results (match:end_of_match_stats, still published by the TS
// server's submit_end_of_match_stats): missions, match XP and rift progress. Any number of replicas: each result is
// recorded once per match and player (SET NX keys).
var builder = OpenVersusHost.CreateBuilder(KnownServices.MatchFlow, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddRewardTracks();
builder.AddMissionResults();
builder.AddRiftResults();
builder.AddPerksLock();
builder.AddMatchToasts();
builder.AddMatchInputs();
builder.AddNodeConfig();
builder.AddRankedSets();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
