using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulsar.Manifests;

/// <summary>Arguments are either a string or <c>{ "if": "var.x", "then": [ ... ] }</c>.</summary>
internal sealed class ArgumentTemplateConverter : JsonConverter<ArgumentTemplate>
{
    public override ArgumentTemplate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentText(reader.GetString()!);
            case JsonTokenType.StartObject:
                string? condition = null;
                List<ArgumentTemplate>? then = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString();
                    reader.Read();
                    switch (name)
                    {
                        case "if":
                            condition = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("'if' must be a string.");
                            break;
                        case "then":
                            then = JsonSerializer.Deserialize<List<ArgumentTemplate>>(ref reader, options) ?? throw new JsonException("'then' must be an array.");
                            break;
                        default:
                            throw new JsonException($"Unknown argument property '{name}'.");
                    }
                }

                return condition is not null && then is not null
                    ? new ArgumentCondition(condition, then)
                    : throw new JsonException("A conditional argument needs 'if' and 'then'.");
            default:
                throw new JsonException("An argument must be a string or a conditional object.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentTemplate value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ArgumentText text:
                writer.WriteStringValue(text.Text);
                break;
            case ArgumentCondition condition:
                writer.WriteStartObject();
                writer.WriteString("if", condition.If);
                writer.WritePropertyName("then");
                JsonSerializer.Serialize(writer, condition.Then, options);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown argument template {value?.GetType().Name}.");
        }
    }
}
