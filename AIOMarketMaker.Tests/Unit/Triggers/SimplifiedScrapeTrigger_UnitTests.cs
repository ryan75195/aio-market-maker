using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Queues;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Tests.Utils;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class SimplifiedScrapeTrigger_UnitTests
{
    private Mock<ILogger<SimplifiedScrapeTrigger>> _loggerMock;
    private EtlDbContext _dbContext;
    private Mock<IWebscraperClient> _webscraperClientMock;
    private Mock<ISearchParser> _searchParserMock;
    private Mock<QueueServiceClient> _queueServiceMock;
    private Mock<QueueClient> _queueClientMock;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<SimplifiedScrapeTrigger>>();
        _dbContext = InMemoryDbContextFactory.Create();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _queueServiceMock = new Mock<QueueServiceClient>();
        _queueClientMock = new Mock<QueueClient>();

        _queueServiceMock
            .Setup(q => q.GetQueueClient("scrape-work"))
            .Returns(_queueClientMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public void Should_construct_with_all_dependencies()
    {
        // Act
        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Assert
        Assert.That(trigger, Is.Not.Null);
    }
}
