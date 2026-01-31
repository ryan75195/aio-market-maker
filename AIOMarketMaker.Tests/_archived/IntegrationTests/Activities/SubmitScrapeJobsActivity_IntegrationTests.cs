using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Azure.Storage.Queues;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;
using System.Text;
using System.Text.Json;

namespace AIOMarketMaker.Tests.IntegrationTests.Activities;

[TestFixture]
[Category("Integration")]
[Explicit("Requires Azurite running on localhost")]
public class SubmitScrapeJobsActivity_IntegrationTests
{
    private const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";

    private QueueServiceClient _queueServiceClient = null!;
    private QueueClient _workQueue = null!;
    private IQueueService _queueService = null!;
    private SubmitScrapeJobsActivity _activity = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _queueServiceClient = new QueueServiceClient(AzuriteConnectionString);
        _workQueue = _queueServiceClient.GetQueueClient("scrape-work");
        await _workQueue.CreateIfNotExistsAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        // Clear queue before each test
        await _workQueue.ClearMessagesAsync();

        var logger = new LoggerFactory().CreateLogger<AzureStorageQueueService>();
        _queueService = new AzureStorageQueueService(_queueServiceClient, logger);

        var urlBuilder = new EbayUrlBuilder();
        var activityLogger = new LoggerFactory().CreateLogger<SubmitScrapeJobsActivity>();

        _activity = new SubmitScrapeJobsActivity(_queueService, urlBuilder, activityLogger);
    }

    [Test]
    public async Task Should_write_messages_to_azure_queue()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 999,
            ListingIds: new List<string> { "123456789", "987654321" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result.SubmittedCount, Is.EqualTo(2));

        // Verify messages are in queue
        var properties = await _workQueue.GetPropertiesAsync();
        Assert.That(properties.Value.ApproximateMessagesCount, Is.EqualTo(4));

        // Peek and verify message content
        var messages = await _workQueue.ReceiveMessagesAsync(maxMessages: 4);
        var decoded = messages.Value.Select(m =>
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(m.Body.ToString()));
            return JsonSerializer.Deserialize<ScrapeQueueMessage>(json);
        }).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Count(m => m!.FileKey == "listing"), Is.EqualTo(2));
            Assert.That(decoded.Count(m => m!.FileKey == "description"), Is.EqualTo(2));
            Assert.That(decoded.All(m => m!.ScrapeRunId == 999), Is.True);
        });
    }
}
