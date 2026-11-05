using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /namechange: the name change page for the account at the browser's IP (the picker first when several are there).
/// TS server: GET /namechange (server.ts).
/// </summary>
public sealed class GetNamechange : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/namechange");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Resolve<IServiceProvider>().GetService<IMongoDatabase>() is not { } mongo || Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>() is not { } redis)
        {
            await Pages.SendTextAsync(HttpContext, "", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var (player, pickerShown) = await WebAccounts.ResolveAsync(HttpContext, mongo, redis.GetDatabase(), "/namechange");
        if (pickerShown)
        {
            return;
        }

        if (player is null)
        {
            // A browser cannot prove which account is its own from an IP alone: accounts are made by the game's login.
            await Pages.SendAsync(HttpContext, Pages.NameChange("Unknown", "Connect to the game before changing your name.", null), StatusCodes.Status401Unauthorized);
            return;
        }

        Logger.LogInformation("Name change requested for IP {Ip} with current name \"{Name}\"", WebAccounts.Ip(HttpContext), WebAccounts.Str(player, "name"));
        await Pages.SendAsync(HttpContext, Pages.NameChange(WebAccounts.Str(player, "name"), null, null));
    }
}
