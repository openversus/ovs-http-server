using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Matches;

// GET /ovs_node_config, and the signature P2P nodes check on what they take from the server (the rollback repo's
// NodeLockdown; the TS server's src/nodeConfig.ts is the same contract). A node takes the settings every player in a
// match shares from this update, and its match configs from /ovs_register, only with X-OVS-Signature: ECDSA P-256 over
// the exact body bytes, SHA-256, IEEE P1363 r||s, base64. The node is built with the key's public half.
//
// Settings        Rollback:NodeSigningKey (P2P_NODE_SIGNING_KEY, PKCS#8 PEM text) or Rollback:NodeSigningKeyFile
//                 (P2P_NODE_SIGNING_KEY_FILE); Rollback:NodeConfigFile (P2P_NODE_CONFIG_FILE), else the built-in copy
//                 of the TS server's src/data/node-config.json; Rollback:NodePublicKey (P2P_NODE_PUBLIC_KEY, "prod" by
//                 default), the public key the signing key must belong to: checked at startup (NodeKeyCheck) and
//                 logged, since a server holding another key is refused by every node, which nodes can only show as
//                 matches going to the relay.
// Answers         200 {"version": N, "config": {sections}} with X-OVS-Signature, the file read and signed at most every
//                 five minutes (decided 2026-10-04: a settings change may take that long to reach new nodes); 503 when
//                 there is no key or no readable update (logged), and the node uses the values built into it.
// C# /ovs_register is still a stub; when it is ported, it signs its answer with Sign as the TS route does.

public interface INodeConfig
{
    /// <summary>The response header carrying the signature, on /ovs_node_config and (once ported) /ovs_register.</summary>
    const string SignatureHeader = "X-OVS-Signature";

    /// <summary>The signed update: the body as it is to be sent and its signature; null when there is none to give.</summary>
    (byte[] Body, string Signature)? Answer();

    /// <summary>The X-OVS-Signature value for <paramref name="body"/>, or null when no key is configured.</summary>
    string? Sign(ReadOnlySpan<byte> body);
}

internal sealed class NodeConfig(IOptionsMonitor<RollbackSettings> settings, TimeProvider time, ILogger<NodeConfig> log) : INodeConfig
{
    public const string BuiltInResource = "OpenVersus.Server.Core.Matches.node-config.json";
    public const string BuiltInPublicKeyPrefix = "OpenVersus.Server.Core.Matches.pki/";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private (DateTimeOffset At, (byte[] Body, string Signature)? Answer)? _cached;
    private (string Source, ECDsa? Key)? _key;

    public (byte[] Body, string Signature)? Answer()
    {
        lock (_lock)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (_cached is { } cached && now - cached.At < CacheFor)
            {
                return cached.Answer;
            }

            var answer = Read();
            _cached = (now, answer);
            return answer;
        }
    }

    public string? Sign(ReadOnlySpan<byte> body)
    {
        lock (_lock)
        {
            return Key() is { } key ? Convert.ToBase64String(key.SignData(body, HashAlgorithmName.SHA256)) : null;
        }
    }

    /// <summary>
    /// Logs whether the signing key is the private half of Rollback:NodePublicKey, and says which. Reported, not
    /// enforced: signing goes on either way, so a stale built-in public key cannot switch off a server whose key is right.
    /// </summary>
    public KeyCheck CheckKey()
    {
        lock (_lock)
        {
            if (Key() is not { } key)
            {
                return KeyCheck.NoKey;
            }

            string name = settings.CurrentValue.NodePublicKey;
            bool isPath = name.Contains('/') || name.Contains('\\');
            string which = isPath ? $"public key {name}" : $"{name} public key, built in from pki/{name}/node-config-public-key.txt";
            byte[] expected;
            try
            {
                string text = isPath ? File.ReadAllText(name) : BuiltInPublicKey(name)
                    ?? throw new FileNotFoundException($"there is no built-in public key \"{name}\" (pki/<name>/node-config-public-key.txt)");
                using var publicKey = ECDsa.Create();
                publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text.Trim()), out _);
                expected = publicKey.ExportSubjectPublicKeyInfo();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
            {
                log.LogError("Cannot check the P2P node signing key against P2P_NODE_PUBLIC_KEY={Name}: {Error}", name, e.Message);
                return KeyCheck.Unreadable;
            }

            byte[] actual = key.ExportSubjectPublicKeyInfo();
            if (actual.AsSpan().SequenceEqual(expected))
            {
                log.LogInformation("The P2P node signing key belongs to the {Which} ({Fingerprint})", which, Fingerprint(actual));
                return KeyCheck.Matches;
            }

            log.LogError("The P2P node signing key does NOT belong to the {Which}: nodes built for it ({Expected}) refuse everything signed with this one ({Actual}) and send every P2P match to the relay",
                which, Fingerprint(expected), Fingerprint(actual));
            return KeyCheck.Differs;
        }
    }

    /// <summary>A public key as the logs name it: the first 16 hex digits of SHA-256 over its SubjectPublicKeyInfo.</summary>
    private static string Fingerprint(byte[] spki) => Convert.ToHexStringLower(SHA256.HashData(spki))[..16];

    private static string? BuiltInPublicKey(string name)
    {
        var assembly = typeof(NodeConfig).Assembly;
        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/') == $"{BuiltInPublicKeyPrefix}{name}/node-config-public-key.txt");
        if (resource is null)
        {
            return null;
        }

        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    private (byte[] Body, string Signature)? Read()
    {
        string file = settings.CurrentValue.NodeConfigFile;
        string source = file.Length > 0 ? file : "the built-in node-config.json";
        JsonNode? json;
        try
        {
            if (file.Length > 0)
            {
                json = JsonNode.Parse(File.ReadAllText(file));
            }
            else
            {
                using var stream = typeof(NodeConfig).Assembly.GetManifestResourceStream(BuiltInResource)
                    ?? throw new InvalidOperationException($"{BuiltInResource} is not in this build");
                json = JsonNode.Parse(stream);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            log.LogError("The node settings update {Source} could not be read: {Error}", source, e.Message);
            return null;
        }

        if (json?["version"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version) || json["config"] is not JsonObject config)
        {
            log.LogError("The node settings update {Source} must be {{\"version\": <integer>, \"config\": {{...}}}}", source);
            return null;
        }

        // Signed as sent: the node checks the bytes it receives.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = version, ["config"] = config.DeepClone() });
        string? signature = Key() is { } key ? Convert.ToBase64String(key.SignData(body, HashAlgorithmName.SHA256)) : null;
        if (signature is null)
        {
            return null;
        }

        log.LogInformation("Node settings update version {Version} from {Source}, signed", version, source);
        return (body, signature);
    }

    /// <summary>The configured signing key, read again only when its setting changes; null (logged) when none or unusable.</summary>
    private ECDsa? Key()
    {
        var current = settings.CurrentValue;
        string source = current.NodeSigningKey.Length > 0 ? "P2P_NODE_SIGNING_KEY" : current.NodeSigningKeyFile;
        string identity = current.NodeSigningKey.Length > 0 ? current.NodeSigningKey : current.NodeSigningKeyFile;
        if (_key is { } known && known.Source == identity)
        {
            return known.Key;
        }

        ECDsa? key = null;
        if (identity.Length == 0)
        {
            log.LogWarning("No P2P_NODE_SIGNING_KEY or P2P_NODE_SIGNING_KEY_FILE: P2P nodes get no settings update and send every P2P match to the relay");
        }
        else
        {
            try
            {
                string pem = current.NodeSigningKey.Length > 0 ? current.NodeSigningKey : File.ReadAllText(current.NodeSigningKeyFile);
                key = ECDsa.Create();
                key.ImportFromPem(pem);
                if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                {
                    log.LogError("The P2P node signing key ({Source}) is not on P-256; signing nothing", source);
                    key.Dispose();
                    key = null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
            {
                log.LogError("The P2P node signing key ({Source}) could not be read: {Error}; signing nothing", source, e.Message);
                key?.Dispose();
                key = null;
            }
        }

        _key?.Key?.Dispose();
        _key = (identity, key);
        return key;
    }
}

/// <summary>What <see cref="NodeConfig.CheckKey"/> found.</summary>
public enum KeyCheck
{
    /// <summary>No signing key is configured (already logged): nothing is signed.</summary>
    NoKey,
    Matches,
    Differs,
    /// <summary>The expected public key could not be read, so nothing was compared.</summary>
    Unreadable,
}

/// <summary>At startup, logs whether the signing key matches the public key the nodes are built with.</summary>
internal sealed class NodeKeyCheck(NodeConfig config) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        config.CheckKey();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class NodeConfigHosting
{
    public static WebApplicationBuilder AddNodeConfig(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RollbackSettings>("Rollback");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<NodeConfig>();
        builder.Services.AddSingleton<INodeConfig>(services => services.GetRequiredService<NodeConfig>());
        builder.Services.AddHostedService<NodeKeyCheck>();
        return builder;
    }
}
