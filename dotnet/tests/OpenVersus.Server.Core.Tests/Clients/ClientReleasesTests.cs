using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;

namespace OpenVersus.Server.Core.Tests.Clients;

/// <summary>The GitHub release fetch: cached five minutes per repo, and every way GitHub can fail becomes one exception.</summary>
public sealed class ClientReleasesTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : this((r, _) => answer(r))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return answer(request, cancellationToken);
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Factory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ClientReleases Releases(HttpMessageHandler handler, ManualTime time, string repo = "openversus/ovs-client", TimeSpan? timeout = null) =>
        new(new Factory(handler, timeout), new TestOptions<ClientSettings>(new ClientSettings { ReleaseRepo = repo }), time, NullLogger<ClientReleases>.Instance);

    [Fact]
    public async Task FetchesTheLatestReleaseOnceEveryFiveMinutes()
    {
        var handler = new Handler(_ => Task.FromResult(Json("""{"tag_name":"2026.10.08.1","assets":[]}""")));
        var time = new ManualTime(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var releases = Releases(handler, time);

        Assert.Equal("2026.10.08.1", (string?)(await releases.LatestAsync())["tag_name"]);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("2026.10.08.1", (string?)(await releases.LatestAsync())["tag_name"]);
        Assert.Single(handler.Requests);
        time.Advance(TimeSpan.FromSeconds(1));
        await releases.LatestAsync();
        Assert.Equal(2, handler.Requests.Count);

        var request = handler.Requests[0];
        Assert.Equal("https://api.github.com/repos/openversus/ovs-client/releases/latest", request.RequestUri?.ToString());
        Assert.Equal("OpenVersus-Server", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task AnotherRepoIsFetchedAgainAndAMalformedOneMeansTheDefault()
    {
        var handler = new Handler(r => Task.FromResult(Json($$"""{"tag_name":"{{r.RequestUri!.Segments[2].TrimEnd('/')}}"}""")));
        var time = new ManualTime(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var settings = new ClientSettings { ReleaseRepo = "someone/ovs-client" };
        var releases = new ClientReleases(new Factory(handler), new TestOptions<ClientSettings>(settings), time, NullLogger<ClientReleases>.Instance);

        Assert.Equal("someone/ovs-client", releases.Repo);
        Assert.Equal("someone", (string?)(await releases.LatestAsync())["tag_name"]);
        settings.ReleaseRepo = "https://evil/x";
        Assert.Equal("openversus/ovs-client", releases.Repo);
        Assert.Equal("openversus", (string?)(await releases.LatestAsync())["tag_name"]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EveryWayGitHubFailsIsOneException()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var status = await Assert.ThrowsAsync<ClientReleaseException>(() => Releases(new Handler(_ => Task.FromResult(Json("""{"message":"rate limited"}""", HttpStatusCode.Forbidden))), time).LatestAsync());
        Assert.Equal("GitHub API returned 403", status.Message);

        await Assert.ThrowsAsync<ClientReleaseException>(() => Releases(new Handler(_ => Task.FromResult(Json("<html>maintenance</html>"))), time).LatestAsync());
        await Assert.ThrowsAsync<ClientReleaseException>(() => Releases(new Handler(_ => Task.FromResult(Json("[1,2]"))), time).LatestAsync());
        await Assert.ThrowsAsync<ClientReleaseException>(() => Releases(new Handler(_ => throw new HttpRequestException("no route to host")), time).LatestAsync());

        // The client's timeout, as HttpClient raises it: a cancellation that is not the caller's.
        var slow = new Handler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(5), token); return Json("{}"); });
        var timedOut = await Assert.ThrowsAsync<ClientReleaseException>(() => Releases(slow, time, timeout: TimeSpan.FromMilliseconds(100)).LatestAsync());
        Assert.Contains("did not answer in time", timedOut.Message);

        // The caller's own cancellation is not GitHub's failure.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Releases(slow, time).LatestAsync(cancelled.Token));
    }
}

