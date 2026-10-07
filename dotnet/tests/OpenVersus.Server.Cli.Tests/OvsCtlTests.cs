using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using OpenVersus.Server.Cli;
using OpenVersus.Server.Core.Hosting;
using Spectre.Console.Testing;

namespace OpenVersus.Server.Cli.Tests;

/// <summary>ovsctl against a real service (a Core host on free ports), run in-process with a test console.</summary>
public sealed class OvsCtlTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private int _controlPort;
    private string _socket = "";

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        _controlPort = FreePort();
        _socket = Path.Combine(Path.GetTempPath(), "ovs-tests", $"{Guid.NewGuid():N}.sock");
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("clitest", "TEST_PORT", DefaultPublicPort: 1, DefaultControlPort: 1),
            [$"--TEST_PORT={FreePort()}", $"--Control:Port={_controlPort}", $"--Control:Socket={_socket}", "--REDIS="]);
        _app = builder.Build();
        _app.UseOpenVersus();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<(int Exit, string Output)> Run(params string[] args)
    {
        var console = new TestConsole().Width(200);
        int exit = await OvsCtl.RunAsync(args, console);
        return (exit, console.Output);
    }

    private string Port => _controlPort.ToString();

    [Fact]
    public async Task StatusOverThePort()
    {
        var (exit, output) = await Run("status", "--port", Port);
        Assert.Equal(OvsCtl.Done, exit);
        Assert.Contains("clitest", output);
        Assert.Contains("this instance only", output);
    }

    [SkippableFact]
    public async Task StatusOverTheSocket()
    {
        Skip.IfNot(Socket.OSSupportsUnixDomainSockets, "no Unix sockets here");
        var (exit, output) = await Run("status", "--socket", _socket);
        Assert.Equal(OvsCtl.Done, exit);
        Assert.Contains(_socket, output);
    }

    [Fact]
    public async Task SetGetAndUnsetAnInstanceSetting()
    {
        Assert.Equal(OvsCtl.Done, (await Run("settings", "set", "Log:Level", "Debug", "--scope", "instance", "--port", Port)).Exit);

        var (exit, json) = await Run("settings", "get", "Log:Level", "--json", "--port", Port);
        Assert.Equal(OvsCtl.Done, exit);
        var setting = JsonDocument.Parse(json).RootElement;
        Assert.Equal("Debug", setting.GetProperty("value").GetString());
        Assert.Equal("Debug", setting.GetProperty("instance").GetString());

        Assert.Equal(OvsCtl.Done, (await Run("settings", "unset", "Log:Level", "--scope", "instance", "--port", Port)).Exit);
        Assert.Equal("Information", JsonDocument.Parse((await Run("settings", "get", "Log:Level", "--json", "--port", Port)).Output).RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ListShowsEverySettingAndFilters()
    {
        var (exit, output) = await Run("settings", "list", "--port", Port);
        Assert.Equal(OvsCtl.Done, exit);
        Assert.Contains("Log:Level", output);
        Assert.Contains("Control:Port", output);

        var (_, filtered) = await Run("settings", "list", "log", "--port", Port);
        Assert.Contains("Log:Level", filtered);
        Assert.DoesNotContain("Control:Port", filtered);
    }

    [SkippableFact]
    public void LobbyAsksTheLobbiesServiceUnlessToldOtherwise()
    {
        Skip.If(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionSettings.ServiceVariable)), "OVS_SERVICE is set here");
        Assert.Equal("lobbies", new LobbySettings().Service);
        Assert.Equal("http", new ConnectionSettings().Service);
    }

    [Theory]
    [InlineData("lobby")]
    [InlineData("lobby", "QX7RT", "--all")]
    public async Task LobbyTakesACodeOrAllButNotBoth(params string[] args)
    {
        var (exit, output) = await Run([.. args, "--port", Port]);
        Assert.NotEqual(OvsCtl.Done, exit);
        Assert.Contains("--all", output);
    }

    [Theory]
    [InlineData("settings", "set", "Log:Level", "Loud", "--scope", "instance")]
    [InlineData("settings", "set", "Log:Level", "Debug")]
    [InlineData("settings", "set", "Control:Port", "5", "--scope", "instance")]
    [InlineData("settings", "get", "No:Such")]
    public async Task ARefusalExitsWithOne(params string[] args)
    {
        var (exit, output) = await Run([.. args, "--port", Port]);
        Assert.Equal(OvsCtl.Refused, exit);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Theory]
    [InlineData("queues")]
    [InlineData("online")]
    [InlineData("matches")]
    [InlineData("lobby", "QX7RT")]
    [InlineData("lobby", "--all")]
    [InlineData("player", "show", "someone")]
    [InlineData("player", "rename", "someone", "else")]
    public async Task OpsWithoutTheirDatabaseAreRefusedClearly(params string[] args)
    {
        // The test service has neither Redis nor Mongo.
        var (exit, output) = await Run([.. args, "--port", Port]);
        Assert.Equal(OvsCtl.Refused, exit);
        Assert.Contains("configured", output);
    }

    [Fact]
    public async Task AnUnreachableServiceExitsWithTwo()
    {
        var (exit, output) = await Run("status", "--port", FreePort().ToString());
        Assert.Equal(OvsCtl.Unreachable, exit);
        Assert.Contains("cannot reach", output);
    }

    [Fact]
    public async Task TheProbeOfAReadyServiceExitsWithZero()
    {
        var (exit, output) = await Run("health", "--probe", "--port", Port);
        Assert.Equal(OvsCtl.Done, exit);
        Assert.Contains("ready", output);
    }

    [Fact]
    public async Task TheProbeOfAServiceThatIsNotReadyExitsWithOneAndSaysWhy()
    {
        int controlPort = FreePort();
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("clitestredis", "TEST_PORT", DefaultPublicPort: 1, DefaultControlPort: 1, ServiceStores.Redis),
            [$"--TEST_PORT={FreePort()}", $"--Control:Port={controlPort}", "--Control:Socket=off", "--REDIS="]);
        await using var app = builder.Build();
        app.UseOpenVersus();
        await app.StartAsync();

        var (exit, output) = await Run("health", "--probe", "--port", controlPort.ToString());
        Assert.Equal(OvsCtl.Refused, exit);
        Assert.Contains("not ready", output);
        Assert.Contains("REDIS is not set", output);
        await app.StopAsync();
    }

    [Fact]
    public async Task HealthWithoutARegistryShowsTheServiceItReachedAndSaysSo()
    {
        var (exit, output) = await Run("health", "--port", Port);
        Assert.Equal(OvsCtl.Done, exit);
        Assert.Contains("No cluster view", output);
        Assert.Contains("ready", output);
    }

    [Fact]
    public async Task HealthOfAnUnreachableServiceExitsWithTwo()
    {
        var (exit, _) = await Run("health", "--port", FreePort().ToString());
        Assert.Equal(OvsCtl.Unreachable, exit);
        (exit, _) = await Run("health", "--probe", "--port", FreePort().ToString());
        Assert.Equal(OvsCtl.Unreachable, exit);
    }

    [Fact]
    public async Task AnUnknownServiceIsRejected()
    {
        var (exit, output) = await Run("status", "--service", "nope");
        Assert.NotEqual(OvsCtl.Done, exit);
        Assert.Contains("unknown service", output);
    }
}
