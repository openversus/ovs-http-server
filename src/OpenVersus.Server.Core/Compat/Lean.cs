using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace OpenVersus.Server.Core.Compat;

/// <summary>
/// Stored values as the TS server's lean reads (mongoose .lean()) hand them to res.send: strings, numbers, booleans,
/// null, arrays and documents, each document's keys in a JS object's order (integer-like keys first). Anything else is refused rather than guessed at (a lean
/// ObjectId or Date would reach the Hydra encoder as an object, which no route here has been checked against).
/// FriendsService and MatchHistoryService have their own copies, with the cases their data needs; they keep stored
/// order, which is the same while no key is integer-like.
/// </summary>
public static class Lean
{
    public static JsonNode? Value(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString s => s.Value,
        BsonBoolean b => b.Value,
        BsonInt32 i => i.Value,
        BsonInt64 l => l.Value,
        BsonDouble d => d.Value,
        BsonArray a => new JsonArray(a.Select(Value).ToArray()),
        // A JS object's key order, not the stored one (Js.OrderedLikeAnObject).
        BsonDocument doc => new JsonObject(Js.OrderedLikeAnObject(doc, e => e.Name).Select(e => KeyValuePair.Create(e.Name, Value(e.Value)))),
        _ => throw new FormatException($"a {value.BsonType} value"),
    };

    /// <summary>Whether JavaScript takes the value as truthy (a document or array always is).</summary>
    public static bool Truthy(BsonValue? value) => value switch
    {
        null or BsonNull or BsonUndefined => false,
        BsonString s => s.Value.Length > 0,
        BsonBoolean b => b.Value,
        BsonInt32 i => i.Value != 0,
        BsonInt64 l => l.Value != 0,
        BsonDouble d => d.Value != 0 && !double.IsNaN(d.Value),
        _ => true,
    };
}
