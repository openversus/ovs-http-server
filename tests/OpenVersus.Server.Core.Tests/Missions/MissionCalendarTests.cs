using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Static;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Core.Tests.Missions;

/// <summary>get_hiss_calendar_events with missions on: the live containers' events run on, nothing else changes.</summary>
public sealed class MissionCalendarTests
{
    private static JsonNode Calendar() => JsonNode.Parse(StaticResponses.Json("ssc-get-hiss-calendar-events"))!;

    private static JsonObject Event(JsonNode calendar, string name) =>
        Find(calendar, name) ?? throw new InvalidOperationException($"no {name}");

    private static JsonObject? Find(JsonNode? node, string name) => node switch
    {
        JsonObject o when o["name"] is JsonValue v && v.TryGetValue(out string? n) && n == name => o,
        JsonObject o => o.Select(kv => Find(kv.Value, name)).FirstOrDefault(f => f is not null),
        JsonArray a => a.Select(c => Find(c, name)).FirstOrDefault(f => f is not null),
        _ => null,
    };

    [Fact]
    public void TheBattlePassEventEndsYearsLaterAndNothingElseChanges()
    {
        var calendar = Calendar();
        Assert.True(MissionCalendar.Adjust(calendar, new MissionSettings { Enabled = true }));

        var bp = Event(calendar, "evt_battlepass_season_five");
        Assert.Equal(2045, bp["data"]!["TimeSpan"]!["EndTime"]!["Year"]!.GetValue<int>());
        Assert.Equal(new DateTimeOffset(2045, 5, 30, 10, 55, 0, TimeSpan.Zero).ToUnixTimeSeconds(), bp["entry_options"]!["end_at"]!["_hydra_unix_date"]!.GetValue<long>());
        Assert.True(bp["bIsCurrentlyActive"]!.GetValue<bool>());

        // Put the three values back: the rest is the static answer, byte for byte (JSON and Hydra).
        var original = Event(Calendar(), "evt_battlepass_season_five");
        bp["data"]!["TimeSpan"]!["EndTime"]!["Year"] = original["data"]!["TimeSpan"]!["EndTime"]!["Year"]!.DeepClone();
        bp["entry_options"]!["end_at"] = original["entry_options"]!["end_at"]!.DeepClone();
        bp["bIsCurrentlyActive"] = original["bIsCurrentlyActive"]!.DeepClone();
        Assert.Equal(StaticResponses.Json("ssc-get-hiss-calendar-events"), Js.Stringify(calendar));
        Assert.Equal(StaticResponses.Hydra("ssc-get-hiss-calendar-events"), HydraCodec.EncodeJson(Js.Stringify(calendar)));
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 0)]
    public void OffOrZeroYearsLeavesTheCalendar(bool enabled, int years) =>
        Assert.False(MissionCalendar.Adjust(Calendar(), new MissionSettings { Enabled = enabled, EventEndYears = years }));

    [Fact]
    public void OnlyTheLiveContainersEventsMove()
    {
        var calendar = Calendar();
        Assert.False(MissionCalendar.Adjust(calendar, new MissionSettings { Enabled = true, Containers = "miscon_unlockable_*" }));
        Assert.True(MissionCalendar.Adjust(calendar, new MissionSettings { Enabled = true, Containers = "miscon_event_arenas5-3" }));
        Assert.Equal(2025, Event(calendar, "evt_battlepass_season_five")["data"]!["TimeSpan"]!["EndTime"]!["Year"]!.GetValue<int>());
    }
}
