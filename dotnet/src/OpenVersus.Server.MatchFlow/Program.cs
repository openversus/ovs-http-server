using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared;

// The match flow: a match from its start to what it changes once played. Its routes (owned by "matchflow" in
// docs/routes.json: the game's check-ins, concedes, faceoff and match config, and the rollback server's callbacks) on
// MATCHFLOW_PORT, where the router sends them; and the results the http service's submit_end_of_match_stats appends to
// the stream match:results (MatchResultStream): missions, match XP, rift progress and each match's stats. Any number of
// replicas: they read the stream as one consumer group, and each result is recorded once per match and player (SET NX keys).
// And each match's gameplay config, kept per player (GameplayConfigs:Mode; built beside the TS websocket, which still sends
// it: docs/MIGRATION-BRIDGES.md 9). With Realtime:Gateway on, the launched matches (match:launched, MatchLaunchStream):
// each one's config built and sent with GameServerReadyNotification to its players.
// And a match's end (MatchEnd, when MatchEnd:Enabled or Realtime:Gateway; until the realtime gateway, the TS websocket ends
// them from match:end), with its delayed websocket messages (DelayedMessages).
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
builder.AddRollbackCallbacks();
builder.AddMatchStatusEvents();
builder.AddMatchResultStream();
builder.AddGameplayConfigs();
builder.AddMatchLaunches();
builder.AddMatchEnd();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
