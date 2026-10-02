using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Rifts;

// The match flow: what happens to a match once it is played. Today its results (match:end_of_match_stats, still
// published by the TS server's submit_end_of_match_stats): missions, match XP and rift progress. No public port yet:
// it only reads Redis and writes Redis and Mongo, and answers on its control listeners (ovsctl -s matchflow,
// /health/*). Any number of replicas: each result is recorded once per match and player (SET NX keys).
var builder = OpenVersusHost.CreateBuilder(KnownServices.MatchFlow, args);
builder.AddRewardTracks();
builder.AddMissionResults();
builder.AddRiftResults();

var app = builder.Build();
app.UseOpenVersus();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
