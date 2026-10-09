// AppSettings.cs — Persisted user configuration for captcho.
// Defaults are sourced from ExportDefaults to keep a single source of truth.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// Identifies which persisted setting a <see cref="SettingsIssue"/> belongs to. Used by
/// <see cref="AppSettings.Validate"/> to attribute each issue so editable tabs can surface
/// inline per-field errors and the composed session can build a cross-tab status, all from
/// the one validation result.
/// </summary>
public enum SettingsField
{
    /// <summary>The <see cref="AppSettings.SaveLocation"/> field.</summary>
    SaveLocation,
    /// <summary>The <see cref="AppSettings.FilenameTemplate"/> field.</summary>
    FilenameTemplate,
    /// <summary>The <see cref="AppSettings.GlobalHotkeyEnabledStates"/> field.</summary>
    GlobalHotkeyEnabledStates,
    /// <summary>The <see cref="AppSettings.GlobalHotkeyBindings"/> field.</summary>
    GlobalHotkeyBindings,
    /// <summary>The <see cref="AppSettings.CaptureOptions"/> field.</summary>
    CaptureOptions,
    /// <summary>The <see cref="AppSettings.AnnotationSettings"/> field.</summary>
    AnnotationSettings,
}

/// <summary>
/// One validation issue produced by <see cref="AppSettings.Validate"/>: which field it
/// concerns and a user-displayable message. Carried verbatim into inline tab errors and
/// the composed session status so the message text has exactly one source.
/// </summary>
public sealed record SettingsIssue(SettingsField Field, string Message);

/// <summary>
/// User-configurable settings persisted to %LOCALAPPDATA%\captcho\settings.json.
/// Unknown properties from future versions are preserved during round-trip.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Directory where screenshots are saved by default.
    /// When null or empty, falls back to <see cref="ExportDefaults.DefaultSaveDirectory"/>.
    /// </summary>
    public string? SaveLocation { get; set; }

    /// <summary>
    /// Filename template supporting date/time, title, and sequence placeholders.
    /// When null or empty/whitespace, falls back to <see cref="ExportDefaults.DefaultFilenameTemplate"/>.
    /// </summary>
    public string? FilenameTemplate { get; set; }

    /// <summary>
    /// Per-Global-Hotkey enabled states, keyed by the capture route each Global Hotkey
    /// triggers (<see cref="GlobalHotkeyRoute"/>). Null (or an absent route) means the
    /// Global Hotkey is enabled — the default, matching the pre-existing "every global
    /// hotkey on" behavior. When non-null, only routes mapped to <c>true</c> are enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying by <see cref="GlobalHotkeyRoute"/> — a stable identity defined here in the
    /// capture layer — keeps this model free of UI and Win32 dependencies.
    /// The UI layer (<c>GlobalHotkeyRouteMap</c>) maps its Win32 hotkey ids to these
    /// routes; this layer never needs the UI map to interpret persisted state.
    /// </para>
    /// <para>
    /// The JSON property name is pinned to <c>hotkeyEnabledStates</c> so settings.json
    /// files written by earlier builds still load. On-disk keys are the route names
    /// (e.g. <c>currentMonitor</c>); settings written by the earlier int-id-keyed format
    /// (M002, stable ids 1–4) are migrated on read by the converter.
    /// </para>
    /// </remarks>
    [JsonPropertyName("hotkeyEnabledStates")]
    [JsonConverter(typeof(GlobalHotkeyEnabledStatesConverter))]
    public Dictionary<GlobalHotkeyRoute, bool>? GlobalHotkeyEnabledStates { get; set; }

    /// <summary>
    /// Per-Global-Hotkey key bindings, keyed by the capture route each Global
    /// Hotkey triggers (<see cref="GlobalHotkeyRoute"/>). Null (or an absent
    /// route) means the route's default combination from
    /// <see cref="GlobalHotkeyBindingDefaults"/> — the legacy hardcoded binding,
    /// which is how settings written before remapping migrated: nothing moves,
    /// behavior is preserved. When non-null, a recorded combination overrides
    /// the default for that route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying by <see cref="GlobalHotkeyRoute"/> keeps this model free of UI and
    /// Win32-id dependencies, mirroring <see cref="GlobalHotkeyEnabledStates"/>.
    /// The UI layer resolves route bindings into Win32 specs at registration
    /// time; this layer never needs the UI map to interpret persisted state.
    /// </para>
    /// <para>
    /// The JSON property name is pinned to <c>hotkeyBindings</c>; on-disk keys
    /// are the route names (e.g. <c>fullDesktop</c>) with nested
    /// <c>modifiers</c>/<c>virtualKey</c> objects, matching the enabled-states
    /// scheme so settings.json stays uniform.
    /// </para>
    /// </remarks>
    [JsonPropertyName("hotkeyBindings")]
    [JsonConverter(typeof(GlobalHotkeyBindingsConverter))]
    public Dictionary<GlobalHotkeyRoute, HotkeyBinding>? GlobalHotkeyBindings { get; set; }

    /// <summary>
    /// Persistent Capture-option defaults (mouse pointer, window decorations, window
    /// shadow) carried through the workflow into the managed/native Capture contract
    /// (spec user stories 27–30). The decoration/shadow dependency is enforced on
    /// read via <see cref="EffectiveCaptureOptions"/>, so a persisted inconsistent
    /// combination is silently reconciled rather than blocking Apply/OK.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see cref="CaptureOptions.WithDefaults"/> and is never null on a
    /// fresh instance. The JSON property name is pinned to <c>captureOptions</c>;
    /// older settings files written before this field shipped load with defaults
    /// (the JSON deserializer leaves the property at its initialized value).
    /// </para>
    /// <para>
    /// A nested object is used (rather than three flat boolean properties) so the
    /// three flags travel together as one unit and the dependency rule has a single
    /// owner (<see cref="CaptureOptions.Normalized"/>).
    /// </para>
    /// </remarks>
    [JsonPropertyName("captureOptions")]
    public CaptureOptions CaptureOptions { get; set; } = CaptureOptions.WithDefaults();

    /// <summary>
    /// Persistent defaults for the post-capture Annotation toolbar. Older settings files
    /// omit this object and retain the pen defaults through the property initializer and
    /// <see cref="EffectiveAnnotationSettings"/>.
    /// </summary>
    [JsonPropertyName("annotationSettings")]
    public AnnotationSettings AnnotationSettings { get; set; } = AnnotationSettings.WithDefaults();

    /// <summary>How long confirmed Selection geometry is retained.</summary>
    public RememberSelectionLifetime RememberSelection { get; set; } = RememberSelectionLifetime.Never;

    /// <summary>
    /// Geometry retained for <see cref="RememberSelectionLifetime.Always"/>. Session
    /// geometry is process state and is never written to Configuration.
    /// </summary>
    public RememberedSelectionGeometry? RememberedSelection { get; set; }

    /// <summary>
    /// Whether a captured Frame must pass through Annotation before Export.
    /// Older Configuration files omit this property and therefore retain the
    /// enabled-by-default post-capture workflow.
    /// </summary>
    public bool AnnotationEnabled { get; set; } = true;

    /// <summary>
    /// BCP-47 tag of the language Windows OCR recognizes with (e.g.,
    /// "en-US"), or null/empty to use the user's default OCR language. The
    /// tag is a selection, not a guarantee: at recognition time it is matched
    /// against the installed OCR language packs, and a tag whose pack was
    /// removed resolves to a retryable unsupported-language outcome rather
    /// than a silent fallback (spec #49).
    /// </summary>
    /// <remarks>
    /// The JSON property name is pinned to <c>ocrLanguageTag</c>; older
    /// settings files written before OCR shipped load with the null default
    /// (engine default language), matching the pre-OCR behavior.
    /// </remarks>
    [JsonPropertyName("ocrLanguageTag")]
    public string? OcrLanguageTag { get; set; }

    // Future properties can be added here. System.Text.Json will ignore
    // unknown properties on read and only serialize declared ones.

    /// <summary>
    /// Returns an <see cref="AppSettings"/> instance populated with production defaults
    /// from <see cref="ExportDefaults"/>.
    /// </summary>
    public static AppSettings WithDefaults() => new()
    {
        SaveLocation = ExportDefaults.DefaultSaveDirectory,
        FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        CaptureOptions = CaptureOptions.WithDefaults(),
        AnnotationSettings = AnnotationSettings.WithDefaults(),
        RememberSelection = RememberSelectionLifetime.Never,
        RememberedSelection = null,
        AnnotationEnabled = true,
        OcrLanguageTag = null,
    };

    /// <summary>
    /// Resolves the effective save location, falling back to the default if null/empty.
    /// </summary>
    [JsonIgnore]
    public string EffectiveSaveLocation =>
        string.IsNullOrWhiteSpace(SaveLocation)
            ? ExportDefaults.DefaultSaveDirectory
            : SaveLocation;

    /// <summary>
    /// Resolves the effective filename template, falling back to the default if null/empty/whitespace.
    /// </summary>
    [JsonIgnore]
    public string EffectiveFilenameTemplate =>
        string.IsNullOrWhiteSpace(FilenameTemplate)
            ? ExportDefaults.DefaultFilenameTemplate
            : FilenameTemplate;

    /// <summary>
    /// Resolves the effective Capture options, falling back to <see cref="CaptureOptions.WithDefaults"/>
    /// when the backing field is null (defensive — it is initialized non-null, but a
    /// future JSON shape or hand-edited settings file could leave it null on read)
    /// and reconciling the decoration/shadow dependency through
    /// <see cref="CaptureOptions.Normalized"/>. This is the value the workflow reads
    /// when composing Capture flags — there is no path where it observes an
    /// impossible combination (shadow on while decorations are off).
    /// </summary>
    [JsonIgnore]
    public CaptureOptions EffectiveCaptureOptions =>
        (CaptureOptions ?? CaptureOptions.WithDefaults()).Normalized();

    /// <summary>Resolves safe Annotation defaults for runtime use.</summary>
    [JsonIgnore]
    public AnnotationSettings EffectiveAnnotationSettings =>
        (AnnotationSettings ?? AnnotationSettings.WithDefaults()).Normalized();

    /// <summary>Returns a valid remembered-Selection lifetime for runtime use.</summary>
    [JsonIgnore]
    public RememberSelectionLifetime EffectiveRememberSelection =>
        Enum.IsDefined(RememberSelection)
            ? RememberSelection
            : RememberSelectionLifetime.Never;

    /// <summary>
    /// Resolves whether the Global Hotkey for the given capture route is enabled.
    /// A null states map (settings file predates per-Global-Hotkey toggles) or a missing
    /// entry means enabled, matching the pre-existing "every global hotkey on"
    /// behavior. An explicit <c>false</c> disables the Global Hotkey.
    /// </summary>
    public bool IsGlobalHotkeyEnabled(GlobalHotkeyRoute route) =>
        GlobalHotkeyEnabledStates is null
        || !GlobalHotkeyEnabledStates.TryGetValue(route, out bool enabled)
        || enabled;

    /// <summary>
    /// Resolves the effective key combination for the Global Hotkey of the given
    /// capture route. A null bindings map (settings written before remapping) or
    /// a missing entry resolves to the route's legacy default combination from
    /// <see cref="GlobalHotkeyBindingDefaults"/> — the pre-remap hardcoded
    /// binding — so existing Configuration migrates without losing behavior.
    /// </summary>
    public HotkeyBinding EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute route) =>
        GlobalHotkeyBindings is not null
        && GlobalHotkeyBindings.TryGetValue(route, out var binding)
            ? binding
            : GlobalHotkeyBindingDefaults.For(route);

    /// <summary>
    /// Validates the settings, returning a list of field-attributed issues found.
    /// An empty list means the settings are valid. This is the single source of truth
    /// for persisted-setting validity: every editable tab and the composed settings
    /// session gate Apply/OK enablement through this method, so a validation rule added
    /// here flows to both inline per-field errors and the cross-tab button gate without
    /// any caller-specific duplication.
    /// </summary>
    public IReadOnlyList<SettingsIssue> Validate()
    {
        var issues = new List<SettingsIssue>();

        // SaveLocation: if provided, must be an absolute path. Empty/whitespace is valid
        // (it resolves to the default on save via Normalized).
        if (!string.IsNullOrWhiteSpace(SaveLocation))
        {
            if (!Path.IsPathRooted(SaveLocation))
            {
                issues.Add(new SettingsIssue(
                    SettingsField.SaveLocation,
                    "Save location must be an absolute path."));
            }
        }

        // FilenameTemplate: if provided (non-null), must not be empty/whitespace after
        // trimming. A null template is valid (resolves to the default via Normalized);
        // an explicitly blank one is a user edit that cannot produce a filename.
        if (FilenameTemplate is not null && string.IsNullOrWhiteSpace(FilenameTemplate))
        {
            issues.Add(new SettingsIssue(
                SettingsField.FilenameTemplate,
                "Filename template must not be empty."));
        }

        // GlobalHotkeyEnabledStates: defensive structural check at the persistence
        // boundary. The typed API and JSON converter cannot introduce an undefined
        // route through normal use, so this rule is a no-op on well-formed state — but
        // it gives the field a real validation path (covered by AppSettings.Validate)
        // so the Global Hotkeys tab is never "valid by accident" and a future rule flows
        // straight through the composed Apply/OK gate.
        if (GlobalHotkeyEnabledStates is not null)
        {
            foreach (var route in GlobalHotkeyEnabledStates.Keys)
            {
                if (!Enum.IsDefined(typeof(GlobalHotkeyRoute), route))
                {
                    issues.Add(new SettingsIssue(
                        SettingsField.GlobalHotkeyEnabledStates,
                        "Global Hotkey enabled states contain an unknown route."));
                    break;
                }
            }
        }

        // GlobalHotkeyBindings: remapped combinations. A binding with no virtual
        // key cannot be registered or pressed — reject it at the persistence
        // boundary so Apply/OK gate on a real combination. The typed API and the
        // recorder cannot produce this through normal use (defensive rule).
        if (GlobalHotkeyBindings is not null)
        {
            foreach (var (route, binding) in GlobalHotkeyBindings)
            {
                if (!Enum.IsDefined(typeof(GlobalHotkeyRoute), route)
                    || binding.VirtualKey <= 0
                    || binding.Modifiers < 0)
                {
                    issues.Add(new SettingsIssue(
                        SettingsField.GlobalHotkeyBindings,
                        "Global Hotkey bindings must use a known route and a real key combination."));
                    break;
                }
            }
        }

        if (AnnotationSettings is null
            || !Enum.IsDefined(AnnotationSettings.DefaultTool)
            || AnnotationSettings.StrokeWidth < AnnotationSettings.MinimumStrokeWidth
            || AnnotationSettings.StrokeWidth > AnnotationSettings.MaximumStrokeWidth)
        {
            issues.Add(new SettingsIssue(
                SettingsField.AnnotationSettings,
                "Annotation defaults must use a supported tool and a stroke width from 1 through 64."));
        }

        return issues;
    }

    /// <summary>
    /// Returns a sanitized copy with null/empty/whitespace fields replaced by defaults.
    /// The Global Hotkey enabled-states dictionary is deep-copied (or kept null) and
    /// CaptureOptions is reconciled through its own Normalized (so the decoration/shadow
    /// dependency is enforced at this boundary), so the returned copy is fully
    /// independent of this instance.
    /// </summary>
    public AppSettings Normalized() => new()
    {
        SaveLocation = EffectiveSaveLocation,
        FilenameTemplate = EffectiveFilenameTemplate,
        GlobalHotkeyEnabledStates = GlobalHotkeyEnabledStates is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, bool>(GlobalHotkeyEnabledStates),
        GlobalHotkeyBindings = GlobalHotkeyBindings is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, HotkeyBinding>(GlobalHotkeyBindings),
        CaptureOptions = EffectiveCaptureOptions,
        AnnotationSettings = EffectiveAnnotationSettings,
        RememberSelection = EffectiveRememberSelection,
        RememberedSelection = EffectiveRememberSelection == RememberSelectionLifetime.Always
            ? RememberedSelection?.Normalized()
            : null,
        AnnotationEnabled = AnnotationEnabled,
        OcrLanguageTag = string.IsNullOrWhiteSpace(OcrLanguageTag) ? null : OcrLanguageTag.Trim(),
    };
}
