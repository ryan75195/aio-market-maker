using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Pipeline;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class OpportunityPostJobStage_UnitTests
{
    [Test]
    public async Task Should_call_compute_with_configured_fee_and_min_comps()
    {
        var service = new Mock<ITaxonomyOpportunityService>();
        service.Setup(s => s.Compute(42, 13.25, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var stage = BuildStage(service.Object, new PricingOptions { FeePercent = 13.25, MinComps = 3 });

        await stage.Execute(new PostJobContext(1, 42, "Test"), CancellationToken.None);

        service.Verify(s => s.Compute(42, 13.25, 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_use_custom_pricing_options()
    {
        var service = new Mock<ITaxonomyOpportunityService>();
        service.Setup(s => s.Compute(7, 10.0, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);

        var stage = BuildStage(service.Object, new PricingOptions { FeePercent = 10.0, MinComps = 5 });

        await stage.Execute(new PostJobContext(1, 7, "iPhone"), CancellationToken.None);

        service.Verify(s => s.Compute(7, 10.0, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Should_have_name_Opportunities()
    {
        var service = new Mock<ITaxonomyOpportunityService>();
        var stage = BuildStage(service.Object, new PricingOptions());

        Assert.That(stage.Name, Is.EqualTo("Opportunities"));
    }

    private static OpportunityPostJobStage BuildStage(
        ITaxonomyOpportunityService opportunityService, PricingOptions options)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<ITaxonomyOpportunityService>(_ => opportunityService);

        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        return new OpportunityPostJobStage(
            scopeFactory,
            Options.Create(options),
            NullLogger<OpportunityPostJobStage>.Instance);
    }
}
