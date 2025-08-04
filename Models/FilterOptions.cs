using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ReadyCrafter.Models;

/// <summary>
/// Filtering and search options for the craftable items UI.
/// Controls what recipes are displayed and how they are sorted.
/// </summary>
public sealed class FilterOptions
{
    /// <summary>
    /// Text search filter for item names, job names, or materials.
    /// Case-insensitive partial matching.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Filter by specific crafting job. Null means all jobs.
    /// </summary>
    public uint? JobFilter { get; set; }

    /// <summary>
    /// Minimum recipe level to display.
    /// </summary>
    public uint? MinLevel { get; set; }

    /// <summary>
    /// Maximum recipe level to display.
    /// </summary>
    public uint? MaxLevel { get; set; }

    /// <summary>
    /// Show only recipes that can currently be crafted.
    /// </summary>
    public bool ShowOnlyCraftable { get; set; } = false;

    /// <summary>
    /// Show only recipes marked as favorites.
    /// </summary>
    public bool ShowOnlyFavorites { get; set; } = false;

    /// <summary>
    /// Show only recipes that can produce HQ results.
    /// </summary>
    public bool ShowOnlyHq { get; set; } = false;

    /// <summary>
    /// Hide specialization recipes (requires master books).
    /// </summary>
    public bool HideSpecialization { get; set; } = false;

    /// <summary>
    /// Hide expert recipes.
    /// </summary>
    public bool HideExpert { get; set; } = false;

    /// <summary>
    /// Show only recipes that have missing materials.
    /// Useful for shopping lists.
    /// </summary>
    public bool ShowOnlyMissingMaterials { get; set; } = false;

    /// <summary>
    /// Minimum quantity that can be crafted to show recipe.
    /// </summary>
    public int MinCraftableQuantity { get; set; } = 0;

    /// <summary>
    /// Maximum quantity that can be crafted to show recipe.
    /// Useful for finding recipes to make with excess materials.
    /// </summary>
    public int? MaxCraftableQuantity { get; set; }

    /// <summary>
    /// Sort criteria for the results.
    /// </summary>
    public SortCriteria SortBy { get; set; } = SortCriteria.ItemName;

    /// <summary>
    /// Sort direction (ascending or descending).
    /// </summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;

    /// <summary>
    /// Maximum number of results to return.
    /// Used for performance when many recipes match.
    /// </summary>
    public int MaxResults { get; set; } = 1000;

    /// <summary>
    /// Whether to group results by job.
    /// </summary>
    public bool GroupByJob { get; set; } = false;

    /// <summary>
    /// Whether to show material requirements in the results.
    /// </summary>
    public bool ShowMaterials { get; set; } = true;

    /// <summary>
    /// Whether to show intermediate craft suggestions.
    /// </summary>
    public bool ShowIntermediateCrafts { get; set; } = true;

    /// <summary>
    /// List of recipe IDs to exclude from results.
    /// Used for hiding unwanted recipes.
    /// </summary>
    public HashSet<uint> ExcludedRecipes { get; set; } = new();

    /// <summary>
    /// List of job IDs to exclude from results.
    /// </summary>
    public HashSet<uint> ExcludedJobs { get; set; } = new();

    /// <summary>
    /// Additional custom filters as key-value pairs.
    /// Extensible for future filter types.
    /// </summary>
    public Dictionary<string, object> CustomFilters { get; set; } = new();

    /// <summary>
    /// Whether this filter configuration has any active filters.
    /// </summary>
    [JsonIgnore]
    public bool HasActiveFilters
    {
        get
        {
            return !string.IsNullOrWhiteSpace(SearchText) ||
                   JobFilter.HasValue ||
                   MinLevel.HasValue ||
                   MaxLevel.HasValue ||
                   ShowOnlyCraftable ||
                   ShowOnlyFavorites ||
                   ShowOnlyHq ||
                   HideSpecialization ||
                   HideExpert ||
                   ShowOnlyMissingMaterials ||
                   MinCraftableQuantity > 0 ||
                   MaxCraftableQuantity.HasValue ||
                   ExcludedRecipes.Any() ||
                   ExcludedJobs.Any() ||
                   CustomFilters.Any();
        }
    }

    /// <summary>
    /// Create default filter options.
    /// </summary>
    public static FilterOptions CreateDefault()
    {
        return new FilterOptions();
    }

    /// <summary>
    /// Create filter options optimized for finding craftable recipes.
    /// </summary>
    public static FilterOptions CreateCraftableOnly()
    {
        return new FilterOptions
        {
            ShowOnlyCraftable = true,
            SortBy = SortCriteria.MaxCraftable,
            SortDirection = SortDirection.Descending,
            ShowMaterials = true,
            ShowIntermediateCrafts = true
        };
    }

    /// <summary>
    /// Create filter options for favorites view.
    /// </summary>
    public static FilterOptions CreateFavoritesOnly()
    {
        return new FilterOptions
        {
            ShowOnlyFavorites = true,
            SortBy = SortCriteria.ItemName,
            SortDirection = SortDirection.Ascending,
            ShowMaterials = true
        };
    }

    /// <summary>
    /// Create filter options for a specific job.
    /// </summary>
    public static FilterOptions CreateForJob(uint jobId, string jobName = "")
    {
        return new FilterOptions
        {
            JobFilter = jobId,
            SortBy = SortCriteria.RecipeLevel,
            SortDirection = SortDirection.Ascending,
            ShowMaterials = true
        };
    }

    /// <summary>
    /// Create filter options for finding recipes with missing materials.
    /// </summary>
    public static FilterOptions CreateMissingMaterials()
    {
        return new FilterOptions
        {
            ShowOnlyMissingMaterials = true,
            SortBy = SortCriteria.ItemName,
            SortDirection = SortDirection.Ascending,
            ShowMaterials = true,
            ShowIntermediateCrafts = true
        };
    }

    /// <summary>
    /// Reset all filters to default values.
    /// </summary>
    public void Reset()
    {
        SearchText = string.Empty;
        JobFilter = null;
        MinLevel = null;
        MaxLevel = null;
        ShowOnlyCraftable = false;
        ShowOnlyFavorites = false;
        ShowOnlyHq = false;
        HideSpecialization = false;
        HideExpert = false;
        ShowOnlyMissingMaterials = false;
        MinCraftableQuantity = 0;
        MaxCraftableQuantity = null;
        SortBy = SortCriteria.ItemName;
        SortDirection = SortDirection.Ascending;
        MaxResults = 1000;
        GroupByJob = false;
        ShowMaterials = true;
        ShowIntermediateCrafts = true;
        ExcludedRecipes.Clear();
        ExcludedJobs.Clear();
        CustomFilters.Clear();
    }

    /// <summary>
    /// Create a copy of these filter options.
    /// </summary>
    public FilterOptions Clone()
    {
        return new FilterOptions
        {
            SearchText = SearchText,
            JobFilter = JobFilter,
            MinLevel = MinLevel,
            MaxLevel = MaxLevel,
            ShowOnlyCraftable = ShowOnlyCraftable,
            ShowOnlyFavorites = ShowOnlyFavorites,
            ShowOnlyHq = ShowOnlyHq,
            HideSpecialization = HideSpecialization,
            HideExpert = HideExpert,
            ShowOnlyMissingMaterials = ShowOnlyMissingMaterials,
            MinCraftableQuantity = MinCraftableQuantity,
            MaxCraftableQuantity = MaxCraftableQuantity,
            SortBy = SortBy,
            SortDirection = SortDirection,
            MaxResults = MaxResults,
            GroupByJob = GroupByJob,
            ShowMaterials = ShowMaterials,
            ShowIntermediateCrafts = ShowIntermediateCrafts,
            ExcludedRecipes = new HashSet<uint>(ExcludedRecipes),
            ExcludedJobs = new HashSet<uint>(ExcludedJobs),
            CustomFilters = new Dictionary<string, object>(CustomFilters)
        };
    }

    /// <summary>
    /// Validate and fix any invalid filter configurations.
    /// </summary>
    public void Validate()
    {
        // Ensure level ranges are valid
        if (MinLevel.HasValue && MaxLevel.HasValue && MinLevel.Value > MaxLevel.Value)
        {
            (MinLevel, MaxLevel) = (MaxLevel, MinLevel);
        }

        // Ensure reasonable limits
        MaxResults = Math.Max(10, Math.Min(MaxResults, 10000));
        MinCraftableQuantity = Math.Max(0, MinCraftableQuantity);
        
        if (MaxCraftableQuantity.HasValue)
        {
            MaxCraftableQuantity = Math.Max(MinCraftableQuantity, MaxCraftableQuantity.Value);
        }

        // Clean up search text
        SearchText = SearchText?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Apply this filter to a collection of craftable items.
    /// </summary>
    public IEnumerable<CraftableItem> Apply(IEnumerable<CraftableItem> items)
    {
        var filtered = items.Where(item => item.MatchesFilter(this));

        // Apply sorting
        filtered = SortDirection == SortDirection.Ascending 
            ? ApplySortingAscending(filtered) 
            : ApplySortingDescending(filtered);

        // Apply result limit
        if (MaxResults > 0)
        {
            filtered = filtered.Take(MaxResults);
        }

        return filtered;
    }

    /// <summary>
    /// Apply ascending sort to the filtered results.
    /// </summary>
    private IEnumerable<CraftableItem> ApplySortingAscending(IEnumerable<CraftableItem> items)
    {
        return SortBy switch
        {
            SortCriteria.ItemName => items.OrderBy(x => x.ItemName),
            SortCriteria.JobName => items.OrderBy(x => x.JobName).ThenBy(x => x.ItemName),
            SortCriteria.RecipeLevel => items.OrderBy(x => x.RecipeLevel).ThenBy(x => x.ItemName),
            SortCriteria.RequiredLevel => items.OrderBy(x => x.RequiredLevel).ThenBy(x => x.ItemName),
            SortCriteria.MaxCraftable => items.OrderBy(x => x.MaxCraftable).ThenBy(x => x.ItemName),
            SortCriteria.ItemLevel => items.OrderBy(x => x.ItemLevel).ThenBy(x => x.ItemName),
            SortCriteria.LastCalculated => items.OrderBy(x => x.LastCalculated).ThenBy(x => x.ItemName),
            _ => items.OrderBy(x => x.ItemName)
        };
    }

    /// <summary>
    /// Apply descending sort to the filtered results.
    /// </summary>
    private IEnumerable<CraftableItem> ApplySortingDescending(IEnumerable<CraftableItem> items)
    {
        return SortBy switch
        {
            SortCriteria.ItemName => items.OrderByDescending(x => x.ItemName),
            SortCriteria.JobName => items.OrderByDescending(x => x.JobName).ThenByDescending(x => x.ItemName),
            SortCriteria.RecipeLevel => items.OrderByDescending(x => x.RecipeLevel).ThenByDescending(x => x.ItemName),
            SortCriteria.RequiredLevel => items.OrderByDescending(x => x.RequiredLevel).ThenByDescending(x => x.ItemName),
            SortCriteria.MaxCraftable => items.OrderByDescending(x => x.MaxCraftable).ThenByDescending(x => x.ItemName),
            SortCriteria.ItemLevel => items.OrderByDescending(x => x.ItemLevel).ThenByDescending(x => x.ItemName),
            SortCriteria.LastCalculated => items.OrderByDescending(x => x.LastCalculated).ThenByDescending(x => x.ItemName),
            _ => items.OrderByDescending(x => x.ItemName)
        };
    }

    /// <summary>
    /// Get a human-readable description of the active filters.
    /// </summary>
    public string GetDescription()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(SearchText))
            parts.Add($"Search: '{SearchText}'");

        if (JobFilter.HasValue)
            parts.Add($"Job: {JobFilter.Value}");

        if (MinLevel.HasValue || MaxLevel.HasValue)
        {
            if (MinLevel.HasValue && MaxLevel.HasValue)
                parts.Add($"Level: {MinLevel.Value}-{MaxLevel.Value}");
            else if (MinLevel.HasValue)
                parts.Add($"Level: {MinLevel.Value}+");
            else
                parts.Add($"Level: up to {MaxLevel.Value}");
        }

        if (ShowOnlyCraftable)
            parts.Add("Craftable only");

        if (ShowOnlyFavorites)
            parts.Add("Favorites only");

        if (ShowOnlyHq)
            parts.Add("HQ capable only");

        if (HideSpecialization)
            parts.Add("No specialization");

        if (HideExpert)
            parts.Add("No expert recipes");

        if (ShowOnlyMissingMaterials)
            parts.Add("Missing materials only");

        if (parts.Count == 0)
            return "No filters active";

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Available sort criteria for craftable items.
/// </summary>
public enum SortCriteria
{
    /// <summary>
    /// Sort by item name alphabetically.
    /// </summary>
    ItemName,

    /// <summary>
    /// Sort by crafting job name.
    /// </summary>
    JobName,

    /// <summary>
    /// Sort by recipe level.
    /// </summary>
    RecipeLevel,

    /// <summary>
    /// Sort by required character level.
    /// </summary>
    RequiredLevel,

    /// <summary>
    /// Sort by maximum craftable quantity.
    /// </summary>
    MaxCraftable,

    /// <summary>
    /// Sort by item level.
    /// </summary>
    ItemLevel,

    /// <summary>
    /// Sort by when the recipe data was last calculated.
    /// </summary>
    LastCalculated
}

/// <summary>
/// Sort direction options.
/// </summary>
public enum SortDirection
{
    /// <summary>
    /// Sort in ascending order (A-Z, 0-9, low to high).
    /// </summary>
    Ascending,

    /// <summary>
    /// Sort in descending order (Z-A, 9-0, high to low).
    /// </summary>
    Descending
}

/// <summary>
/// Predefined filter presets for common use cases.
/// </summary>
public static class FilterPresets
{
    /// <summary>
    /// All available crafting job IDs and their display names.
    /// Used for job filter dropdowns.
    /// </summary>
    public static readonly Dictionary<uint, string> CraftingJobs = new()
    {
        { 8, "Carpenter" },
        { 9, "Blacksmith" },
        { 10, "Armorer" },
        { 11, "Goldsmith" },
        { 12, "Leatherworker" },
        { 13, "Weaver" },
        { 14, "Alchemist" },
        { 15, "Culinarian" }
    };

    /// <summary>
    /// Common level ranges for filtering.
    /// </summary>
    public static readonly Dictionary<string, (uint Min, uint Max)> LevelRanges = new()
    {
        { "1-15 (Beginner)", (1, 15) },
        { "16-30 (Novice)", (16, 30) },
        { "31-50 (Journeyman)", (31, 50) },
        { "51-60 (Artisan)", (51, 60) },
        { "61-70 (Master)", (61, 70) },
        { "71-80 (Grandmaster)", (71, 80) },
        { "81-90 (Legendary)", (81, 90) }
    };

    /// <summary>
    /// Get a filter preset for endgame crafting.
    /// </summary>
    public static FilterOptions Endgame => new()
    {
        MinLevel = 80,
        ShowOnlyCraftable = true,
        SortBy = SortCriteria.RecipeLevel,
        SortDirection = SortDirection.Descending,
        ShowMaterials = true,
        ShowIntermediateCrafts = true
    };

    /// <summary>
    /// Get a filter preset for leveling recipes.
    /// </summary>
    public static FilterOptions Leveling => new()
    {
        MaxLevel = 79,
        ShowOnlyCraftable = true,
        SortBy = SortCriteria.RequiredLevel,
        SortDirection = SortDirection.Ascending,
        ShowMaterials = false,
        HideSpecialization = true
    };

    /// <summary>
    /// Get a filter preset for quick crafting.
    /// </summary>
    public static FilterOptions QuickCrafts => new()
    {
        ShowOnlyCraftable = true,
        MinCraftableQuantity = 5,
        SortBy = SortCriteria.MaxCraftable,
        SortDirection = SortDirection.Descending,
        ShowMaterials = false,
        HideSpecialization = true,
        HideExpert = true
    };
}