// GlobalHotkeyBindingsConverter.cs — JSON converter for per-Global-Hotkey bindings.
//
// Writes the on-disk map keyed by capture-route name (e.g. "fullDesktop"), each
// value an object with camel-cased "modifiers" and "virtualKey" — matching the
// rest of settings.json. Reads route names case-insensitively; unknown route
// keys throw JsonException so the ConfigurationService corruption path (backup +
// defaults) applies, mirroring GlobalHotkeyEnabledStatesConverter.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// Serializes <see cref="AppSettings.GlobalHotkeyBindings"/> keyed by capture-route
/// name with nested modifier/VK objects. Keeps the on-disk identity stable and
/// UI-independent (the same scheme as the enabled-states map).
/// </summary>
internal sealed class GlobalHotkeyBindingsConverter
    : JsonConverter<Dictionary<GlobalHotkeyRoute, HotkeyBinding>?>
{
    private static readonly JsonNamingPolicy CamelCase = JsonNamingPolicy.CamelCase;

    /// <inheritdoc/>
    public override Dictionary<GlobalHotkeyRoute, HotkeyBinding>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for hotkeyBindings.");

        var bindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            string key = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"Expected a binding object for hotkey key '{key}'.");

            var route = ResolveRoute(key);
            var binding = ReadBinding(ref reader, key);
            bindings[route] = binding;
        }

        return bindings;
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<GlobalHotkeyRoute, HotkeyBinding>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var (route, binding) in value)
        {
            writer.WritePropertyName(CamelCase.ConvertName(route.ToString()));
            writer.WriteStartObject();
            writer.WriteNumber("modifiers", binding.Modifiers);
            writer.WriteNumber("virtualKey", binding.VirtualKey);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static HotkeyBinding ReadBinding(ref Utf8JsonReader reader, string key)
    {
        int modifiers = 0;
        int virtualKey = 0;
        bool hasModifiers = false;
        bool hasVirtualKey = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            string property = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"Expected a number for '{property}' in hotkey key '{key}'.");

            long number = reader.GetInt64();
            if (number is < 0 or > int.MaxValue)
                throw new JsonException($"Value for '{property}' in hotkey key '{key}' is out of range.");

            if (property.Equals("modifiers", StringComparison.OrdinalIgnoreCase))
            {
                modifiers = (int)number;
                hasModifiers = true;
            }
            else if (property.Equals("virtualKey", StringComparison.OrdinalIgnoreCase))
            {
                virtualKey = (int)number;
                hasVirtualKey = true;
            }
            // Unknown properties are skipped (forward compatibility).
        }

        if (!hasModifiers || !hasVirtualKey)
            throw new JsonException($"Binding for hotkey key '{key}' must specify modifiers and virtualKey.");

        return new HotkeyBinding(modifiers, virtualKey);
    }

    /// <summary>
    /// Resolves an on-disk key to a capture route (case-insensitive route name).
    /// Unknown keys throw so corrupted files fall to the backup + defaults path.
    /// </summary>
    private static GlobalHotkeyRoute ResolveRoute(string key)
    {
        if (Enum.TryParse(key, ignoreCase: true, out GlobalHotkeyRoute route))
            return route;

        throw new JsonException($"Unknown global hotkey route '{key}'.");
    }
}
