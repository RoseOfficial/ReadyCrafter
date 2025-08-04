using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ReadyCrafter.Models;

/// <summary>
/// Configuration options for inventory scanning and recipe analysis.
/// Defines which containers to scan and how to process the data.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>
    /// Default container IDs for standard inventory scanning.
    /// Includes main inventory, armory chest, chocobo saddlebag, and FC chest.
    /// </summary>
    public static readonly uint[] DefaultContainers = 
    {
        0, 1, 2, 3, 4,      // Main inventory bags
        5, 6, 7, 8, 9,      // Armory chest sections
        10, 11, 12,         // Additional armory slots
        13, 14, 15, 16,     // Chocobo saddlebag
        17, 18, 19, 20,     // Premium saddlebag
        21, 22, 23, 24,     // Free Company chest tabs
        25, 26, 27, 28,     // Additional FC chest tabs
        29, 30, 31, 32, 33  // Extra storage containers
    };

    /// <summary>
    /// Retainer container ID range (1000-1070).
    /// Each retainer has multiple containers for different item types.
    /// </summary>
    public static readonly uint[] RetainerContainers = 
        Enumerable.Range(1000, 71).Select(x => (uint)x).ToArray();

    /// <summary>
    /// List of container IDs to scan for materials.
    /// </summary>
    public HashSet<uint> ContainerIds { get; set; } = new(DefaultContainers);

    /// <summary>
    /// Whether to include retainer inventories in the scan.
    /// This significantly increases scan time but provides complete material availability.
    /// </summary>
    public bool IncludeRetainers { get; set; } = false;

    /// <summary>
    /// Maximum recipe level to include in results.
    /// Higher level recipes require more processing time.
    /// </summary>
    public uint MaxRecipeLevel { get; set; } = 90;

    /// <summary>
    /// Minimum recipe level to include in results.
    /// Used to filter out low-level recipes that may not be relevant.
    /// </summary>
    public uint MinRecipeLevel { get; set; } = 1;

    /// <summary>
    /// Whether to resolve one level of intermediate crafts.
    /// When enabled, will check if missing materials can be crafted.
    /// </summary>
    public bool ResolveIntermediateCrafts { get; set; } = true;

    /// <summary>
    /// Maximum number of recipes to process in a single scan.
    /// Used to maintain performance targets (&lt;150ms).
    /// </summary>
    public int MaxRecipesToProcess { get; set; } = int.MaxValue; // Show all recipes by default

    /// <summary>
    /// Whether to include HQ materials in availability calculations.
    /// HQ materials can often substitute for NQ in recipes.
    /// </summary>
    public bool IncludeHqMaterials { get; set; } = true;

    /// <summary>
    /// Whether to treat HQ and NQ materials as separate items.
    /// When false, HQ materials count toward NQ requirements.
    /// </summary>
    public bool SeparateHqCalculation { get; set; } = false;

    /// <summary>
    /// Jobs to include in the scan. Empty set means all jobs.
    /// </summary>
    public HashSet<uint> JobFilter { get; set; } = new();

    /// <summary>
    /// Whether to include expert recipes in results.
    /// Expert recipes require special books and materials.
    /// </summary>
    public bool IncludeExpertRecipes { get; set; } = true;

    /// <summary>
    /// Whether to include specialization recipes.
    /// These require master books and may have additional requirements.
    /// </summary>
    public bool IncludeSpecializationRecipes { get; set; } = true;

    /// <summary>
    /// Whether to filter recipes based on player's current job levels.
    /// When enabled, only shows recipes the player can actually craft.
    /// </summary>
    public bool FilterByJobLevel { get; set; } = true;

    /// <summary>
    /// Whether to show recipes that are above the player's current job level.
    /// When false, filters out recipes requiring higher levels than the player has.
    /// </summary>
    public bool ShowHigherLevelRecipes { get; set; } = false;

    /// <summary>
    /// Cache duration for recipe data in minutes.
    /// Longer cache improves performance but may miss game updates.
    /// </summary>
    public int CacheDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to perform parallel processing during scans.
    /// Improves performance on multi-core systems.
    /// </summary>
    public bool EnableParallelProcessing { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism for scan operations.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Whether to preload commonly used recipe data.
    /// Trades memory for scan performance.
    /// </summary>
    public bool PreloadRecipeData { get; set; } = true;

    /// <summary>
    /// Get all container IDs to scan based on current options.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<uint> AllContainerIds
    {
        get
        {
            var containers = new HashSet<uint>(ContainerIds);
            
            if (IncludeRetainers)
            {
                foreach (var retainerContainer in RetainerContainers)
                {
                    containers.Add(retainerContainer);
                }
            }
            
            return containers;
        }
    }

    /// <summary>
    /// Create scan options optimized for performance.
    /// Reduces scope to maintain &lt;150ms scan times.
    /// </summary>
    public static ScanOptions CreatePerformanceOptimized()
    {
        return new ScanOptions
        {
            ContainerIds = new HashSet<uint>(DefaultContainers.Take(20)), // Limit containers
            IncludeRetainers = false,
            MaxRecipeLevel = 80, // Limit recipe complexity
            ResolveIntermediateCrafts = false, // Skip intermediate resolution
            MaxRecipesToProcess = 2000, // Reduce recipe count
            EnableParallelProcessing = true,
            PreloadRecipeData = true,
            CacheDurationMinutes = 60 // Longer cache for performance
        };
    }

    /// <summary>
    /// Create scan options optimized for completeness.
    /// Includes all available data sources.
    /// </summary>
    public static ScanOptions CreateComprehensive()
    {
        return new ScanOptions
        {
            ContainerIds = new HashSet<uint>(DefaultContainers),
            IncludeRetainers = true,
            MaxRecipeLevel = 90,
            ResolveIntermediateCrafts = true,
            MaxRecipesToProcess = 10000,
            IncludeExpertRecipes = true,
            IncludeSpecializationRecipes = true,
            EnableParallelProcessing = true,
            PreloadRecipeData = true,
            CacheDurationMinutes = 15 // Shorter cache for accuracy
        };
    }

    /// <summary>
    /// Create default scan options with balanced performance and features.
    /// </summary>
    public static ScanOptions CreateDefault()
    {
        return new ScanOptions();
    }

    /// <summary>
    /// Validate the scan options and fix any invalid configurations.
    /// </summary>
    public void Validate()
    {
        // Ensure level ranges are valid
        if (MinRecipeLevel > MaxRecipeLevel)
        {
            (MinRecipeLevel, MaxRecipeLevel) = (MaxRecipeLevel, MinRecipeLevel);
        }

        // Ensure reasonable limits
        // No limit on recipes - show all available
        MaxRecipesToProcess = Math.Max(100, MaxRecipesToProcess);
        CacheDurationMinutes = Math.Max(1, Math.Min(CacheDurationMinutes, 1440)); // 1 min to 24 hours
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(MaxDegreeOfParallelism, Environment.ProcessorCount * 2));

        // Ensure we have at least some containers to scan
        if (!ContainerIds.Any() && !IncludeRetainers)
        {
            ContainerIds = new HashSet<uint>(DefaultContainers);
        }
    }

    /// <summary>
    /// Create a copy of these scan options.
    /// </summary>
    public ScanOptions Clone()
    {
        return new ScanOptions
        {
            ContainerIds = new HashSet<uint>(ContainerIds),
            IncludeRetainers = IncludeRetainers,
            MaxRecipeLevel = MaxRecipeLevel,
            MinRecipeLevel = MinRecipeLevel,
            ResolveIntermediateCrafts = ResolveIntermediateCrafts,
            MaxRecipesToProcess = MaxRecipesToProcess,
            IncludeHqMaterials = IncludeHqMaterials,
            SeparateHqCalculation = SeparateHqCalculation,
            JobFilter = new HashSet<uint>(JobFilter),
            IncludeExpertRecipes = IncludeExpertRecipes,
            IncludeSpecializationRecipes = IncludeSpecializationRecipes,
            FilterByJobLevel = FilterByJobLevel,
            ShowHigherLevelRecipes = ShowHigherLevelRecipes,
            CacheDurationMinutes = CacheDurationMinutes,
            EnableParallelProcessing = EnableParallelProcessing,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            PreloadRecipeData = PreloadRecipeData
        };
    }

    /// <summary>
    /// Get estimated scan time based on current options.
    /// </summary>
    public TimeSpan EstimatedScanTime()
    {
        var baseTime = 50; // Base 50ms for minimal scan
        
        // Add time for container count
        baseTime += ContainerIds.Count * 2;
        
        // Add significant time for retainers
        if (IncludeRetainers)
            baseTime += 200;
        
        // Add time for recipe processing
        baseTime += MaxRecipesToProcess / 100;
        
        // Add time for intermediate craft resolution
        if (ResolveIntermediateCrafts)
            baseTime += 50;
        
        // Reduce time for parallel processing
        if (EnableParallelProcessing && MaxDegreeOfParallelism > 1)
            baseTime = (int)(baseTime * 0.7);
        
        return TimeSpan.FromMilliseconds(Math.Min(baseTime, 5000)); // Cap at 5 seconds
    }
}