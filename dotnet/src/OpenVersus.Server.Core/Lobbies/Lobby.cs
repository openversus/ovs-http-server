using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Lobbies;

// The game's lobby tree (jmap multiversus.h: UMvsPlayerLobby, and under it UMvsPartyLobby, UCustomGameLobby,
// UMvsRiftLobby, UMvsArenaLobby) as the server keeps it: one record per lobby, lobby:{id} (LobbyStore). The base class's
// parser, which every kind's answer meets (the virtual at 0x1428e9290, build f97148ff), requires MatchID, LeaderID,
// ReadyPlayers and Teams. The base is shaped by the party lobby, the one every login makes; the other kinds conform to it.
//
// lobby:{id}, JSON, with the TS server's fields (ssc.ts, websocket.ts), which every reader of the key relies on:
//   lobbyId, ownerId (who leads), ownerUsername, mode ("1v1", "2v2", ... in a party; "rift_lobby" and "arena_lobby" name
//   the kind), playerIds (in the order they joined), createdAt (ms)
// Any other field (joinable; a rift lobby's riftConfigSlug, chapterGuid, chapterDifficulty) is kept as it was read. A
// record with no playerIds array (an older custom lobby) is no lobby here. Custom lobbies are still their own documents
// (custom_lobby_ssc:{id}, CustomLobbyService) until they move onto the base.

/// <summary>A lobby as lobby:{id} holds it; its kind (<see cref="Template"/>) is the game's lobby template.</summary>
public abstract class Lobby
{
    private readonly JsonObject _json;

    private protected Lobby(string id, string ownerId, string ownerUsername, string mode, List<string> playerIds, JsonNode? createdAt, JsonObject? read)
    {
        Id = id;
        OwnerId = ownerId;
        OwnerUsername = ownerUsername;
        Mode = mode;
        PlayerIds = playerIds;
        CreatedAt = createdAt;
        _json = read?.DeepClone().AsObject() ?? new JsonObject
        {
            ["lobbyId"] = id,
            ["ownerId"] = ownerId,
            ["ownerUsername"] = ownerUsername,
            ["mode"] = mode,
            ["playerIds"] = null,
            ["createdAt"] = createdAt?.DeepClone(),
        };
    }

    public string Id { get; }

    /// <summary>The player who leads the lobby (the game's LeaderID).</summary>
    public string OwnerId { get; set; }

    /// <summary>The name of the player who made the lobby, as it was then.</summary>
    public string OwnerUsername { get; }

    public string Mode { get; set; }

    /// <summary>The players, in the order they joined.</summary>
    public List<string> PlayerIds { get; }

    public JsonNode? CreatedAt { get; }

    /// <summary>The game's lobby template: party_lobby, rift_lobby, arena_lobby.</summary>
    public abstract string Template { get; }

    /// <summary>When the lobby was made (whole seconds); a createdAt that is not a number is the TS server's NaN date, sent as 0.</summary>
    public long CreatedSeconds => CreatedAt is JsonValue v && v.TryGetValue<double>(out double ms) && double.IsFinite(ms) ? (long)Math.Floor(ms / 1000) : 0;

    /// <summary>A field the base does not model (a kind's own), as read; null when absent.</summary>
    protected JsonNode? Field(string name) => _json[name];

    /// <summary>Sets a field the base does not model; it is written with the lobby.</summary>
    public void SetField(string name, JsonNode? value) => _json[name] = value;

    /// <summary>The record: the fields as read, with the base's own set from this object.</summary>
    public string ToJson()
    {
        var json = _json.DeepClone().AsObject();
        json["ownerId"] = OwnerId;
        json["mode"] = Mode;
        json["playerIds"] = new JsonArray([.. PlayerIds.Select(p => (JsonNode?)p)]);
        return Js.Stringify(json);
    }

    /// <summary>The lobby a stored record holds, of the kind its mode names; null when it is not JSON or has no playerIds.</summary>
    public static Lobby? FromJson(string id, string raw)
    {
        JsonObject? json;
        try
        {
            json = Js.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (json?["playerIds"] is not JsonArray ids)
        {
            return null;
        }

        string ownerId = Str(json, "ownerId") ?? "", ownerUsername = Str(json, "ownerUsername") ?? "", mode = Str(json, "mode") ?? "";
        var playerIds = ids.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : Js.Stringify(n)).ToList();
        return mode switch
        {
            RiftLobby.TemplateName => new RiftLobby(id, ownerId, ownerUsername, playerIds, json["createdAt"], json),
            ArenaLobby.TemplateName => new ArenaLobby(id, ownerId, ownerUsername, playerIds, json["createdAt"], json),
            _ => new PartyLobby(id, ownerId, ownerUsername, mode, playerIds, json["createdAt"], json),
        };
    }

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>The party lobby (UMvsPartyLobby): the one every login makes; its mode is the party's game mode.</summary>
public sealed class PartyLobby : Lobby
{
    public const string TemplateName = "party_lobby";

    /// <summary>A new party lobby (createdAt in ms).</summary>
    public PartyLobby(string id, string ownerId, string ownerUsername, string mode, List<string> playerIds, long createdAtMs)
        : base(id, ownerId, ownerUsername, mode, playerIds, createdAtMs, null)
    {
    }

    internal PartyLobby(string id, string ownerId, string ownerUsername, string mode, List<string> playerIds, JsonNode? createdAt, JsonObject read)
        : base(id, ownerId, ownerUsername, mode, playerIds, createdAt, read)
    {
    }

    public override string Template => TemplateName;
}

/// <summary>The rift lobby (UMvsRiftLobby, RiftLobbyService): a rift, a chapter and a difficulty.</summary>
public sealed class RiftLobby : Lobby
{
    public const string TemplateName = "rift_lobby";

    internal RiftLobby(string id, string ownerId, string ownerUsername, List<string> playerIds, JsonNode? createdAt, JsonObject read)
        : base(id, ownerId, ownerUsername, TemplateName, playerIds, createdAt, read)
    {
    }

    public override string Template => TemplateName;

    public string RiftConfigSlug => Field("riftConfigSlug") is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    public int ChapterDifficulty => Field("chapterDifficulty") is JsonValue d && d.TryGetValue(out double n) ? (int)n : 0;
}

/// <summary>The Arena lobby (UMvsArenaLobby, ArenaLobbyService): eight teams of two.</summary>
public sealed class ArenaLobby : Lobby
{
    public const string TemplateName = "arena_lobby";

    internal ArenaLobby(string id, string ownerId, string ownerUsername, List<string> playerIds, JsonNode? createdAt, JsonObject read)
        : base(id, ownerId, ownerUsername, TemplateName, playerIds, createdAt, read)
    {
    }

    public override string Template => TemplateName;
}
