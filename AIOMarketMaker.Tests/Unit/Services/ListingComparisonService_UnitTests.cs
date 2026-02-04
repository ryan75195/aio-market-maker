using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingComparisonService_UnitTests
{
    private static Listing CreateListing(int id, string title, decimal price, string condition, string? description = null) =>
        new()
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            Price = price,
            Condition = condition,
            Description = description ?? $"Description for {title}",
            ScrapeJobId = 1
        };

    [Test]
    public void Should_build_prompt_containing_both_listing_details()
    {
        var listingA = CreateListing(1, "iPhone 15 Pro 256GB", 899.99m, "New");
        var listingB = CreateListing(2, "Apple iPhone 15 Pro 256GB Black", 879.00m, "New");

        var prompt = ListingComparisonService.BuildPrompt(listingA, listingB);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("iPhone 15 Pro 256GB"));
            Assert.That(prompt, Does.Contain("Apple iPhone 15 Pro 256GB Black"));
            Assert.That(prompt, Does.Contain("899.99"));
            Assert.That(prompt, Does.Contain("879"));
            Assert.That(prompt, Does.Contain("New"));
        });
    }

    [Test]
    public void Should_parse_comparable_true_response()
    {
        var json = """{"isComparable": true, "explanation": "Both are iPhone 15 Pro 256GB in new condition"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Is.EqualTo("Both are iPhone 15 Pro 256GB in new condition"));
        });
    }

    [Test]
    public void Should_parse_comparable_false_response()
    {
        var json = """{"isComparable": false, "explanation": "Different storage capacities: 256GB vs 128GB"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Is.EqualTo("Different storage capacities: 256GB vs 128GB"));
        });
    }

    [Test]
    public void Should_truncate_explanation_to_500_characters()
    {
        var longExplanation = new string('x', 600);
        var json = $$$"""{"isComparable": true, "explanation": "{{{longExplanation}}}"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.That(result.Explanation.Length, Is.EqualTo(500));
    }

    [Test]
    public void Should_handle_malformed_json_gracefully()
    {
        var badJson = "not valid json at all";

        var result = ListingComparisonService.ParseResponse(badJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Does.Contain("Failed to parse"));
        });
    }
}
