using System.Runtime.CompilerServices;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The test servers must never share settings through a real Redis: a cluster change one test makes would reach the
/// others (and whatever else uses that Redis). The environment's REDIS is cleared before any server is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
