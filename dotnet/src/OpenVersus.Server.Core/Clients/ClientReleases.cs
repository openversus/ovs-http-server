using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Clients;

// The OpenVersus client's update check (GET /ovs/client-version), ported from the TS server (server.ts
// getLatestClientRelease, services/clientReleaseManifest.ts): the latest GitHub release of the client repo, cached
// for five minutes, turned into the list of files the in-game updater may install (the one plugin, complete pak groups),
// each with the SHA-256 GitHub records for it. ZIPs stay for manual installs and are never offered to the updater.

/// <summary>One file of a release the updater may install.</summary>
/// <param name="Kind">"plugin" (the .asi) or "paks" (an OVS_* pak, utoc, ucas or sig).</param>
public sealed record ClientUpdateFile(string Name, string Kind, long Size, string Sha256, string DownloadUrl);

/// <summary>Why a release cannot be offered (the TS messages, matched by its tests).</summary>
public sealed class ClientReleaseException(string message) : Exception(message);

public static partial class ClientReleaseManifest
{
    public const string DefaultRepo = "openversus/ovs-client";

    [GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex ReleaseRepo();

    [GeneratedRegex(@"^OVS_[A-Za-z0-9._-]+\.(pak|utoc|ucas|sig)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentAsset();

    // The plugin: legacy C++ releases ship "OpenVersus.asi"; C# releases keep the full version in the name
    // ("OpenVersus_<YYYY.MM.DD.N>.asi"). Their ".asi.sha256" sidecars and zips never match, so they are not offered.
    [GeneratedRegex(@"^openversus(?:_(\d{4}\.\d{2}\.\d{2}\.\d+))?\.asi$", RegexOptions.IgnoreCase)]
    private static partial Regex PluginAsset();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$")]
    private static partial Regex Digest();

    /// <summary>Whether <paramref name="repo"/> is a plain GitHub "owner/repo".</summary>
    public static bool IsReleaseRepo(string repo) => ReleaseRepo().IsMatch(repo);

    /// <summary>The version in a versioned plugin file name, or null for the legacy "OpenVersus.asi" (and anything else).</summary>
    public static string? PluginAssetVersion(string name) => PluginAsset().Match(name) is { Success: true } m && m.Groups[1].Success ? m.Groups[1].Value : null;

    /// <summary>
    /// The files of a release the updater may install, content first then the plugin, each kind by name. Throws
    /// <see cref="ClientReleaseException"/> for a release that must not be offered: not exactly one verified plugin,
    /// a versioned plugin for another version than <paramref name="releaseVersion"/>, an incomplete IoStore group
    /// (utoc or ucas without the full pak, utoc, ucas trio), or a repo that is not "owner/repo". Only assets downloaded
    /// from <paramref name="repo"/>'s releases, with GitHub's sha256 digest and a size, count.
    /// </summary>
    public static List<ClientUpdateFile> Build(JsonArray assets, string? releaseVersion = null, string repo = DefaultRepo)
    {
        if (!IsReleaseRepo(repo))
        {
            throw new ClientReleaseException($"not a GitHub owner/repo: {repo}");
        }

        string downloadPrefix = $"https://github.com/{repo}/releases/download/";
        var files = assets.Select(a => Normalize(a as JsonObject, downloadPrefix)).OfType<ClientUpdateFile>().ToList();
        var plugins = files.Where(f => f.Kind == "plugin").ToList();
        if (plugins.Count != 1)
        {
            throw new ClientReleaseException($"release must contain exactly one verified OpenVersus plugin (.asi) asset (found {plugins.Count})");
        }

        string? pluginVersion = PluginAssetVersion(plugins[0].Name);
        if (!string.IsNullOrEmpty(releaseVersion) && pluginVersion is not null && pluginVersion != releaseVersion)
        {
            throw new ClientReleaseException($"release {releaseVersion} ships plugin {plugins[0].Name} for a different version");
        }

        // IoStore content is only usable as a complete .pak/.utoc/.ucas group; a standalone .pak is fine, .sig optional.
        var contentByStem = new Dictionary<string, HashSet<string>>();
        foreach (var file in files.Where(f => f.Kind == "paks"))
        {
            int dot = file.Name.LastIndexOf('.');
            string stem = file.Name[..dot].ToLowerInvariant();
            string extension = file.Name[(dot + 1)..].ToLowerInvariant();
            if (!contentByStem.TryGetValue(stem, out var extensions))
            {
                contentByStem[stem] = extensions = [];
            }

            extensions.Add(extension);
        }

        foreach (var (stem, extensions) in contentByStem)
        {
            if ((extensions.Contains("utoc") || extensions.Contains("ucas")) && !(extensions.Contains("pak") && extensions.Contains("utoc") && extensions.Contains("ucas")))
            {
                throw new ClientReleaseException($"release contains an incomplete IoStore group for {stem}");
            }
        }

        // Content installs before the loaded plugin; the order is the same everywhere (logs, tests, the client's progress).
        return files.OrderBy(f => f.Kind == "paks" ? 0 : 1).ThenBy(f => f.Name, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None)).ToList();
    }

    /// <summary>The TS flattening: file_count, then file_{i}_name|kind|size|sha256|url for each.</summary>
    public static JsonObject Flatten(IReadOnlyList<ClientUpdateFile> files)
    {
        var flattened = new JsonObject { ["file_count"] = files.Count };
        for (int i = 0; i < files.Count; i++)
        {
            flattened[$"file_{i}_name"] = files[i].Name;
            flattened[$"file_{i}_kind"] = files[i].Kind;
            flattened[$"file_{i}_size"] = files[i].Size;
            flattened[$"file_{i}_sha256"] = files[i].Sha256;
            flattened[$"file_{i}_url"] = files[i].DownloadUrl;
        }

        return flattened;
    }

    /// <summary>A file as the response lists it under "files".</summary>
    public static JsonObject ToJson(ClientUpdateFile file) => new()
    {
        ["name"] = file.Name,
        ["kind"] = file.Kind,
        ["size"] = file.Size,
        ["sha256"] = file.Sha256,
        ["download_url"] = file.DownloadUrl,
    };

    private static ClientUpdateFile? Normalize(JsonObject? asset, string downloadPrefix)
    {
        if (asset is null)
        {
            return null;
        }

        string name = Str(asset["name"]);
        string url = Str(asset["browser_download_url"]);
        // typeof size === "number" && Number.isSafeInteger(size)
        long size = asset["size"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Number(v) is { } d && d == Math.Floor(d) && Math.Abs(d) <= 9007199254740991 ? (long)d : 0;
        string digest = Str(asset["digest"]).ToLowerInvariant();
        bool isPlugin = PluginAsset().IsMatch(name);
        bool isContent = ContentAsset().IsMatch(name);
        if ((!isPlugin && !isContent) || size <= 0 || !url.StartsWith(downloadPrefix, StringComparison.Ordinal) || !Digest().IsMatch(digest))
        {
            return null;
        }

        return new ClientUpdateFile(name, isPlugin ? "plugin" : "paks", size, digest["sha256:".Length..], url);
    }

    private static string Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    // A JSON number whether it came from a document (JsonElement) or was built in code (a long, an int, a double).
    private static double? Number(JsonValue value) =>
        value.TryGetValue(out double d) ? d : value.TryGetValue(out long l) ? l : value.TryGetValue(out int i) ? i : value.TryGetValue(out decimal m) ? (double)m : null;
}

public interface IClientReleases
{
    /// <summary>The configured repo's latest GitHub release (its JSON), cached for five minutes; throws when GitHub does not answer 2xx.</summary>
    Task<JsonObject> LatestAsync(CancellationToken ct = default);

    /// <summary>Clients:ReleaseRepo when it is a plain "owner/repo", else the default.</summary>
    string Repo { get; }
}

internal sealed class ClientReleases(IHttpClientFactory http, IOptionsMonitor<ClientSettings> settings, TimeProvider time, ILogger<ClientReleases> log) : IClientReleases
{
    public const string HttpClient = "github-releases";
    private static readonly TimeSpan s_cacheLifetime = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _fetch = new(1, 1);
    private (JsonObject Data, DateTimeOffset FetchedAt, string Repo)? _cached;

    public string Repo => ClientReleaseManifest.IsReleaseRepo(settings.CurrentValue.ReleaseRepo) ? settings.CurrentValue.ReleaseRepo : ClientReleaseManifest.DefaultRepo;

    public async Task<JsonObject> LatestAsync(CancellationToken ct = default)
    {
        string repo = Repo;
        if (Fresh(repo) is { } cached)
        {
            return cached;
        }

        await _fetch.WaitAsync(ct);
        try
        {
            if (Fresh(repo) is { } fetchedMeanwhile)
            {
                return fetchedMeanwhile;
            }

            JsonObject data;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
                request.Headers.TryAddWithoutValidation("User-Agent", "OpenVersus-Server");
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using var response = await http.CreateClient(HttpClient).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ClientReleaseException($"GitHub API returned {(int)response.StatusCode}");
                }

                data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct) ?? throw new ClientReleaseException("GitHub API returned no release");
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                // The client's timeout, not the caller's cancellation: GitHub did not answer in time.
                throw new ClientReleaseException($"GitHub API did not answer in time ({e.InnerException?.Message ?? e.Message})");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                throw new ClientReleaseException($"GitHub API unreachable or unreadable ({e.Message})");
            }

            _cached = (data, time.GetUtcNow(), repo);
            log.LogInformation("Cached GitHub release: {Release} ({Repo})", Str(data["tag_name"]) is { Length: > 0 } tag ? tag : Str(data["name"]), repo);
            return data;
        }
        finally
        {
            _fetch.Release();
        }
    }

    private JsonObject? Fresh(string repo) => _cached is { } c && c.Repo == repo && time.GetUtcNow() - c.FetchedAt <= s_cacheLifetime ? c.Data : null;

    private static string Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
}

public static class ClientReleasesHosting
{
    /// <summary>The client's update check: the latest GitHub release, cached (a test registers an <see cref="IClientReleases"/> of its own).</summary>
    public static WebApplicationBuilder AddClientReleases(this WebApplicationBuilder builder)
    {
        builder.AddSetting<ClientSettings>("Clients");
        builder.Services.AddHttpClient(ClientReleases.HttpClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IClientReleases, ClientReleases>();
        return builder;
    }
}
