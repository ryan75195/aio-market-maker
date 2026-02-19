using System.Net;
using AIOMarketMaker.Tests.E2E.Infrastructure;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("Unit")]
public class MockEbayServer_Tests
{
    [Test]
    public async Task Should_serve_listing_html_for_known_id()
    {
        // Arrange
        using var server = new MockEbayServer(port: 19999);
        server.Start();
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync("http://localhost:19999/itm/306278488042");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("<!DOCTYPE html>").Or.Contain("<html"));
    }

    [Test]
    public async Task Should_return_404_for_unknown_listing_id()
    {
        // Arrange
        using var server = new MockEbayServer(port: 19998);
        server.Start();
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync("http://localhost:19998/itm/999999999999");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Should_serve_search_html()
    {
        // Arrange
        using var server = new MockEbayServer(port: 19997);
        server.Start();
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync("http://localhost:19997/sch/i.html?_nkw=test");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("<!DOCTYPE html>").Or.Contain("<html"));
    }
}
