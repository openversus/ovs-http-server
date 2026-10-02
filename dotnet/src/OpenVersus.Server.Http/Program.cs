using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Calendar;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.FileStorage;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Core.Layouts;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Batch;
using OpenVersus.Server.Http.Shared;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

// The game's HTTP API (the TS server's index service). The public port is HTTP_PORT, as in the TS server's .env.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Http, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddProfiles();
builder.AddLayouts();
builder.AddFileStorage();
builder.AddRanks();
builder.AddLeaderboards();
builder.AddAccountResolver();
builder.AddInventory();
builder.AddMatchHistory();
builder.AddPartyLobbies();
builder.AddCustomLobbies();
builder.AddMatchLauncher();
builder.AddCalendar();
builder.AddSeasons();
builder.AddMissions();
builder.AddRewardTracks();
builder.AddPerks();
builder.AddRankedData();
builder.AddCosmetics();
builder.AddHiss();
builder.AddRifts();
builder.AddBatch();

var app = builder.Build();

// First: /batch runs its sub-requests through everything below.
app.UseBatchPipeline();
// Unknown paths come here (the router's default), so this service answers them with the stub fallback.
app.UseGameHttp(fallback: true);
app.Logger.LogWarning("MIGRATION BRIDGE: /batch sends the sub-requests C# has not ported to the TS server ({TsUrl}); see dotnet/docs/MIGRATION-BRIDGES.md (3)",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<BatchSettings>>().CurrentValue.TsUrl);

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
