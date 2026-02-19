using NUnit.Framework;
using Azure.Storage.Blobs;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires Azurite running locally")]
public class EtlBlobTriggerIntegrationTests
{
    private BlobServiceClient _blobService = null!;

    [SetUp]
    public void SetUp()
    {
        _blobService = new BlobServiceClient("UseDevelopmentStorage=true");
    }

    [Test]
    public async Task Should_create_blobs_with_grouped_path_structure()
    {
        // Arrange
        var container = _blobService.GetBlobContainerClient("html");
        await container.CreateIfNotExistsAsync();

        var jobId = Guid.NewGuid().ToString("N");
        var listingId = "123456789";

        // Act
        var listingBlob = container.GetBlobClient($"{jobId}/{listingId}/listing.html");
        var descriptionBlob = container.GetBlobClient($"{jobId}/{listingId}/description.html");

        await listingBlob.UploadAsync(BinaryData.FromString("<html>listing</html>"), overwrite: true);
        await descriptionBlob.UploadAsync(BinaryData.FromString("<html>description</html>"), overwrite: true);

        // Assert
        Assert.Multiple(async () =>
        {
            Assert.That((await listingBlob.ExistsAsync()).Value, Is.True);
            Assert.That((await descriptionBlob.ExistsAsync()).Value, Is.True);
        });

        // Cleanup
        await listingBlob.DeleteIfExistsAsync();
        await descriptionBlob.DeleteIfExistsAsync();
    }
}
