using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Identity;

namespace OpenVersus.Server.Core.Calendar;

// GET /ssc/invoke/get_calendar_events, ported from the TS server's handleSsc_invoke_get_calendar_events (handlers/ssc.ts,
// branch infinity-war): the front-end carousel (calendar-events.json, generated from the TS literal by
// tools/access/gen_templates.mjs), with the required-update popup kept only for a player the client gate says must
// update. The popup's ids come from the session token's id and the player's toast count, so a new toast is a new
// carousel entry for the game to show.
//
// Redis, read     what IClientUpdateGate.ForRequestAsync reads; client_update_modal_nonce:{id}

public interface ICalendarService
{
    /// <summary>The calendar for the player a request is from (<paramref name="claims"/>: the session token's).</summary>
    Task<JsonNode> EventsAsync(AccountLookup lookup, JsonObject? claims);
}

internal sealed class CalendarService(IClientUpdateGate gate, IOptionsMonitor<ClientSettings> settings, TimeProvider time, ILogger<CalendarService> log) : ICalendarService
{
    private const string UpdateSlug = "ovs-required-update";
    private static readonly JsonNode s_template = Load();

    public async Task<JsonNode> EventsAsync(AccountLookup lookup, JsonObject? claims)
    {
        // Math.floor(Date.now() / 1000) - 60.
        long start = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000 - 60;
        // As there, the calendar always answers: when the check fails it is served without the popup.
        var state = new ClientUpdateState("", "", false, false);
        double nonce = 0;
        try
        {
            state = await gate.ForRequestAsync(lookup, claims);
            nonce = await gate.ModalNonceAsync(state.AccountId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError("Client update check for the calendar failed: {Error}", e.Message);
        }

        var response = s_template.DeepClone();
        var events = response["body"]!["Events"]!.AsArray();
        if (!state.Required)
        {
            foreach (var update in events.Where(e => (string?)e?["data"]?["slug"] == UpdateSlug).ToList())
            {
                events.Remove(update);
            }

            return response;
        }

        log.LogWarning("Serving required-update calendar modal to {Account} (version={Version})",
            state.AccountId.Length > 0 ? state.AccountId : "unresolved", state.ClientVersion.Length > 0 ? state.ClientVersion : "legacy");
        // `ovs-required-update:${req.token?.id || "unresolved"}:${updateModalNonce}`: the token's id, not the resolved
        // player's. The count is a whole number (parseInt), written as JavaScript writes it below 1e21.
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"ovs-required-update:{TokenId(claims)}:{nonce.ToString("0", CultureInfo.InvariantCulture)}")));
        var values = new Dictionary<string, JsonNode>
        {
            ["{{start}}"] = JsonValue.Create(start),
            ["{{entryId}}"] = JsonValue.Create(hash[..24]),
            ["{{recordId}}"] = JsonValue.Create(hash[24..48]),
            ["{{updateUrl}}"] = JsonValue.Create(settings.CurrentValue.UpdateUrl),
            ["{{updateMessage}}"] = JsonValue.Create(settings.CurrentValue.UpdateMessage),
        };
        Fill(response, values);
        return response;
    }

    // req.token?.id || "unresolved": a string or a number as JavaScript writes it; anything falsy is "unresolved".
    private static string TokenId(JsonObject? claims) => claims?["id"] switch
    {
        JsonValue v when v.TryGetValue(out string? s) && s.Length > 0 => s,
        JsonValue v when v.TryGetValue(out double d) && d != 0 && !double.IsNaN(d) => d.ToString("R", CultureInfo.InvariantCulture),
        _ => "unresolved",
    };

    private static void Fill(JsonNode? node, IReadOnlyDictionary<string, JsonNode> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue(out string? text) && values.TryGetValue(text, out var value))
                    {
                        obj[key] = value.DeepClone();
                    }
                    else
                    {
                        Fill(obj[key], values);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Fill(item, values);
                }

                break;
        }
    }

    private static JsonNode Load()
    {
        using var stream = typeof(CalendarService).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Calendar.calendar-events.json")
            ?? throw new InvalidOperationException("calendar-events.json is not embedded");
        return JsonNode.Parse(stream)!;
    }
}

public static class CalendarHosting
{
    public static WebApplicationBuilder AddCalendar(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICalendarService, CalendarService>();
        return builder;
    }
}
