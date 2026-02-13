using System.Net;
using System.Net.Http.Json;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class VariantClassifierClient_UnitTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private VariantClassifierClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:8010")
        };
        _client = new VariantClassifierClient(
            httpClient,
            Mock.Of<ILogger<VariantClassifierClient>>());
    }

    [Test]
    public async Task Should_return_verdict_when_model_responds()
    {
        var response = new ClassifyResponse(
            new[] { new PairResult(true, 0.95f, false) });

        SetupResponse(HttpStatusCode.OK, response);

        var results = await _client.Classify(new[]
        {
            new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B")
        });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.True);
            Assert.That(results[0].Confidence, Is.EqualTo(0.95f).Within(0.01));
            Assert.That(results[0].NeedsFallback, Is.False);
        });
    }

    [Test]
    public async Task Should_return_fallback_needed_when_confidence_low()
    {
        var response = new ClassifyResponse(
            new[] { new PairResult(false, 0.62f, true) });

        SetupResponse(HttpStatusCode.OK, response);

        var results = await _client.Classify(new[]
        {
            new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B")
        });

        Assert.That(results[0].NeedsFallback, Is.True);
    }

    [Test]
    public void Should_throw_when_service_unavailable()
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _client.Classify(new[]
            {
                new ClassifyPairRequest("A", "A", "B", "B")
            }));
    }

    [Test]
    public async Task Should_report_healthy_when_service_responds()
    {
        SetupResponse(HttpStatusCode.OK,
            new ClassifyResponse(Array.Empty<PairResult>()));

        var healthy = await _client.IsHealthy();

        Assert.That(healthy, Is.True);
    }

    [Test]
    public async Task Should_report_unhealthy_when_service_down()
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var healthy = await _client.IsHealthy();

        Assert.That(healthy, Is.False);
    }

    private void SetupResponse(HttpStatusCode status, ClassifyResponse body)
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = JsonContent.Create(body)
            });
    }
}
