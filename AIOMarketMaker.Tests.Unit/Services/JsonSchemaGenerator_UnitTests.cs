using System.Text.Json;
using System.Text.Json.Serialization;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.Tests.Unit.Services;

// --- Test types ---

public record SimpleStringRecord(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public record MixedTypesRecord(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("enabled")] bool Enabled);

public enum TestVerdict
{
    [JsonStringEnumMemberName("same")]
    Same,
    [JsonStringEnumMemberName("different")]
    Different
}

public record EnumRecord(
    [property: JsonPropertyName("verdict")] TestVerdict TestVerdict,
    [property: JsonPropertyName("reason")] string Reason);

public record DoubleRecord(
    [property: JsonPropertyName("confidence")] double Confidence);

// --- Tests ---

[TestFixture]
[Category("Unit")]
public class JsonSchemaGenerator_UnitTests
{
    [Test]
    public void Should_generate_object_type_at_root()
    {
        var schema = JsonSchemaGenerator.Generate<SimpleStringRecord>();
        using var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("object"));
    }

    [Test]
    public void Should_set_additionalProperties_false()
    {
        var schema = JsonSchemaGenerator.Generate<SimpleStringRecord>();
        using var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("additionalProperties").GetBoolean(), Is.False);
    }

    [Test]
    public void Should_include_all_properties_as_required()
    {
        var schema = JsonSchemaGenerator.Generate<SimpleStringRecord>();
        using var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;

        var required = root.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.That(required, Is.EquivalentTo(new[] { "name", "value" }));
    }

    [Test]
    public void Should_use_JsonPropertyName_for_property_names()
    {
        var schema = JsonSchemaGenerator.Generate<SimpleStringRecord>();
        using var doc = JsonDocument.Parse(schema);
        var props = doc.RootElement.GetProperty("properties");

        Assert.Multiple(() =>
        {
            Assert.That(props.TryGetProperty("name", out _), Is.True);
            Assert.That(props.TryGetProperty("value", out _), Is.True);
            Assert.That(props.TryGetProperty("Name", out _), Is.False, "Should use JsonPropertyName, not CLR name");
        });
    }

    [Test]
    public void Should_map_string_properties_to_string_type()
    {
        var schema = JsonSchemaGenerator.Generate<SimpleStringRecord>();
        using var doc = JsonDocument.Parse(schema);
        var nameType = doc.RootElement.GetProperty("properties")
            .GetProperty("name").GetProperty("type").GetString();

        Assert.That(nameType, Is.EqualTo("string"));
    }

    [Test]
    public void Should_map_int_to_integer_type()
    {
        var schema = JsonSchemaGenerator.Generate<MixedTypesRecord>();
        using var doc = JsonDocument.Parse(schema);
        var countType = doc.RootElement.GetProperty("properties")
            .GetProperty("count").GetProperty("type").GetString();

        Assert.That(countType, Is.EqualTo("integer"));
    }

    [Test]
    public void Should_map_float_to_number_type()
    {
        var schema = JsonSchemaGenerator.Generate<MixedTypesRecord>();
        using var doc = JsonDocument.Parse(schema);
        var scoreType = doc.RootElement.GetProperty("properties")
            .GetProperty("score").GetProperty("type").GetString();

        Assert.That(scoreType, Is.EqualTo("number"));
    }

    [Test]
    public void Should_map_double_to_number_type()
    {
        var schema = JsonSchemaGenerator.Generate<DoubleRecord>();
        using var doc = JsonDocument.Parse(schema);
        var confType = doc.RootElement.GetProperty("properties")
            .GetProperty("confidence").GetProperty("type").GetString();

        Assert.That(confType, Is.EqualTo("number"));
    }

    [Test]
    public void Should_map_bool_to_boolean_type()
    {
        var schema = JsonSchemaGenerator.Generate<MixedTypesRecord>();
        using var doc = JsonDocument.Parse(schema);
        var enabledType = doc.RootElement.GetProperty("properties")
            .GetProperty("enabled").GetProperty("type").GetString();

        Assert.That(enabledType, Is.EqualTo("boolean"));
    }

    [Test]
    public void Should_map_enum_to_string_with_enum_values()
    {
        var schema = JsonSchemaGenerator.Generate<EnumRecord>();
        using var doc = JsonDocument.Parse(schema);
        var verdictProp = doc.RootElement.GetProperty("properties").GetProperty("verdict");

        Assert.Multiple(() =>
        {
            Assert.That(verdictProp.GetProperty("type").GetString(), Is.EqualTo("string"));

            var enumValues = verdictProp.GetProperty("enum").EnumerateArray()
                .Select(e => e.GetString())
                .ToList();
            Assert.That(enumValues, Is.EquivalentTo(new[] { "same", "different" }));
        });
    }

    [Test]
    public void Should_produce_valid_json()
    {
        var schema = JsonSchemaGenerator.Generate<MixedTypesRecord>();

        Assert.DoesNotThrow(() => JsonDocument.Parse(schema));
    }

    [Test]
    public void Should_produce_valid_json_for_enum_type()
    {
        var schema = JsonSchemaGenerator.Generate<EnumRecord>();

        Assert.DoesNotThrow(() => JsonDocument.Parse(schema));
    }
}
