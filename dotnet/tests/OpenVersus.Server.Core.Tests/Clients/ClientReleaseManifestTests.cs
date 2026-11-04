using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Clients;

namespace OpenVersus.Server.Core.Tests.Clients;

/// <summary>The TS server's tests/clientReleaseManifest.test.ts, case for case.</summary>
public sealed class ClientReleaseManifestTests
{
    private static JsonObject Asset(string name, long size = 100, string repo = "openversus/ovs-client") => new()
    {
        ["name"] = name,
        ["size"] = size,
        ["digest"] = "sha256:" + new string('a', 64),
        ["browser_download_url"] = $"https://github.com/{repo}/releases/download/v1/{name}",
    };

    private static JsonArray Assets(params JsonObject[] assets) => [.. assets];

    [Fact]
    public void BuildsAnIndividualFileManifestAndIgnoresZips()
    {
        var files = ClientReleaseManifest.Build(Assets(Asset("OpenVersus_v1.zip"), Asset("OpenVersus.asi"), Asset("OVS_P.pak"), Asset("OVS_P.utoc"), Asset("OVS_P.ucas")));

        Assert.Equal([("OVS_P.pak", "paks"), ("OVS_P.ucas", "paks"), ("OVS_P.utoc", "paks"), ("OpenVersus.asi", "plugin")], files.Select(f => (f.Name, f.Kind)));
        var flat = ClientReleaseManifest.Flatten(files);
        Assert.Equal(4, (int?)flat["file_count"]);
        Assert.Equal("OVS_P.pak", (string?)flat["file_0_name"]);
        Assert.Equal(new string('a', 64), (string?)flat["file_3_sha256"]);
        Assert.Equal(100, (long?)flat["file_3_size"]);
        Assert.Equal(["file_count", "file_0_name", "file_0_kind", "file_0_size", "file_0_sha256", "file_0_url"], flat.Select(p => p.Key).Take(6));
    }

    [Fact]
    public void NodeArchivesAreOfferedPerPlatformAfterThePluginAndTheInstallZipIsNot()
    {
        var files = ClientReleaseManifest.Build(Assets(Asset("OpenVersus_v2026.11.04.01.zip"), Asset("OVS_Node_2026.11.04.01_osx-arm64.tar.gz"),
            Asset("OpenVersus_2026.11.04.01.asi"), Asset("OVS_Node_2026.11.04.01_win-x64.zip"), Asset("OVS_Node_2026.11.04.01_linux-x64.tar.gz"), Asset("OVS_P.pak")), "2026.11.04.01");

        Assert.Equal([("OVS_P.pak", "paks", ""), ("OpenVersus_2026.11.04.01.asi", "plugin", ""), ("OVS_Node_2026.11.04.01_linux-x64.tar.gz", "node", "linux-x64"),
            ("OVS_Node_2026.11.04.01_osx-arm64.tar.gz", "node", "osx-arm64"), ("OVS_Node_2026.11.04.01_win-x64.zip", "node", "win-x64")], files.Select(f => (f.Name, f.Kind, f.Platform)));
        Assert.Equal("2026.11.04.01", ClientReleaseManifest.NodeVersion(files));
        Assert.Equal("win-x64", (string?)ClientReleaseManifest.ToJson(files[4])["platform"]);
        Assert.Null(ClientReleaseManifest.ToJson(files[0])["platform"]);
        var flat = ClientReleaseManifest.Flatten(files);
        Assert.Equal("linux-x64", (string?)flat["file_2_platform"]);
        Assert.Null(flat["file_0_platform"]);
        Assert.Equal("", ClientReleaseManifest.NodeVersion(ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi")))));
    }

    [Fact]
    public void ANodeArchiveForAnotherVersionOrTwoForOnePlatformIsRefused()
    {
        var other = Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus_2026.11.04.01.asi"), Asset("OVS_Node_2026.11.03.01_win-x64.zip")), "2026.11.04.01"));
        Assert.Contains("for a different version", other.Message);
        var twice = Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi"), Asset("OVS_Node_1_win-x64.zip"), Asset("OVS_Node_1_WIN-X64.zip"))));
        Assert.Contains("more than one node archive", twice.Message);
    }

    [Fact]
    public void RejectsAnIncompleteIoStoreGroup()
    {
        var e = Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi"), Asset("OVS_P.utoc"))));
        Assert.Contains("incomplete IoStore group", e.Message);
    }

    [Fact]
    public void RejectsUntrustedUrlsAndMissingDigests()
    {
        var elsewhere = Asset("OpenVersus.asi");
        elsewhere["browser_download_url"] = "https://example.com/OpenVersus.asi";
        Assert.Contains("exactly one verified", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(elsewhere))).Message);

        var undigested = Asset("OpenVersus.asi");
        undigested["digest"] = null;
        Assert.Contains("exactly one verified", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(undigested))).Message);
    }

    [Fact]
    public void CSharpReleasesOfferTheVersionedPluginAndNotTheSidecarsOrZips()
    {
        var files = ClientReleaseManifest.Build(Assets(
            Asset("OpenVersus_2026.09.25.07.asi"), Asset("OpenVersus_2026.09.25.07.asi.sha256"), Asset("OpenVersus_v2026.09.25.07.zip"),
            Asset("OpenVersus_v2026.09.25.07.zip.sha256"), Asset("SHA256SUMS")), "2026.09.25.07");

        Assert.Equal([("OpenVersus_2026.09.25.07.asi", "plugin")], files.Select(f => (f.Name, f.Kind)));
    }

    [Fact]
    public void AVersionedPluginMustMatchTheReleaseAndTheLegacyNameIsStillAccepted()
    {
        Assert.Contains("different version", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus_2026.09.25.06.asi")), "2026.09.25.07")).Message);
        Assert.Equal("plugin", ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi")), "2026.04.08.14")[0].Kind);
        Assert.Contains("exactly one verified", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi"), Asset("OpenVersus_2026.09.25.07.asi")), "2026.09.25.07")).Message);
    }

    [Fact]
    public void AForksReleaseIsOfferedOnlyWhenItsRepoIsTheConfiguredOne()
    {
        var files = ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi", repo: "someone/ovs-client"), Asset("OVS_P.pak", repo: "someone/ovs-client")), null, "someone/ovs-client");
        Assert.Equal(["OVS_P.pak", "OpenVersus.asi"], files.Select(f => f.Name));
        Assert.Contains("exactly one verified", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi", repo: "someone/ovs-client")))).Message);
        Assert.Contains("owner/repo", Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(Asset("OpenVersus.asi")), null, "https://evil/x")).Message);
    }

    [Theory]
    [InlineData("OpenVersus_2026.09.25.07.asi", "2026.09.25.07")]
    [InlineData("openversus_2026.09.25.07.ASI", "2026.09.25.07")]
    [InlineData("OpenVersus.asi", null)]
    [InlineData("OpenVersus_2026.09.25.07.asi.sha256", null)]
    public void ReadsThePluginAssetVersion(string name, string? version)
    {
        Assert.Equal(version, ClientReleaseManifest.PluginAssetVersion(name));
    }

    [Fact]
    public void ASizeThatIsNotASafeIntegerOrAStringIsNoSize()
    {
        var text = Asset("OpenVersus.asi");
        text["size"] = "100";
        var fraction = Asset("OpenVersus.asi");
        fraction["size"] = 100.5;
        Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(text)));
        Assert.Throws<ClientReleaseException>(() => ClientReleaseManifest.Build(Assets(fraction)));
    }
}
