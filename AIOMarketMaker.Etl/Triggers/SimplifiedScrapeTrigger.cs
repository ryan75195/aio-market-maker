using Microsoft.Extensions.Logging;
using Azure.Storage.Queues;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class SimplifiedScrapeTrigger
{
    private readonly ILogger<SimplifiedScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _queueClient;

    public SimplifiedScrapeTrigger(
        ILogger<SimplifiedScrapeTrigger> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _queueClient = queueService.GetQueueClient("scrape-work");
    }
}
