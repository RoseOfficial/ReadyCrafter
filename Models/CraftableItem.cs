using System;
using System.Collections.Generic;

namespace ReadyCrafter.Models;

/// <summary>
/// Represents a craftable recipe with current inventory calculations.
/// This is the primary data structure for the main UI display.
/// </summary>
public sealed class CraftableItem
{
    /// <summary>
    /// Unique identifier for the recipe from Lumina data.
    /// </summary>
    public uint RecipeId { get; set; }

    /// <summary>
    /// Item ID of the crafted result.
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// Localized name of the item being crafted.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Job class required for this recipe (Carpenter, Blacksmith, etc.).
    /// </summary>
    public uint JobId { get; set; }

    /// <summary>
    /// Localized job name for display purposes.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Recipe level requirement.
    /// </summary>
    public uint RecipeLevel { get; set; }

    /// <summary>
    /// Character level requirement for this recipe.
    /// </summary>
    public uint RequiredLevel { get; set; }

    /// <summary>
    /// Number of items produced per craft.
    /// </summary>
    public uint Yield { get; set; } = 1;

    /// <summary>
    /// Whether this recipe can produce HQ results.
    /// </summary>
    public bool CanHq { get; set; }

    /// <summary>
    /// Maximum number of times this recipe can be crafted with current inventory.
    /// Calculated based on available materials.
    /// </summary>
    public int MaxCraftable { get; set; }

    /// <summary>
    /// Whether all required materials are available in sufficient quantities.
    /// </summary>
    public bool HasAllMaterials { get; set; }

    /// <summary>
    /// List of required materials with current availability.
    /// </summary>
    public List<MaterialRequirement> Materials { get; set; } = new();

    /// <summary>
    /// List of intermediate materials that could be crafted to fulfill requirements.
    /// Only populated when dependency resolution is requested.
    /// </summary>
    public List<IntermediateCraft> IntermediateCrafts { get; set; } = new();

    /// <summary>
    /// Whether this recipe is marked as favorite by the user.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Timestamp when this item data was last calculated.
    /// Used for cache invalidation.
    /// </summary>
    public DateTime LastCalculated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Icon ID for the crafted item.
    /// </summary>
    public uint IconId { get; set; }

    /// <summary>
    /// Item level of the crafted result.
    /// </summary>
    public uint ItemLevel { get; set; }

    /// <summary>
    /// Whether this is a specialization recipe requiring master book.
    /// </summary>
    public bool IsSpecialization { get; set; }

    /// <summary>
    /// Whether the player meets the job level requirement for this recipe.
    /// True if the player's job level is sufficient, false if higher level is needed.
    /// </summary>
    public bool MeetsJobLevelRequirement { get; set; } = true;

    /// <summary>
    /// Required craftsmanship stat for the recipe.
    /// </summary>
    public uint RequiredCraftsmanship { get; set; }

    /// <summary>
    /// Required control stat for the recipe.
    /// </summary>
    public uint RequiredControl { get; set; }

    /// <summary>
    /// Quality threshold required for HQ result (if applicable).
    /// </summary>
    public uint QualityThreshold { get; set; }

    /// <summary>
    /// Calculate a search score for text filtering.
    /// </summary>
    public float CalculateSearchScore(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return 1.0f;

        var lowerSearch = searchText.ToLowerInvariant();
        var lowerName = ItemName.ToLowerInvariant();
        var lowerJob = JobName.ToLowerInvariant();

        // Exact name match gets highest score
        if (lowerName == lowerSearch)
            return 1.0f;

        // Start of name match gets high score
        if (lowerName.StartsWith(lowerSearch))
            return 0.9f;

        // Name contains search gets medium score
        if (lowerName.Contains(lowerSearch))
            return 0.7f;

        // Job name match gets lower score
        if (lowerJob.Contains(lowerSearch))
            return 0.5f;

        // Material name match gets lowest score
        foreach (var material in Materials)
        {
            if (material.ItemName.ToLowerInvariant().Contains(lowerSearch))
                return 0.3f;
        }

        return 0.0f;
    }

    /// <summary>
    /// Check if this item matches the given filter options.
    /// </summary>
    public bool MatchesFilter(FilterOptions filter)
    {
        // Job filter
        if (filter.JobFilter.HasValue && JobId != filter.JobFilter.Value)
            return false;

        // Level range filter
        if (filter.MinLevel.HasValue && RequiredLevel < filter.MinLevel.Value)
            return false;

        if (filter.MaxLevel.HasValue && RequiredLevel > filter.MaxLevel.Value)
            return false;

        // Craftable filter
        if (filter.ShowOnlyCraftable && MaxCraftable <= 0)
            return false;

        // Favorites filter
        if (filter.ShowOnlyFavorites && !IsFavorite)
            return false;

        // HQ filter
        if (filter.ShowOnlyHq && !CanHq)
            return false;

        // Specialization filter
        if (filter.HideSpecialization && IsSpecialization)
            return false;

        // Text search
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var score = CalculateSearchScore(filter.SearchText);
            if (score < 0.1f)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Represents a material requirement for a recipe.
/// </summary>
public sealed class MaterialRequirement
{
    /// <summary>
    /// Item ID of the required material.
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// Localized name of the material.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Quantity required for one craft.
    /// </summary>
    public uint Required { get; set; }

    /// <summary>
    /// Quantity currently available in inventory.
    /// </summary>
    public uint Available { get; set; }

    /// <summary>
    /// Whether this material requirement is satisfied.
    /// </summary>
    public bool IsSatisfied => Available >= Required;

    /// <summary>
    /// How many additional items are needed.
    /// </summary>
    public uint Needed => Available >= Required ? 0 : Required - Available;

    /// <summary>
    /// Whether HQ version of this material is preferred/required.
    /// </summary>
    public bool RequiresHq { get; set; }

    /// <summary>
    /// Icon ID for the material item.
    /// </summary>
    public uint IconId { get; set; }
}

/// <summary>
/// Represents an intermediate craft that could be made to fulfill material requirements.
/// </summary>
public sealed class IntermediateCraft
{
    /// <summary>
    /// Recipe ID for the intermediate craft.
    /// </summary>
    public uint RecipeId { get; set; }

    /// <summary>
    /// Item ID of the intermediate result.
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// Name of the intermediate item.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Job required for this intermediate craft.
    /// </summary>
    public uint JobId { get; set; }

    /// <summary>
    /// How many of this intermediate item are needed.
    /// </summary>
    public uint QuantityNeeded { get; set; }

    /// <summary>
    /// How many times this recipe can be crafted with current materials.
    /// </summary>
    public int MaxCraftable { get; set; }

    /// <summary>
    /// Whether crafting this intermediate would be beneficial.
    /// </summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// Materials required for this intermediate craft.
    /// </summary>
    public List<MaterialRequirement> Materials { get; set; } = new();
}