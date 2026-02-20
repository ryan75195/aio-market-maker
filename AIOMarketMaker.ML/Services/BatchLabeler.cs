using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.ML.Services;

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";

    private static readonly string ResponseSchema = JsonSchemaGenerator.Generate<ClassifierResponse>();

    /// <summary>
    /// Builds a single JSONL line for the OpenAI Batch API.
    /// </summary>
    public static string BuildBatchRequestLine(string customId, ClassifyPairRequest pair)
    {
        var userPrompt = LlmVariantClassifier.BuildUserPrompt(pair);

        var request = new
        {
            custom_id = customId,
            method = "POST",
            url = "/v1/chat/completions",
            body = new
            {
                model = Model,
                messages = new object[]
                {
                    new { role = "system", content = LlmVariantClassifier.SystemPromptText },
                    new { role = "user", content = userPrompt }
                },
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "ClassifierResponse",
                        strict = true,
                        schema = JsonSerializer.Deserialize<JsonElement>(ResponseSchema)
                    }
                }
            }
        };

        return JsonSerializer.Serialize(request);
    }
}
