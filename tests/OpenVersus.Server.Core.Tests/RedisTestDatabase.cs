namespace OpenVersus.Server.Core.Tests;

/// <summary>
/// The test classes that use Redis database 15 (OVS_TEST_REDIS) run one at a time: OpsTests clears the whole database
/// before and after, which in parallel deletes the other classes' keys halfway through their tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RedisTestDatabase
{
    public const string Name = "Redis database 15";
}
