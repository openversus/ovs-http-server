using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/unfriend/{id}.
/// Seen in: binary 0x140fa3780; TS server: PUT /friends/me/unfriend/{id}.
/// Social layer; method from server.
/// </summary>
public sealed class PutFriendsMeUnfriendById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/unfriend/{id}");
    }
}
