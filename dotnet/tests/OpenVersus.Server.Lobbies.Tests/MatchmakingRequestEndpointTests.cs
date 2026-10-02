using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.TestSupport;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Lobbies.Tests;

/// <summary>
/// The game has the matchmaking answer before the ticket is queued (the TS server sends, then queues): what happens after
/// the answer waits here until the client has read it, which only works if the answer has really gone, Hydra included.
/// </summary>
public sealed class MatchmakingRequestEndpointTests(ServiceFactory<Program> factory) : IClassFixture<ServiceFactory<Program>>
{
    private sealed class Requests : IMatchmakingRequestService
    {
        public TaskCompletionSource ClientHasAnswer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MatchmakingAnswer?> RequestAsync(string criteria, PartyRequest request, CancellationToken ct) =>
            Task.FromResult<MatchmakingAnswer?>(new MatchmakingAnswer(200, new JsonObject { ["id"] = "request" }, async () =>
            {
                await ClientHasAnswer.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Queued.SetResult();
            }));

        public Task<JsonObject> CancelAsync(string requestId, PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAnswerArrivesBeforeTheTicketIsQueued(bool hydra)
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        var requests = new Requests();
        string[] parts = s_redis!.Split(':');
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Clients:VersionCheck", "false");
            // The endpoint wants a Redis (the test Redis, a database of its own); the service under it is a fake.
            b.UseSetting("REDIS", parts[0]);
            b.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
            b.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
            b.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
            b.UseSetting("REDIS_DB", "13");
            b.ConfigureTestServices(s => s.AddSingleton<IMatchmakingRequestService>(requests));
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, ServiceFactory<Program>.Token());
        HttpContent content = hydra
            ? new ByteArrayContent(HydraCodec.EncodeJson("""{"match":"m"}""")) { Headers = { ContentType = new(HydraBodies.ContentType) } }
            : new StringContent("""{"match":"m"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/matches/matchmaking/1v1-retail/request", content).WaitAsync(TimeSpan.FromSeconds(3));
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.False(requests.Queued.Task.IsCompleted);
        requests.ClientHasAnswer.SetResult();
        await requests.Queued.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(hydra ? HydraCodec.EncodeJson("""{"id":"request"}""") : """{"id":"request"}"""u8.ToArray(), body);
    }
}
