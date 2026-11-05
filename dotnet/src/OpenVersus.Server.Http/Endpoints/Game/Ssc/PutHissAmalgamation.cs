using FastEndpoints;
using OpenVersus.Server.Core.Hiss;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/hiss_amalgamation: the game's configuration (<see cref="IHissService"/>). The body's Crc is not read:
/// the whole answer is always sent, as the TS server sends it. In the login batch.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/hiss_amalgamation.
/// </summary>
public sealed class PutHissAmalgamation : HissAmalgamationEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/hiss_amalgamation");
    }
}
