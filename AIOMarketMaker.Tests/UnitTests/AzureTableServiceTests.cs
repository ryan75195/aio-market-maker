using AIOMarketMaker.Core.Models.Azure;
using AIOMarketMaker.Core.Services;
using Azure; // For RequestFailedException
using Azure.Data.Tables; // For TableClient
using Moq;
using NUnit.Framework; // Using NUnit as per project setup
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AIOMarketMaker.Tests.UnitTests
{
    [TestFixture]
    public class AzureTableServiceTests
    {
        // Use a connection string that is structurally valid but won't connect to a real resource for some tests
        private const string DummyConnectionString = "DefaultEndpointsProtocol=https;AccountName=dummystorageaccount;AccountKey=dummykeydummykeydummykeydummykeydummykeydummykeydummykeydummykeydummykeydummykeydummykey==;EndpointSuffix=core.windows.net";
        private const string RealButPotentiallyNonExistentTable = "TestTermsTableNonExistent";
        // A connection string that AzureTableService constructor should reject
        private const string DefaultPlaceholderConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";


        [TestCase(null)]
        [TestCase("")]
        [TestCase("YOUR_CONNECTION_STRING")] // Test specific default placeholder
        [TestCase(DefaultPlaceholderConnectionString)] // Test another default placeholder
        public void Constructor_ThrowsArgumentException_ForInvalidConnectionString(string invalidConnectionString)
        {
            Assert.Throws<ArgumentException>(() => new AzureTableService(invalidConnectionString, "ValidTable"), "connectionString");
        }

        [TestCase(null)]
        [TestCase("")]
        public void Constructor_ThrowsArgumentException_ForInvalidTableName(string invalidTableName)
        {
            Assert.Throws<ArgumentException>(() => new AzureTableService(DummyConnectionString, invalidTableName), "tableName");
        }

        [Test]
        public async Task GetSearchTermsAsync_ReturnsEmptyList_WhenTableClientConstructorThrows()
        {
            // This test uses a connection string that is syntactically malformed enough
            // that the TableClient constructor itself should throw an exception.
            // AzureTableService's constructor now validates against specific placeholders,
            // so we need a string that passes *its* validation but fails for TableClient.
            // An AccountName that is too short or has invalid characters might work, or an invalid EndpointSuffix.
            // Let's try with a very minimal string that TableClient should reject.
            string malformedForTableClient = "AccountName=;;"; //This should be rejected by TableClient
            
            var serviceWithMalformedConnection = new AzureTableService(malformedForTableClient, RealButPotentiallyNonExistentTable);
            
            var result = await serviceWithMalformedConnection.GetSearchTermsAsync();

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);
            // Ideally, also assert that an error was logged.
            // Since AzureTableService catches generic Exception after RequestFailedException,
            // a FormatException from TableClient constructor would be caught and logged.
        }
        
        [Test]
        public async Task GetSearchTermsAsync_ReturnsEmptyList_WhenTableDoesNotExistAndCreateFailsOrIsEmpty()
        {
            // This test uses a valid connection string format but points to a (likely) non-existent resource
            // or a table that will be empty. It tests the flow where CreateIfNotExistsAsync might succeed (creating an empty table)
            // or the query returns no items, or an error occurs that is handled.
            // The DummyConnectionString is designed for this scenario.
            var service = new AzureTableService(DummyConnectionString, RealButPotentiallyNonExistentTable);
            var result = await service.GetSearchTermsAsync();

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty); // Expect empty if table was just created or truly doesn't exist and error was handled
                                         // The service logs RequestFailedException and returns empty list.
        }

        // The more advanced tests requiring TableClient injection are omitted for now
        // as per the subtask's focus, given AzureTableService news up TableClient directly.
    }
}
