using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace ReadyCrafter.Services;

/// <summary>
/// Manages hotkey binding and keyboard input detection for ReadyCrafter plugin.
/// Provides configurable key combinations, conflict detection, and safe integration with Dalamud's input system.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly IKeyState _keyState;
    private readonly IFramework _framework;
    private readonly IPluginLog _pluginLog;
    private readonly SettingsManager _settingsManager;
    
    private readonly ReaderWriterLockSlim _hotkeyLock = new();
    private readonly Dictionary<string, HotkeyBinding> _hotkeyBindings = new();
    private readonly HashSet<VirtualKey> _previousPressedKeys = new();
    private readonly Dictionary<string, DateTime> _lastKeyPressTime = new();
    
    private bool _disposed = false;
    private bool _enabled = true;
    
    // Debounce timing to prevent rapid-fire hotkey activation
    private const int HotkeyDebounceMs = 200;
    
    /// <summary>
    /// Event fired when a registered hotkey is activated.
    /// </summary>
    public event EventHandler<HotkeyActivatedEventArgs>? HotkeyActivated;
    
    /// <summary>
    /// Event fired when hotkey conflicts are detected.
    /// </summary>
    public event EventHandler<HotkeyConflictEventArgs>? HotkeyConflict;

    /// <summary>
    /// Initialize the hotkey service with required Dalamud services.
    /// </summary>
    public HotkeyService(IKeyState keyState, IFramework framework, IPluginLog pluginLog, SettingsManager settingsManager)
    {
        _keyState = keyState ?? throw new ArgumentNullException(nameof(keyState));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        
        // Subscribe to framework updates for key monitoring
        _framework.Update += OnFrameworkUpdate;
        
        // Subscribe to settings changes for real-time hotkey updates
        _settingsManager.SettingChanged += OnSettingChanged;
        
        // Initialize default hotkey bindings from settings
        InitializeDefaultHotkeys();
        
    }

    /// <summary>
    /// Whether the hotkey system is enabled.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
            }
        }
    }

    /// <summary>
    /// Register a new hotkey binding.
    /// </summary>
    /// <param name="id">Unique identifier for the hotkey</param>
    /// <param name="key">Primary key for the hotkey</param>
    /// <param name="modifiers">Modifier keys (Ctrl, Alt, Shift, etc.)</param>
    /// <param name="description">Human-readable description</param>
    /// <param name="globalOnly">Whether this hotkey only works when game window is focused</param>
    /// <returns>True if registration succeeded, false if conflict detected</returns>
    public bool RegisterHotkey(string id, VirtualKey key, ModifierKeys modifiers, string description, bool globalOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(description);
        
        _hotkeyLock.EnterWriteLock();
        try
        {
            var binding = new HotkeyBinding
            {
                Id = id,
                Key = key,
                Modifiers = modifiers,
                Description = description,
                GlobalOnly = globalOnly,
                Enabled = true,
                LastActivated = DateTime.MinValue
            };
            
            // Check for conflicts with existing bindings
            if (HasHotkeyConflict(binding, out var conflictingBinding))
            {
                _pluginLog.Warning($"Hotkey conflict detected: {id} conflicts with {conflictingBinding.Id}");
                HotkeyConflict?.Invoke(this, new HotkeyConflictEventArgs(binding, conflictingBinding));
                return false;
            }
            
            _hotkeyBindings[id] = binding;
            return true;
        }
        finally
        {
            _hotkeyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Unregister a hotkey binding.
    /// </summary>
    public bool UnregisterHotkey(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        
        _hotkeyLock.EnterWriteLock();
        try
        {
            if (_hotkeyBindings.Remove(id))
            {
                return true;
            }
            return false;
        }
        finally
        {
            _hotkeyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Update an existing hotkey binding.
    /// </summary>
    public bool UpdateHotkey(string id, VirtualKey key, ModifierKeys modifiers)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        
        _hotkeyLock.EnterWriteLock();
        try
        {
            if (!_hotkeyBindings.TryGetValue(id, out var binding))
                return false;
            
            var updatedBinding = binding with { Key = key, Modifiers = modifiers };
            
            // Check for conflicts with other bindings (excluding self)
            if (HasHotkeyConflict(updatedBinding, out var conflictingBinding, excludeId: id))
            {
                _pluginLog.Warning($"Hotkey update conflict: {id} would conflict with {conflictingBinding.Id}");
                HotkeyConflict?.Invoke(this, new HotkeyConflictEventArgs(updatedBinding, conflictingBinding));
                return false;
            }
            
            _hotkeyBindings[id] = updatedBinding;
            return true;
        }
        finally
        {
            _hotkeyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Enable or disable a specific hotkey.
    /// </summary>
    public void SetHotkeyEnabled(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        
        _hotkeyLock.EnterWriteLock();
        try
        {
            if (_hotkeyBindings.TryGetValue(id, out var binding))
            {
                _hotkeyBindings[id] = binding with { Enabled = enabled };
            }
        }
        finally
        {
            _hotkeyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get all registered hotkey bindings.
    /// </summary>
    public IReadOnlyDictionary<string, HotkeyBinding> GetHotkeyBindings()
    {
        _hotkeyLock.EnterReadLock();
        try
        {
            return _hotkeyBindings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        finally
        {
            _hotkeyLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get a human-readable display string for a hotkey combination.
    /// </summary>
    public static string GetHotkeyDisplayString(VirtualKey key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        
        parts.Add(GetKeyDisplayName(key));
        
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Get a human-readable display string for a hotkey binding.
    /// </summary>
    public static string GetHotkeyDisplayString(HotkeyBinding binding)
    {
        return GetHotkeyDisplayString(binding.Key, binding.Modifiers);
    }

    /// <summary>
    /// Get a human-readable name for a virtual key.
    /// </summary>
    public static string GetKeyDisplayName(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.SPACE => "Space",
            VirtualKey.RETURN => "Enter",
            VirtualKey.ESCAPE => "Esc",
            VirtualKey.TAB => "Tab",
            VirtualKey.BACK => "Backspace",
            VirtualKey.DELETE => "Delete",
            VirtualKey.INSERT => "Insert",
            VirtualKey.HOME => "Home",
            VirtualKey.END => "End",
            VirtualKey.PRIOR => "Page Up",
            VirtualKey.NEXT => "Page Down",
            VirtualKey.UP => "Up Arrow",
            VirtualKey.DOWN => "Down Arrow",
            VirtualKey.LEFT => "Left Arrow",
            VirtualKey.RIGHT => "Right Arrow",
            VirtualKey.F1 => "F1",
            VirtualKey.F2 => "F2",
            VirtualKey.F3 => "F3",
            VirtualKey.F4 => "F4",
            VirtualKey.F5 => "F5",
            VirtualKey.F6 => "F6",
            VirtualKey.F7 => "F7",
            VirtualKey.F8 => "F8",
            VirtualKey.F9 => "F9",
            VirtualKey.F10 => "F10",
            VirtualKey.F11 => "F11",
            VirtualKey.F12 => "F12",
            VirtualKey.NUMPAD0 => "Num 0",
            VirtualKey.NUMPAD1 => "Num 1",
            VirtualKey.NUMPAD2 => "Num 2",
            VirtualKey.NUMPAD3 => "Num 3",
            VirtualKey.NUMPAD4 => "Num 4",
            VirtualKey.NUMPAD5 => "Num 5",
            VirtualKey.NUMPAD6 => "Num 6",
            VirtualKey.NUMPAD7 => "Num 7",
            VirtualKey.NUMPAD8 => "Num 8",
            VirtualKey.NUMPAD9 => "Num 9",
            VirtualKey.MULTIPLY => "Num *",
            VirtualKey.ADD => "Num +",
            VirtualKey.SUBTRACT => "Num -",
            VirtualKey.DECIMAL => "Num .",
            VirtualKey.DIVIDE => "Num /",
            VirtualKey.OEM_1 => ";",
            VirtualKey.OEM_PLUS => "=",
            VirtualKey.OEM_COMMA => ",",
            VirtualKey.OEM_MINUS => "-",
            VirtualKey.OEM_PERIOD => ".",
            VirtualKey.OEM_2 => "/",
            VirtualKey.OEM_3 => "`",
            VirtualKey.OEM_4 => "[",
            VirtualKey.OEM_5 => "\\",
            VirtualKey.OEM_6 => "]",
            VirtualKey.OEM_7 => "'",
            VirtualKey.KEY_0 => "0",
            VirtualKey.KEY_1 => "1",
            VirtualKey.KEY_2 => "2",
            VirtualKey.KEY_3 => "3",
            VirtualKey.KEY_4 => "4",
            VirtualKey.KEY_5 => "5",
            VirtualKey.KEY_6 => "6",
            VirtualKey.KEY_7 => "7",
            VirtualKey.KEY_8 => "8",
            VirtualKey.KEY_9 => "9",
            VirtualKey.A => "A",
            VirtualKey.B => "B",
            VirtualKey.C => "C",
            VirtualKey.D => "D",
            VirtualKey.E => "E",
            VirtualKey.F => "F",
            VirtualKey.G => "G",
            VirtualKey.H => "H",
            VirtualKey.I => "I",
            VirtualKey.J => "J",
            VirtualKey.K => "K",
            VirtualKey.L => "L",
            VirtualKey.M => "M",
            VirtualKey.N => "N",
            VirtualKey.O => "O",
            VirtualKey.P => "P",
            VirtualKey.Q => "Q",
            VirtualKey.R => "R",
            VirtualKey.S => "S",
            VirtualKey.T => "T",
            VirtualKey.U => "U",
            VirtualKey.V => "V",
            VirtualKey.W => "W",
            VirtualKey.X => "X",
            VirtualKey.Y => "Y",
            VirtualKey.Z => "Z",
            _ => key.ToString()
        };
    }

    /// <summary>
    /// Export current hotkey configuration as JSON.
    /// </summary>
    public string ExportHotkeyConfiguration()
    {
        _hotkeyLock.EnterReadLock();
        try
        {
            var exportData = new HotkeyProfile
            {
                Name = "Current Configuration",
                CreatedDate = DateTime.UtcNow,
                Bindings = _hotkeyBindings.Values.ToArray()
            };
            
            return System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
        finally
        {
            _hotkeyLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Import hotkey configuration from JSON.
    /// </summary>
    public bool ImportHotkeyConfiguration(string jsonData, bool replaceExisting = false)
    {
        try
        {
            var profile = System.Text.Json.JsonSerializer.Deserialize<HotkeyProfile>(jsonData);
            if (profile?.Bindings == null)
                return false;

            _hotkeyLock.EnterWriteLock();
            try
            {
                if (replaceExisting)
                {
                    _hotkeyBindings.Clear();
                }

                var conflictCount = 0;
                foreach (var binding in profile.Bindings)
                {
                    if (HasHotkeyConflict(binding, out var conflictingBinding, replaceExisting ? null : binding.Id))
                    {
                        conflictCount++;
                        _pluginLog.Warning($"Skipping conflicting hotkey import: {binding.Id}");
                        continue;
                    }

                    _hotkeyBindings[binding.Id] = binding;
                }

                _pluginLog.Information($"Imported {profile.Bindings.Length - conflictCount} hotkey bindings" +
                    (conflictCount > 0 ? $" ({conflictCount} skipped due to conflicts)" : ""));
                return true;
            }
            finally
            {
                _hotkeyLock.ExitWriteLock();
            }
        }
        catch (Exception ex)
        {
            _pluginLog.Error(ex, "Failed to import hotkey configuration");
            return false;
        }
    }

    /// <summary>
    /// Check if a key combination is valid and can be bound.
    /// </summary>
    public static bool IsValidKeyCombination(VirtualKey key, ModifierKeys modifiers)
    {
        // Prevent binding system keys and invalid combinations
        var invalidKeys = new[]
        {
            VirtualKey.LWIN, VirtualKey.RWIN,
            VirtualKey.LMENU, VirtualKey.RMENU,
            VirtualKey.LCONTROL, VirtualKey.RCONTROL,
            VirtualKey.LSHIFT, VirtualKey.RSHIFT,
            VirtualKey.CAPITAL, VirtualKey.NUMLOCK, VirtualKey.SCROLL
        };

        if (invalidKeys.Contains(key))
            return false;

        // Require at least one modifier for common keys to avoid conflicts
        var commonKeys = new[]
        {
            VirtualKey.SPACE, VirtualKey.RETURN, VirtualKey.ESCAPE, VirtualKey.TAB,
            VirtualKey.BACK, VirtualKey.DELETE
        };

        if (commonKeys.Contains(key) && modifiers == ModifierKeys.None)
            return false;

        // Letters and numbers should generally have modifiers to avoid game conflicts
        if ((key >= VirtualKey.A && key <= VirtualKey.Z) ||
            (key >= VirtualKey.KEY_0 && key <= VirtualKey.KEY_9))
        {
            if (modifiers == ModifierKeys.None)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Initialize default hotkey bindings from settings.
    /// </summary>
    private void InitializeDefaultHotkeys()
    {
        var settings = _settingsManager.Settings;
        
        // Register the main toggle hotkey from settings
        var toggleKey = (VirtualKey)settings.HotkeyCode;
        var toggleModifiers = settings.HotkeyModifiers;
        
        RegisterHotkey("toggle_main_window", toggleKey, toggleModifiers, "Toggle ReadyCrafter main window");
        
    }

    /// <summary>
    /// Handle settings changes for real-time hotkey updates.
    /// </summary>
    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.SettingName == nameof(PluginConfiguration.HotkeyCode) ||
            e.SettingName == nameof(PluginConfiguration.HotkeyModifiers) ||
            e.SettingName == nameof(PluginConfiguration.HotkeyEnabled))
        {
            UpdateMainWindowHotkey();
        }
    }

    /// <summary>
    /// Update the main window toggle hotkey from current settings.
    /// </summary>
    private void UpdateMainWindowHotkey()
    {
        var settings = _settingsManager.Settings;
        var toggleKey = (VirtualKey)settings.HotkeyCode;
        var toggleModifiers = settings.HotkeyModifiers;
        
        SetHotkeyEnabled("toggle_main_window", settings.HotkeyEnabled);
        
        if (settings.HotkeyEnabled)
        {
            UpdateHotkey("toggle_main_window", toggleKey, toggleModifiers);
        }
    }

    /// <summary>
    /// Framework update handler for key state monitoring.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed || !_enabled)
            return;

        try
        {
            CheckHotkeyActivation();
        }
        catch (Exception ex)
        {
            _pluginLog.Warning(ex, "Error during hotkey monitoring");
        }
    }

    /// <summary>
    /// Check for hotkey activation and handle debouncing.
    /// </summary>
    private void CheckHotkeyActivation()
    {
        _hotkeyLock.EnterReadLock();
        try
        {
            foreach (var binding in _hotkeyBindings.Values.Where(b => b.Enabled))
            {
                if (IsHotkeyPressed(binding))
                {
                    // Check debounce timing
                    var now = DateTime.UtcNow;
                    if (_lastKeyPressTime.TryGetValue(binding.Id, out var lastPress))
                    {
                        if ((now - lastPress).TotalMilliseconds < HotkeyDebounceMs)
                            continue;
                    }

                    _lastKeyPressTime[binding.Id] = now;
                    
                    // Fire the hotkey activated event
                    HotkeyActivated?.Invoke(this, new HotkeyActivatedEventArgs(binding));
                    
                }
            }
        }
        finally
        {
            _hotkeyLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Check if a specific hotkey combination is currently pressed.
    /// </summary>
    private bool IsHotkeyPressed(HotkeyBinding binding)
    {
        try
        {
            // Check if the main key is pressed
            if (!_keyState[binding.Key])
                return false;

            // Check if the main key was just pressed (not held)
            if (_previousPressedKeys.Contains(binding.Key))
                return false;

            // Check modifier keys
            if (binding.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (!(_keyState[VirtualKey.LCONTROL] || _keyState[VirtualKey.RCONTROL]))
                    return false;
            }

            if (binding.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                if (!(_keyState[VirtualKey.LMENU] || _keyState[VirtualKey.RMENU]))
                    return false;
            }

            if (binding.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (!(_keyState[VirtualKey.LSHIFT] || _keyState[VirtualKey.RSHIFT]))
                    return false;
            }

            if (binding.Modifiers.HasFlag(ModifierKeys.Windows))
            {
                if (!(_keyState[VirtualKey.LWIN] || _keyState[VirtualKey.RWIN]))
                    return false;
            }

            // Update previous pressed keys for next frame
            _previousPressedKeys.Add(binding.Key);
            
            return true;
        }
        catch (Exception ex)
        {
            _pluginLog.Warning(ex, $"Error checking hotkey state for {binding.Id}");
            return false;
        }
        finally
        {
            // Clean up previous pressed keys that are no longer pressed
            _previousPressedKeys.RemoveWhere(key => !_keyState[key]);
        }
    }

    /// <summary>
    /// Check if a hotkey binding conflicts with existing bindings.
    /// </summary>
    private bool HasHotkeyConflict(HotkeyBinding binding, out HotkeyBinding conflictingBinding, string? excludeId = null)
    {
        conflictingBinding = default;
        
        foreach (var existing in _hotkeyBindings.Values)
        {
            if (excludeId != null && existing.Id == excludeId)
                continue;
                
            if (existing.Key == binding.Key && existing.Modifiers == binding.Modifiers)
            {
                conflictingBinding = existing;
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Dispose of resources and clean up the service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            // Unsubscribe from events
            _framework.Update -= OnFrameworkUpdate;
            _settingsManager.SettingChanged -= OnSettingChanged;

            // Clear hotkey bindings
            _hotkeyLock.EnterWriteLock();
            try
            {
                _hotkeyBindings.Clear();
                _lastKeyPressTime.Clear();
                _previousPressedKeys.Clear();
            }
            finally
            {
                _hotkeyLock.ExitWriteLock();
            }

        }
        catch (Exception ex)
        {
            _pluginLog.Error(ex, "Error during HotkeyService disposal");
        }
        finally
        {
            _hotkeyLock?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents a hotkey binding configuration.
/// </summary>
public sealed record HotkeyBinding
{
    public string Id { get; init; } = string.Empty;
    public VirtualKey Key { get; init; }
    public ModifierKeys Modifiers { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool GlobalOnly { get; init; }
    public bool Enabled { get; init; }
    public DateTime LastActivated { get; init; }
}

/// <summary>
/// Event arguments for hotkey activation.
/// </summary>
public sealed class HotkeyActivatedEventArgs : EventArgs
{
    public HotkeyBinding Binding { get; }
    public DateTime Timestamp { get; }

    public HotkeyActivatedEventArgs(HotkeyBinding binding)
    {
        Binding = binding;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
/// Event arguments for hotkey conflicts.
/// </summary>
public sealed class HotkeyConflictEventArgs : EventArgs
{
    public HotkeyBinding NewBinding { get; }
    public HotkeyBinding ConflictingBinding { get; }

    public HotkeyConflictEventArgs(HotkeyBinding newBinding, HotkeyBinding conflictingBinding)
    {
        NewBinding = newBinding;
        ConflictingBinding = conflictingBinding;
    }
}

/// <summary>
/// Represents a hotkey profile for import/export.
/// </summary>
public sealed record HotkeyProfile
{
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedDate { get; init; }
    public string Description { get; init; } = string.Empty;
    public HotkeyBinding[] Bindings { get; init; } = Array.Empty<HotkeyBinding>();
}