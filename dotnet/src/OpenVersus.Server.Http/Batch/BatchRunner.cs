using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Batch;

/// <summary>/batch settings, changeable while the server runs.</summary>
public sealed class BatchSettings : IValidatableObject
{
    [Description("The TS server, which answers the sub-requests of a batch that C# has not ported, as one TS batch. A migration bridge: see dotnet/docs/MIGRATION-BRIDGES.md.")]
    [Url]
    public string TsUrl { get; set; } = "http://127.0.0.1:18000";

    [Description("Routes the TS server answers inside a batch although C# has ported them, as METHOD /path separated by commas (the form of Proxy:PortedRoutes). Taking a route out of Proxy:PortedRoutes does not reach into batches; listing it here does. Empty: C# answers every route it has ported.")]
    public string ForwardRoutes { get; set; } = "";

    [Description("How long a batch waits for the TS server, in seconds. After that the sub-requests sent there answer 504 and the batch answers with the rest (the TS server itself waits for ever on a sub-request that never answers).")]
    [Range(1, 600)]
    public int ForwardTimeoutSeconds { get; set; } = 30;

    // Refused when set, so the value shown is always the one in use.
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        string? problem = null;
        try
        {
            RouteList.Parse(ForwardRoutes);
        }
        catch (FormatException e)
        {
            problem = e.Message;
        }

        if (problem is not null)
        {
            yield return new ValidationResult(problem, [nameof(ForwardRoutes)]);
        }
    }
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
/// MIGRATION BRIDGE (dotnet/docs/MIGRATION-BRIDGES.md, 3): a sub-request that reaches a stub (not ported), or whose
/// route is in <see cref="BatchSettings.ForwardRoutes"/>, goes to the TS server; all of those in one TS /batch that
/// carries this batch's headers, so the TS server runs them exactly as it runs its own. Its answers, and this service's
/// own, are put into the response as the bytes they came as (<see cref="HydraRaw"/>).
/// </para>
/// <para>
/// Deliberately unlike the TS server, where one sub-request that never answers hangs the whole batch and a throwing
/// one can take the process down: a sub-request that fails answers 500, the TS server's part answers 502 (or 504 when
/// it takes longer than <see cref="BatchSettings.ForwardTimeoutSeconds"/>), a nested /batch 400, and the rest of the
/// batch answers as usual. The game asks for exactly that (<c>options.allow_failures</c>).
/// </para>
/// </summary>
public sealed class BatchRunner(
    BatchPipeline pipeline,
    IHttpContextFactory contexts,
    IHttpClientFactory clients,
    IOptionsMonitor<BatchSettings> settings,
    ILogger<BatchRunner> log)
{
    public const string ClientName = "batch-ts";

    /// <summary>
    /// Marks the batch this service sends the TS server. A batch arriving here with it came back: Batch:TsUrl points at
    /// this service or at the proxy (which sends /batch here) instead of the TS server, and forwarding it again would
    /// go round until the timeouts.
    /// </summary>
    public const string ForwardedHeader = "X-OVS-Batch-Forwarded";

    // Not copied onto the TS request: they describe this connection or this body, not the batch.
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

        if (batch.Request.Headers.ContainsKey(ForwardedHeader))
        {
            throw new BatchLoopException();
        }

        var watch = Stopwatch.StartNew();
        var current = settings.CurrentValue;
        var forwardRoutes = new RouteList(current.ForwardRoutes);
        string clientAddress = ClientAddress.Of(batch);
        var items = new JsonNode?[requests.Count];
        var forward = new List<int>();
        var local = new List<(int Index, Task<(bool Stub, JsonNode Item)> Answer)>();
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
            else if (forwardRoutes.Contains(sub.Method, sub.Path))
            {
                forward.Add(i);
            }
            else
            {
                local.Add((i, DispatchAsync(batch, sub, clientAddress, ct)));
            }
        }

        foreach (var (index, answer) in local)
        {
            var (stub, item) = await answer;
            if (stub)
            {
                forward.Add(index);
            }
            else
            {
                items[index] = item;
            }
        }

        forward.Sort();
        if (forward.Count > 0)
        {
            var answers = await ForwardAsync(batch, body, requests, forward, clientAddress, current, ct);
            for (int k = 0; k < forward.Count; k++)
            {
                items[forward[k]] = answers[k];
            }
        }

        log.LogDebug("Batch of {Count}: {Local} answered here, {Forwarded} by the TS server, in {Ms} ms",
            requests.Count, requests.Count - forward.Count, forward.Count, watch.ElapsedMilliseconds);
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

        // The method routing will see, for Batch:ForwardRoutes (HydraMethodOverride makes the same change).
        string method = headers.TryGetValue(HydraMethodOverride.Header, out string? real) && HydraMethodOverride.IsMethod(real) ? real.ToUpperInvariant() : verb;
        int query = url.IndexOf('?');
        return new SubRequest(verb, method, url,
            PathString.FromUriComponent(query < 0 ? url : url[..query]),
            query < 0 ? QueryString.Empty : QueryString.FromUriComponent(url[query..]),
            headers, sub["body"]);
    }

    private async Task<(bool Stub, JsonNode Item)> DispatchAsync(HttpContext batch, SubRequest sub, string clientAddress, CancellationToken ct)
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
                if (response.Headers.ContainsKey(Stub.Header))
                {
                    return (true, new JsonObject());
                }

                // A Hydra answer as its bytes; a text answer as its text, empty included (res.send("") gives the TS batch "");
                // no answer at all (no content type, nothing sent) as null.
                byte[] bytes = responseBody.ToArray();
                bool hydra = string.Equals(response.ContentType, HydraBodies.ContentType, StringComparison.OrdinalIgnoreCase);
                JsonNode? answer = hydra ? (bytes.Length == 0 ? null : HydraRaw.Node(bytes))
                    : bytes.Length > 0 || response.ContentType is { Length: > 0 } ? JsonValue.Create(Encoding.UTF8.GetString(bytes))
                    : null;
                return (false, Item(response.StatusCode, answer));
            }
            finally
            {
                contexts.Dispose(context);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "Batch sub-request {Verb} {Url} failed; it answers 500", sub.Verb, sub.Url);
            return (false, Item(StatusCodes.Status500InternalServerError, new JsonObject()));
        }
    }

    private async Task<JsonNode?[]> ForwardAsync(HttpContext batch, JsonNode body, JsonArray requests, List<int> forward, string clientAddress, BatchSettings current, CancellationToken ct)
    {
        var tsBatch = new JsonObject();
        if (body["options"] is { } options)
        {
            tsBatch["options"] = options.DeepClone();
        }

        tsBatch["requests"] = new JsonArray(forward.Select(i => requests[i]?.DeepClone()).ToArray());
        int failure = StatusCodes.Status502BadGateway;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Put, $"{current.TsUrl.TrimEnd('/')}/batch")
            {
                Content = new ByteArrayContent(HydraEncoder.Encode(tsBatch)),
            };
            message.Content.Headers.ContentType = new(HydraBodies.ContentType);
            foreach (var (name, values) in batch.Request.Headers)
            {
                if (!s_notForwarded.Contains(name))
                {
                    message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
                }
            }

            message.Headers.Host = batch.Request.Host.Value;
            message.Headers.TryAddWithoutValidation("x-real-ip", clientAddress);
            message.Headers.TryAddWithoutValidation(ForwardedHeader, "1");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(current.ForwardTimeoutSeconds));
            using var response = await clients.CreateClient(ClientName).SendAsync(message, timeout.Token);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if ((int)response.StatusCode != StatusCodes.Status200OK || response.Content.Headers.ContentType?.MediaType != HydraBodies.ContentType)
            {
                log.LogWarning("Batch: the TS server answered {Status} ({Type}) for {Count} sub-request(s); they answer 502",
                    (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, forward.Count);
            }
            else if (HydraDecoder.ArrayItems(bytes, "responses") is { } ranges && ranges.Count == forward.Count)
            {
                return ranges.Select(r => (JsonNode?)HydraRaw.Node(bytes[r])).ToArray();
            }
            else
            {
                log.LogWarning("Batch: the TS server's answer does not hold {Count} responses; they answer 502", forward.Count);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Batch: the TS server did not answer {Count} sub-request(s) within {Seconds} s; they answer 504", forward.Count, current.ForwardTimeoutSeconds);
            failure = StatusCodes.Status504GatewayTimeout;
        }
        catch (Exception e) when (e is HttpRequestException or HydraFormatException or IOException)
        {
            log.LogWarning("Batch: forwarding {Count} sub-request(s) to the TS server failed ({Error}); they answer 502", forward.Count, e.Message);
        }

        return forward.Select(_ => (JsonNode?)Item(failure, new JsonObject())).ToArray();
    }
}

/// <summary>A batch this service forwarded came back to it (see <see cref="BatchRunner.ForwardedHeader"/>).</summary>
public sealed class BatchLoopException() : Exception("a batch forwarded to Batch:TsUrl came back to this service");

public static class BatchHosting
{
    public static WebApplicationBuilder AddBatch(this WebApplicationBuilder builder)
    {
        builder.AddSetting<BatchSettings>("Batch");
        builder.Services.AddSingleton<BatchPipeline>();
        builder.Services.AddSingleton<BatchRunner>();
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
