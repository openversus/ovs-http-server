using FastEndpoints;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Calendar;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Cosmetics;
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
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

// The game's HTTP API (the TS server's index service). The public port is HTTP_PORT, as in the TS server's .env.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Http, args);
builder.AddSetting<StubSettings>("Stubs");
builder.AddAccess();
builder.AddFriends();
builder.AddProfiles();
builder.AddLayouts();
builder.AddFileStorage();
builder.AddRanks();
builder.AddLeaderboards();
builder.AddAccountResolver();
builder.AddInventory();
builder.AddMatchHistory();
builder.AddClientUpdateGate();
builder.AddPartyLobbies();
builder.AddMatchLauncher();
builder.AddCalendar();
builder.AddSeasons();
builder.AddMissions();
builder.AddRewardTracks();
builder.AddPerks();
builder.AddRankedData();
builder.AddCosmetics();
builder.AddGameplayPreferences();
builder.AddHiss();
builder.AddRifts();
builder.AddBatch();
builder.Services.AddFastEndpoints();

var app = builder.Build();

// First: /batch runs its sub-requests through everything below.
app.UseBatchPipeline();
// Before routing: the Hydra SDK sends some GETs as PUT with the real method in a header.
app.UseHydraMethodOverride();
// The game's Hydra binary bodies become JSON for the endpoints, and their JSON answers go back as Hydra.
app.UseHydraBodies();
app.UseRouting();
// Names the endpoint that answered (ported or stub), for captures and the route map tests.
app.Use((context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<EndpointDefinition>() is { } endpoint)
    {
        context.Response.Headers[Stub.EndpointHeader] = endpoint.EndpointType.Name;
    }

    return next(context);
});
// Game routes answer only a valid session token, as the TS server's hydraTokenMiddleware.
app.UseHydraToken();
// Gameplay transitions only from a current, registered client, as the TS server's requireCurrentClientForGameplay.
app.UseClientGameplayGate();
// The player's input settings the game sends with party-lobby requests (Core/Preferences).
app.UseGameplayPreferencesRecorder();
app.UseFastEndpoints(c =>
{
    // ASP.NET's authorization is not used: game endpoints require the session token through RequiresHydraToken
    // (see HydraToken), unless they carry NoHydraToken; the other kinds (OpenVersus client, website, AccelByte) do
    // their own checks, as in the TS server.
    c.Endpoints.Configurator = ep =>
    {
        ep.AllowAnonymous();
        // FastEndpoints does not carry class attributes into the endpoint's metadata, so the exemption is read here.
        var exemption = ep.EndpointType.GetCustomAttributes(typeof(NoHydraTokenAttribute), false).OfType<NoHydraTokenAttribute>().SingleOrDefault();
        bool game = ep.EndpointType.Namespace?.StartsWith("OpenVersus.Server.Http.Endpoints.Game", StringComparison.Ordinal) == true;
        if (game && (exemption is null || exemption.RouteValue is not null))
        {
            ep.Options(b => b.WithMetadata(exemption is null ? [RequiresHydraToken.Instance] : [RequiresHydraToken.Instance, exemption]));
        }
    };
});
app.UseOpenVersus();
// The TS server checks the token before it knows whether a path exists.
app.MapFallback(Stub.FallbackAsync).WithMetadata(RequiresHydraToken.Instance);
app.Logger.LogWarning("MIGRATION BRIDGE: /batch sends the sub-requests C# has not ported to the TS server ({TsUrl}); see dotnet/docs/MIGRATION-BRIDGES.md (3)",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<BatchSettings>>().CurrentValue.TsUrl);
app.Logger.LogWarning("MIGRATION BRIDGE: the party routes send custom lobby requests to the TS server ({TsUrl}); see dotnet/docs/MIGRATION-BRIDGES.md (6)",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<BatchSettings>>().CurrentValue.TsUrl);

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
