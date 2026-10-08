using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/client-version?v=: the OpenVersus client's update check. The latest GitHub release of the client repo
/// (<see cref="IClientReleases"/>, cached), the files the updater may install (<see cref="ClientReleaseManifest"/>),
/// whether the asking client is current and whether it must update (Clients:VersionCheck, Clients:MinimumVersion).
/// A release that cannot be offered, or GitHub not answering, gives the TS fallback: no version, is_latest true.
/// Seen in: captured 10x; TS server: GET /ovs/client-version (server.ts).
/// </summary>
public sealed class GetOvsClientVersion : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/client-version");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string clientVersion = Query<string?>("v", isRequired: false) ?? "";
        JsonObject body;
        try
        {
            var releases = Resolve<IClientReleases>();
            var release = await releases.LatestAsync(ct);
            // (release.tag_name || release.name || "").replace(/^v/i, "")
            string latestVersion = Truthy(release["tag_name"]) ? Js(release["tag_name"]) : Truthy(release["name"]) ? Js(release["name"]) : "";
            if (latestVersion.Length > 0 && latestVersion[0] is 'v' or 'V')
            {
                latestVersion = latestVersion[1..];
            }

            var assets = release["assets"] as JsonArray ?? [];
            var files = ClientReleaseManifest.Build(assets, latestVersion, releases.Repo);
            string downloadUrl = files.FirstOrDefault(f => f.Kind == "plugin")?.DownloadUrl ?? "";
            // A prerelease/test client may be newer than the latest published release: never offer it an older asset.
            bool isLatest = clientVersion.Length > 0 && latestVersion.Length > 0 && ClientVersions.Compare(clientVersion, latestVersion) >= 0;
            var gate = Resolve<IOptionsMonitor<ClientSettings>>().CurrentValue;
            bool updateRequired = gate.VersionCheck && ClientVersions.UpdateRequired(clientVersion, gate.MinimumVersion);
            if (!isLatest && clientVersion.Length > 0 && ClientVersions.Compare(clientVersion, latestVersion) < 0)
            {
                Logger.LogInformation("Client version {Client} is outdated (latest: {Latest})", clientVersion, latestVersion);
            }

            body = new JsonObject
            {
                ["latest_version"] = latestVersion,
                ["download_url"] = downloadUrl,
                ["is_latest"] = isLatest,
                ["minimum_version"] = gate.MinimumVersion,
                ["update_required"] = updateRequired,
                ["release_name"] = Truthy(release["name"]) ? Js(release["name"]) : "",
                ["files"] = new JsonArray([.. files.Select(ClientReleaseManifest.ToJson)]),
            };
            foreach (var (key, value) in ClientReleaseManifest.Flatten(files))
            {
                body[key] = value?.DeepClone();
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Anything but the request going away (a timeout surfaces as a cancellation too): the TS fallback.
            Logger.LogError(e, "Error in /ovs/client-version");
            body = new JsonObject { ["latest_version"] = "", ["download_url"] = "", ["is_latest"] = true, ["release_name"] = "" };
        }

        await Send.StringAsync(Core.Compat.Js.Stringify(body), contentType: "application/json; charset=utf-8", cancellation: ct);
    }

    // JavaScript truthiness of a release field (a string with characters, a non-zero number, ...).
    private static bool Truthy(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>().Length > 0,
        JsonValueKind.Number => node.GetValue<double>() != 0,
        JsonValueKind.True => true,
        JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false,
    };

    // The field as a template literal would print it.
    private static string Js(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node?.ToJsonString() ?? "";
}
