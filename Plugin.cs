using System;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ReadyCrafter.Services;
using ReadyCrafter.UI;

namespace ReadyCrafter;

/// <summary>
/// Main plugin entry point for ReadyCrafter.
/// Implements the Dalamud plugin lifecycle and coordinates all core services.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    /// <summary>
    /// Plugin name used by Dalamud.
    /// </summary>
    public string Name => "ReadyCrafter";

    // Dalamud services - injected via IoC
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    // Core services
    private readonly SettingsManager _settingsManager;
    private readonly InventoryService _inventoryService;
    private readonly RecipeRepositorySimple _recipeRepository;
    private readonly JobLevelService _jobLevelService;
    private readonly CraftSolver _craftSolver;
    private readonly HotkeyService _hotkeyService;
    
    // UI management
    private readonly WindowSystem _windowSystem;
    private readonly ReadyCrafterWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    
    // State management
    private bool _disposed = false;
    private DateTime _lastInventoryCheck = DateTime.MinValue;
    private string _lastInventoryHash = string.Empty;

    // Command constants
    private const string MainCommand = "/readycrafter";
    private const string ShortCommand = "/rc";

    /// <summary>
    /// Initializes the ReadyCrafter plugin and all core services.
    /// </summary>
    public Plugin()
    {
        try
        {
            PluginLog.Information("ReadyCrafter plugin initializing...");

            // Initialize configuration first
            _settingsManager = new SettingsManager(PluginInterface);
            
            // Initialize core services with dependency injection pattern
            _recipeRepository = new RecipeRepositorySimple(DataManager, ClientState, PluginLog);
            _inventoryService = new InventoryService(GameInventory, ClientState, PluginLog);
            _jobLevelService = new JobLevelService(ClientState, PluginLog);
            _craftSolver = new CraftSolver(_recipeRepository, _inventoryService, _jobLevelService, PluginLog);
            _hotkeyService = new HotkeyService(KeyState, Framework, PluginLog, _settingsManager);

            // Initialize UI system
            _windowSystem = new WindowSystem("ReadyCrafter");
            _mainWindow = new ReadyCrafterWindow(_craftSolver, _settingsManager, PluginLog);
            _settingsWindow = new SettingsWindow(_settingsManager);

            _windowSystem.AddWindow(_mainWindow);
            _windowSystem.AddWindow(_settingsWindow);

            // Register UI with Dalamud
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += () => _settingsWindow.IsOpen = true;
            PluginInterface.UiBuilder.OpenMainUi += () => _mainWindow.IsOpen = true;

            // Register commands
            RegisterCommands();

            // Set up framework event loop for inventory monitoring
            Framework.Update += OnFrameworkUpdate;

            // Initialize hotkey system
            InitializeHotkeys();

            // Perform initial data loading
            _ = Task.Run(async () =>
            {
                try
                {
                    await _recipeRepository.InitializeAsync();
                    PluginLog.Information("ReadyCrafter initialization completed successfully");
                    
                    // Trigger initial refresh now that we're ready
                    if (_craftSolver.IsReady)
                    {
                        try
                        {
                            RefreshCraftableItems();
                        }
                        catch (Exception refreshEx)
                        {
                            PluginLog.Warning(refreshEx, "Failed to perform initial refresh after initialization");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Failed to complete async initialization");
                }
            });
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Failed to initialize ReadyCrafter plugin");
            throw;
        }
    }

    /// <summary>
    /// Register chat commands for the plugin.
    /// </summary>
    private void RegisterCommands()
    {
        CommandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ReadyCrafter window to view craftable recipes."
        });

        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ReadyCrafter window (short command)."
        });
    }

    /// <summary>
    /// Initialize hotkey binding system.
    /// </summary>
    private void InitializeHotkeys()
    {
        try
        {
            // Subscribe to hotkey activation events
            _hotkeyService.HotkeyActivated += OnHotkeyActivated;
            _hotkeyService.HotkeyConflict += OnHotkeyConflict;
            
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to initialize hotkey system - hotkeys may not work");
        }
    }

    /// <summary>
    /// Framework update event handler for inventory monitoring and hotkey detection.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed || ClientState.LocalPlayer == null)
            return;

        try
        {
            // Check if enough time has passed since last inventory check
            var now = DateTime.UtcNow;
            var settings = _settingsManager.Settings;
            
            if (now - _lastInventoryCheck < TimeSpan.FromMilliseconds(settings.ScanIntervalMs))
                return;

            _lastInventoryCheck = now;

            // Check for inventory changes
            if (settings.AutoScanEnabled && ShouldRefreshInventory() && _craftSolver.IsReady)
            {
                RefreshCraftableItems();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Error in framework update loop");
        }
    }

    /// <summary>
    /// Handle hotkey activation events from the HotkeyService.
    /// </summary>
    private void OnHotkeyActivated(object? sender, HotkeyActivatedEventArgs e)
    {
        try
        {
            switch (e.Binding.Id)
            {
                case "toggle_main_window":
                    ToggleMainWindow();
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Error handling hotkey activation: {e.Binding.Id}");
        }
    }

    /// <summary>
    /// Handle hotkey conflict notifications from the HotkeyService.
    /// </summary>
    private void OnHotkeyConflict(object? sender, HotkeyConflictEventArgs e)
    {
        try
        {
            var message = $"Hotkey conflict detected: {e.NewBinding.Description} conflicts with {e.ConflictingBinding.Description}";
            PluginLog.Warning(message);
            
            // Optionally show notification to user
            // Notification temporarily disabled due to API changes
            // message, "ReadyCrafter Hotkey Conflict"
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Error handling hotkey conflict notification");
        }
    }

    /// <summary>
    /// Toggle the main window visibility.
    /// </summary>
    public void ToggleMainWindow()
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    /// <summary>
    /// Check if inventory should be refreshed based on current state.
    /// </summary>
    private bool ShouldRefreshInventory()
    {
        try
        {
            var currentHash = _inventoryService.LastSnapshot?.StateHash ?? string.Empty;
            if (currentHash != _lastInventoryHash)
            {
                _lastInventoryHash = currentHash;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to check inventory state");
            return false;
        }
    }

    /// <summary>
    /// Refresh the craftable items list.
    /// </summary>
    private void RefreshCraftableItems()
    {
        try
        {
            var startTime = DateTime.UtcNow;
            
            // Trigger craft solver to recalculate
            _craftSolver.InvalidateCache();
            
            var elapsed = DateTime.UtcNow - startTime;
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Failed to refresh craftable items");
        }
    }

    /// <summary>
    /// Handle chat commands.
    /// </summary>
    private void OnCommand(string command, string args)
    {
        try
        {
            var arguments = args.Trim().ToLowerInvariant();
            
            switch (arguments)
            {
                case "config":
                case "settings":
                    _settingsWindow.IsOpen = true;
                    break;
                case "refresh":
                    RefreshCraftableItems();
                    PluginLog.Information("Manually refreshed craftable items list");
                    break;
                case "toggle":
                case "":
                default:
                    ToggleMainWindow();
                    break;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Error executing command: {command} {args}");
        }
    }

    /// <summary>
    /// Draw the UI system.
    /// </summary>
    private void DrawUI()
    {
        try
        {
            _windowSystem.Draw();
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Error drawing UI");
        }
    }

    /// <summary>
    /// Get the settings manager for other components.
    /// </summary>
    public SettingsManager GetSettingsManager() => _settingsManager;

    /// <summary>
    /// Get the craft solver for other components.
    /// </summary>
    public CraftSolver GetCraftSolver() => _craftSolver;

    /// <summary>
    /// Get the hotkey service for other components.
    /// </summary>
    public HotkeyService GetHotkeyService() => _hotkeyService;

    /// <summary>
    /// Dispose of all resources and clean up the plugin.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            PluginLog.Information("ReadyCrafter plugin disposing...");

            // Unregister framework events
            Framework.Update -= OnFrameworkUpdate;

            // Unregister hotkey events
            if (_hotkeyService != null)
            {
                _hotkeyService.HotkeyActivated -= OnHotkeyActivated;
                _hotkeyService.HotkeyConflict -= OnHotkeyConflict;
            }

            // Unregister UI events
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= () => _settingsWindow.IsOpen = true;
            PluginInterface.UiBuilder.OpenMainUi -= () => _mainWindow.IsOpen = true;

            // Remove commands
            CommandManager.RemoveHandler(MainCommand);
            CommandManager.RemoveHandler(ShortCommand);

            // Dispose UI system
            _windowSystem?.RemoveAllWindows();
            _mainWindow?.Dispose();
            _settingsWindow?.Dispose();

            // Dispose services
            _hotkeyService?.Dispose();
            _craftSolver?.Dispose();
            _jobLevelService?.Dispose();
            _inventoryService?.Dispose();
            _recipeRepository?.Dispose();
            _settingsManager?.Dispose();

            _disposed = true;
            PluginLog.Information("ReadyCrafter plugin disposed successfully");
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Error during plugin disposal");
        }
    }
}