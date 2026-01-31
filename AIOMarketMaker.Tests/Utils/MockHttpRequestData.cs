using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AIOMarketMaker.Tests.Utils;

/// <summary>
/// Utility class for creating mock HttpRequestData for Azure Functions tests.
/// </summary>
public static class MockHttpRequestData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Creates a mock HttpRequestData with JSON body.
    /// </summary>
    /// <typeparam name="T">The type of the request body.</typeparam>
    /// <param name="body">The request body to serialize as JSON.</param>
    /// <returns>A mock HttpRequestData configured for testing.</returns>
    public static HttpRequestData Create<T>(T body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return CreateWithBody(json);
    }

    /// <summary>
    /// Creates a mock HttpRequestData with a raw string body.
    /// </summary>
    /// <param name="body">The raw string body.</param>
    /// <returns>A mock HttpRequestData configured for testing.</returns>
    public static HttpRequestData CreateWithBody(string body)
    {
        var services = new ServiceCollection();
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(JsonOptions);
        });
        var serviceProvider = services.BuildServiceProvider();

        var mockContext = new Mock<FunctionContext>();
        mockContext.Setup(c => c.InstanceServices).Returns(serviceProvider);

        var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var mockRequest = new Mock<HttpRequestData>(mockContext.Object);
        mockRequest.Setup(r => r.Body).Returns(bodyStream);

        mockRequest.Setup(r => r.CreateResponse()).Returns(() =>
        {
            var mockResponse = new Mock<HttpResponseData>(mockContext.Object);
            mockResponse.SetupProperty(r => r.StatusCode, HttpStatusCode.OK);
            mockResponse.SetupProperty(r => r.Body, new MemoryStream());
            var headers = new HttpHeadersCollection();
            mockResponse.SetupGet(r => r.Headers).Returns(headers);
            mockResponse.SetupSet(r => r.Headers = It.IsAny<HttpHeadersCollection>());
            return mockResponse.Object;
        });

        return mockRequest.Object;
    }

    /// <summary>
    /// Creates a mock HttpRequestData with empty body.
    /// </summary>
    /// <returns>A mock HttpRequestData configured for testing.</returns>
    public static HttpRequestData CreateEmpty()
    {
        return CreateWithBody(string.Empty);
    }

    /// <summary>
    /// Reads and deserializes the response body from an HttpResponseData.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="response">The response to read from.</param>
    /// <returns>The deserialized response body.</returns>
    public static async Task<T> ReadResponseAsync<T>(HttpResponseData response)
    {
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }
}
