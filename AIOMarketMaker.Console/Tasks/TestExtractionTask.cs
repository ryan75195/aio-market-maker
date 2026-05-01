using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Console.Tasks;

public class TestExtractionTask : ITask
{
    private readonly IExtractionModelRunner _extractor;

    public string Name => "test-extraction";
    public string Description => "Run a few titles through the extraction model with a custom skeleton";

    public TestExtractionTask(IExtractionModelRunner extractor)
    {
        _extractor = extractor;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        // Variant-only skeleton — no packaging/contents/condition axes
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("edition", "disc drive or digital-only",
                new[] { "disc", "digital" }),
            new SkeletonAxis("generation", "hardware revision",
                new[] { "launch", "slim", "pro" }),
            new SkeletonAxis("storage", "internal SSD capacity",
                new[] { "825gb", "1tb", "2tb" }),
            new SkeletonAxis("color", "console body color",
                new[] { "white", "black", "red", "blue", "grey" }),
            new SkeletonAxis("model_number", "Sony model identifier on the box",
                new[] { "cfi-1016a", "cfi-1016b", "cfi-1116a", "cfi-1116b",
                        "cfi-2016", "cfi-2016b", "cfi-1216a", "cfi-1216b",
                        "cfi-2116", "cfi-2116b" }),
        });

        var titles = new[]
        {
            // Clear variants
            "Sony PlayStation 5 Slim Disc Edition 1TB White Console",
            "PS5 Digital Edition 825GB White CFI-1116B",
            "PlayStation 5 Pro 2TB Console - Black",
            "Sony PS5 Slim Digital Edition 1TB White",
            // Short/ambiguous
            "PS5 Console",
            "PS5 Pro",
            "PlayStation 5",
            "ps5 disc edition",
            // Accessories (should return all null)
            "PS5 DualSense Wireless Controller - White",
            "Sony PlayStation Portal Remote Player for PS5",
            "Cooling Fan for PS5 Slim Console",
            // Bundles
            "Sony PS5 Slim Disc 1TB White + 2 Controllers + Charging Dock + 3 Games",
            "PlayStation 5 Pro 2TB Console with Vertical Stand & Spider-Man 2",
            // Edge cases
            "SEALED Sony PlayStation 5 Slim Disc Edition 1TB - BRAND NEW IN BOX",
            "PS5 825GB CFI-1016A Disc Console White - Used Good Condition",
            "Sony PlayStation 5 Digital Edition Console 825GB White CFI-1216B",
        };

        System.Console.WriteLine($"Testing {titles.Length} titles with variant-only skeleton ({skeleton.Axes.Count()} axes)");
        System.Console.WriteLine(new string('=', 80));

        foreach (var title in titles)
        {
            var result = await _extractor.Extract(title, skeleton);
            var json = result != null
                ? System.Text.Json.JsonSerializer.Serialize(result)
                : "null (accessory/unmatched)";

            System.Console.WriteLine();
            System.Console.WriteLine($"Title: {title}");
            System.Console.WriteLine($"  → {json}");
        }

        return 0;
    }
}
