using System.Runtime.CompilerServices;

namespace OpenVersus.Server.Web.Tests;

/// <summary>
/// A test service must never reach a real Redis by accident. The environment's REDIS is
/// cleared before any host is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
