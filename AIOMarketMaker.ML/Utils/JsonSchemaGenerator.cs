using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOMarketMaker.ML.Utils;

public static class JsonSchemaGenerator
{
    public static string Generate<T>()
    {
        return Generate(typeof(T));
    }

    public static string Generate(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var schemaProperties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in properties)
        {
            var jsonName = GetJsonPropertyName(prop);
            schemaProperties[jsonName] = BuildPropertySchema(prop.PropertyType);
            required.Add(jsonName);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = schemaProperties,
            ["required"] = required,
            ["additionalProperties"] = false
        };

        return JsonSerializer.Serialize(schema);
    }

    private static string GetJsonPropertyName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
    }

    private static object BuildPropertySchema(Type propertyType)
    {
        if (propertyType == typeof(string))
        {
            return new Dictionary<string, object> { ["type"] = "string" };
        }

        if (propertyType == typeof(int) || propertyType == typeof(long))
        {
            return new Dictionary<string, object> { ["type"] = "integer" };
        }

        if (propertyType == typeof(float) || propertyType == typeof(double) || propertyType == typeof(decimal))
        {
            return new Dictionary<string, object> { ["type"] = "number" };
        }

        if (propertyType == typeof(bool))
        {
            return new Dictionary<string, object> { ["type"] = "boolean" };
        }

        if (propertyType.IsEnum)
        {
            return BuildEnumSchema(propertyType);
        }

        return new Dictionary<string, object> { ["type"] = "string" };
    }

    private static readonly JsonSerializerOptions EnumSerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static object BuildEnumSchema(Type enumType)
    {
        var values = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(v => JsonSerializer.Serialize(v, enumType, EnumSerializerOptions).Trim('"'))
            .ToList();

        return new Dictionary<string, object>
        {
            ["type"] = "string",
            ["enum"] = values
        };
    }
}
