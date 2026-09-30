using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenVersus.Server.Core.Tests.Static;

/// <summary>
/// The field manifests (docs/fields/*.json) say which parts of a generated response are static, the player's own, or
/// computed. They are checked against the generated files, so regenerating a file from the TS server cannot leave its
/// manifest describing fields that no longer exist, or leave new fields unclassified.
/// </summary>
public sealed partial class FieldManifestTests
{
    private static readonly HashSet<string> s_kinds = ["static", "account", "computed"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "fields")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static JsonNode Load(params string[] path) => JsonNode.Parse(File.ReadAllText(Path.Combine([Root(), .. path])))!;

    /// <summary>Every leaf path: a.b[0].c; an empty object or array, or an array of plain values, is one leaf.</summary>
    private static IEnumerable<string> Leaves(JsonNode? node, string path = "")
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            return obj.SelectMany(p => Leaves(p.Value, path.Length == 0 ? p.Key : $"{path}.{p.Key}"));
        }

        if (node is JsonArray array && array.Count > 0 && array.All(x => x is JsonObject))
        {
            return array.SelectMany((x, i) => Leaves(x, $"{path}[{i}]"));
        }

        return [path];
    }

    /// <summary>A manifest path covers a leaf when it is the leaf or one of its parents; [] stands for any index.</summary>
    private static bool Covers(string manifestPath, string leaf) =>
        Regex.IsMatch(leaf, "^" + Regex.Escape(manifestPath).Replace(@"\[]", @"\[\d+]") + @"($|[.\[])");

    [Theory]
    [InlineData("login-response.json", 500)]
    [InlineData("load-rifts.json", 10000)]
    public void TheResponseIsClassifiedLeafByLeaf(string manifestFile, int minimumLeaves)
    {
        var manifest = Load("docs", "fields", manifestFile);
        var leaves = Leaves(Load(((string)manifest["file"]!).Split('/'))).ToList();
        var fields = manifest["fields"]!.AsArray().Select(f => f!.AsObject()).ToList();
        Assert.True(leaves.Count > minimumLeaves, $"the template has only {leaves.Count} leaves");

        var problems = new List<string>();
        foreach (string leaf in leaves)
        {
            int count = fields.Count(f => Covers((string)f["path"]!, leaf));
            if (count != 1)
            {
                problems.Add($"{leaf}: classified {count} times");
            }
        }

        foreach (var field in fields)
        {
            string path = (string)field["path"]!;
            if (!leaves.Any(l => Covers(path, l)))
            {
                problems.Add($"{path}: in the manifest, not in the template");
            }

            if (!s_kinds.Contains((string)field["kind"]!))
            {
                problems.Add($"{path}: unknown kind {field["kind"]}");
            }

            if (field["status"] is { } status && manifest["statuses"]![(string)status!] is null)
            {
                problems.Add($"{path}: unknown status {status}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    // A policy value is what every player is given on purpose (a grant, a flag that skips the tutorial): the template
    // must send it, so regenerating the template or editing it cannot take it away. sent_today records a known gap.
    public void TheLoginResponseSendsEveryPolicyValue()
    {
        var manifest = Load("docs", "fields", "login-response.json");
        var template = Load(((string)manifest["file"]!).Split('/'));
        var policies = manifest["fields"]!.AsArray().Select(f => f!.AsObject()).Where(f => (string?)f["status"] == "policy").ToList();
        Assert.True(policies.Count > 10, $"only {policies.Count} policy values");

        var problems = new List<string>();
        foreach (var policy in policies)
        {
            string path = (string)policy["path"]!;
            if (!policy.ContainsKey("value"))
            {
                problems.Add($"{path}: a policy without a value");
                continue;
            }

            var expected = policy.ContainsKey("sent_today") ? policy["sent_today"] : policy["value"];
            var actual = path.Split('.').Aggregate<string, JsonNode?>(template, (node, key) => node?[key]);
            if (!JsonNode.DeepEquals(expected, actual))
            {
                problems.Add($"{path}: the template sends {actual?.ToJsonString() ?? "nothing"}, the manifest expects {expected?.ToJsonString()}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryStoreProductFieldIsClassified()
    {
        var manifest = Load("docs", "fields", "store-layouts.json");
        var products = manifest["products"]!;
        var fields = products["fields"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        var topLevel = fields.Where(f => !f.Contains('.')).ToHashSet();
        var layouts = Directory.GetFiles(Path.Combine(Root(), "src", "OpenVersus.Server.Core", "Static"), "layout-*.json");
        Assert.NotEmpty(layouts);

        var problems = new List<string>();
        var seen = new HashSet<string>();
        int count = 0;
        foreach (string file in layouts)
        {
            var layout = JsonNode.Parse(File.ReadAllText(file));
            foreach (var product in Products(layout))
            {
                count++;
                foreach (var (key, _) in product)
                {
                    seen.Add(key);
                    if (!topLevel.Contains(key))
                    {
                        problems.Add($"{Path.GetFileName(file)}: product field {key} is not classified");
                    }
                }

                // Nested fields (skus[].price_type_options...): note which ones occur.
                foreach (string leaf in Leaves(product))
                {
                    foreach (string field in fields.Where(f => f.Contains('.') && Covers(f, leaf)))
                    {
                        seen.Add(field);
                    }
                }
            }
        }

        Assert.True(count > 1000, $"only {count} products found: the product paths are out of date");
        problems.AddRange(fields.Where(f => !seen.Contains(f)).Select(f => $"{f}: classified, but no product has it"));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void EveryLayoutVariantHasASelectionEntry()
    {
        var manifest = Load("docs", "fields", "store-layouts.json");
        var classified = manifest["selection"]!["variants"]!.AsArray().Select(v => (string)v!["variant"]!).Order().ToList();
        var generated = Directory.GetFiles(Path.Combine(Root(), "src", "OpenVersus.Server.Core", "Static"), "layout-*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)["layout-".Length..]).Order().ToList();
        Assert.Equal(generated, classified);
    }

    // The store products of a layout: areas[].items[].embedded_object, and a bundle's type_options.store_products[].
    private static IEnumerable<JsonObject> Products(JsonNode? layout)
    {
        foreach (var area in layout?["areas"]?.AsArray() ?? [])
        {
            foreach (var item in area?["items"]?.AsArray() ?? [])
            {
                if (item?["embedded_object"] is JsonObject product && product.ContainsKey("already_owned"))
                {
                    yield return product;
                    foreach (var inner in product["type_options"]?["store_products"]?.AsArray() ?? [])
                    {
                        if (inner is JsonObject innerProduct && innerProduct.ContainsKey("already_owned"))
                        {
                            yield return innerProduct;
                        }
                    }
                }
            }
        }
    }
}
