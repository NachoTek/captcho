// SettingsView.cs — Immutable view model the Settings window binds against.
//
// Every SettingsSession action (open, edit, Apply/OK/Cancel/Reset) returns a fresh
// SettingsView carrying everything the code-behind needs to rebind: the editable
// General-tab fields (flat, per the #8 design), the Global Hotkeys rows, the
// editable Capture and Annotation tab content, nested read-only tab content,
// composed button gating, and an inline status message. The view is a
// record so value-equality behaves predictably for callers that want to diff.
// Read-only tab content is nested so it stays self-contained as it grows; the
// small, fixed General field set stays flat to keep binding sites simple.

using System.Collections.Generic;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Editable content for the Export tab, sourced from
/// <see cref="ExportTabSettings"/>. Carries the working format, JPEG quality,
/// the format-derived extension (display only), the quality control's
/// availability (false for PNG), and the inline quality validation error.
/// The code-behind binds these to the format ComboBox and quality slider.
/// </summary>
public sealed record ExportTabContent(
    ExportImageFormat Format,
    int JpegQuality,
    bool JpegQualityAvailable,
    string FileExtension,
    string? JpegQualityError);

/// <summary>
/// Read-only content for the Interface tab, sourced from <see cref="InterfaceTabSettings"/>.
/// </summary>
public sealed record InterfaceTabContent(
    string Heading,
    string Message,
    string PlannedSettingsNote);

/// <summary>
/// Editable content for the Capture tab, sourced from <see cref="CaptureTabSettings"/>.
/// Carries the three Capture-option flags plus the effective shadow value after the
/// decoration/shadow dependency has been reconciled at edit time. The code-behind
/// binds these to checkbox controls; the decoration/shadow dependency is enforced
/// by the underlying <see cref="CaptureTabSettings"/> so the bound view never shows
/// an impossible combination.
/// </summary>
public sealed record CaptureTabContent(
    bool IncludePointer,
    bool IncludeDecorations,
    bool IncludeShadow,
    RememberSelectionLifetime RememberSelection,
    bool AnnotationEnabled,
    string? OcrLanguageTag);

/// <summary>Editable defaults shown by the Annotation Settings tab.</summary>
public sealed record AnnotationTabContent(
    AnnotationTool DefaultTool,
    AnnotationColor PenColor,
    int StrokeWidth,
    string? Error);

/// <summary>
/// Immutable snapshot of everything the Settings window code-behind binds. Produced
/// by <see cref="SettingsSession"/> on open and after every edit or session verb.
/// Failures surface as <see cref="StatusMessage"/> + <see cref="StatusIsError"/>
/// (and per-field/per-row errors), never as exceptions.
/// </summary>
public sealed record SettingsView(
    // ── General tab (editable) ──
    string SaveLocation,
    string FilenameTemplate,
    string FilenameTemplatePreview,
    string? SaveLocationError,
    string? FilenameTemplateError,

    // ── Global Hotkeys tab (editable) ──
    IReadOnlyList<GlobalHotkeyRow> GlobalHotkeyRows,

    // ── Capture tab (editable) ──
    CaptureTabContent Capture,

    // ── Annotation tab (editable) ──
    AnnotationTabContent Annotation,

    // ── Export tab (editable) ──
    ExportTabContent Export,

    // ── Read-only tabs ──
    InterfaceTabContent Interface,

    // ── Composed gating across all editable tabs ──
    bool CanApply,
    bool CanConfirm,
    bool CanCancel,
    bool CanReset,

    // ── Status ──
    string? StatusMessage,
    bool StatusIsError,
    bool ShouldClose);
