using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using ReadyCrafter.Services;

namespace ReadyCrafter.UI;

/// <summary>
/// Settings window for ReadyCrafter plugin configuration.
/// Serves as a dedicated settings panel separate from the main UI.
/// </summary>
public sealed class SettingsWindow : Window, IDisposable
{
    private readonly SettingsManager _settingsManager;
    private bool _disposed = false;

    /// <summary>
    /// Initialize the settings window.
    /// </summary>
    public SettingsWindow(SettingsManager settingsManager) 
        : base("ReadyCrafter Settings###ReadyCrafterSettingsWindow")
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

        // Window configuration
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(800, 600)
        };

        Size = new Vector2(500, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// Draw the settings window content.
    /// </summary>
    public override void Draw()
    {
        if (_disposed)
            return;

        try
        {
            var settings = _settingsManager.Settings;

            ImGui.Text("ReadyCrafter Configuration");
            ImGui.Separator();

            // General Settings
            ImGui.Text("General Settings");
            
            var pluginEnabled = settings.PluginEnabled;
            if (ImGui.Checkbox("Plugin Enabled", ref pluginEnabled))
            {
                _settingsManager.UpdateSetting("PluginEnabled", pluginEnabled);
            }

            var autoScanEnabled = settings.AutoScanEnabled;
            if (ImGui.Checkbox("Auto Scan on Inventory Change", ref autoScanEnabled))
            {
                _settingsManager.UpdateSetting("AutoScanEnabled", autoScanEnabled);
            }

            var scanInterval = settings.ScanIntervalMs;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Scan Interval (ms)", ref scanInterval, 100, 5000))
            {
                _settingsManager.UpdateSetting("ScanIntervalMs", scanInterval);
            }

            ImGui.Separator();

            // Scanning Options
            ImGui.Text("Scanning Options");

            var scanRetainers = settings.ScanRetainersEnabled;
            if (ImGui.Checkbox("Scan Retainer Inventory", ref scanRetainers))
            {
                _settingsManager.UpdateSetting("ScanRetainersEnabled", scanRetainers);
            }
            
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Warning: Retainer scanning uses additional CPU and memory.");
            }

            ImGui.Separator();

            // Hotkey Settings
            ImGui.Text("Hotkey Settings");

            var hotkeyEnabled = settings.HotkeyEnabled;
            if (ImGui.Checkbox("Enable Hotkey", ref hotkeyEnabled))
            {
                _settingsManager.UpdateSetting("HotkeyEnabled", hotkeyEnabled);
            }

            if (hotkeyEnabled)
            {
                var hotkeyCode = settings.HotkeyCode;
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("Hotkey Code", ref hotkeyCode))
                {
                    _settingsManager.UpdateSetting("HotkeyCode", hotkeyCode);
                }
                ImGui.SameLine();
                ImGui.Text("(Virtual Key Code)");
            }

            ImGui.Separator();

            // Performance Settings
            ImGui.Text("Performance Settings");

            var performanceMode = (int)settings.PerformanceMode;
            ImGui.SetNextItemWidth(150);
            if (ImGui.Combo("Performance Mode", ref performanceMode, "Performance\0Balanced\0Quality\0"))
            {
                _settingsManager.UpdateSetting("PerformanceMode", (PerformanceMode)performanceMode);
            }

            ImGui.Separator();

            // Save/Reset buttons
            if (ImGui.Button("Save Settings"))
            {
                _settingsManager.SaveSettings();
                IsOpen = false;
            }

            ImGui.SameLine();

            if (ImGui.Button("Reset to Defaults"))
            {
                ImGui.OpenPopup("Reset Confirmation");
            }

            // Reset confirmation popup
            var resetOpen = true;
            if (ImGui.BeginPopupModal("Reset Confirmation", ref resetOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("Are you sure you want to reset all settings to default values?");
                ImGui.Text("This action cannot be undone.");
                ImGui.Separator();

                if (ImGui.Button("Yes, Reset"))
                {
                    _settingsManager.ResetToDefaults();
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }
        catch (Exception ex)
        {
            ImGui.Text($"Error drawing settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Clean up resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}