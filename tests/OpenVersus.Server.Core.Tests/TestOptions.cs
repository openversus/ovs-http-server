using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Tests;

/// <summary>A fixed settings value for a service that reads IOptionsMonitor.</summary>
public sealed class TestOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
