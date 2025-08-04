using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using ReadyCrafter.Models;

namespace ReadyCrafter.Services;

/// <summary>
/// Manages plugin configuration and settings persistence using Dalamud's configuration system.
/// Provides thread-safe access to all plugin settings with validation, defaults, and change notifications.
/// </summary>
public sealed class SettingsManager : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ReaderWriterLockSlim _settingsLock = new();
    private PluginConfiguration _settings;
    private bool _disposed = false;

    /// <summary>
    /// Event raised when any setting value changes.
    /// </summary>
    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    /// <summary>
    /// Event raised when settings are loaded or reloaded.
    /// </summary>
    public event EventHandler? SettingsLoaded;

    /// <summary>
    /// Thread-safe access to current settings.
    /// </summary>
    public PluginConfiguration Settings
    {
        get
        {
            _settingsLock.EnterReadLock();
            try
            {
                return _settings.Clone();
            }
            finally
            {
                _settingsLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Initialize the settings manager with Dalamud plugin interface.
    /// </summary>
    public SettingsManager(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        
        try
        {
            LoadSettings();
        }
        catch (Exception ex)
        {
            // Log error but continue with defaults - don't crash the plugin
            // Notification temporarily disabled due to API changes
            // $"Failed to load ReadyCrafter settings: {ex.Message}. Using defaults."
            
            _settings = PluginConfiguration.CreateDefault();
            SaveSettings(); // Save valid defaults
        }
    }

    /// <summary>
    /// Load settings from Dalamud configuration system.
    /// </summary>
    private void LoadSettings()
    {
        _settingsLock.EnterWriteLock();
        try
        {
            var loaded = _pluginInterface.GetPluginConfig() as PluginConfiguration;
            
            if (loaded == null)
            {
                _settings = PluginConfiguration.CreateDefault();
            }
            else
            {
                _settings = loaded;
                
                // Perform version migration if needed
                if (_settings.Version < PluginConfiguration.CurrentVersion)
                {
                    MigrateSettings(_settings);
                }
                
                // Validate and fix any invalid settings
                _settings.Validate();
            }
            
            SettingsLoaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _settingsLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Save current settings to Dalamud configuration system.
    /// </summary>
    public void SaveSettings()
    {
        if (_disposed) return;

        _settingsLock.EnterReadLock();
        try
        {
            var settingsToSave = _settings.Clone();
            settingsToSave.Validate(); // Ensure settings are valid before saving
            
            _pluginInterface.SavePluginConfig(settingsToSave);
        }
        finally
        {
            _settingsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Update a setting value with validation and change notification.
    /// </summary>
    public void UpdateSetting<T>(string settingName, T newValue, bool saveImmediately = true)
    {
        if (_disposed) return;

        _settingsLock.EnterWriteLock();
        try
        {
            var oldValue = GetSettingValue<T>(settingName);
            
            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                SetSettingValue(settingName, newValue);
                _settings.Validate(); // Validate after change
                
                if (saveImmediately)
                {
                    // Release write lock temporarily to avoid deadlock
                    _settingsLock.ExitWriteLock();
                    try
                    {
                        SaveSettings();
                    }
                    finally
                    {
                        _settingsLock.EnterWriteLock();
                    }
                }
                
                SettingChanged?.Invoke(this, new SettingChangedEventArgs(settingName, oldValue, newValue));
            }
        }
        finally
        {
            _settingsLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get a setting value by name with type safety.
    /// </summary>
    public T GetSetting<T>(string settingName, T defaultValue = default(T))
    {
        _settingsLock.EnterReadLock();
        try
        {
            return GetSettingValue<T>(settingName) ?? defaultValue;
        }
        finally
        {
            _settingsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Reset all settings to defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        if (_disposed) return;

        _settingsLock.EnterWriteLock();
        try
        {
            var oldSettings = _settings.Clone();
            _settings = PluginConfiguration.CreateDefault();
            
            SaveSettings();
            SettingChanged?.Invoke(this, new SettingChangedEventArgs("*", oldSettings, _settings));
            SettingsLoaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _settingsLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Import settings from a JSON string.
    /// </summary>
    public bool ImportSettings(string jsonData)
    {
        if (_disposed) return false;

        try
        {
            var importedSettings = JsonConvert.DeserializeObject<PluginConfiguration>(jsonData);
            if (importedSettings == null) return false;

            _settingsLock.EnterWriteLock();
            try
            {
                var oldSettings = _settings.Clone();
                
                // Migrate if necessary
                if (importedSettings.Version < PluginConfiguration.CurrentVersion)
                {
                    MigrateSettings(importedSettings);
                }
                
                importedSettings.Validate();
                _settings = importedSettings;
                
                SaveSettings();
                SettingChanged?.Invoke(this, new SettingChangedEventArgs("*", oldSettings, _settings));
                SettingsLoaded?.Invoke(this, EventArgs.Empty);
                
                return true;
            }
            finally
            {
                _settingsLock.ExitWriteLock();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Export current settings as JSON string.
    /// </summary>
    public string ExportSettings()
    {
        _settingsLock.EnterReadLock();
        try
        {
            return JsonConvert.SerializeObject(_settings, Formatting.Indented);
        }
        finally
        {
            _settingsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Migrate settings from older versions to current version.
    /// </summary>
    private void MigrateSettings(PluginConfiguration settings)
    {
        // Version 1 -> 2: Added recursion depth setting
        if (settings.Version < 2)
        {
            // Set default recursion depth based on previous behavior
            settings.RecursionDepth = settings.ScanOptions.ResolveIntermediateCrafts ? 1 : 0;
        }

        // Version 2 -> 3: Restructured UI settings
        if (settings.Version < 3)
        {
            // Migrate any old UI position data if it existed
            // This is a placeholder for future migrations
        }

        // Always update to current version after migration
        settings.Version = PluginConfiguration.CurrentVersion;
    }

    /// <summary>
    /// Get setting value using reflection.
    /// </summary>
    private T GetSettingValue<T>(string settingName)
    {
        var property = typeof(PluginConfiguration).GetProperty(settingName);
        if (property != null && property.CanRead)
        {
            var value = property.GetValue(_settings);
            if (value is T typedValue)
                return typedValue;
        }
        return default(T);
    }

    /// <summary>
    /// Set setting value using reflection.
    /// </summary>
    private void SetSettingValue<T>(string settingName, T value)
    {
        var property = typeof(PluginConfiguration).GetProperty(settingName);
        if (property != null && property.CanWrite && property.PropertyType.IsAssignableFrom(typeof(T)))
        {
            property.SetValue(_settings, value);
        }
    }

    /// <summary>
    /// Dispose of resources and save final settings.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            SaveSettings();
        }
        catch
        {
            // Ignore save errors during disposal
        }
        finally
        {
            _settingsLock?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Event arguments for setting change notifications.
/// </summary>
public sealed class SettingChangedEventArgs : EventArgs
{
    public string SettingName { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }

    public SettingChangedEventArgs(string settingName, object? oldValue, object? newValue)
    {
        SettingName = settingName;
        OldValue = oldValue;
        NewValue = newValue;
    }
}

/// <summary>
/// Main plugin configuration class implementing Dalamud's IPluginConfiguration.
/// Contains all settings organized by category with validation and defaults.
/// </summary>
[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    /// <summary>
    /// Current configuration version for migration support.
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Configuration version for automatic migration.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    #region General Settings

    /// <summary>
    /// Whether the plugin is enabled and should perform background scanning.
    /// </summary>
    public bool PluginEnabled { get; set; } = true;

    /// <summary>
    /// Whether hotkey binding is enabled.
    /// </summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Virtual key code for the toggle hotkey.
    /// Default is F12 (123).
    /// </summary>
    public int HotkeyCode { get; set; } = 123; // F12

    /// <summary>
    /// Modifier keys required for hotkey (Ctrl, Shift, Alt combinations).
    /// </summary>
    public ModifierKeys HotkeyModifiers { get; set; } = ModifierKeys.None;

    /// <summary>
    /// Automatic inventory scan interval in milliseconds.
    /// </summary>
    public int ScanIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Whether to show notifications for important events.
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Language preference for localization.
    /// </summary>
    public string Language { get; set; } = "en";

    #endregion

    #region Inventory Settings

    /// <summary>
    /// Whether automatic scanning is enabled when inventory changes.
    /// </summary>
    public bool AutoScanEnabled { get; set; } = true;

    /// <summary>
    /// Whether to include retainer inventories in scans.
    /// </summary>
    public bool ScanRetainersEnabled { get; set; } = false;

    /// <summary>
    /// Recursion depth for intermediate craft resolution (0-1).
    /// 0 = No recursion, 1 = One level of intermediate crafts.
    /// </summary>
    public int RecursionDepth { get; set; } = 1;

    /// <summary>
    /// Scan options configuration.
    /// </summary>
    public ScanOptions ScanOptions { get; set; } = ScanOptions.CreateDefault();

    /// <summary>
    /// Container selection preferences.
    /// </summary>
    public HashSet<uint> SelectedContainers { get; set; } = new(ScanOptions.DefaultContainers);

    #endregion

    #region Performance Settings

    /// <summary>
    /// Maximum number of results to display in UI.
    /// </summary>
    public int MaxResults { get; set; } = 1000;

    /// <summary>
    /// Whether to enable parallel processing for better performance.
    /// </summary>
    public bool ParallelProcessingEnabled { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism for background operations.
    /// </summary>
    public int MaxParallelTasks { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Cache settings for recipe and inventory data.
    /// </summary>
    public CacheSettings CacheSettings { get; set; } = new();

    /// <summary>
    /// Memory limit for caching in megabytes.
    /// </summary>
    public int MemoryLimitMb { get; set; } = 256;

    /// <summary>
    /// Performance mode selection.
    /// </summary>
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Balanced;

    #endregion

    #region UI Settings

    /// <summary>
    /// Main window position and size settings.
    /// </summary>
    public WindowSettings MainWindow { get; set; } = new();

    /// <summary>
    /// Settings window position and size.
    /// </summary>
    public WindowSettings SettingsWindow { get; set; } = new();

    /// <summary>
    /// Column width preferences for the main table.
    /// </summary>
    public Dictionary<string, float> ColumnWidths { get; set; } = new()
    {
        { "ItemName", 200f },
        { "JobName", 100f },
        { "Level", 60f },
        { "MaxCraftable", 80f },
        { "Materials", 150f }
    };

    /// <summary>
    /// Default filter options for the main window.
    /// </summary>
    public FilterOptions DefaultFilters { get; set; } = FilterOptions.CreateDefault();

    /// <summary>
    /// List of favorite recipe IDs.
    /// </summary>
    public HashSet<uint> FavoriteRecipes { get; set; } = new();

    /// <summary>
    /// UI theme preference.
    /// </summary>
    public UiTheme Theme { get; set; } = UiTheme.Auto;

    /// <summary>
    /// Whether to show tooltips with additional information.
    /// </summary>
    public bool ShowTooltips { get; set; } = true;

    /// <summary>
    /// Font scale for UI elements.
    /// </summary>
    public float FontScale { get; set; } = 1.0f;

    #endregion

    #region Advanced Settings


    /// <summary>
    /// Whether to enable experimental features.
    /// </summary>
    public bool ExperimentalFeatures { get; set; } = false;

    /// <summary>
    /// Custom settings for advanced users.
    /// </summary>
    public Dictionary<string, object> CustomSettings { get; set; } = new();

    /// <summary>
    /// Plugin update check preferences.
    /// </summary>
    public UpdateSettings UpdateSettings { get; set; } = new();

    #endregion

    /// <summary>
    /// Create default configuration with sensible values.
    /// </summary>
    public static PluginConfiguration CreateDefault()
    {
        return new PluginConfiguration();
    }

    /// <summary>
    /// Validate and fix any invalid configuration values.
    /// </summary>
    public void Validate()
    {
        // Clamp numeric values to reasonable ranges
        ScanIntervalMs = Math.Max(100, Math.Min(ScanIntervalMs, 10000));
        MaxResults = Math.Max(10, Math.Min(MaxResults, 10000));
        MaxParallelTasks = Math.Max(1, Math.Min(MaxParallelTasks, Environment.ProcessorCount * 2));
        MemoryLimitMb = Math.Max(32, Math.Min(MemoryLimitMb, 2048));
        RecursionDepth = Math.Max(0, Math.Min(RecursionDepth, 1));
        FontScale = Math.Max(0.5f, Math.Min(FontScale, 3.0f));

        // Validate hotkey code
        if (HotkeyCode < 1 || HotkeyCode > 255)
            HotkeyCode = 123; // Reset to F12

        // Ensure we have valid containers selected
        if (!SelectedContainers.Any())
            SelectedContainers = new HashSet<uint>(ScanOptions.DefaultContainers);

        // Validate scan options
        ScanOptions?.Validate();

        // Validate sub-objects
        MainWindow?.Validate();
        SettingsWindow?.Validate();
        DefaultFilters?.Validate();
        CacheSettings?.Validate();
        UpdateSettings?.Validate();

        // Ensure collections are not null
        FavoriteRecipes ??= new HashSet<uint>();
        ColumnWidths ??= new Dictionary<string, float>();
        CustomSettings ??= new Dictionary<string, object>();

        // Validate language setting
        if (string.IsNullOrWhiteSpace(Language))
            Language = "en";
    }

    /// <summary>
    /// Create a deep copy of this configuration.
    /// </summary>
    public PluginConfiguration Clone()
    {
        var json = JsonConvert.SerializeObject(this);
        return JsonConvert.DeserializeObject<PluginConfiguration>(json) ?? CreateDefault();
    }
}

/// <summary>
/// Window position and size settings.
/// </summary>
[Serializable]
public sealed class WindowSettings
{
    public float X { get; set; } = -1;
    public float Y { get; set; } = -1;
    public float Width { get; set; } = 800;
    public float Height { get; set; } = 600;
    public bool IsOpen { get; set; } = false;
    public bool RememberPosition { get; set; } = true;
    public bool RememberSize { get; set; } = true;

    public void Validate()
    {
        Width = Math.Max(300, Math.Min(Width, 2000));
        Height = Math.Max(200, Math.Min(Height, 1500));
    }
}

/// <summary>
/// Cache configuration settings.
/// </summary>
[Serializable]
public sealed class CacheSettings
{
    public int RecipeCacheDurationMinutes { get; set; } = 30;
    public int InventoryCacheDurationMinutes { get; set; } = 5;
    public bool EnableDiskCache { get; set; } = false;
    public int MaxCacheEntries { get; set; } = 10000;

    public void Validate()
    {
        RecipeCacheDurationMinutes = Math.Max(1, Math.Min(RecipeCacheDurationMinutes, 1440));
        InventoryCacheDurationMinutes = Math.Max(1, Math.Min(InventoryCacheDurationMinutes, 60));
        MaxCacheEntries = Math.Max(100, Math.Min(MaxCacheEntries, 100000));
    }
}

/// <summary>
/// Update check settings.
/// </summary>
[Serializable]
public sealed class UpdateSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;
    public bool IncludeBetaVersions { get; set; } = false;
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

    public void Validate()
    {
        // No specific validation needed for update settings
    }
}

/// <summary>
/// Modifier keys for hotkey combinations.
/// </summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>
/// Performance mode options.
/// </summary>
public enum PerformanceMode
{
    /// <summary>
    /// Optimized for minimum resource usage.
    /// </summary>
    Performance,
    
    /// <summary>
    /// Balanced performance and features.
    /// </summary>
    Balanced,
    
    /// <summary>
    /// Maximum features and accuracy.
    /// </summary>
    Quality
}

/// <summary>
/// UI theme options.
/// </summary>
public enum UiTheme
{
    /// <summary>
    /// Follow Dalamud's theme setting.
    /// </summary>
    Auto,
    
    /// <summary>
    /// Light theme.
    /// </summary>
    Light,
    
    /// <summary>
    /// Dark theme.
    /// </summary>
    Dark
}