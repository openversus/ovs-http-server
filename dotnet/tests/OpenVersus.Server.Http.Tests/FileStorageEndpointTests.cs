using System.Net;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The file records and the drive sync the game asks for at login.</summary>
public sealed class FileStorageEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await factory.CreateGameClient().SendAsync(request);
    }

    [Theory]
    // The client-update art is served by this server: its URL is this server's, as the request reached it.
    [InlineData(null, "http://game.example:8000/assets/openversus-update-required-keyart.png")]
    [InlineData("https", "https://game.example:8000/assets/openversus-update-required-keyart.png")]
    [InlineData("https, http", "https://game.example:8000/assets/openversus-update-required-keyart.png")]
    public async Task TheUpdateArtPointsAtThisServer(string? forwardedProto, string expected)
    {
        var headers = forwardedProto is null ? [] : new[] { ("X-Forwarded-Proto", forwardedProto) };
        using var record = await SendAsync(HttpMethod.Get, "http://game.example:8000/file_storage/openversus-update-required-keyart", headers);
        Assert.Equal(expected, (string?)JsonNode.Parse(await record.Content.ReadAsStringAsync())!["download_url"]);

        using var list = await SendAsync(HttpMethod.Get, "http://game.example:8000/file_storage", headers);
        var records = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(expected, (string?)records[0]!["download_url"]);
        Assert.Equal(expected.Replace("keyart", "thumbnail"), (string?)records[1]!["download_url"]);
        Assert.True(records.Count > 50);
    }

    [Fact]
    public async Task AnotherRecordIsTheSameForEveryone()
    {
        using var response = await SendAsync(HttpMethod.Get, "/file_storage/s5-bp-carousel-keyart");
        Assert.Equal(StaticResponses.Json("file-storage-s5-bp-carousel-keyart"), await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/file_storage/not-a-file")]
    [InlineData("PUT", "/drives/another/sync")]
    public async Task WhatTheTsServerHasNoRouteForIsNotPorted(string method, string path)
    {
        using var response = await SendAsync(new HttpMethod(method), path);
        Assert.True(response.Headers.Contains(Stub.Header));
    }

    [Fact]
    public async Task TheDriveSyncHasNothingToSync()
    {
        using var response = await SendAsync(HttpMethod.Put, "/drives/multiversus/sync");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"additions":[],"deletions":[]}""", await response.Content.ReadAsStringAsync());
    }
}
