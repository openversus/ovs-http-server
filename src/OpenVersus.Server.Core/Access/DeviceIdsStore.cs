using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Access;

/// <summary>
/// The device ids on an account (docs/IDENTIFY.md): <c>deviceIds</c>, the current hash per kind, and
/// <c>deviceIdHistory</c>, every (kind, id) the account ever presented with when it was first and last seen. Bans read
/// both; nothing here picks an account. The runtime the client reports and its VM flag sit beside them.
/// </summary>
public static class DeviceIdsStore
{
    public const string CurrentField = "deviceIds";
    public const string HistoryField = "deviceIdHistory";
    public const string RuntimeField = "runtime";
    public const string VirtualMachineField = "virtualMachine";

    /// <summary>Every device id the account holds, current and past, each once, in order of first appearance.</summary>
    public static IReadOnlyList<string> IdsOf(BsonDocument account)
    {
        var ids = new List<string>();
        void Add(BsonValue value)
        {
            if (value is { IsString: true } s && s.AsString.Length > 0 && !ids.Contains(s.AsString))
            {
                ids.Add(s.AsString);
            }
        }

        if (account.GetValue(HistoryField, BsonNull.Value) is BsonArray history)
        {
            foreach (var entry in history.OfType<BsonDocument>())
            {
                Add(entry.GetValue("id", BsonNull.Value));
            }
        }

        if (account.GetValue(CurrentField, BsonNull.Value) is BsonDocument current)
        {
            foreach (var element in current)
            {
                Add(element.Value);
            }
        }

        return ids;
    }

    /// <summary>The account's current device ids, kind to hash, in kind order.</summary>
    public static SortedDictionary<string, string> CurrentOf(BsonDocument account)
    {
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (account.GetValue(CurrentField, BsonNull.Value) is BsonDocument current)
        {
            foreach (var element in current.Where(e => e.Value.IsString && e.Value.AsString.Length > 0))
            {
                ids[element.Name] = element.Value.AsString;
            }
        }

        return ids;
    }

    /// <summary>
    /// Records a login's device ids, runtime and VM flag on the account: the current map takes each kind sent, the
    /// history gets a new entry per (kind, id) not seen before and a fresh last-seen time on those seen again. Nothing
    /// is removed. One or two updates, after the login's own save, so the record's change tracking is not involved.
    /// </summary>
    public static async Task RecordAsync(IMongoCollection<BsonDocument> players, ObjectId id, IReadOnlyDictionary<string, string> deviceIds, string runtime, bool virtualMachine, DateTime now, CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var set = new BsonDocument();
        if (runtime.Length > 0)
        {
            set[RuntimeField] = runtime;
            set[VirtualMachineField] = virtualMachine;
        }

        foreach (var (kind, hash) in deviceIds)
        {
            set[$"{CurrentField}.{kind}"] = hash;
        }

        if (set.ElementCount > 0)
        {
            await players.UpdateOneAsync(filter, new BsonDocument("$set", set), cancellationToken: ct);
        }

        if (deviceIds.Count == 0)
        {
            return;
        }

        var account = await players.Find(filter).Project(new BsonDocument(HistoryField, 1)).FirstOrDefaultAsync(ct);
        var known = new HashSet<(string Kind, string Id)>();
        if (account?.GetValue(HistoryField, BsonNull.Value) is BsonArray history)
        {
            foreach (var entry in history.OfType<BsonDocument>())
            {
                known.Add((entry.GetValue("kind", "").ToString()!, entry.GetValue("id", "").ToString()!));
            }
        }

        var seenAgain = deviceIds.Where(d => known.Contains((d.Key, d.Value))).ToList();
        var fresh = deviceIds.Where(d => !known.Contains((d.Key, d.Value))).ToList();
        if (seenAgain.Count > 0)
        {
            // $[e] with one filter per entry: the same (kind, id) matched by kind and id together.
            var filters = seenAgain.Select((d, i) => (ArrayFilterDefinition)new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument { { $"e{i}.kind", d.Key }, { $"e{i}.id", d.Value } })).ToList();
            var touch = new BsonDocument(seenAgain.Select((d, i) => new BsonElement($"{HistoryField}.$[e{i}].lastSeenAt", now)));
            await players.UpdateOneAsync(filter, new BsonDocument("$set", touch), new UpdateOptions { ArrayFilters = filters }, ct);
        }

        if (fresh.Count > 0)
        {
            var entries = new BsonArray(fresh.Select(d => new BsonDocument { { "kind", d.Key }, { "id", d.Value }, { "firstSeenAt", now }, { "lastSeenAt", now } }));
            await players.UpdateOneAsync(filter, new BsonDocument("$push", new BsonDocument(HistoryField, new BsonDocument("$each", entries))), cancellationToken: ct);
        }
    }

    /// <summary>How many accounts hold <paramref name="deviceId"/>, now or in their history.</summary>
    public static Task<long> HoldersAsync(IMongoCollection<BsonDocument> players, string deviceId, CancellationToken ct) =>
        players.CountDocumentsAsync(Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq($"{HistoryField}.id", deviceId),
            Builders<BsonDocument>.Filter.Or(IdentityRules.DeviceKinds.Select(kind => Builders<BsonDocument>.Filter.Eq($"{CurrentField}.{kind}", deviceId)))), cancellationToken: ct);
}
