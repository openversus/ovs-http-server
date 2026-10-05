namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/check_training_server_ready: <see cref="NeverSeenSscEndpoint"/>.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyCheckTrainingServerReady : NeverSeenSscEndpoint
{
    protected override string Route => "check_training_server_ready";
}
