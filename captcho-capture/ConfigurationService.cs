// ConfigurationService.cs — Loads and saves AppSettings from/to a local JSON file.
// Uses temp+replace for atomic writes and backs up corrupted files.
// Returns structured results instead of throwing for expected failures.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace captcho.Capture;

// ── Result Types ──────────────────────────────────────────────────────

/// <summary>
/// Result of a configuration load operation.
/// Check <see cref="Success"/> before using <see cref="Settings"/>.
/// </summary>
public sealed class ConfigurationLoadResult
{
    /// <summary>Whether the load succeeded (possibly with defaults after corruption).</summary>
    public bool Success { get; init; }

    /// <summary>Phase where a failure occurred (e.g. "ReadFile", "Deserialize").</summary>
    public string? Phase { get; init; }

    /// <summary>Human-readable sanitized error message (no stack traces or secrets).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The loaded settings, or defaults if the file was missing/corrupt.</summary>
    public AppSettings Settings { get; init; } = AppSettings.WithDefaults();

    /// <summary>Full path to the settings file.</summary>
    public string ConfigurationPath { get; init; } = string.Empty;

    /// <summary>Full path to the backup file created for a corrupted settings file, if any.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Whether the file was missing and defaults were used.</summary>
    public bool UsedDefaults { get; init; }
}

/// <summary>
/// Result of a configuration save operation.
/// Check <see cref="Success"/> before relying on persistence.
/// </summary>
public sealed class ConfigurationSaveResult
{
    /// <summary>Whether the save succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Phase where a failure occurred (e.g. "CreateDirectory", "WriteTemp", "Move").</summary>
    public string? Phase { get; init; }

    /// <summary>Human-readable sanitized error message (no stack traces or secrets).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Full path to the settings file.</summary>
    public string ConfigurationPath { get; init; } = string.Empty;

    /// <summary>Full path to the temp file used during atomic write.</summary>
    public string? TempPath { get; init; }
}

// ── Service ───────────────────────────────────────────────────────────

/// <summary>
/// Loads and saves <see cref="AppSettings"/> from/to a JSON settings file.
/// Supports path injection for testing via the constructor.
/// Production default: %LOCALAPPDATA%\captcho\settings.json
/// Not sealed so tests can substitute a forced save result by overriding
/// <see cref="Save(AppSettings)"/> (the class stays the single seam, no port needed).
/// </summary>
public class ConfigurationService
{
    private const string SettingsFileName = "settings.json";
    private const string BackupExtension = ".backup";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Unknown properties from future versions are ignored (not an error)
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    private readonly string _configurationDirectory;
    private readonly string _configurationPath;

    /// <summary>
    /// Creates a ConfigurationService using the production path
    /// (%LOCALAPPDATA%\captcho\settings.json).
    /// </summary>
    public ConfigurationService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "captcho"))
    {
    }

    /// <summary>
    /// Creates a ConfigurationService with an explicit configuration directory.
    /// Use this for testing to inject a temporary directory.
    /// </summary>
    /// <param name="configurationDirectory">Directory where settings.json will be stored.</param>
    public ConfigurationService(string configurationDirectory)
    {
        _configurationDirectory = configurationDirectory;
        _configurationPath = Path.Combine(configurationDirectory, SettingsFileName);
    }

    /// <summary>
    /// Full path to the settings file (exposed for diagnostics).
    /// </summary>
    public string ConfigurationPath => _configurationPath;

    /// <summary>
    /// Loads settings from the JSON file. Returns defaults if the file is missing.
    /// If the file contains invalid JSON, backs up the corrupted file and returns defaults.
    /// Never throws for expected file/JSON failures.
    /// </summary>
    public ConfigurationLoadResult Load()
    {
        // 1. Check if file exists
        if (!File.Exists(_configurationPath))
        {
            return new ConfigurationLoadResult
            {
                Success = true,
                Settings = AppSettings.WithDefaults(),
                ConfigurationPath = _configurationPath,
                UsedDefaults = true,
            };
        }

        // 2. Read file content
        string json;
        try
        {
            json = File.ReadAllText(_configurationPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigurationLoadResult
            {
                Success = false,
                Phase = "ReadFile",
                ErrorMessage = SanitizeErrorMessage(ex),
                Settings = AppSettings.WithDefaults(),
                ConfigurationPath = _configurationPath,
                UsedDefaults = true,
            };
        }

        // 3. Deserialize
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Back up the corrupted file and return defaults
            string? backupPath = BackupCorruptedFile();
            return new ConfigurationLoadResult
            {
                Success = true,
                Phase = "Deserialize",
                ErrorMessage = "Settings file contained invalid JSON. A backup was created and defaults were loaded.",
                Settings = AppSettings.WithDefaults(),
                ConfigurationPath = _configurationPath,
                BackupPath = backupPath,
                UsedDefaults = true,
            };
        }

        // 4. Handle null deserialization result (valid JSON but wrong shape)
        if (settings is null)
        {
            string? backupPath = BackupCorruptedFile();
            return new ConfigurationLoadResult
            {
                Success = true,
                Phase = "Deserialize",
                ErrorMessage = "Settings file was empty or had unexpected structure. Defaults were loaded.",
                Settings = AppSettings.WithDefaults(),
                ConfigurationPath = _configurationPath,
                BackupPath = backupPath,
                UsedDefaults = true,
            };
        }

        return new ConfigurationLoadResult
        {
            Success = true,
            Settings = settings,
            ConfigurationPath = _configurationPath,
            UsedDefaults = false,
        };
    }

    /// <summary>
    /// Saves settings to the JSON file using an atomic temp+replace strategy.
    /// Never throws for expected file/IO failures. Virtual so tests can substitute
    /// a forced result without a port adapter (the class stays the single seam).
    /// </summary>
    public virtual ConfigurationSaveResult Save(AppSettings settings)
    {
        // 1. Ensure directory exists
        try
        {
            Directory.CreateDirectory(_configurationDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigurationSaveResult
            {
                Success = false,
                Phase = "CreateDirectory",
                ErrorMessage = SanitizeErrorMessage(ex),
                ConfigurationPath = _configurationPath,
            };
        }

        // 2. Serialize to JSON
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        // 3. Write to temp file first
        string tempPath = _configurationPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            return new ConfigurationSaveResult
            {
                Success = false,
                Phase = "WriteTemp",
                ErrorMessage = SanitizeErrorMessage(ex),
                ConfigurationPath = _configurationPath,
                TempPath = tempPath,
            };
        }

        // 4. Replace the original file with the temp file
        try
        {
            File.Move(tempPath, _configurationPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            return new ConfigurationSaveResult
            {
                Success = false,
                Phase = "Move",
                ErrorMessage = SanitizeErrorMessage(ex),
                ConfigurationPath = _configurationPath,
                TempPath = tempPath,
            };
        }

        return new ConfigurationSaveResult
        {
            Success = true,
            ConfigurationPath = _configurationPath,
            TempPath = tempPath,
        };
    }

    /// <summary>
    /// Creates a backup of the corrupted settings file by renaming it with a .backup extension.
    /// If a .backup already exists, appends a numeric suffix.
    /// Returns the backup path, or null if backup failed.
    /// </summary>
    private string? BackupCorruptedFile()
    {
        try
        {
            if (!File.Exists(_configurationPath))
                return null;

            string backupPath = _configurationPath + BackupExtension;
            int suffix = 1;
            while (File.Exists(backupPath))
            {
                backupPath = $"{_configurationPath}.{suffix}{BackupExtension}";
                suffix++;
            }

            File.Move(_configurationPath, backupPath);
            return backupPath;
        }
        catch
        {
            // Backup failure is non-fatal; return null
            return null;
        }
    }

    /// <summary>
    /// Sanitizes an exception message for safe display — removes full paths
    /// and stack traces from user-facing error messages.
    /// </summary>
    private static string SanitizeErrorMessage(Exception ex)
    {
        // Strip any path-like segments (e.g. C:\Users\...) for security
        string message = ex.Message ?? "Unknown error";
        // Remove newlines that might contain stack trace fragments
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Trim();
    }

    /// <summary>
    /// Attempts to delete a file, ignoring failures.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
