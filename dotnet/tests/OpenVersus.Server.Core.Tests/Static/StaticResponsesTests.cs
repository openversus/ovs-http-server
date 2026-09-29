using OpenVersus.Server.Core.Static;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Core.Tests.Static;

/// <summary>
/// The static answers against what the game was sent. With OVS_TEST_HYDRA_CORPUS set, every captured response of a
/// route answered from Static/ must be, byte for byte, the Hydra encoding of one of that route's files.
/// </summary>
public sealed class StaticResponsesTests
{
    // Captured file name suffix (tools/hydra/extract_corpus.py) -> the Static/ files that route answers with.
    public static TheoryData<string, string[]> Routes() => new()
    {
        { "commerce_products.bin", ["commerce-products", "commerce-products-partial"] },
        { "commerce_purchases_me.bin", ["commerce-purchases-me"] },
        { "commerce_steam_mtx_user_info_me.bin", ["commerce-steam-mtx-user-info-me"] },
        // Layouts (file names cut at 60 characters by the extractor). fighter-road-layout was never captured.
        { "layout_dokken_layout_type_personalized_account_cosmetics_va.bin", ["layout-account-cosmetics-variant"] },
        { "layout_dokken_layout_type_personalized_battlepass_variant_I.bin", ["layout-battlepass-variant"] },
        { "layout_dokken_layout_type_personalized_currency_variant_ID.bin", ["layout-currency-variant"] },
        { "layout_dokken_layout_type_personalized_fighter_variant_ID.bin", ["layout-fighter-variant"] },
        { "layout_dokken_layout_type_personalized_main_variant_ID.bin", ["layout-main-variant"] },
        { "layout_dokken_layout_type_personalized_prestige_variant_ID.bin", ["layout-prestige-variant"] },
        { "layout_dokken_layout_type_personalized_rift_variant_ID.bin", ["layout-rift-variant"] },
        { "layout_dokken_layout_type_personalized_skin_variant_ID.bin", ["layout-skin-variant"] },
    };

    [SkippableTheory]
    [MemberData(nameof(Routes))]
    public void EveryCapturedResponseIsOneOfTheFiles(string suffix, string[] names)
    {
        string? corpus = Environment.GetEnvironmentVariable("OVS_TEST_HYDRA_CORPUS");
        Skip.If(string.IsNullOrEmpty(corpus), "set OVS_TEST_HYDRA_CORPUS to run");
        var files = Directory.GetFiles(Path.Combine(corpus!, "resp"), $"*__{suffix}");
        Assert.NotEmpty(files);
        var encoded = names.ToDictionary(n => n, n => HydraCodec.EncodeJson(StaticResponses.Json(n)));
        var matched = new HashSet<string>();
        var unmatched = new List<string>();
        foreach (string file in files)
        {
            byte[] captured = File.ReadAllBytes(file);
            string? name = encoded.FirstOrDefault(e => e.Value.AsSpan().SequenceEqual(captured)).Key;
            if (name is null)
            {
                unmatched.Add(Path.GetFileName(file));
            }
            else
            {
                matched.Add(name);
            }
        }

        Assert.True(unmatched.Count == 0, "no file matches: " + string.Join(", ", unmatched));
        // Every file is seen in the captures, so none is untested.
        Assert.Equal(names.Order(), matched.Order());
    }

    [Fact]
    public void WritesCompactJson()
    {
        Assert.Equal("""{"currency":"USD","state":"IL","country":"US","status":"Trusted"}""", StaticResponses.Json("commerce-steam-mtx-user-info-me"));
        Assert.Same(StaticResponses.Json("commerce-products"), StaticResponses.Json("commerce-products"));
    }
}
