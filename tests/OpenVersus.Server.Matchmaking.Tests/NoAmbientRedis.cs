using System.Runtime.CompilerServices;

namespace OpenVersus.Server.Matchmaking.Tests;

/// <summary>
/// A test matchmaker must never reach a real Redis: it would work the live queues there. The environment's REDIS is
/// cleared before any host is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
