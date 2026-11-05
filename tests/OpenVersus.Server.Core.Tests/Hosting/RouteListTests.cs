using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Core.Tests.Hosting;

/// <summary>"METHOD /path" route lists.</summary>
public sealed class RouteListTests
{
    private readonly RouteList _routes = new("GET /commerce/products, GET /profiles/{id}/inventory, PUT /matches/{id}");

    [Theory]
    [InlineData("GET", "/commerce/products", true)]
    [InlineData("get", "/Commerce/Products", true)]
    [InlineData("GET", "/profiles/0000000000000000000a0001/inventory", true)]
    [InlineData("PUT", "/profiles/0000000000000000000a0001/inventory", false)]
    [InlineData("PUT", "/matches/abc", true)]
    [InlineData("GET", "/matches/abc", false)]
    [InlineData("GET", "/commerce/products/extra", false)]
    [InlineData("GET", "/profiles//inventory", false)]
    public void MatchesMethodAndTemplate(string method, string path, bool listed) => Assert.Equal(listed, _routes.Contains(method, path));

    [Fact]
    public void AnEmptyListHoldsNothing() => Assert.False(new RouteList("").Contains("GET", "/commerce/products"));

    [Theory]
    [InlineData("GET")]
    [InlineData("FETCH /x")]
    [InlineData("GET x")]
    public void AMalformedEntryIsRefused(string routes) => Assert.Throws<FormatException>(() => new RouteList(routes));
}
