// GlobalHotkeyEnabledStatesConverter.cs — JSON converter for per-Global-Hotkey enabled state.
//
// Writes the on-disk map keyed by capture-route name (e.g. "currentMonitor") so the
// on-disk identity is the stable, UI-independent GlobalHotkeyRoute defined in this
// layer. Reads both the current route-name format and the legacy M002 wire format
// (keys were the Win32 stable hotkey ids 1–4) by migrating numeric keys through a
// frozen historical table. The migration is self-contained — it does not reference the
// UI-layer GlobalHotkeyRouteMap.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// Serializes <see cref="AppSettings.GlobalHotkeyEnabledStates"/> keyed by capture-route
/// name, and deserializes both the current route-name format and the legacy M002 int-id
/// format. Keeps the on-disk identity stable and UI-independent while preserving
/// backward compatibility with settings files written by earlier builds.
/// </summary>
internal sealed class GlobalHotkeyEnabledStatesConverter
    : JsonConverter<Dictionary<GlobalHotkeyRoute, bool>?>
{
    /// <summary>
    /// Frozen mapping from the legacy M002 on-disk keys (the Win32 stable hotkey ids as
    /// strings, 1–4) to the capture routes they triggered. Used only to migrate settings
    /// files written by the earlier int-keyed format; current builds write route names.
    /// </summary>
    private static readonly Dictionary<string, GlobalHotkeyRoute> LegacyIdToRoute = new()
    {
        ["1"] = GlobalHotkeyRoute.CurrentMonitor,
        ["2"] = GlobalHotkeyRoute.ActiveWindow,
        ["3"] = GlobalHotkeyRoute.FullDesktop,
        ["4"] = GlobalHotkeyRoute.RectangularRegion,
    };

    /// <summary>Camel-cases route names so on-disk keys match the rest of settings.json.</summary>
    private static readonly JsonNamingPolicy CamelCase = JsonNamingPolicy.CamelCase;

    /// <inheritdoc/>
    public override Dictionary<GlobalHotkeyRoute, bool>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for hotkeyEnabledStates.");

        var states = new Dictionary<GlobalHotkeyRoute, bool>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            string key = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.True && reader.TokenType != JsonTokenType.False)
                throw new JsonException($"Expected a boolean value for hotkey key '{key}'.");

            bool value = reader.GetBoolean();
            states[ResolveRoute(key)] = value;
        }

        return states;
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<GlobalHotkeyRoute, bool>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var (route, enabled) in value)
        {
            string key = CamelCase.ConvertName(route.ToString());
            writer.WriteBoolean(key, enabled);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Resolves an on-disk key to a capture route. Numeric keys are the legacy M002
    /// stable-id format (migrated through the frozen table); any other key is parsed as a
    /// route name (case-insensitive).
    /// </summary>
    private static GlobalHotkeyRoute ResolveRoute(string key)
    {
        if (int.TryParse(key, out _))
        {
            if (LegacyIdToRoute.TryGetValue(key, out var legacyRoute))
                return legacyRoute;
            throw new JsonException($"Unknown legacy global hotkey id '{key}'.");
        }

        if (Enum.TryParse(key, ignoreCase: true, out GlobalHotkeyRoute route))
            return route;

        throw new JsonException($"Unknown global hotkey route '{key}'.");
    }
}
