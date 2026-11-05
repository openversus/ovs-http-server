using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/all-players: the first 200 accounts as {accountId, username}, for a client's startup pre-registration (the
/// C# client no longer asks; the route stays for older ones). Nothing on error, as the TS server: an empty list.
/// Seen in: TS server: GET /ovs/all-players (router.ts).
/// </summary>
public sealed class GetOvsAllPlayers : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/all-players");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var players = new JsonArray();
        try
        {
            if (Resolve<IServiceProvider>().GetService<IMongoDatabase>() is not { } mongo)
            {
                throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            }

            var accounts = await mongo.GetCollection<BsonDocument>("playertesters").Find(FilterDefinition<BsonDocument>.Empty)
                .Project(new BsonDocument { { "_id", 1 }, { "name", 1 } }).Limit(200).ToListAsync(ct);
            foreach (var account in accounts)
            {
                string id = account["_id"].IsObjectId ? account["_id"].AsObjectId.ToString() : account["_id"].ToString() ?? "";
                string name = account.TryGetValue("name", out var n) && n.IsString ? n.AsString : "";
                if (id.Length > 0 && name.Length > 0)
                {
                    players.Add(new JsonObject { ["accountId"] = id, ["username"] = name });
                }
            }

            Logger.LogInformation("Returning {Count} players for client pre-registration", players.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogError(e, "Error in /ovs/all-players");
            players = [];
        }

        await Send.StringAsync(Js.Stringify(players), contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}
