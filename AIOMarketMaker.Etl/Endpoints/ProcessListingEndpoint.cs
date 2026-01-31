using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;

namespace AIOMarketMaker.Etl.Endpoints;

public class ProcessListingEndpoint
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IListingParser _listingParser;
    private readonly ILogger<ProcessListingEndpoint> _logger;

    public ProcessListingEndpoint(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IListingParser listingParser,
        ILogger<ProcessListingEndpoint> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _logger = logger;
    }
}
