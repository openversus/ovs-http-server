using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Access;

/// <summary>
/// Every name change of an account, kept on it (<c>nameHistory</c>): the name before and after, when, from which IP,
/// and what changed it (web, manual, login). The last <see cref="Kept"/> entries stay; nothing else reads it.
/// </summary>
public static class NameHistory
{
    public const string Field = "nameHistory";
    public const int Kept = 200;

    /// <summary>Appends one change; a change to the same name is not one.</summary>
    public static Task PushAsync(IMongoCollection<BsonDocument> players, ObjectId id, string from, string to, string ip, string source, DateTime now, CancellationToken ct = default)
    {
        if (from == to)
        {
            return Task.CompletedTask;
        }

        var entry = new BsonDocument { { "from", from }, { "to", to }, { "at", now }, { "ip", ip }, { "source", source } };
        var push = new BsonDocument("$push", new BsonDocument(Field, new BsonDocument { { "$each", new BsonArray { entry } }, { "$slice", -Kept } }));
        return players.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id), push, cancellationToken: ct);
    }

    /// <summary>The account's changes, oldest first.</summary>
    public static IReadOnlyList<NameChange> Of(BsonDocument account)
    {
        if (account.GetValue(Field, BsonNull.Value) is not BsonArray history)
        {
            return [];
        }

        return history.OfType<BsonDocument>()
            .Select(e => new NameChange(e.GetValue("from", "").ToString()!, e.GetValue("to", "").ToString()!,
                e.GetValue("at", BsonNull.Value) is { IsValidDateTime: true } at ? at.ToUniversalTime() : DateTime.MinValue,
                e.GetValue("ip", "").ToString()!, e.GetValue("source", "").ToString()!))
            .ToList();
    }
}

/// <summary>One name change: from what to what, when, from where, and by which route.</summary>
public sealed record NameChange(string From, string To, DateTime At, string Ip, string Source);
