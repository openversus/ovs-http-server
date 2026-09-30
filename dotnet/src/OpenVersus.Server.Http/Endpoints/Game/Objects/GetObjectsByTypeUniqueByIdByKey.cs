using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Objects;

/// <summary>
/// GET /objects/{type}/unique/{id}/{key}: for preferences, the TS server's fixed preferences object owned by the session's
/// player (Static/objects-preferences.json; owner_id and unique_key are the token's id). A token with no id gives them
/// as the TS server's undefined: NaN in Hydra (mvs-dump's encoding of it), left out of JSON (as JSON.stringify does). Other object types are not answered (the TS server has no route for
/// them). In the login batch. Seen in: binary 0x14505c760; TS server: GET /objects/preferences/unique/{id}/{id1}.
/// </summary>
public sealed class GetObjectsByTypeUniqueByIdByKey : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/objects/{type}/unique/{id}/{key}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Express routes ignore letter case.
        if (!string.Equals(Route<string>("type"), "preferences", StringComparison.OrdinalIgnoreCase))
        {
            await SendNotPortedAsync();
            return;
        }

        var preferences = JsonNode.Parse(StaticResponses.Json("objects-preferences"))!.AsObject();
        string? id = HttpContext.Session()?.Claims["id"] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
        bool hydra = HydraBodies.IsHydra(HttpContext);
        foreach (string field in new[] { "owner_id", "unique_key" })
        {
            if (id is not null)
            {
                preferences[field] = id;
            }
            else if (hydra)
            {
                preferences[field] = double.NaN;
            }
            else
            {
                preferences.Remove(field);
            }
        }

        if (hydra && id is null)
        {
            await HydraBodies.WriteAsync(HttpContext, preferences, ct);
            return;
        }

        await SendJsonAsync(preferences, ct);
    }
}
