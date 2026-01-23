using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using ScraperWorker.Services;
using System.Net;
using System.Net.Http.Json;

namespace AIOMarketMaker.Tests.Unit
{
    [TestFixture]
    public class WebscraperClientUnitTests
    {
        private Mock<IJobRepository> _mockJobRepository;
        private Mock<ILogger<WebscraperClient>> _mockLogger;

        [SetUp]
        public void Setup()
        {
            _mockJobRepository = new Mock<IJobRepository>();
            _mockLogger = new Mock<ILogger<WebscraperClient>>();
        }

        [Test]
        public async Task Should_pass_correlation_id_header_when_provided()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;
            var correlationId = "test-correlation-123";

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new StartResponse("job-123"))
                });

            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("http://localhost:7126/")
            };

            var config = new ScraperApiConfig("http://localhost:7126/", "test-api-key");
            var client = new WebscraperClient(httpClient, config, _mockJobRepository.Object, _mockLogger.Object);

            // Act
            await client.NewJobAsync(new[] { "http://example.com" }, correlationId: correlationId);

            // Assert
            Assert.That(capturedRequest, Is.Not.Null, "Request should have been captured");
            Assert.That(
                capturedRequest!.Headers.Contains("X-Correlation-Id"),
                Is.True,
                "Request should contain X-Correlation-Id header");
            Assert.That(
                capturedRequest.Headers.GetValues("X-Correlation-Id").First(),
                Is.EqualTo(correlationId),
                "X-Correlation-Id header value should match");
        }

        [Test]
        public async Task Should_not_include_correlation_id_header_when_not_provided()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new StartResponse("job-123"))
                });

            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("http://localhost:7126/")
            };

            var config = new ScraperApiConfig("http://localhost:7126/", "test-api-key");
            var client = new WebscraperClient(httpClient, config, _mockJobRepository.Object, _mockLogger.Object);

            // Act
            await client.NewJobAsync(new[] { "http://example.com" }, correlationId: null);

            // Assert
            Assert.That(capturedRequest, Is.Not.Null, "Request should have been captured");
            Assert.That(
                capturedRequest!.Headers.Contains("X-Correlation-Id"),
                Is.False,
                "Request should NOT contain X-Correlation-Id header when not provided");
        }

        [Test]
        public async Task Should_not_include_correlation_id_header_when_empty_string()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new StartResponse("job-123"))
                });

            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("http://localhost:7126/")
            };

            var config = new ScraperApiConfig("http://localhost:7126/", "test-api-key");
            var client = new WebscraperClient(httpClient, config, _mockJobRepository.Object, _mockLogger.Object);

            // Act
            await client.NewJobAsync(new[] { "http://example.com" }, correlationId: "");

            // Assert
            Assert.That(capturedRequest, Is.Not.Null, "Request should have been captured");
            Assert.That(
                capturedRequest!.Headers.Contains("X-Correlation-Id"),
                Is.False,
                "Request should NOT contain X-Correlation-Id header when empty string");
        }
    }
}
