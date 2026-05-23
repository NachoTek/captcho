# M002 Context Draft: Settings UI

## Vision

Replace basic JSON configuration with a full-featured settings dialog that users can access through the main UI. Configure hotkeys, export options, default paths, filename templates, and other preferences through a polished settings interface instead of manual JSON editing.

## Scope

### What's In This Milestone

1. **Settings Window** — Open a standalone settings dialog from main window
2. **Configuration Categories** — Organized settings with tabs or sections
   - **General** — Default save location, filename template, auto-save on capture
   - **Hotkeys** — View/configure the four global hotkeys, option to disable individually
   - **Export** — Default format (PNG/JPEG), JPEG quality slider, default action (save/clipboard)
   - **Interface** — Theme, window behavior, other UI preferences
3. **Persistence Layer** — Load/save settings through existing ConfigurationService
4. **Reset to Defaults** — Button to restore default settings
5. **Validation** — Settings validation with error display

### What's Out of This Milestone

- Drawing/editing tools on screenshots (deferred to future milestone)
- Export format JPEG implementation (settings UI only, actual encoding in future)
- Advanced capture features (rolling capture, window selection list, etc.)
- Cross-platform support

## Success Criteria

- [ ] Settings window opens from main UI with keyboard shortcut (F4 or Ctrl+,)
- [ ] Settings window shows current configuration values loaded from JSON
- [ ] Changes in settings window persist to JSON on Apply/OK
- [ ] Reset to Defaults restores all settings to application defaults
- [ ] Invalid settings show validation errors and prevent save
- [ ] Settings window shows/hides cleanly (same position between opens)
- [ ] Hotkey configuration displays current bindings
- [ ] All settings categories (General, Hotkeys, Export, Interface) render correctly

## Key Technical Decisions

- **Settings Window Type** — Dialog window with OK/Cancel/Apply buttons, separate from main window
- **Window Management** — Single instance pattern, reuse existing window if open
- **Data Binding** — Bind settings controls directly to AppSettings model with change tracking
- **Validation Timing** — Validate on Apply, show errors inline, prevent invalid states
- **Cancel Behavior** — Cancel discards unsaved changes, Apply saves immediately

## Integration Points

- Uses `ConfigurationService` from S10 (load/save JSON)
- Uses `AppSettings` model from S10 (settings data)
- Hooks into `MainWindow` for settings menu item and open command
- May need `HotkeyManager` read-only access for current hotkey bindings

## Dependencies

- Requires S10 (ConfigurationService) to be complete
- Uses existing `MainWindow` structure from earlier slices
- No new capture engine changes needed
