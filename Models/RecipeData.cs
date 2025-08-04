using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ReadyCrafter.Models;

/// <summary>
/// Cached recipe information from Lumina data.
/// Optimized for fast lookups and minimal memory usage.
/// </summary>
public sealed class RecipeData
{
    /// <summary>
    /// Unique recipe identifier from game data.
    /// </summary>
    public uint RecipeId { get; set; }

    /// <summary>
    /// Item ID of the crafted result.
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// Crafting job class ID (Carpenter = 8, Blacksmith = 9, etc.).
    /// </summary>
    public uint JobId { get; set; }
    
    /// <summary>
    /// Crafting job class as byte (alias for JobId for backward compatibility).
    /// </summary>
    public byte CraftJob 
    { 
        get => (byte)JobId; 
        set => JobId = value; 
    }
    
    /// <summary>
    /// Recipe level as byte (alias for RecipeLevel for backward compatibility).
    /// </summary>
    public byte Level 
    { 
        get => (byte)RecipeLevel; 
        set => RecipeLevel = value; 
    }

    /// <summary>
    /// Recipe level (not the same as character level requirement).
    /// </summary>
    public uint RecipeLevel { get; set; }

    /// <summary>
    /// Character level required to craft this recipe.
    /// </summary>
    public uint RequiredLevel { get; set; }

    /// <summary>
    /// Number of items produced per successful craft.
    /// </summary>
    public uint Yield { get; set; } = 1;

    /// <summary>
    /// Whether this recipe can produce high-quality results.
    /// </summary>
    public bool CanHq { get; set; }

    /// <summary>
    /// Required craftsmanship stat for the recipe.
    /// Used for stat requirement checking.
    /// </summary>
    public uint RequiredCraftsmanship { get; set; }

    /// <summary>
    /// Required control stat for the recipe.
    /// Used for stat requirement checking.
    /// </summary>
    public uint RequiredControl { get; set; }

    /// <summary>
    /// Quality threshold needed for HQ result.
    /// </summary>
    public uint QualityThreshold { get; set; }

    /// <summary>
    /// Durability points for this recipe.
    /// </summary>
    public uint Durability { get; set; }

    /// <summary>
    /// Progress points needed to complete the craft.
    /// </summary>
    public uint Progress { get; set; }

    /// <summary>
    /// Maximum quality points achievable.
    /// </summary>
    public uint MaxQuality { get; set; }

    /// <summary>
    /// Whether this is a specialization recipe requiring master book.
    /// </summary>
    public bool IsSpecialization { get; set; }

    /// <summary>
    /// Whether this is an expert recipe with special requirements.
    /// </summary>
    public bool IsExpert { get; set; }

    /// <summary>
    /// Recipe ingredients with quantities.
    /// Optimized for fast lookup during inventory scanning.
    /// </summary>
    public List<RecipeIngredient> Ingredients { get; set; } = new();

    /// <summary>
    /// Cached item name for display purposes.
    /// Populated during data loading to avoid repeated Lumina lookups.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Cached job name for display purposes.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Icon ID for the crafted item.
    /// </summary>
    public uint IconId { get; set; }

    /// <summary>
    /// Item level of the crafted result.
    /// </summary>
    public uint ItemLevel { get; set; }

    /// <summary>
    /// Timestamp when this recipe data was cached.
    /// Used for cache invalidation.
    /// </summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hash of the recipe data for change detection.
    /// </summary>
    public string DataHash { get; set; } = string.Empty;

    /// <summary>
    /// Whether this recipe data has been validated against current game data.
    /// </summary>
    public bool IsValidated { get; set; } = false;

    /// <summary>
    /// Additional metadata for special recipe types.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Check if this recipe data is still valid based on cache duration.
    /// </summary>
    public bool IsValid(TimeSpan maxAge)
    {
        return IsValidated && DateTime.UtcNow - CachedAt < maxAge;
    }

    /// <summary>
    /// Calculate the total material cost for crafting a specific quantity.
    /// </summary>
    public Dictionary<uint, uint> CalculateMaterialCost(uint quantity)
    {
        var materials = new Dictionary<uint, uint>();
        
        foreach (var ingredient in Ingredients)
        {
            var totalNeeded = ingredient.Quantity * quantity;
            
            if (materials.ContainsKey(ingredient.ItemId))
                materials[ingredient.ItemId] += totalNeeded;
            else
                materials[ingredient.ItemId] = totalNeeded;
        }
        
        return materials;
    }

    /// <summary>
    /// Get all unique material item IDs required for this recipe.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<uint> MaterialItemIds
    {
        get
        {
            foreach (var ingredient in Ingredients)
            {
                yield return ingredient.ItemId;
            }
        }
    }

    /// <summary>
    /// Check if this recipe requires a specific material.
    /// </summary>
    public bool RequiresMaterial(uint itemId)
    {
        foreach (var ingredient in Ingredients)
        {
            if (ingredient.ItemId == itemId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get quantity of a specific material required.
    /// </summary>
    public uint GetMaterialQuantity(uint itemId)
    {
        foreach (var ingredient in Ingredients)
        {
            if (ingredient.ItemId == itemId)
                return ingredient.Quantity;
        }
        return 0;
    }

    /// <summary>
    /// Generate a hash for this recipe's data.
    /// Used for change detection and caching.
    /// </summary>
    public string GenerateDataHash()
    {
        var hashSource = $"{RecipeId}:{ItemId}:{JobId}:{RecipeLevel}:{RequiredLevel}:{Yield}:{CanHq}:{IsSpecialization}:{IsExpert}";
        
        foreach (var ingredient in Ingredients)
        {
            hashSource += $":{ingredient.ItemId}:{ingredient.Quantity}:{ingredient.RequiresHq}";
        }
        
        // Simple hash calculation - in production you might use SHA256 or similar
        return hashSource.GetHashCode().ToString("X8");
    }

    /// <summary>
    /// Update the data hash and mark as validated.
    /// </summary>
    public void UpdateValidation()
    {
        DataHash = GenerateDataHash();
        IsValidated = true;
        CachedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Represents a single ingredient in a recipe.
/// Optimized for memory usage and fast lookups.
/// </summary>
public sealed class RecipeIngredient
{
    /// <summary>
    /// Item ID of the required ingredient.
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// Quantity of this ingredient required per craft.
    /// </summary>
    public uint Quantity { get; set; }

    /// <summary>
    /// Whether this ingredient must be high-quality.
    /// Most ingredients accept both HQ and NQ, but some recipes have HQ requirements.
    /// </summary>
    public bool RequiresHq { get; set; } = false;

    /// <summary>
    /// Cached item name for display purposes.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Icon ID for the ingredient item.
    /// </summary>
    public uint IconId { get; set; }

    /// <summary>
    /// Whether this ingredient can be obtained from vendors.
    /// Used for suggesting acquisition methods.
    /// </summary>
    public bool IsVendorItem { get; set; } = false;

    /// <summary>
    /// Whether this ingredient can be crafted by any job.
    /// Used for intermediate craft resolution.
    /// </summary>
    public bool IsCraftable { get; set; } = false;

    /// <summary>
    /// Market board average price for this ingredient.
    /// Used for cost calculations (optional).
    /// </summary>
    public uint MarketPrice { get; set; } = 0;

    /// <summary>
    /// Vendor price for this ingredient (if available from vendors).
    /// </summary>
    public uint VendorPrice { get; set; } = 0;

    /// <summary>
    /// Create a copy of this ingredient with a different quantity.
    /// </summary>
    public RecipeIngredient WithQuantity(uint newQuantity)
    {
        return new RecipeIngredient
        {
            ItemId = ItemId,
            Quantity = newQuantity,
            RequiresHq = RequiresHq,
            ItemName = ItemName,
            IconId = IconId,
            IsVendorItem = IsVendorItem,
            IsCraftable = IsCraftable,
            MarketPrice = MarketPrice,
            VendorPrice = VendorPrice
        };
    }
}

/// <summary>
/// Compact recipe lookup structure optimized for fast searches.
/// Used internally by the recipe repository for performance.
/// </summary>
public sealed class RecipeIndex
{
    /// <summary>
    /// Dictionary mapping item IDs to recipe IDs that produce them.
    /// One item can have multiple recipes (different jobs, levels).
    /// </summary>
    public Dictionary<uint, List<uint>> ItemToRecipes { get; set; } = new();

    /// <summary>
    /// Dictionary mapping job IDs to recipe IDs for that job.
    /// </summary>
    public Dictionary<uint, List<uint>> JobToRecipes { get; set; } = new();

    /// <summary>
    /// Dictionary mapping material item IDs to recipe IDs that use them.
    /// Used for finding what can be made with specific materials.
    /// </summary>
    public Dictionary<uint, List<uint>> MaterialToRecipes { get; set; } = new();

    /// <summary>
    /// Dictionary mapping recipe levels to recipe IDs.
    /// Used for level-based filtering.
    /// </summary>
    public Dictionary<uint, List<uint>> LevelToRecipes { get; set; } = new();

    /// <summary>
    /// Set of recipe IDs that can produce HQ results.
    /// </summary>
    public HashSet<uint> HqCapableRecipes { get; set; } = new();

    /// <summary>
    /// Set of specialization recipe IDs.
    /// </summary>
    public HashSet<uint> SpecializationRecipes { get; set; } = new();

    /// <summary>
    /// Set of expert recipe IDs.
    /// </summary>
    public HashSet<uint> ExpertRecipes { get; set; } = new();

    /// <summary>
    /// Timestamp when this index was built.
    /// </summary>
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Build the index from a collection of recipe data.
    /// </summary>
    public void BuildIndex(IEnumerable<RecipeData> recipes)
    {
        // Clear existing index
        ItemToRecipes.Clear();
        JobToRecipes.Clear();
        MaterialToRecipes.Clear();
        LevelToRecipes.Clear();
        HqCapableRecipes.Clear();
        SpecializationRecipes.Clear();
        ExpertRecipes.Clear();

        foreach (var recipe in recipes)
        {
            // Index by produced item
            if (!ItemToRecipes.ContainsKey(recipe.ItemId))
                ItemToRecipes[recipe.ItemId] = new List<uint>();
            ItemToRecipes[recipe.ItemId].Add(recipe.RecipeId);

            // Index by job
            if (!JobToRecipes.ContainsKey(recipe.JobId))
                JobToRecipes[recipe.JobId] = new List<uint>();
            JobToRecipes[recipe.JobId].Add(recipe.RecipeId);

            // Index by level
            if (!LevelToRecipes.ContainsKey(recipe.RecipeLevel))
                LevelToRecipes[recipe.RecipeLevel] = new List<uint>();
            LevelToRecipes[recipe.RecipeLevel].Add(recipe.RecipeId);

            // Index by materials
            foreach (var ingredient in recipe.Ingredients)
            {
                if (!MaterialToRecipes.ContainsKey(ingredient.ItemId))
                    MaterialToRecipes[ingredient.ItemId] = new List<uint>();
                MaterialToRecipes[ingredient.ItemId].Add(recipe.RecipeId);
            }

            // Special flags
            if (recipe.CanHq)
                HqCapableRecipes.Add(recipe.RecipeId);
            
            if (recipe.IsSpecialization)
                SpecializationRecipes.Add(recipe.RecipeId);
            
            if (recipe.IsExpert)
                ExpertRecipes.Add(recipe.RecipeId);
        }

        IndexedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Get recipes that produce a specific item.
    /// </summary>
    public IEnumerable<uint> GetRecipesForItem(uint itemId)
    {
        return ItemToRecipes.TryGetValue(itemId, out var recipes) ? recipes : Enumerable.Empty<uint>();
    }

    /// <summary>
    /// Get recipes for a specific job.
    /// </summary>
    public IEnumerable<uint> GetRecipesForJob(uint jobId)
    {
        return JobToRecipes.TryGetValue(jobId, out var recipes) ? recipes : Enumerable.Empty<uint>();
    }

    /// <summary>
    /// Get recipes that use a specific material.
    /// </summary>
    public IEnumerable<uint> GetRecipesUsingMaterial(uint itemId)
    {
        return MaterialToRecipes.TryGetValue(itemId, out var recipes) ? recipes : Enumerable.Empty<uint>();
    }
}