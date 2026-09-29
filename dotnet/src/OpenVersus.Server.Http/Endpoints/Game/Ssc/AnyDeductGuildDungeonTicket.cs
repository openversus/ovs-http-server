using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/deduct_guild_dungeon_ticket.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyDeductGuildDungeonTicket : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/deduct_guild_dungeon_ticket");
    }
}
