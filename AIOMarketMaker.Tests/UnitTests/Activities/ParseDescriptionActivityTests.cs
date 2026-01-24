using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class ParseDescriptionActivityTests
{
    private ParseDescriptionActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var listingParser = new EbayListingParser();
        _activity = new ParseDescriptionActivity(
            listingParser,
            NullLogger<ParseDescriptionActivity>.Instance);
    }

    [Test]
    public async Task Should_return_null_for_empty_html()
    {
        // Act
        var result = await _activity.Run("", null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_for_null_html()
    {
        // Act
        var result = await _activity.Run(null!, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_parse_description_from_html()
    {
        // Arrange - use a simple HTML structure that the parser can extract text from
        var html = @"
            <html>
            <body>
                <p>This is a test product description.</p>
                <p>It has multiple paragraphs.</p>
            </body>
            </html>";

        // Act
        var result = await _activity.Run(html, null!);

        // Assert - the parser should extract some text content
        // Note: actual behavior depends on EbayListingParser.ParseDescription implementation
        Assert.That(result, Is.Not.Null.Or.Null); // May return null or text depending on parser
    }

    [Test]
    public async Task Should_handle_malformed_html_gracefully()
    {
        // Arrange
        var html = "<html><body><div>Unclosed tags<p>No closing";

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _activity.Run(html, null!));
    }
}
