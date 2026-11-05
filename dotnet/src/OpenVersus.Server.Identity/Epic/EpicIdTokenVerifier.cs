using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Epic;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Identity.Epic;

/// <summary>Where the signing keys come from: Epic's JWKS over HTTP, or a test's document.</summary>
public interface IEpicKeySource
{
    /// <summary>The JWKS document, or null when it could not be fetched (the reason is logged by the source).</summary>
    Task<string?> FetchAsync(CancellationToken ct);
}

public interface IEpicIdTokenVerifier
{
    /// <summary>Judges <paramref name="token"/> as of <paramref name="now"/>; fetches keys when it holds none or the token names a key it lacks.</summary>
    ValueTask<EpicTokenCheck> CheckAsync(string token, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// Verifies Epic account ID tokens against Epic's published keys, kept in memory: fetched at startup (<see cref="EpicKeyWarmup"/>),
/// again when a token arrives after <see cref="EpicSettings.JwksRefreshMinutes"/>, and at once (at most once a minute) when
/// a token names a key not held.
/// The keys last fetched are the floor: when Epic cannot be reached they serve on; with none ever fetched a token is
/// unavailable to judge, and the Epic id it names stays a claim (logged once per outage).
/// </summary>
public sealed class EpicIdTokenVerifier(IEpicKeySource source, IOptionsMonitor<EpicSettings> settings, TimeProvider time, ILogger<EpicIdTokenVerifier> log) : IEpicIdTokenVerifier
{
    /// <summary>The least time between two fetches a token's unknown kid can trigger.</summary>
    public static readonly TimeSpan MinimumRefetch = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile KeySet? _keys;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private bool _outage;

    private sealed record KeySet(Dictionary<string, RSAParameters> Keys, DateTimeOffset FetchedAt);

    /// <summary>How many keys are held, for the status line.</summary>
    public int KeyCount => _keys?.Keys.Count ?? 0;

    public async ValueTask<EpicTokenCheck> CheckAsync(string token, DateTimeOffset now, CancellationToken ct = default)
    {
        var options = settings.CurrentValue;
        if (options.ClientId.Length == 0)
        {
            return new EpicTokenCheck.Unavailable("Epic:ClientId is not set");
        }

        if (EpicIdToken.Parse(token) is not { } parsed)
        {
            return new EpicTokenCheck.Refused("not a JWT");
        }

        if (parsed.Algorithm != "RS256")
        {
            // Before any key is looked up: a token under another algorithm never earns a fetch.
            return new EpicTokenCheck.Refused($"alg {(parsed.Algorithm.Length > 0 ? parsed.Algorithm : "missing")}, not RS256");
        }

        if (parsed.KeyId.Length == 0)
        {
            return new EpicTokenCheck.Refused("no kid");
        }

        var keys = await KeysAsync(parsed.KeyId, now, ct);
        if (keys is null)
        {
            return new EpicTokenCheck.Unavailable("no signing keys from Epic yet");
        }

        if (!keys.TryGetValue(parsed.KeyId, out var key))
        {
            return new EpicTokenCheck.Refused($"kid {parsed.KeyId} is not one of Epic's keys");
        }

        return parsed.Verify(key, options.Issuers, options.ClientId, now, TimeSpan.FromSeconds(options.ClockSkewSeconds));
    }

    /// <summary>Fetches the keys now if none are held or they are due; the startup warm-up calls this.</summary>
    public async Task RefreshIfDueAsync(CancellationToken ct) => await KeysAsync(null, time.GetUtcNow(), ct);

    private async ValueTask<Dictionary<string, RSAParameters>?> KeysAsync(string? wantedKid, DateTimeOffset now, CancellationToken ct)
    {
        var held = _keys;
        if (!Due(held, wantedKid, now))
        {
            return held?.Keys;
        }

        await _refresh.WaitAsync(ct);
        try
        {
            held = _keys;
            if (Due(held, wantedKid, now) && now - _lastAttempt >= MinimumRefetch)
            {
                _lastAttempt = now;
                await FetchAsync(now, ct);
            }
        }
        finally
        {
            _refresh.Release();
        }

        return _keys?.Keys;
    }

    private bool Due(KeySet? held, string? wantedKid, DateTimeOffset now) =>
        held is null
        || now - held.FetchedAt >= TimeSpan.FromMinutes(settings.CurrentValue.JwksRefreshMinutes)
        || (wantedKid is not null && !held.Keys.ContainsKey(wantedKid));

    private async Task FetchAsync(DateTimeOffset now, CancellationToken ct)
    {
        string? document;
        try
        {
            document = await source.FetchAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            document = null;
            Outage($"fetching Epic's signing keys failed: {e.Message}");
        }

        if (document is null)
        {
            Outage("Epic's signing keys could not be fetched");
            return;
        }

        var keys = EpicKeySet.Parse(document);
        if (keys is null || keys.Count == 0)
        {
            Outage("Epic's signing keys document held no usable RSA key");
            return;
        }

        _keys = new KeySet(keys, now);
        if (_outage)
        {
            _outage = false;
            log.LogInformation("Epic's signing keys are back: {Count} key(s)", keys.Count);
        }
        else
        {
            log.LogInformation("Epic's signing keys fetched: {Count} key(s) ({Kids})", keys.Count, string.Join(", ", keys.Keys));
        }
    }

    private void Outage(string reason)
    {
        if (_outage)
        {
            return;
        }

        _outage = true;
        if (_keys is null)
        {
            log.LogWarning("{Reason}: Epic ids are claims until they are (the login identifies those clients by install id, hardware and IP)", reason);
        }
        else
        {
            log.LogWarning("{Reason}: the keys fetched at {When:O} serve on", reason, _keys.FetchedAt);
        }
    }
}

/// <summary>Epic's JWKS over HTTP (<see cref="EpicSettings.JwksUrl"/>), with a short timeout: a fetch sits inside a registration at worst.</summary>
internal sealed class EpicHttpKeySource(IHttpClientFactory http, IOptionsMonitor<EpicSettings> settings, ILogger<EpicHttpKeySource> log) : IEpicKeySource
{
    public const string HttpClient = "epic-jwks";

    public async Task<string?> FetchAsync(CancellationToken ct)
    {
        string url = settings.CurrentValue.JwksUrl;
        try
        {
            using var client = http.CreateClient(HttpClient);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Epic's JWKS at {Url} answered {Status}", url, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Epic's JWKS at {Url} could not be fetched: {Reason}", url, e.Message);
            return null;
        }
    }
}

/// <summary>
/// Warms the keys when the web service starts, without blocking it and without a background service of its own (the
/// web service runs none): the first Epic registration after a restart then finds them held. Later fetches happen
/// inside a registration when the keys are due or a token names one not held (<see cref="EpicIdTokenVerifier"/>).
/// </summary>
internal sealed class EpicKeyWarmup(EpicIdTokenVerifier verifier, IOptionsMonitor<EpicSettings> settings, ILogger<EpicKeyWarmup> log) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (settings.CurrentValue.Enforced)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await verifier.RefreshIfDueAsync(CancellationToken.None);
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "The Epic key warm-up failed; the first Epic registration fetches them");
                }
            });
        }

        next(app);
    };
}

public static class EpicIdentityHosting
{
    /// <summary>The Epic ID token check: its settings, Epic's keys over HTTP, the verifier and its refresh (a test registers an <see cref="IEpicKeySource"/> of its own).</summary>
    public static WebApplicationBuilder AddEpicIdTokens(this WebApplicationBuilder builder)
    {
        builder.AddSetting<EpicSettings>("Epic");
        builder.Services.AddHttpClient(EpicHttpKeySource.HttpClient, c => c.Timeout = TimeSpan.FromSeconds(3));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IEpicKeySource, EpicHttpKeySource>();
        builder.Services.TryAddSingleton<EpicIdTokenVerifier>();
        builder.Services.TryAddSingleton<IEpicIdTokenVerifier>(sp => sp.GetRequiredService<EpicIdTokenVerifier>());
        builder.Services.AddTransient<IStartupFilter, EpicKeyWarmup>();
        return builder;
    }
}
