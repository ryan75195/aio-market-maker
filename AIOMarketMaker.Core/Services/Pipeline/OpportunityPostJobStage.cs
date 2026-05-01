using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Core.Services.Pipeline;

public class OpportunityPostJobStage : IPostJobStage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PricingOptions _options;
    private readonly ILogger<OpportunityPostJobStage> _logger;

    public string Name => "Opportunities";

    public OpportunityPostJobStage(
        IServiceScopeFactory scopeFactory,
        IOptions<PricingOptions> options,
        ILogger<OpportunityPostJobStage> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Execute(PostJobContext context, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var opportunityService = scope.ServiceProvider.GetRequiredService<ITaxonomyOpportunityService>();

        var count = await opportunityService.Compute(
            context.JobId, _options.FeePercent, _options.MinComps, ct);

        _logger.LogInformation(
            "Computed {Count} taxonomy opportunities for job {JobId} \"{SearchTerm}\"",
            count, context.JobId, context.SearchTerm);
    }
}
