using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>GET /ovs_node_config's signed update and the signature P2P nodes check (<see cref="INodeConfig"/>).</summary>
public sealed class NodeConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-nodeconfig-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private NodeConfig Service(RollbackSettings settings) => new(new TestOptions<RollbackSettings>(settings), _clock, NullLogger<NodeConfig>.Instance);

    private RollbackSettings Signed(string configFile = "") => new() { NodeSigningKey = _key.ExportPkcs8PrivateKeyPem(), NodeConfigFile = configFile };

    private bool Verifies(byte[] body, string signature) => _key.VerifyData(body, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);

    [Fact]
    public void TheBuiltInUpdateIsTheTsServersFile()
    {
        // Two copies (the C# image's build context is dotnet/ alone); this keeps them one.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "data", "node-config.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        using var stream = typeof(INodeConfig).Assembly.GetManifestResourceStream(NodeConfig.BuiltInResource)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "src", "data", "node-config.json"))), JsonNode.Parse(stream)),
            "dotnet/src/OpenVersus.Server.Core/Matches/node-config.json differs from src/data/node-config.json");
    }

    [Fact]
    public void TheBuiltInPublicKeysAreTheTsServersFiles()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "data", "pki")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var names = Directory.GetDirectories(Path.Combine(dir, "src", "data", "pki")).Select(Path.GetFileName).ToList();
        Assert.Contains("prod", names);
        foreach (string? name in names)
        {
            string ts = File.ReadAllText(Path.Combine(dir, "src", "data", "pki", name!, "node-config-public-key.txt")).Trim();
            string? resource = typeof(INodeConfig).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.Replace('\\', '/') == $"{NodeConfig.BuiltInPublicKeyPrefix}{name}/node-config-public-key.txt");
            Assert.True(resource is not null, $"no built-in copy of src/data/pki/{name}");
            using var reader = new StreamReader(typeof(INodeConfig).Assembly.GetManifestResourceStream(resource)!);
            Assert.Equal(ts, reader.ReadToEnd().Trim());
        }
    }

    [Fact]
    public void TheKeyCheckSaysWhetherTheSigningKeyBelongsToTheExpectedPublicKey()
    {
        string own = Path.Combine(_dir, "node-config-public-key.txt");
        File.WriteAllText(own, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) + "\n");

        RollbackSettings Expecting(string publicKey)
        {
            var settings = Signed();
            settings.NodePublicKey = publicKey;
            return settings;
        }

        Assert.Equal(KeyCheck.Matches, Service(Expecting(own)).CheckKey());
        // The built-in prod key (the default) is readable, and it is not this test's key.
        Assert.Equal(KeyCheck.Differs, Service(Signed()).CheckKey());
        Assert.Equal(KeyCheck.Unreadable, Service(Expecting("no-such-environment")).CheckKey());
        Assert.Equal(KeyCheck.Unreadable, Service(Expecting(Path.Combine(_dir, "missing.txt"))).CheckKey());
        Assert.Equal(KeyCheck.NoKey, Service(new RollbackSettings()).CheckKey());
    }

    [Fact]
    public void TheUpdateIsSignedAsSent()
    {
        var answer = Service(Signed()).Answer();

        Assert.NotNull(answer);
        Assert.True(Verifies(answer.Value.Body, answer.Value.Signature));
        var json = JsonNode.Parse(answer.Value.Body)!;
        Assert.Equal(1, (int)json["version"]!);
        Assert.Equal(65536, (int)json["config"]!["Networking"]!["ReceiveBufferSize"]!);
        // One byte changed and the node refuses it.
        byte[] tampered = (byte[])answer.Value.Body.Clone();
        tampered[^2] ^= 1;
        Assert.False(Verifies(tampered, answer.Value.Signature));
    }

    [Fact]
    public void WithoutAKeyThereIsNothingToGiveOrSign()
    {
        var service = Service(new RollbackSettings());

        Assert.Null(service.Answer());
        Assert.Null(service.Sign("{}"u8));
    }

    [Fact]
    public void TheKeyCanComeFromAFile_AndAKeyOffP256IsRefused()
    {
        string file = Path.Combine(_dir, "key.pem");
        File.WriteAllText(file, _key.ExportPkcs8PrivateKeyPem());
        var fromFile = Service(new RollbackSettings { NodeSigningKeyFile = file }).Answer();
        Assert.NotNull(fromFile);
        Assert.True(Verifies(fromFile.Value.Body, fromFile.Value.Signature));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(Service(new RollbackSettings { NodeSigningKey = p384.ExportPkcs8PrivateKeyPem() }).Answer());
        Assert.Null(Service(new RollbackSettings { NodeSigningKey = "not a key" }).Answer());
    }

    [Fact]
    public void AnUpdateFileIsReadAtMostEveryFiveMinutes()
    {
        string file = Path.Combine(_dir, "node-config.json");
        File.WriteAllText(file, """{ "version": 2, "config": { "RiftCalculation": { "TargetRift": 1.5 } } }""");
        var service = Service(Signed(file));
        Assert.Equal(2, (int)JsonNode.Parse(service.Answer()!.Value.Body)!["version"]!);

        File.WriteAllText(file, """{ "version": 3, "config": {} }""");
        _clock.Now += NodeConfig.CacheFor - TimeSpan.FromSeconds(1);
        Assert.Equal(2, (int)JsonNode.Parse(service.Answer()!.Value.Body)!["version"]!);
        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(3, (int)JsonNode.Parse(service.Answer()!.Value.Body)!["version"]!);
    }

    [Theory]
    [InlineData("""{ "config": {} }""")]
    [InlineData("""{ "version": "2", "config": {} }""")]
    [InlineData("""{ "version": 2, "config": [] }""")]
    [InlineData("""not json""")]
    public void AnUpdateFileOfTheWrongShapeGivesNothing(string text)
    {
        string file = Path.Combine(_dir, "node-config.json");
        File.WriteAllText(file, text);

        Assert.Null(Service(Signed(file)).Answer());
    }
}
