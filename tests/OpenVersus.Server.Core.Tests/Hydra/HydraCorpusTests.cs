using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;

namespace OpenVersus.Server.Core.Tests.Hydra;

/// <summary>
/// The codec against real traffic: every Hydra body the game sent (req/) and every one the TS server answered with and
/// the game accepted (resp/), extracted from captures. The bodies hold tokens and account ids, so they are never in
/// the repository: point OVS_TEST_HYDRA_CORPUS at a directory with req/ and resp/ of .bin files to run this.
/// Every body must decode, and every response must encode back to exactly the TS server's bytes (compressed ones to
/// the same value: zlib builds differ).
/// </summary>
public sealed class HydraCorpusTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string? s_corpus = Environment.GetEnvironmentVariable("OVS_TEST_HYDRA_CORPUS");

    [SkippableFact]
    public void EveryCapturedBodyDecodesAndEveryResponseEncodesBackExactly()
    {
        Skip.If(string.IsNullOrEmpty(s_corpus), "set OVS_TEST_HYDRA_CORPUS to a directory with req/ and resp/ .bin files");
        var problems = new List<string>();
        int requests = 0, responses = 0, exact = 0, compressed = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(s_corpus!, "req"), "*.bin"))
        {
            requests++;
            try
            {
                HydraDecoder.Decode(File.ReadAllBytes(file));
            }
            catch (Exception e)
            {
                problems.Add($"req {Path.GetFileName(file)}: {e.Message}");
            }
        }

        foreach (var file in Directory.GetFiles(Path.Combine(s_corpus!, "resp"), "*.bin"))
        {
            responses++;
            byte[] original = File.ReadAllBytes(file);
            try
            {
                var value = HydraDecoder.Decode(original);
                byte[] again = HydraEncoder.Encode(value);
                if (again.AsSpan().SequenceEqual(original))
                {
                    exact++;
                }
                else if (value!.ToJsonString().Contains("\"_hydra_compressed\"", StringComparison.Ordinal) && JsonNode.DeepEquals(value, HydraDecoder.Decode(again)))
                {
                    compressed++;
                }
                else
                {
                    problems.Add($"resp {Path.GetFileName(file)}: re-encoded bytes differ ({again.Length} vs {original.Length})");
                }
            }
            catch (Exception e)
            {
                problems.Add($"resp {Path.GetFileName(file)}: {e.Message}");
            }
        }

        output.WriteLine($"corpus: {requests} requests decoded; {responses} responses: {exact} byte-exact, {compressed} compressed and equal");
        Assert.True(requests + responses > 0, "the corpus is empty");
        Assert.True(problems.Count == 0, $"{problems.Count} problem(s) in {requests} requests / {responses} responses ({exact} exact, {compressed} compressed):\n" + string.Join("\n", problems.Take(20)));
    }
}
