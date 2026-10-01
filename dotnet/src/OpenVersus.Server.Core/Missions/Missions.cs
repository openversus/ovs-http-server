using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Missions;

// POST /ssc/invoke/get_or_create_mission_object, ported from the TS server's handleSsc_invoke_get_or_create_mission_object
// and applyMissionsSwitch (handlers/ssc.ts): one fixed mission object for every player (mission-object.json, generated
// from the TS source by tools/missions/gen_mission_object.mjs), owner_id the player's id, and no containers at all
// unless Missions:Enabled (MISSIONS_ENABLED there; off by default and on prod: every captured answer since the switch
// came in carries MissionControllerContainers {}).
//
// The fixed object is one WB account's, as it stood when copied (its id 6658a026... is the one in WB's own websocket
// messages): its progress, GUIDs and dates are that account's, and nothing ever changes them. Real per-player missions
// are planned in docs/MISSIONS.md.
//
// Unlike there: a request with no player id gets no owner_id (JSON.stringify drops the TS server's undefined; its
// Hydra encoder would write NaN).

/// <summary>Mission settings.</summary>
public sealed class MissionSettings
{
    [Description("Answer get_or_create_mission_object with the fixed mission set (one WB account's missions, never updated) instead of none (MISSIONS_ENABLED on the TS server).")]
    public bool Enabled { get; set; }
}

public static class MissionObject
{
    private static readonly Lazy<JsonObject> s_object = new(() =>
    {
        using var stream = typeof(MissionObject).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Missions.mission-object.json")
            ?? throw new InvalidOperationException("mission-object.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>The get_or_create_mission_object answer for <paramref name="accountId"/> (a copy).</summary>
    public static JsonObject Answer(string? accountId, bool enabled)
    {
        var answer = s_object.Value.DeepClone().AsObject();
        var body = answer["body"]!.AsObject();
        if (string.IsNullOrEmpty(accountId))
        {
            body.Remove("owner_id");
        }
        else
        {
            body["owner_id"] = accountId;
        }

        if (!enabled)
        {
            body["server_data"]!["MissionControllerContainers"] = new JsonObject();
        }

        return answer;
    }
}

public static class MissionHosting
{
    public static WebApplicationBuilder AddMissions(this WebApplicationBuilder builder)
    {
        builder.AddSetting<MissionSettings>("Missions");
        return builder;
    }
}
