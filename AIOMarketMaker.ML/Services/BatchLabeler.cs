using System.Text;
using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.ML.Services;

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";

    private static readonly string ResponseSchema = JsonSchemaGenerator.Generate<ClassifierResponse>();

    /// <summary>
    /// Reads a v8 CSV file and writes a JSONL batch input file.
    /// Returns the number of pairs written.
    /// </summary>
    public static async Task<int> GenerateBatchInput(string csvPath, string outputPath)
    {
        var count = 0;
        using var reader = new StreamReader(csvPath);
        using var writer = new StreamWriter(outputPath);

        // Skip header
        var header = await reader.ReadLineAsync();
        if (header is null)
        {
            return 0;
        }

        var columnIndices = ParseHeader(header);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Length < columnIndices.NeighborDesc + 1)
            {
                continue;
            }

            var pair = new ClassifyPairRequest(
                TitleA: fields[columnIndices.AnchorTitle],
                DescriptionA: CleanField(fields[columnIndices.AnchorDesc]),
                TitleB: fields[columnIndices.NeighborTitle],
                DescriptionB: CleanField(fields[columnIndices.NeighborDesc]));

            var jsonLine = BuildBatchRequestLine($"pair-{count}", pair);
            await writer.WriteLineAsync(jsonLine);
            count++;
        }

        return count;
    }

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

    private record CsvColumnIndices(
        int AnchorTitle,
        int NeighborTitle,
        int AnchorDesc,
        int NeighborDesc);

    private static CsvColumnIndices ParseHeader(string header)
    {
        var columns = header.Split(',');
        return new CsvColumnIndices(
            AnchorTitle: Array.IndexOf(columns, "anchor_title"),
            NeighborTitle: Array.IndexOf(columns, "neighbor_title"),
            AnchorDesc: Array.IndexOf(columns, "anchor_desc"),
            NeighborDesc: Array.IndexOf(columns, "neighbor_desc"));
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var inQuotes = false;
        var field = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }

    private static string CleanField(string field)
    {
        if (string.IsNullOrEmpty(field) || field == "nan")
        {
            return "";
        }
        return field;
    }
}
