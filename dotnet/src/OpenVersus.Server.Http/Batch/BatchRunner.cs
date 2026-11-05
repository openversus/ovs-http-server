using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Batch;

/// <summary>/batch settings, changeable while the server runs.</summary>
public sealed class BatchSettings
{
    [Description("Where a sub-request another service answers is sent (docs/routes.json, owner): the router in front of the services, which sends it to its owner like any request of the game's.")]
    [Url]
    public string EdgeUrl { get; set; } = "http://127.0.0.1:18000";

    [Description("How long a sub-request sent through the router (Batch:EdgeUrl) may take, in seconds. After that it answers 504 and the batch answers with the rest.")]
    [Range(1, 600)]
    public int ForwardTimeoutSeconds { get; set; } = 30;
}

/// <summary>Where the http service's request pipeline starts, so a batch's sub-requests go through all of it.</summary>
public sealed class BatchPipeline
{
    public RequestDelegate? Head { get; set; }
}

/// <summary>
/// Runs the sub-requests of PUT /batch. The game sends a batch as <c>{options, requests: [{verb, url, headers, body}]}</c>
/// and reads <c>{responses: [{status_code, headers, body}]}</c> in the same order (the login sends two: 14 SSC and
/// config reads, then the inventory).
/// <para>
/// Each sub-request goes through this service's whole pipeline as a request of its own, in parallel, as the TS server
/// runs each through its whole Express stack. It gets what a TS sub-request gets: its own headers, the batch's
/// x-hydra-access-token, the Hydra content type, and the batch's client address (X-Real-IP here; the TS copy inherits
/// the batch request's realIp). The batch's other headers (x-steam-id, x-install-id, X-OVS-Identity, ...) are not
/// copied, there or here.
/// </para>
/// <para>
/// A sub-request another service answers (its route's owner in docs/routes.json is not this service) goes to that
/// service through the router (<see cref="BatchSettings.EdgeUrl"/>) as a request of its own, with the same headers.
/// </para>
/// <para>
/// Every answer is put into the response as the bytes it came as (<see cref="HydraRaw"/>).
/// </para>
/// <para>
/// Deliberately unlike the TS server, where one sub-request that never answers hangs the whole batch and a throwing
/// one can take the process down: a sub-request that fails answers 500, one sent through the router 502 (or 504 when
/// it takes longer than <see cref="BatchSettings.ForwardTimeoutSeconds"/>), a nested /batch 400, and the rest of the
/// batch answers as usual. The game asks for exactly that (<c>options.allow_failures</c>).
/// </para>
/// </summary>
public sealed class BatchRunner(
    BatchPipeline pipeline,
    IHttpContextFactory contexts,
    IHttpClientFactory clients,
    IOptionsMonitor<BatchSettings> settings,
    ServiceDefinition service,
    RouteOwners owners,
    ILogger<BatchRunner> log)
{
    public const string ClientName = "batch-edge";

    // Not copied onto a request sent through the router: they describe this connection or this body, not the sub-request.
    private static readonly HashSet<string> s_notForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Keep-Alive", "Expect", "Upgrade",
        "TE", "Trailer", "Proxy-Connection", "Accept-Encoding", "x-real-ip",
    };

    private sealed record SubRequest(string Verb, string Method, string Url, PathString Path, QueryString Query, Dictionary<string, string> Headers, JsonNode? Body);

    /// <summary>The Hydra-encoded answer to the batch <paramref name="body"/>; null when it holds no requests array.</summary>
    public async Task<byte[]?> RunAsync(HttpContext batch, JsonNode? body, CancellationToken ct)
    {
        if (body?["requests"] is not JsonArray requests)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        var current = settings.CurrentValue;
        string clientAddress = ClientAddress.Of(batch);
        var items = new JsonNode?[requests.Count];
        var local = new List<(int Index, Task<JsonNode> Answer)>();
        var elsewhere = new List<(int Index, Task<JsonNode> Answer)>();
        for (int i = 0; i < requests.Count; i++)
        {
            if (Parse(requests[i]) is not { } sub)
            {
                items[i] = Item(StatusCodes.Status400BadRequest, new JsonObject());
            }
            else if (sub.Path.Equals("/batch", StringComparison.OrdinalIgnoreCase))
            {
                items[i] = Item(StatusCodes.Status400BadRequest, new JsonObject());
            }
            else if (owners.OwnerOf(sub.Method, sub.Path) != service.Name)
            {
                elsewhere.Add((i, EdgeAsync(batch, sub, clientAddress, current, ct)));
            }
            else
            {
                local.Add((i, DispatchAsync(batch, sub, clientAddress, ct)));
            }
        }

        foreach (var (index, answer) in local.Concat(elsewhere))
        {
            items[index] = await answer;
        }

        log.LogDebug("Batch of {Count}: {Local} answered here, {Elsewhere} by other services, in {Ms} ms",
            requests.Count, requests.Count - elsewhere.Count, elsewhere.Count, watch.ElapsedMilliseconds);
        return HydraEncoder.Encode(new JsonObject { ["responses"] = new JsonArray(items) });
    }

    // TS: status_code, headers, body, in that order (the key order is part of the bytes). headers holds only what a
    // handler set itself; none of the ported ones sets any.
    private static JsonObject Item(int status, JsonNode? body) =>
        new() { ["status_code"] = status, ["headers"] = new JsonObject(), ["body"] = body };

    private static SubRequest? Parse(JsonNode? node)
    {
        if (node is not JsonObject sub || sub["url"] is not JsonValue urlValue || !urlValue.TryGetValue(out string? url) || !url.StartsWith('/'))
        {
            return null;
        }

        string verb = sub["verb"] is JsonValue v && v.TryGetValue(out string? s) ? s.ToUpperInvariant() : HttpMethods.Get;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sub["headers"] is JsonObject given)
        {
            foreach (var (name, value) in given)
            {
                headers[name] = value is JsonValue text && text.TryGetValue(out string? t) ? t : value?.ToJsonString() ?? "";
            }
        }

        // The method routing will see, for the route's owner (HydraMethodOverride makes the same change).
        string method = headers.TryGetValue(HydraMethodOverride.Header, out string? real) && HydraMethodOverride.IsMethod(real) ? real.ToUpperInvariant() : verb;
        int query = url.IndexOf('?');
        return new SubRequest(verb, method, url,
            PathString.FromUriComponent(query < 0 ? url : url[..query]),
            query < 0 ? QueryString.Empty : QueryString.FromUriComponent(url[query..]),
            headers, sub["body"]);
    }

    private async Task<JsonNode> DispatchAsync(HttpContext batch, SubRequest sub, string clientAddress, CancellationToken ct)
    {
        try
        {
            var head = pipeline.Head ?? throw new InvalidOperationException("the pipeline's head is unknown (UseBatchPipeline must come first)");
            byte[] requestBody = sub.Body is null ? [] : HydraEncoder.Encode(sub.Body);
            IHeaderDictionary headers = new HeaderDictionary();
            foreach (var (name, value) in sub.Headers)
            {
                headers[name] = value;
            }

            headers.Host = batch.Request.Host.Value;
            headers[HydraToken.Header] = batch.Request.Headers[HydraToken.Header];
            if (batch.Request.Headers.TryGetValue(HissZstd.Header, out var zstd))
            {
                // The client's plugin puts its capability header on the request it sees, the batch itself; the hiss
                // amalgamation inside the batch decides zstd or zlib by it.
                headers[HissZstd.Header] = zstd;
            }

            headers["x-real-ip"] = clientAddress;
            headers.ContentType = HydraBodies.ContentType;
            headers.ContentLength = requestBody.Length;

            var responseBody = new MemoryStream();
            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(new HttpRequestFeature
            {
                Protocol = batch.Request.Protocol,
                Scheme = batch.Request.Scheme,
                Method = sub.Verb,
                Path = sub.Path.Value ?? "/",
                QueryString = sub.Query.Value ?? "",
                RawTarget = sub.Url,
                Headers = headers,
                Body = new MemoryStream(requestBody),
            });
            features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            var bodyFeature = new StreamResponseBodyFeature(responseBody);
            features.Set<IHttpResponseBodyFeature>(bodyFeature);
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = batch.Connection.Id,
                RemoteIpAddress = batch.Connection.RemoteIpAddress,
                RemotePort = batch.Connection.RemotePort,
                LocalIpAddress = batch.Connection.LocalIpAddress,
                LocalPort = batch.Connection.LocalPort,
            });
            features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = ct });

            var context = contexts.Create(features);
            try
            {
                await head(context);
                await bodyFeature.CompleteAsync();
                var response = context.Response;
                return Item(response.StatusCode, Answer(response.ContentType, responseBody.ToArray()));
            }
            finally
            {
                contexts.Dispose(context);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "Batch sub-request {Verb} {Url} failed; it answers 500", sub.Verb, sub.Url);
            return Item(StatusCodes.Status500InternalServerError, new JsonObject());
        }
    }

    // A Hydra answer as its bytes; a text answer as its text, empty included (res.send("") gives the TS batch ""); no
    // answer at all (no content type, nothing sent) as null.
    private static JsonNode? Answer(string? contentType, byte[] bytes)
    {
        bool hydra = string.Equals(contentType, HydraBodies.ContentType, StringComparison.OrdinalIgnoreCase);
        return hydra ? (bytes.Length == 0 ? null : HydraRaw.Node(bytes))
            : bytes.Length > 0 || contentType is { Length: > 0 } ? JsonValue.Create(Encoding.UTF8.GetString(bytes))
            : null;
    }

    // A sub-request of another service's, through the router: what the in-process copy gets (its own headers, the
    // batch's token and client address, the batch's Host), as a request of its own.
    private async Task<JsonNode> EdgeAsync(HttpContext batch, SubRequest sub, string clientAddress, BatchSettings current, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(sub.Verb), $"{current.EdgeUrl.TrimEnd('/')}{sub.Url}");
            if (sub.Body is not null)
            {
                message.Content = new ByteArrayContent(HydraEncoder.Encode(sub.Body));
                message.Content.Headers.ContentType = new(HydraBodies.ContentType);
            }

            foreach (var (name, value) in sub.Headers)
            {
                if (!s_notForwarded.Contains(name))
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }

            message.Headers.Host = batch.Request.Host.Value;
            message.Headers.TryAddWithoutValidation(HydraToken.Header, (IEnumerable<string?>)batch.Request.Headers[HydraToken.Header]);
            if (batch.Request.Headers.TryGetValue(HissZstd.Header, out var zstd))
            {
                message.Headers.TryAddWithoutValidation(HissZstd.Header, (IEnumerable<string?>)zstd);
            }

            message.Headers.TryAddWithoutValidation("x-real-ip", clientAddress);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(current.ForwardTimeoutSeconds));
            using var response = await clients.CreateClient(ClientName).SendAsync(message, timeout.Token);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return Item((int)response.StatusCode, Answer(response.Content.Headers.ContentType?.MediaType, bytes));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Batch: {Verb} {Url} did not answer through {Edge} within {Seconds} s; it answers 504", sub.Verb, sub.Url, current.EdgeUrl, current.ForwardTimeoutSeconds);
            return Item(StatusCodes.Status504GatewayTimeout, new JsonObject());
        }
        catch (Exception e) when (e is HttpRequestException or HydraFormatException or IOException)
        {
            log.LogWarning("Batch: {Verb} {Url} through {Edge} failed ({Error}); it answers 502", sub.Verb, sub.Url, current.EdgeUrl, e.Message);
            return Item(StatusCodes.Status502BadGateway, new JsonObject());
        }
    }
}

public static class BatchHosting
{
    public static WebApplicationBuilder AddBatch(this WebApplicationBuilder builder)
    {
        builder.AddSetting<BatchSettings>("Batch");
        builder.Services.AddSingleton<BatchPipeline>();
        builder.Services.AddSingleton<BatchRunner>();
        builder.Services.TryAddSingleton(RouteOwners.Routes);
        // The timeout is per batch, from the setting, so the client's own is off; answers are passed on as they are.
        builder.Services.AddHttpClient(BatchRunner.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
        return builder;
    }

    /// <summary>Must come first in the pipeline: remembers where it starts, for the batch's sub-requests.</summary>
    public static IApplicationBuilder UseBatchPipeline(this IApplicationBuilder app)
    {
        var pipeline = app.ApplicationServices.GetRequiredService<BatchPipeline>();
        return app.Use(next =>
        {
            pipeline.Head = next;
            return next;
        });
    }
}
