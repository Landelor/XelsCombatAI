using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XelsCombatAI.Runtime;

internal static class CombatLogPrivacy
{
    public static JsonElement RedactSettings(JsonElement settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSettingsElement(writer, settings, propertyName: null);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSettingsElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName != null && IsSensitiveSettingsProperty(propertyName))
        {
            writer.WriteStringValue("<redacted>");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSettingsElement(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSettingsElement(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                writer.WriteStringValue(ShouldRedactStringValue(value) ? "<redacted>" : value);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveSettingsProperty(string propertyName)
    {
        var normalized = NormalizePropertyName(propertyName);
        return normalized.EndsWith("name", StringComparison.Ordinal) ||
               normalized.Contains("path", StringComparison.Ordinal) ||
               normalized.Contains("directory", StringComparison.Ordinal) ||
               normalized.Contains("folder", StringComparison.Ordinal) ||
               normalized.Contains("url", StringComparison.Ordinal) ||
               normalized.Contains("uri", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("accesskey", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("webhook", StringComparison.Ordinal) ||
               normalized.Contains("character", StringComparison.Ordinal) ||
               normalized.Contains("world", StringComparison.Ordinal) ||
               normalized.Contains("server", StringComparison.Ordinal) ||
               normalized.Contains("account", StringComparison.Ordinal) ||
               normalized.Contains("user", StringComparison.Ordinal) ||
               normalized.Contains("email", StringComparison.Ordinal);
    }

    private static bool ShouldRedactStringValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains('@', StringComparison.Ordinal) ||
               value.Contains("://", StringComparison.Ordinal) ||
               value.StartsWith("/", StringComparison.Ordinal) ||
               value.StartsWith("~/", StringComparison.Ordinal) ||
               value.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static string NormalizePropertyName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}

internal sealed class CombatLogAnonymizer
{
    private readonly Dictionary<ulong, ulong> transientIds = [];
    private ulong nextTransientId = 1;

    public string AnonymizeRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            this.WriteElement(writer, document.RootElement, propertyName: null);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName != null && IsTransientIdentifierProperty(propertyName))
        {
            this.WriteAnonymizedIdentifierValue(writer, element);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    this.WriteElement(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    this.WriteElement(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void WriteAnonymizedIdentifierValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when TryReadUInt64(element, out var id):
                writer.WriteNumberValue(this.MapTransientId(id));
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    this.WriteAnonymizedIdentifierValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue("<redacted-id>");
                break;
        }
    }

    private ulong MapTransientId(ulong id)
    {
        if (id == 0)
        {
            return 0;
        }

        if (this.transientIds.TryGetValue(id, out var mapped))
        {
            return mapped;
        }

        mapped = this.nextTransientId++;
        this.transientIds[id] = mapped;
        return mapped;
    }

    private static bool IsTransientIdentifierProperty(string propertyName)
    {
        return propertyName.EndsWith("ObjectId", StringComparison.Ordinal) ||
               propertyName.EndsWith("ObjectIds", StringComparison.Ordinal) ||
               propertyName.EndsWith("EntityId", StringComparison.Ordinal) ||
               propertyName.EndsWith("EntityIds", StringComparison.Ordinal) ||
               propertyName.EndsWith("ActorId", StringComparison.Ordinal) ||
               propertyName.EndsWith("ActorIds", StringComparison.Ordinal) ||
               propertyName.Equals("PrimaryTargetId", StringComparison.Ordinal) ||
               propertyName.Equals("DominantTargetIds", StringComparison.Ordinal) ||
               propertyName.Equals("StragglerTargetIds", StringComparison.Ordinal);
    }

    private static bool TryReadUInt64(JsonElement element, out ulong value)
    {
        if (element.TryGetUInt64(out value))
        {
            return true;
        }

        if (element.TryGetInt64(out var signed) && signed >= 0)
        {
            value = (ulong)signed;
            return true;
        }

        value = 0;
        return false;
    }
}
