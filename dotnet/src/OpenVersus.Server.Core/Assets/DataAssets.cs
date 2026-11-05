using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Assets;

/// <summary>
/// The game's data assets (characters, skins, taunts, icons, ...) as the TS server's loadAssets reads them: the enabled
/// ones in the order Mongo returns them, test characters and their assets left out (withoutTestCharacters with
/// ENABLE_TEST_CHARACTERS off, its default). The TS server reads them at startup (and again on POST /syncAsset); these
/// are read when asked, so a change reaches every replica at once.
/// </summary>
public static class DataAssets
{
    // data/testCharacters.ts.
    private static readonly HashSet<string> s_testCharacters = new(
        ["character_supershaggy", "character_Meeseeks", "character_C022", "character_C033", "character_cmanny", "character_manny", "character_C037", "character_C099"],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsTestCharacter(string? slug) => !string.IsNullOrEmpty(slug) && s_testCharacters.Contains(slug);

    public static async Task<List<BsonDocument>> EnabledAsync(IMongoDatabase mongo, CancellationToken ct) =>
        (await mongo.GetCollection<BsonDocument>("dataassets").Find(new BsonDocument("enabled", true)).ToListAsync(ct))
            .Where(a => !(Str(a, "assetType") == "CharacterData" && IsTestCharacter(Str(a, "slug"))) && !IsTestCharacter(Str(a, "character_slug")))
            .ToList();

    public static string? Str(BsonDocument doc, string field) => doc.GetValue(field, BsonNull.Value) is { IsString: true } v ? v.AsString : null;
}
