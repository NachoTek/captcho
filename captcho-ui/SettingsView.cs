// SettingsView.cs — Immutable view model the Settings window binds against.
//
// Every SettingsSession action (open, edit, Apply/OK/Cancel/Reset) returns a fresh
// SettingsView carrying everything the code-behind needs to rebind: the editable
// General-tab fields (flat, per the #8 design), the Global Hotkeys rows, nested read-only tab
// content, composed button gating, and an inline status message. The view is a record so
// value-equality behaves predictably for callers that want to diff. Read-only tab content
// is nested so it stays self-contained as it grows; the small, fixed General field set
// stays flat to keep binding sites simple.

using System.Collections.Generic;

namespace captcho.UI;

/// <summary>
/// Read-only content for the Export tab, sourced from <see cref="ExportTabSettings"/>.
/// Carried through the view so the code-behind binds one object.
/// </summary>
public sealed record ExportTabContent(
    string Heading,
    string FormatName,
    string FileExtension,
    string FormatDescription,
    string PlannedFormatsNote);

/// <summary>
/// Read-only content for the Interface tab, sourced from <see cref="InterfaceTabSettings"/>.
/// </summary>
public sealed record InterfaceTabContent(
    string Heading,
    string Message,
    string PlannedSettingsNote);

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

    // ── Read-only tabs ──
    ExportTabContent Export,
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
