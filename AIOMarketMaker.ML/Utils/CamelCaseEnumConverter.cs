using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOMarketMaker.ML.Utils;

public class CamelCaseEnumConverter : JsonStringEnumConverter
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}
