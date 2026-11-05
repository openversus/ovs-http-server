using FastEndpoints;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>
/// The game talks Hydra binary (application/x-ag-binary); endpoints talk JSON. For a request whose Content-Type is
/// Hydra, this decodes the body and hands it on as JSON, and turns the endpoint's JSON response back into Hydra with
/// the headers the TS server sends. Endpoints bind and answer JSON as usual and never see the difference. This is the
/// TS server's hydraDecoderMiddleware, which does the same by overriding res.json.
/// <para>
/// As there: a body that does not decode is logged and passed on as <c>{}</c>, and only a JSON response is encoded
/// (a stub's empty answer, text or a file passes through as it is). Unlike there, X-Hydra-Processing-Time is the time
/// taken (the TS server writes start minus end, a negative number); nothing reads it.
/// </para>
/// <para>
/// Must run before routing (call <c>UseRouting</c> after it): endpoint matching checks the request's content type, and
/// would turn a Hydra request away from a JSON endpoint with 415 before this had made it JSON.
/// </para>
/// </summary>
public static class HydraBodies
{
    public const string ContentType = "application/x-ag-binary";

    /// <summary>Set on a request that came in as Hydra; its JSON response goes back as Hydra.</summary>
    public const string ItemKey = "ovs.hydra";

    // A decoded request can hold a NaN double, which plain JSON cannot; this writes it as NaN so the conversion never fails.
    private static readonly JsonSerializerOptions s_json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static IApplicationBuilder UseHydraBodies(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!string.Equals(context.Request.ContentType, ContentType, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var watch = Stopwatch.StartNew();
        context.Items[ItemKey] = true;
        await DecodeRequestAsync(context);

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        await EncodeResponseAsync(context, buffer, watch);
    });

    private static async Task DecodeRequestAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        JsonNode? value;
        if (body.Length == 0)
        {
            value = null;
        }
        else
        {
            try
            {
                value = HydraDecoder.Decode(body.GetBuffer().AsSpan(0, (int)body.Length));
            }
            catch (HydraFormatException e)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HydraBodies))
                    .LogError("Hydra body of {Method} {Path} did not decode ({Error}); passing on {{}}", context.Request.Method, context.Request.Path, e.Message);
                value = new JsonObject();
            }
        }

        byte[] json = value is null ? [] : Encoding.UTF8.GetBytes(value.ToJsonString(s_json));
        context.Request.Body = new MemoryStream(json);
        context.Request.ContentLength = json.Length;
        context.Request.ContentType = "application/json";
    }

    private static async Task EncodeResponseAsync(HttpContext context, MemoryStream buffer, Stopwatch watch)
    {
        var response = context.Response;
        bool json = response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
        if (!json || buffer.Length == 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(response.Body);
            return;
        }

        await WriteHydraAsync(context, HydraEncoder.Encode(JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), documentOptions: new JsonDocumentOptions())), watch.Elapsed, response.Body, default);
    }

    /// <summary>
    /// Sends <paramref name="value"/> as the response directly: Hydra for a Hydra request, else JSON. For a value JSON
    /// text cannot carry, such as the NaN the TS server sends for an undefined field.
    /// </summary>
    public static async Task WriteAsync(HttpContext context, JsonNode value, CancellationToken ct)
    {
        if (!IsHydra(context))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(value.ToJsonString(s_json), ct);
            return;
        }

        // The buffered body this middleware swapped in is still the response body; the Hydra content type makes it
        // pass through untouched. Nothing has reached the client yet, so FastEndpoints is told a response was sent,
        // or it would send its own (204) after the handler.
        await WriteHydraAsync(context, HydraEncoder.Encode(value), TimeSpan.Zero, context.Response.Body, ct);
        context.MarkResponseStart();
    }

    /// <summary>Sends already-encoded Hydra as the response to a Hydra request (see <see cref="WriteAsync"/>).</summary>
    public static async Task WriteEncodedAsync(HttpContext context, byte[] encoded, CancellationToken ct)
    {
        await WriteHydraAsync(context, encoded, TimeSpan.Zero, context.Response.Body, ct);
        context.MarkResponseStart();
    }

    private static async Task WriteHydraAsync(HttpContext context, byte[] encoded, TimeSpan elapsed, Stream body, CancellationToken ct)
    {
        var response = context.Response;
        response.ContentType = ContentType;
        response.ContentLength = encoded.Length;
        response.Headers["X-Hydra-Server-Time"] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Hydra-Info"] = "openversus";
        response.Headers["X-Hydra-Processing-Time"] = elapsed.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
        await body.WriteAsync(encoded, ct);
    }

    /// <summary>True when the request came in as Hydra (so its JSON response goes back as Hydra).</summary>
    public static bool IsHydra(HttpContext context) => context.Items.ContainsKey(ItemKey);
}
