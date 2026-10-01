using Microsoft.Extensions.Options;
using OpenVersus.Server.Http.Batch;

namespace OpenVersus.Server.Http.Hosting;

/// <summary>
/// MIGRATION BRIDGE (dotnet/docs/MIGRATION-BRIDGES.md, 6): hands one request to the TS server unchanged and answers with
/// what it answered. The party lobby routes the game also uses inside a custom lobby (create_party_lobby,
/// leave_player_lobby, invite_to_player_lobby, lock_lobby_loadout, set_ready_for_lobby) are ported, custom lobbies are
/// not: those requests go here. The body goes as the game sent it (the Hydra bytes, or the JSON of a batch
/// sub-request), with the game's headers and its address in X-Real-IP; the answer comes back byte for byte. The TS
/// server is <see cref="BatchSettings.TsUrl"/>, and <see cref="BatchSettings.ForwardTimeoutSeconds"/> bounds the wait (then
/// 504; a failure to reach it 502).
/// </summary>
public static class TsForwarder
{
    /// <summary>Marks a request this service sent the TS server; one arriving here came back (Batch:TsUrl points here).</summary>
    public const string ForwardedHeader = "X-OVS-Forwarded";

    private static readonly HashSet<string> s_notForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Keep-Alive", "Expect", "Upgrade",
        "TE", "Trailer", "Proxy-Connection", "Accept-Encoding", "x-real-ip",
    };

    // The answer's headers that describe the transfer, not the answer.
    private static readonly HashSet<string> s_notReturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Date", "Server",
    };

    public static async Task ForwardAsync(HttpContext context, CancellationToken ct)
    {
        var settings = context.RequestServices.GetRequiredService<IOptionsMonitor<BatchSettings>>().CurrentValue;
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TsForwarder));
        var request = context.Request;
        if (request.Headers.ContainsKey(ForwardedHeader))
        {
            log.LogError("{Path} forwarded to the TS server came back here: Batch:TsUrl ({TsUrl}) points at this service", request.Path, settings.TsUrl);
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            return;
        }

        byte[] body;
        string? contentType;
        if (context.Items[HydraBodies.RawItemKey] is byte[] raw)
        {
            body = raw;
            contentType = HydraBodies.ContentType;
        }
        else
        {
            request.EnableBuffering();
            request.Body.Position = 0;
            using var copy = new MemoryStream();
            await request.Body.CopyToAsync(copy, ct);
            request.Body.Position = 0;
            body = copy.ToArray();
            contentType = request.ContentType;
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"{settings.TsUrl.TrimEnd('/')}{request.Path}{request.QueryString}")
        {
            Content = new ByteArrayContent(body),
        };
        if (contentType is not null)
        {
            message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (var (name, values) in request.Headers)
        {
            if (!s_notForwarded.Contains(name))
            {
                message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        message.Headers.Host = request.Host.Value;
        message.Headers.TryAddWithoutValidation("x-real-ip", ClientAddress.Of(context));
        message.Headers.TryAddWithoutValidation(ForwardedHeader, "1");

        var response = context.Response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ForwardTimeoutSeconds));
        try
        {
            using var answer = await context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(BatchRunner.ClientName)
                .SendAsync(message, timeout.Token);
            byte[] bytes = await answer.Content.ReadAsByteArrayAsync(timeout.Token);
            response.StatusCode = (int)answer.StatusCode;
            foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
            {
                if (!s_notReturned.Contains(name))
                {
                    response.Headers[name] = values.ToArray();
                }
            }

            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, ct);
            log.LogInformation("{Method} {Path} handed to the TS server: {Status}", request.Method, request.Path, response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("{Method} {Path}: the TS server did not answer within {Seconds} s; 504", request.Method, request.Path, settings.ForwardTimeoutSeconds);
            response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("{Method} {Path}: forwarding to the TS server failed ({Error}); 502", request.Method, request.Path, e.Message);
            response.StatusCode = StatusCodes.Status502BadGateway;
        }

        // FastEndpoints would send its own answer after the handler otherwise.
        FastEndpoints.HttpContextExtensions.MarkResponseStart(context);
    }
}
