using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Access;

/// <summary>
/// A playertesters document, read and saved the way the TS server's mongoose model does, so the TS services that
/// read the same documents see what they always saw:
/// <list type="bullet">
/// <item>a new document has every schema default, in the order mongoose writes them;</item>
/// <item>a loaded document missing a schema field gets its default, which the next save writes (mongoose does both);</item>
/// <item>a save writes only the fields whose value changed ($set), and nothing at all when none did;</item>
/// <item>an array push is saved as $push with $each, and increments the version key.</item>
/// </list>
/// </summary>
internal sealed class PlayerRecord
{
    public const string Collection = "playertesters";

    private readonly BsonDocument _doc;
    private readonly List<string> _modified = [];
    private readonly List<string> _defaulted = [];
    private readonly Dictionary<string, BsonArray> _pushed = [];
    private bool _isNew;
    private bool _arrayAssigned;

    private PlayerRecord(BsonDocument doc)
    {
        _doc = doc;
    }

    public ObjectId Id => _doc["_id"].AsObjectId;

    /// <summary>The player id everything else uses: the document id's hex.</summary>
    public string IdHex => Id.ToString();

    public BsonDocument Document => _doc;

    /// <summary>
    /// A loaded document, with the defaults of any schema field it lacks: the plain defaults in schema order, then the
    /// ones the schema computes (a function), as mongoose applies them; a save writes them in that order (recorded).
    /// </summary>
    public static PlayerRecord Load(BsonDocument doc, DateTime now)
    {
        var record = new PlayerRecord(doc);
        var defaults = Defaults(now).ToList();
        foreach (var (field, value) in defaults.Where(d => !s_computedDefaults.Contains(d.Field)).Concat(defaults.Where(d => s_computedDefaults.Contains(d.Field))))
        {
            if (!doc.Contains(field))
            {
                doc[field] = value;
                record._defaulted.Add(field);
            }
        }

        return record;
    }

    /// <summary>A new document: the given fields over the schema defaults, as mongoose builds it.</summary>
    public static PlayerRecord New(IReadOnlyDictionary<string, BsonValue> fields, DateTime now)
    {
        var defaults = Defaults(now).ToDictionary(d => d.Field, d => d.Value);
        var doc = new BsonDocument();
        // The given fields and the plain defaults in schema order, then _id, then the defaults that are functions
        // or objects (token, account, profile_id, public_id): that is the order mongoose sends.
        foreach (string field in s_insertOrder)
        {
            if (field == "_id")
            {
                doc["_id"] = ObjectId.GenerateNewId();
                continue;
            }

            doc[field] = fields.TryGetValue(field, out var value) ? value : defaults[field];
        }

        doc["__v"] = 0;
        return new PlayerRecord(doc) { _isNew = true };
    }

    private static readonly string[] s_insertOrder =
    [
        "name", "hydraUsername", "ip", "GameplayPreferences", "profile_icon", "blockedPlayers", "character", "variant",
        "party_key", "steamId", "epicId", "hardwareId", "hardwareIdVersion", "hardwareIdQuality", "installId",
        "lastSeenAt", "ipSeenAt", "provisional", "_id", "token", "account", "profile_id", "public_id",
    ];

    // The defaults the schema gives as functions (() => ...); the rest are plain values.
    private static readonly HashSet<string> s_computedDefaults = ["token", "account", "profile_id", "public_id", "lastSeenAt", "ipSeenAt"];

    /// <summary>The PlayerTester schema's defaults (database/PlayerTester.ts), in schema order.</summary>
    private static IEnumerable<(string Field, BsonValue Value)> Defaults(DateTime now) =>
    [
        ("name", ""),
        ("hydraUsername", ""),
        ("ip", ""),
        ("token", DefaultToken()),
        ("account", DefaultToken()),
        ("GameplayPreferences", 964),
        ("profile_id", ObjectId.GenerateNewId()),
        ("public_id", Guid.NewGuid().ToString()),
        ("profile_icon", "profile_icon_default"),
        ("blockedPlayers", new BsonArray()),
        ("character", "character_shaggy"),
        ("variant", "skin_shaggy_default"),
        ("party_key", ""),
        ("steamId", ""),
        ("epicId", ""),
        ("hardwareId", ""),
        ("hardwareIdVersion", ""),
        ("hardwareIdQuality", ""),
        ("installId", ""),
        ("lastSeenAt", now),
        ("ipSeenAt", now),
        ("provisional", false),
    ];

    // new AccountToken() (types/AccountToken.ts): its constructor's fields, in its order.
    private static BsonDocument DefaultToken() => new()
    {
        { "public_id", "" }, { "wb_network_id", "" }, { "id", "" }, { "profile_id", "" }, { "username", "" },
        { "hydraUsername", "" }, { "current_ip", "" }, { "lobby_id", "" }, { "GameplayPreferences", 964 },
        { "steamId", "" }, { "epicId", "" }, { "hardwareId", "" }, { "hardwareIdVersion", "" },
        { "hardwareIdQuality", "" }, { "installId", "" }, { "clientVersion", "" }, { "identityRegistered", "" },
    };

    /// <summary>A string field as the TS code reads it: its value when a string, else null.</summary>
    public string? Str(string field) => _doc.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;

    public bool Bool(string field) => _doc.TryGetValue(field, out var v) && v.IsBoolean && v.AsBoolean;

    public BsonValue? Get(string field) => _doc.TryGetValue(field, out var v) ? v : null;

    /// <summary>Assigns a field; it is saved only if the value changed (mongoose's change tracking).</summary>
    public void Set(string field, BsonValue value)
    {
        if (_doc.TryGetValue(field, out var current) && current.Equals(value) && !value.IsValidDateTime)
        {
            return;
        }

        _doc[field] = value;
        _defaulted.Remove(field);
        if (!_modified.Contains(field))
        {
            _modified.Add(field);
        }
    }

    /// <summary>
    /// Assigns a whole array field, as <c>doc.field = [...]</c> does in mongoose: saved as <c>$set</c> with
    /// <c>$inc: {__v: 1}</c>, filtered on _id and the loaded __v (recorded 2026-10-06, friends_diff: removeFriend's
    /// blockedPlayers). Saved only if the value changed.
    /// </summary>
    public void SetArray(string field, BsonArray value)
    {
        if (_doc.TryGetValue(field, out var current) && current.Equals(value))
        {
            return;
        }

        Set(field, value);
        _arrayAssigned = true;
    }

    /// <summary>
    /// Appends to an array field as mongoose's push does: saved as <c>$push: {field: {$each: [...]}}</c> with
    /// <c>$inc: {__v: 1}</c>, filtered on _id alone. A field that was missing (defaulted to []) is pushed, not $set.
    /// </summary>
    public void Push(string field, BsonValue value)
    {
        if (_doc.GetValue(field, BsonNull.Value) is not BsonArray array)
        {
            throw new InvalidOperationException($"{field} is not an array");
        }

        array.Add(value);
        _defaulted.Remove(field);
        if (!_pushed.TryGetValue(field, out var each))
        {
            _pushed[field] = each = [];
        }

        each.Add(value);
    }

    /// <summary>
    /// Inserts a new document; for a loaded one, writes the changed fields, then any defaults it was given on load,
    /// and nothing when there are none.
    /// </summary>
    public async Task SaveAsync(IMongoCollection<BsonDocument> players, CancellationToken ct)
    {
        if (_isNew)
        {
            await players.InsertOneAsync(_doc, cancellationToken: ct);
            _isNew = false;
            _modified.Clear();
            return;
        }

        if (_modified.Count == 0 && _defaulted.Count == 0 && _pushed.Count == 0)
        {
            return;
        }

        // Recorded from mongoose: a push with defaults sends $push, $set, $inc in that order. A push together with a
        // changed field was never recorded (mongoose orders its operators by the fields' schema positions); refuse it
        // rather than guess.
        if (_pushed.Count > 0 && _modified.Count > 0)
        {
            throw new InvalidOperationException("a push and a changed field in one save: not recorded from mongoose");
        }

        var update = new BsonDocument();
        if (_pushed.Count > 0)
        {
            update["$push"] = new BsonDocument(_pushed.Select(p => new BsonElement(p.Key, new BsonDocument("$each", p.Value))));
        }

        var set = new BsonDocument();
        foreach (string field in _modified.Concat(_defaulted))
        {
            set[field] = _doc[field];
        }

        if (set.ElementCount > 0)
        {
            update["$set"] = set;
        }

        if (_pushed.Count > 0 || _arrayAssigned)
        {
            update["$inc"] = new BsonDocument("__v", 1);
        }

        // An assigned array: the version it was loaded with must still be the stored one.
        var filter = new BsonDocument("_id", Id);
        if (_arrayAssigned && _doc.TryGetValue("__v", out var version))
        {
            filter["__v"] = version;
        }

        _modified.Clear();
        _defaulted.Clear();
        _pushed.Clear();
        _arrayAssigned = false;
        await players.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
