using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ImGuiNET;
using ReadyCrafter.Models;
using ReadyCrafter.Services;
#if UNSAFE_CLIENT_STRUCTS
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
#endif

namespace ReadyCrafter.UI;

/// <summary>
/// Main ReadyCrafter window implementing the reactive ImGui panel.
/// Displays craftable items with real-time filtering, sorting, and search capabilities.
/// </summary>
public sealed class ReadyCrafterWindow : Window, IDisposable
{
    private readonly CraftSolver _craftSolver;
    private readonly SettingsManager _settingsManager;
    private readonly IPluginLog _logger;
    private readonly object _lockObject = new();

    // Data state
    private CraftableItem[] _allCraftableItems = Array.Empty<CraftableItem>();
    private CraftableItem[] _filteredItems = Array.Empty<CraftableItem>();
    private FilterOptions _currentFilter = FilterOptions.CreateDefault();
    private DateTime _lastDataUpdate = DateTime.MinValue;
    private int _totalCraftableCount = 0;
    private bool _isRefreshing = false;

    // UI state
    private string _searchBuffer = string.Empty;
    private int _selectedJobId = -1; // -1 means "All Jobs"
    private bool _showOnlyCraftable = false;
    private bool _showOnlyFavorites = false;
    private bool _showOnlyHq = false;
    private bool _includeIntermediates = true;
    private bool _filterByJobLevel = false; // DISABLED: Job level filtering disabled
    private bool _showHigherLevelRecipes = false;
    private bool _showSettings = false;
    private int _sortColumnIndex = 0; // 0=Item, 1=Job, 2=Level, 3=Qty
    private ImGuiSortDirection _sortDirection = ImGuiSortDirection.Ascending;
    private string? _pendingClipboardText = null; // Deferred clipboard operation
    
    // Table state
    private const ImGuiTableFlags TableFlags = 
        ImGuiTableFlags.Resizable | 
        ImGuiTableFlags.Sortable | 
        ImGuiTableFlags.ScrollY | 
        ImGuiTableFlags.BordersOuter | 
        ImGuiTableFlags.BordersV | 
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Hideable;

    // Color scheme (color-blind safe palette from PRD)
    private readonly Vector4 _colorCraftable = new(0.2f, 0.7f, 0.3f, 1.0f);      // Green
    private readonly Vector4 _colorNotCraftable = new(0.7f, 0.3f, 0.2f, 1.0f);   // Red
    private readonly Vector4 _colorIntermediate = new(0.2f, 0.5f, 0.8f, 1.0f);   // Blue
    private readonly Vector4 _colorFavorite = new(0.9f, 0.7f, 0.1f, 1.0f);       // Gold
    private readonly Vector4 _colorHq = new(0.8f, 0.4f, 0.9f, 1.0f);             // Purple
    private readonly Vector4 _colorLevelTooLow = new(0.9f, 0.6f, 0.2f, 1.0f);     // Orange

    // Job mappings for dropdown
    private readonly Dictionary<int, string> _jobNames = new()
    {
        { -1, "All Jobs" },
        { 8, "Carpenter" },
        { 9, "Blacksmith" },
        { 10, "Armorer" },
        { 11, "Goldsmith" },
        { 12, "Leatherworker" },
        { 13, "Weaver" },
        { 14, "Alchemist" },
        { 15, "Culinarian" }
    };

    private bool _disposed = false;

    /// <summary>
    /// Initialize the ReadyCrafter window with required services.
    /// </summary>
    public ReadyCrafterWindow(CraftSolver craftSolver, SettingsManager settingsManager, IPluginLog logger) 
        : base("ReadyCrafter###ReadyCrafterMainWindow")
    {
        _craftSolver = craftSolver ?? throw new ArgumentNullException(nameof(craftSolver));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Window configuration
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(1400, 1000)
        };

        Size = new Vector2(800, 600);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Subscribe to craft solver events
        _craftSolver.SolveCompleted += OnSolveCompleted;
        _craftSolver.CacheInvalidated += OnCacheInvalidated;

        // Don't automatically refresh on initialization - wait for repository to be ready
        // The Plugin will trigger refresh after repository initialization completes
    }

    /// <summary>
    /// Main draw method for the window.
    /// </summary>
    public override void Draw()
    {
        try
        {
            if (_disposed)
                return;

            // Handle any pending clipboard operations at the start of the frame
            if (_pendingClipboardText != null)
            {
                try
                {
                    ImGui.SetClipboardText(_pendingClipboardText);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to set clipboard text");
                }
                finally
                {
                    _pendingClipboardText = null;
                }
            }

            DrawHeader();
            DrawFilters();
            
            ImGui.Separator();
            
            if (_showSettings)
            {
                DrawSettings();
            }
            else
            {
                DrawCraftableTable();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error drawing ReadyCrafter window");
            ImGui.Text($"Error drawing window: {ex.Message}");
        }
    }

    /// <summary>
    /// Draw the panel header with title, count, and controls.
    /// </summary>
    private void DrawHeader()
    {
        // Title with count
        var title = $"Craftable ({_totalCraftableCount})";
        ImGui.Text(title);

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 120);

        // Refresh button
        if (_isRefreshing)
        {
            ImGuiComponents.DisabledButton(FontAwesomeIcon.Sync.ToIconString());
        }
        else
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Sync))
            {
                RefreshCraftableItems();
            }
        }
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Refresh craftable items list");
        }

        ImGui.SameLine();

        // Settings button
        var settingsIcon = _showSettings ? FontAwesomeIcon.Times : FontAwesomeIcon.Cog;
        if (ImGuiComponents.IconButton(settingsIcon))
        {
            _showSettings = !_showSettings;
        }
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_showSettings ? "Close settings" : "Open settings");
        }
    }

    /// <summary>
    /// Draw the filters bar with job dropdown, checkboxes, and search.
    /// </summary>
    private void DrawFilters()
    {
        var filtersChanged = false;

        // Job dropdown
        ImGui.SetNextItemWidth(150);
        var currentJobName = _jobNames.GetValueOrDefault(_selectedJobId, "All Jobs");
        if (ImGui.BeginCombo("##JobFilter", currentJobName))
        {
            foreach (var (jobId, jobName) in _jobNames)
            {
                var isSelected = _selectedJobId == jobId;
                if (ImGui.Selectable(jobName, isSelected))
                {
                    _selectedJobId = jobId;
                    filtersChanged = true;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();

        // Include intermediates checkbox
        if (ImGui.Checkbox("Include intermediates", ref _includeIntermediates))
        {
            filtersChanged = true;
        }

        ImGui.SameLine();

        // Favorites only checkbox
        if (ImGui.Checkbox("Favorites", ref _showOnlyFavorites))
        {
            filtersChanged = true;
        }

        ImGui.SameLine();

        // HQ only checkbox
        if (ImGui.Checkbox("HQ", ref _showOnlyHq))
        {
            filtersChanged = true;
        }

        // DISABLED: Job level filtering options removed due to inconsistent behavior
        // ImGui.SameLine();

        // // Filter by job level checkbox
        // if (ImGui.Checkbox("Filter by level", ref _filterByJobLevel))
        // {
        //     filtersChanged = true;
        // }
        // 
        // if (ImGui.IsItemHovered())
        // {
        //     ImGui.SetTooltip("Only show recipes you can craft with your current job levels");
        // }

        // ImGui.SameLine();

        // // Show higher level recipes checkbox (only enabled if filtering by job level)
        // if (_filterByJobLevel)
        // {
        //     if (ImGui.Checkbox("Show higher level", ref _showHigherLevelRecipes))
        //     {
        //         filtersChanged = true;
        //     }
        //     
        //     if (ImGui.IsItemHovered())
        //     {
        //         ImGui.SetTooltip("Show recipes that require higher job levels (marked with orange text)");
        //     }
        // }

        // Search field on new line
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.InputTextWithHint("##SearchFilter", "Search items...", ref _searchBuffer, 256))
        {
            filtersChanged = true;
        }

        if (filtersChanged)
        {
            UpdateFilters();
        }
    }

    /// <summary>
    /// Draw the settings panel.
    /// </summary>
    private void DrawSettings()
    {
        var settings = _settingsManager.Settings;
        var settingsChanged = false;

        ImGui.Text("Scan Settings");
        ImGui.Separator();

        // Auto scan toggle
        var autoScan = settings.AutoScanEnabled;
        if (ImGui.Checkbox("Auto scan on inventory change", ref autoScan))
        {
            _settingsManager.UpdateSetting("AutoScanEnabled", autoScan);
            settingsChanged = true;
        }

        // Scan interval
        var scanInterval = settings.ScanIntervalMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Scan interval (ms)", ref scanInterval, 100, 5000))
        {
            _settingsManager.UpdateSetting("ScanIntervalMs", scanInterval);
            settingsChanged = true;
        }

        ImGui.Separator();

        // Retainer scan toggle
        var retainerScan = settings.ScanRetainersEnabled;
        if (ImGui.Checkbox("Scan retainer inventory", ref retainerScan))
        {
            _settingsManager.UpdateSetting("ScanRetainersEnabled", retainerScan);
            settingsChanged = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Warning: Retainer scanning may use additional CPU and take longer to complete.");
        }

        ImGui.Separator();

        // Performance info
        ImGui.Text("Performance");
        if (_craftSolver.GetPerformanceMetrics(out var avgTime, out var cacheHitRate))
        {
            ImGui.Text($"Average scan time: {avgTime:F1}ms");
            ImGui.Text($"Cache hit rate: {cacheHitRate:P1}");
        }

        if (settingsChanged)
        {
            RefreshCraftableItems();
        }
    }

    /// <summary>
    /// Draw the main craftable items table.
    /// </summary>
    private void DrawCraftableTable()
    {
        if (_filteredItems.Length == 0)
        {
            ImGui.Text("No craftable items found.");
            ImGui.Text("Try adjusting your filters or check your inventory.");
            return;
        }

        if (ImGui.BeginTable("CraftableItemsTable", 6, TableFlags, new Vector2(0, 0)))
        {
            // Setup columns
            ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 32);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 30);

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Handle sorting
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
            {
                ApplySorting(sortSpecs);
                sortSpecs.SpecsDirty = false;
            }

            // Draw rows
            for (int i = 0; i < _filteredItems.Length; i++)
            {
                var item = _filteredItems[i];
                ImGui.TableNextRow();

                DrawItemRow(item, i);
            }

            ImGui.EndTable();
        }
    }

    /// <summary>
    /// Draw a single item row in the table.
    /// </summary>
    private void DrawItemRow(CraftableItem item, int rowIndex)
    {
        var isClickable = true;
        var rowColor = Vector4.Zero;

        // Determine row color based on state
        if (item.IsFavorite)
        {
            rowColor = _colorFavorite * 0.3f;
        }
        else if (item.MaxCraftable > 0)
        {
            rowColor = _colorCraftable * 0.2f;
        }
        else if (item.IntermediateCrafts.Any())
        {
            rowColor = _colorIntermediate * 0.2f;
        }

        if (rowColor != Vector4.Zero)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(rowColor));
        }

        // Icon column
        ImGui.TableNextColumn();
        if (item.IconId > 0)
        {
            // In a real implementation, you would load and display the icon texture
            // For now, we'll use a placeholder
            ImGui.Text("🔧"); // Placeholder icon
        }

        // Item name column
        ImGui.TableNextColumn();
        var itemText = item.ItemName;
        if (item.IsFavorite)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorFavorite);
            itemText = $"★ {itemText}";
        }
        else if (item.MaxCraftable <= 0 && item.IntermediateCrafts.Any())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorIntermediate);
        }

        if (ImGui.Selectable(itemText, false, ImGuiSelectableFlags.SpanAllColumns))
        {
            // Single left-click opens recipe
            OpenRecipeWindow(item.ItemId);
        }
        
        // Right-click toggles favorite
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ToggleFavorite(item);
        }

        if (item.IsFavorite || (item.MaxCraftable <= 0 && item.IntermediateCrafts.Any()))
        {
            ImGui.PopStyleColor();
        }

        if (ImGui.IsItemHovered())
        {
            DrawItemTooltip(item);
        }

        // Job column
        ImGui.TableNextColumn();
        ImGui.Text(item.JobName);

        // Level column
        ImGui.TableNextColumn();
        if (!item.MeetsJobLevelRequirement)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorLevelTooLow);
            ImGui.Text($"{item.RequiredLevel}!");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Text(item.RequiredLevel.ToString());
        }

        // Quantity column
        ImGui.TableNextColumn();
        if (item.MaxCraftable > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorCraftable);
            ImGui.Text(item.MaxCraftable.ToString());
            ImGui.PopStyleColor();
        }
        else if (item.IntermediateCrafts.Any())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorIntermediate);
            ImGui.Text("*");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorNotCraftable);
            ImGui.Text("0");
            ImGui.PopStyleColor();
        }

        // HQ column
        ImGui.TableNextColumn();
        if (item.CanHq)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorHq);
            ImGui.Text("★");
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Draw tooltip for an item showing materials and requirements.
    /// </summary>
    private void DrawItemTooltip(CraftableItem item)
    {
        ImGui.BeginTooltip();

        ImGui.Text($"{item.ItemName} (Level {item.RequiredLevel})");
        ImGui.Text($"Recipe Level: {item.RecipeLevel}");
        ImGui.Text($"Yield: {item.Yield}");

        if (item.IsSpecialization)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorIntermediate);
            ImGui.Text("Requires Specialization");
            ImGui.PopStyleColor();
        }

        if (!item.MeetsJobLevelRequirement)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, _colorLevelTooLow);
            ImGui.Text($"Requires {item.JobName} Level {item.RequiredLevel}");
            ImGui.Text("Your level is too low for this recipe");
            ImGui.PopStyleColor();
        }

        if (item.Materials.Any())
        {
            ImGui.Separator();
            ImGui.Text("Materials:");

            foreach (var material in item.Materials.Take(10)) // Limit to avoid huge tooltips
            {
                var color = material.IsSatisfied ? _colorCraftable : _colorNotCraftable;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.Text($"  {material.ItemName}: {material.Available}/{material.Required}");
                ImGui.PopStyleColor();
            }

            if (item.Materials.Count > 10)
            {
                ImGui.Text($"  ... and {item.Materials.Count - 10} more");
            }
        }

        if (item.IntermediateCrafts.Any())
        {
            ImGui.Separator();
            ImGui.Text("Can craft intermediates:");

            foreach (var intermediate in item.IntermediateCrafts.Take(5))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, _colorIntermediate);
                ImGui.Text($"  {intermediate.ItemName} x{intermediate.QuantityNeeded}");
                ImGui.PopStyleColor();
            }
        }

        ImGui.Text("\nLeft-click to open crafting log (UNSAFE - may corrupt other plugin text)");
        ImGui.Text("Right-click to toggle favorite");

        ImGui.EndTooltip();
    }

    /// <summary>
    /// Apply sorting to the filtered items based on table sort specifications.
    /// </summary>
    private void ApplySorting(ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.Specs.ColumnIndex < 0 || sortSpecs.Specs.ColumnIndex >= 6)
            return;

        var ascending = sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending;

        _filteredItems = sortSpecs.Specs.ColumnIndex switch
        {
            1 => ascending 
                ? _filteredItems.OrderBy(x => x.ItemName).ToArray()
                : _filteredItems.OrderByDescending(x => x.ItemName).ToArray(),
            2 => ascending 
                ? _filteredItems.OrderBy(x => x.JobName).ThenBy(x => x.ItemName).ToArray()
                : _filteredItems.OrderByDescending(x => x.JobName).ThenByDescending(x => x.ItemName).ToArray(),
            3 => ascending 
                ? _filteredItems.OrderBy(x => x.RequiredLevel).ThenBy(x => x.ItemName).ToArray()
                : _filteredItems.OrderByDescending(x => x.RequiredLevel).ThenByDescending(x => x.ItemName).ToArray(),
            4 => ascending 
                ? _filteredItems.OrderBy(x => x.MaxCraftable).ThenBy(x => x.ItemName).ToArray()
                : _filteredItems.OrderByDescending(x => x.MaxCraftable).ThenByDescending(x => x.ItemName).ToArray(),
            5 => ascending 
                ? _filteredItems.OrderBy(x => x.CanHq ? 0 : 1).ThenBy(x => x.ItemName).ToArray()
                : _filteredItems.OrderBy(x => x.CanHq ? 1 : 0).ThenByDescending(x => x.ItemName).ToArray(),
            _ => _filteredItems
        };
    }

    /// <summary>
    /// Update the filter options and refresh filtered items.
    /// </summary>
    private void UpdateFilters()
    {
        lock (_lockObject)
        {
            _currentFilter = new FilterOptions
            {
                SearchText = _searchBuffer.Trim(),
                JobFilter = _selectedJobId >= 0 ? (uint)_selectedJobId : null,
                ShowOnlyCraftable = false,
                ShowOnlyFavorites = _showOnlyFavorites,
                ShowOnlyHq = _showOnlyHq,
                ShowIntermediateCrafts = _includeIntermediates,
                SortBy = SortCriteria.ItemName,
                SortDirection = SortDirection.Ascending,
                MaxResults = int.MaxValue // FIXED: Show all items, don't limit to 1000
            };

            ApplyFilters();
        }
    }

    /// <summary>
    /// Apply current filters to the craftable items list.
    /// </summary>
    private void ApplyFilters()
    {
        try
        {
            // DEBUG: Check if Maple Lumber is in the all items list
            var mapleLumberBefore = _allCraftableItems.Where(item => item.ItemName.Contains("Maple Lumber")).ToList();
            _logger.Warning($"DEBUG: Found {mapleLumberBefore.Count} Maple Lumber items before filtering");
            _logger.Warning($"DEBUG: _currentFilter.MaxResults = {_currentFilter.MaxResults}");
            
            var filteredList = _allCraftableItems.Where(item => item.MatchesFilter(_currentFilter));
            
            // DEBUG: Check if Maple Lumber passes filtering
            var mapleLumberAfterFiltering = filteredList.Where(item => item.ItemName.Contains("Maple Lumber")).ToList();
            _logger.Warning($"DEBUG: Found {mapleLumberAfterFiltering.Count} Maple Lumber items after filtering");

            // Note: The _includeIntermediates flag is handled via FilterOptions.ShowIntermediateCrafts
            // in the CraftableItem.MatchesFilter() method. The ShowOnlyCraftable filter
            // in FilterOptions already handles filtering by MaxCraftable > 0.
            // No additional filtering is needed here.

            _filteredItems = filteredList.Take(_currentFilter.MaxResults).ToArray();
            
            // DEBUG: Check if Maple Lumber is in final results
            var mapleLumberFinal = _filteredItems.Where(item => item.ItemName.Contains("Maple Lumber")).ToList();
            _logger.Warning($"DEBUG: Found {mapleLumberFinal.Count} Maple Lumber items in final results (Total items: {_filteredItems.Length})");
            
            // DEBUG: Log total counts at each stage
            _logger.Warning($"DEBUG: Items flow: All={_allCraftableItems.Length} -> Filtered={filteredList.Count()} -> Final={_filteredItems.Length}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error applying filters to craftable items");
            _filteredItems = Array.Empty<CraftableItem>();
        }
    }

    /// <summary>
    /// Refresh the craftable items list from the craft solver.
    /// </summary>
    private void RefreshCraftableItems()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;

        try
        {
            // Check if craft solver is ready (which includes login check)
            if (!_craftSolver.IsReady)
            {
                return;
            }

            var scanOptions = new ScanOptions
            {
                IncludeRetainers = _settingsManager.Settings.ScanRetainersEnabled,
                ResolveIntermediateCrafts = _includeIntermediates,
                FilterByJobLevel = _filterByJobLevel,
                ShowHigherLevelRecipes = _showHigherLevelRecipes,
                EnableParallelProcessing = true,
                MaxRecipesToProcess = int.MaxValue, // Show all recipes
                MinRecipeLevel = 0, // FIXED: Include level 0 recipes like Maple Lumber
                MaxRecipeLevel = 100 // FIXED: Increase max level to ensure we don't exclude high-level recipes
            };

            var craftableItems = _craftSolver.GetCraftableItems(scanOptions);
            
            lock (_lockObject)
            {
                _allCraftableItems = craftableItems.ToArray();
                _totalCraftableCount = _allCraftableItems.Length;
                _lastDataUpdate = DateTime.UtcNow;
                
                // FIXED: Ensure filters are updated with correct MaxResults before applying
                UpdateFilters();
            }

        }
        catch (InvalidOperationException ex) when (ex.Message == "Client is not logged in")
        {
            // Expected when plugin loads before player logs in
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to refresh craftable items");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Toggle favorite status for an item.
    /// </summary>
    private void ToggleFavorite(CraftableItem item)
    {
        try
        {
            item.IsFavorite = !item.IsFavorite;
            
            // In a real implementation, you would persist this to settings
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Failed to toggle favorite for item {item.ItemName}");
        }
    }

    /// <summary>
    /// Open the native FFXIV recipe window for an item.
    /// WARNING: Uses unsafe code that may cause ImGui text corruption in other plugins.
    /// This is an experimental feature - use at your own risk.
    /// </summary>
    private unsafe void OpenRecipeWindow(uint itemId)
    {
        try
        {
            
            // Get the craftable item to find the recipe ID
            var craftableItem = _filteredItems.FirstOrDefault(i => i.ItemId == itemId);
            if (craftableItem == null)
            {
                _logger.Warning($"Item {itemId} not found in filtered items");
                return;
            }
            
#if UNSAFE_CLIENT_STRUCTS
            // WARNING: Unsafe approach using FFXIVClientStructs
            // This may cause ImGui state corruption affecting other plugins
            try
            {
                _logger.Warning($"USING UNSAFE CODE: Opening recipe window for '{craftableItem.ItemName}' - this may cause text corruption in other plugins!");
                
                // Get the AgentRecipeNote instance
                var agentRecipeNote = AgentRecipeNote.Instance();
                if (agentRecipeNote == null)
                {
                    _logger.Warning("AgentRecipeNote instance is null - cannot open recipe window");
                    
                    // Fallback to safe approach
                    _pendingClipboardText = craftableItem.ItemName;
                    _logger.Information("Falling back to clipboard copy approach");
                    return;
                }
                
                // Try to open the recipe by recipe ID
                
                // This is the risky call that may corrupt ImGui state
                agentRecipeNote->OpenRecipeByRecipeId(craftableItem.RecipeId);
                
                _logger.Information($"Successfully called OpenRecipeByRecipeId for recipe {craftableItem.RecipeId}");
                
                // Also copy to clipboard as backup
                _pendingClipboardText = craftableItem.ItemName;
                return; // Success - don't execute fallback
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open recipe window using unsafe code - falling back to safe approach");
                // Fall through to safe approach
            }
#else
            _logger.Information("Unsafe client structs not available - using safe approach");
#endif
            
            // Fallback to safe approach if unsafe code fails or is not available
            _pendingClipboardText = craftableItem.ItemName;
            _logger.Information($"Recipe clicked: '{craftableItem.ItemName}'");
            _logger.Information("To craft this item:");
            _logger.Information("1. Press 'N' to open your Crafting Log");
            _logger.Information($"2. Search for '{craftableItem.ItemName}' (already copied to clipboard)");
            _logger.Information("3. Or use the Quick Synthesis option if available");
            
            // Show a more visible notification to the user
            var message = $"[ReadyCrafter] '{craftableItem.ItemName}' copied to clipboard. Press 'N' to open Crafting Log.";
            
            // Try to display in chat
            try
            {
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    // This is a notification that will appear in the user's chat
                    Plugin.PluginLog.Information(message);
                });
            }
            catch (Exception notifyEx)
            {
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to open recipe window for item ID {itemId}");
        }
    }

    /// <summary>
    /// Handle solve completed event from craft solver.
    /// </summary>
    private void OnSolveCompleted(object? sender, Services.SolveCompletedEventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            // Refresh the items since solve completed
            RefreshCraftableItems();

        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error handling solve completed event");
        }
    }

    /// <summary>
    /// Handle cache invalidated event from craft solver.
    /// </summary>
    private void OnCacheInvalidated(object? sender, Services.CacheInvalidatedEventArgs e)
    {
        if (_disposed)
            return;

        RefreshCraftableItems();
    }

    /// <summary>
    /// Clean up resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _craftSolver.SolveCompleted -= OnSolveCompleted;
            _craftSolver.CacheInvalidated -= OnCacheInvalidated;

            _disposed = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error disposing ReadyCrafterWindow");
        }
    }
}