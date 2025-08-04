using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ReadyCrafter.Models;

namespace ReadyCrafter.Services;

/// <summary>
/// Core algorithm for computing craftable items with memoization and performance optimization.
/// Implements depth-first evaluation with one-level recursion for intermediate crafts.
/// Target performance: &lt;150ms full scan with 5000+ recipes.
/// </summary>
public sealed class CraftSolver : IDisposable
{
    private readonly RecipeRepositorySimple _recipeRepository;
    private readonly InventoryService _inventoryService;
    private readonly JobLevelService _jobLevelService;
    private readonly IPluginLog _logger;
    private readonly object _lockObject = new();
    
    // Memoization cache for performance
    private readonly ConcurrentDictionary<string, CraftabilityResult> _craftabilityCache = new();
    private readonly ConcurrentDictionary<uint, List<uint>> _intermediateRecipeCache = new();
    
    // Performance tracking
    private readonly ConcurrentQueue<double> _solveTimes = new();
    private string _lastInventoryHash = string.Empty;
    private DateTime _lastCacheInvalidation = DateTime.UtcNow;
    private int _totalSolves = 0;
    private int _cacheHits = 0;
    private bool _disposed = false;
    
    // Configuration
    private const int MaxCacheEntries = 10000;
    private const int MaxSolveTimeMs = 150;
    private const int CacheInvalidationMinutes = 5;
    
    /// <summary>
    /// Event raised when solve operation completes.
    /// </summary>
    public event EventHandler<SolveCompletedEventArgs>? SolveCompleted;
    
    /// <summary>
    /// Event raised when cache is invalidated due to inventory changes.
    /// </summary>
    public event EventHandler<CacheInvalidatedEventArgs>? CacheInvalidated;
    
    /// <summary>
    /// Whether the solver is ready for operations.
    /// </summary>
    public bool IsReady => _recipeRepository.IsInitialized && !_disposed;
    
    /// <summary>
    /// Average solve time over the last 10 operations.
    /// </summary>
    public double AverageSolveTimeMs
    {
        get
        {
            lock (_lockObject)
            {
                return _solveTimes.IsEmpty ? 0 : _solveTimes.Average();
            }
        }
    }
    
    /// <summary>
    /// Cache hit rate as a percentage.
    /// </summary>
    public double CacheHitRate => _totalSolves == 0 ? 0 : (_cacheHits / (double)_totalSolves) * 100;
    
    /// <summary>
    /// Number of entries in the craftability cache.
    /// </summary>
    public int CacheSize => _craftabilityCache.Count;

    public CraftSolver(RecipeRepositorySimple recipeRepository, InventoryService inventoryService, JobLevelService jobLevelService, IPluginLog logger)
    {
        _recipeRepository = recipeRepository ?? throw new ArgumentNullException(nameof(recipeRepository));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _jobLevelService = jobLevelService ?? throw new ArgumentNullException(nameof(jobLevelService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Subscribe to inventory changes for cache invalidation
        _inventoryService.InventoryChanged += OnInventoryChanged;
        
    }
    
    /// <summary>
    /// Compute all craftable items based on current inventory and scan options.
    /// This is the main entry point for the craft solving algorithm.
    /// </summary>
    /// <param name="options">Scan options for filtering and performance tuning</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of craftable items with material calculations</returns>
    public async Task<CraftableItem[]> SolveAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CraftSolver));
        
        if (!IsReady)
        {
            _logger.Warning("CraftSolver called before repository initialization completed. Returning empty results.");
            return Array.Empty<CraftableItem>();
        }
        
        var stopwatch = Stopwatch.StartNew();
        var results = new List<CraftableItem>();
        
        try
        {
            
            // Get current inventory snapshot
            var inventory = await _inventoryService.ScanAsync(options, cancellationToken);
            CheckCacheInvalidation(inventory);
            
            // Check inventory for Maple Log and shards
            
            // Check specifically for Maple Log and common shards
            if (inventory.Items.ContainsKey(5380))
            {
                var mapleLog = inventory.Items[5380];
                _logger.Warning($"MAPLE LOG FOUND: ItemID 5380, NQ={mapleLog.Nq}, HQ={mapleLog.Hq}");
            }
            
            // Check for shards (needed for crafting)
            for (uint shardId = 1; shardId <= 7; shardId++)
            {
                if (inventory.Items.ContainsKey(shardId))
                {
                    var shard = inventory.Items[shardId];
                    _logger.Warning($"SHARD FOUND: ItemID {shardId}, NQ={shard.Nq}, HQ={shard.Hq}");
                    
                    // Specifically check for Wind Shard (ItemID 4)
                    if (shardId == 4)
                    {
                        _logger.Warning($"WIND SHARD DETECTED: ItemID 4, Quantity={shard.Nq}");
                    }
                }
            }
            
            // Get recipes to process based on filter options
            var recipesToProcess = GetFilteredRecipes(options);
            
            
            // Process recipes with parallel execution if enabled
            if (options.EnableParallelProcessing && recipesToProcess.Count > 100)
            {
                results.AddRange(await ProcessRecipesParallel(recipesToProcess, inventory, options, cancellationToken));
            }
            else
            {
                results.AddRange(await ProcessRecipesSequential(recipesToProcess, inventory, options, cancellationToken));
            }
            
            stopwatch.Stop();
            
            // Update performance metrics
            UpdatePerformanceMetrics(stopwatch.Elapsed.TotalMilliseconds);
            
            _logger.Information($"Craft solve completed in {stopwatch.Elapsed.TotalMilliseconds:F2}ms " +
                              $"({results.Count} craftable items found from {recipesToProcess.Count} recipes)");
            
            OnSolveCompleted(new SolveCompletedEventArgs(results.Count, recipesToProcess.Count, stopwatch.Elapsed.TotalMilliseconds));
            
            return results.ToArray();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, $"Craft solve failed after {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
            throw;
        }
    }
    
    /// <summary>
    /// Compute craftability for a specific recipe with current inventory.
    /// Uses memoization for performance optimization.
    /// </summary>
    /// <param name="recipeId">Recipe to analyze</param>
    /// <param name="inventory">Current inventory snapshot</param>
    /// <param name="options">Scan options</param>
    /// <returns>Craftable item with material breakdown</returns>
    public async Task<CraftableItem?> SolveRecipeAsync(uint recipeId, InventorySnapshot inventory, ScanOptions options)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CraftSolver));
        
        var recipe = _recipeRepository.GetRecipe(recipeId);
        if (recipe == null)
        {
            _logger.Warning($"Recipe {recipeId} not found in repository");
            return null;
        }
        
        return await ProcessSingleRecipe(recipe, inventory, options);
    }
    
    /// <summary>
    /// Clear the memoization cache to force fresh calculations.
    /// This should be called when game data updates or significant inventory changes occur.
    /// </summary>
    public void InvalidateCache()
    {
        var entriesCleared = _craftabilityCache.Count;
        _craftabilityCache.Clear();
        _intermediateRecipeCache.Clear();
        _lastCacheInvalidation = DateTime.UtcNow;
        
        OnCacheInvalidated(new CacheInvalidatedEventArgs(entriesCleared, DateTime.UtcNow));
    }
    
    /// <summary>
    /// Synchronous version of SolveAsync for UI components.
    /// This method blocks the calling thread - use carefully.
    /// </summary>
    public IEnumerable<CraftableItem> GetCraftableItems(ScanOptions options)
    {
        try
        {
            var task = SolveAsync(options, CancellationToken.None);
            task.Wait();
            return task.Result;
        }
        catch (AggregateException ex)
        {
            // Unwrap aggregate exception for cleaner error handling
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Get current performance metrics for display in UI.
    /// </summary>
    public bool GetPerformanceMetrics(out double averageTimeMs, out double cacheHitRate)
    {
        try
        {
            averageTimeMs = AverageSolveTimeMs;
            cacheHitRate = CacheHitRate / 100.0; // Convert to 0-1 range
            return true;
        }
        catch
        {
            averageTimeMs = 0;
            cacheHitRate = 0;
            return false;
        }
    }

    /// <summary>
    /// Get performance statistics for the solver.
    /// </summary>
    public CraftSolverStats GetPerformanceStats()
    {
        lock (_lockObject)
        {
            return new CraftSolverStats
            {
                TotalSolves = _totalSolves,
                CacheHits = _cacheHits,
                CacheHitRate = CacheHitRate,
                AverageSolveTimeMs = AverageSolveTimeMs,
                CacheSize = CacheSize,
                LastCacheInvalidation = _lastCacheInvalidation,
                IsReady = IsReady
            };
        }
    }
    
    private List<RecipeData> GetFilteredRecipes(ScanOptions options)
    {
        var allRecipes = new List<RecipeData>();
        
        var allAvailableRecipes = _recipeRepository.GetAllRecipes().ToList();
        
        if (allAvailableRecipes.Count == 0)
        {
            _logger.Warning("Recipe repository is empty! Check if initialization completed successfully.");
            return allRecipes;
        }
        
        var mapleLumberRecipes = allAvailableRecipes.Where(r => r.ItemId == 5361).ToList();
        if (mapleLumberRecipes.Any())
        {
            foreach (var mlr in mapleLumberRecipes)
            {
                _logger.Warning($"FOUND RECIPE FOR ITEM 5361 (Maple Lumber): RecipeID={mlr.RecipeId}, ItemName='{mlr.ItemName}', Level={mlr.RecipeLevel}, Job={mlr.JobName}");
            }
        }
        else
        {
            _logger.Warning("NO RECIPE FOUND THAT PRODUCES ITEM ID 5361 (Maple Lumber)!");
        }
        
        // Check for basic lumber recipes (low level)
        var basicLumberRecipes = allAvailableRecipes
            .Where(r => r.ItemName != null && r.ItemName.Contains("Lumber") && r.RecipeLevel <= 20)
            .ToList();
        
        if (basicLumberRecipes.Any())
        {
            foreach (var lr in basicLumberRecipes)
            {
                _logger.Warning($"BASIC LUMBER RECIPE: ID {lr.RecipeId}, ItemName='{lr.ItemName}', ItemID={lr.ItemId}, Level={lr.RecipeLevel}, Job={lr.JobName}");
            }
        }
        else
        {
            _logger.Warning("NO LOW-LEVEL LUMBER RECIPES FOUND!");
        }
        
        // Check if there are ANY Carpenter recipes that use Maple Log
        var mapleLogUserRecipes = allAvailableRecipes
            .Where(r => r.Ingredients.Any(i => i.ItemId == 5380) && r.JobId == 8) // 8 = Carpenter
            .Take(5)
            .ToList();
        
        foreach (var mlr in mapleLogUserRecipes)
        {
            _logger.Warning($"RECIPE USING MAPLE LOG: ID {mlr.RecipeId}, ItemName='{mlr.ItemName}', ItemID={mlr.ItemId}, Level={mlr.RecipeLevel}");
        }
        
        
        // Apply job filter if specified
        if (options.JobFilter.Any())
        {
            // Filter by job and level
            allRecipes.AddRange(allAvailableRecipes
                .Where(r => options.JobFilter.Contains(r.JobId) && 
                           r.RecipeLevel >= options.MinRecipeLevel && 
                           r.RecipeLevel <= options.MaxRecipeLevel));
        }
        else
        {
            // Get all recipes within level range
            allRecipes.AddRange(allAvailableRecipes
                .Where(r => r.RecipeLevel >= options.MinRecipeLevel && r.RecipeLevel <= options.MaxRecipeLevel));
        }
        
        
        // Apply additional filters
        allRecipes = allRecipes.Where(recipe =>
        {
            if (!options.IncludeExpertRecipes && recipe.IsExpert)
                return false;
            
            if (!options.IncludeSpecializationRecipes && recipe.IsSpecialization)
                return false;
            
            // DISABLED: Job level filtering disabled due to inconsistent results
            // Apply job level filtering if enabled
            // if (options.FilterByJobLevel && _jobLevelService.IsJobLevelCheckingAvailable())
            // {
            //     var canCraft = _jobLevelService.CanCraftRecipe(recipe.JobId, recipe.RequiredLevel);
            //     if (!canCraft && !options.ShowHigherLevelRecipes)
            //     {
            //         return false;
            //     }
            // }
            
            return true;
        }).ToList();
        
        // No longer limiting recipes - show all available
        
        return allRecipes;
    }
    
    private async Task<List<CraftableItem>> ProcessRecipesParallel(List<RecipeData> recipes, InventorySnapshot inventory, 
        ScanOptions options, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<CraftableItem>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
        };
        
        await Task.Run(() =>
        {
            Parallel.ForEach(recipes, parallelOptions, recipe =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var craftableItem = ProcessSingleRecipe(recipe, inventory, options).Result;
                if (craftableItem != null)
                {
                    results.Add(craftableItem);
                }
            });
        }, cancellationToken);
        
        return results.ToList();
    }
    
    private async Task<List<CraftableItem>> ProcessRecipesSequential(List<RecipeData> recipes, InventorySnapshot inventory, 
        ScanOptions options, CancellationToken cancellationToken)
    {
        var results = new List<CraftableItem>();
        
        foreach (var recipe in recipes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var craftableItem = await ProcessSingleRecipe(recipe, inventory, options);
            if (craftableItem != null)
            {
                results.Add(craftableItem);
            }
        }
        
        return results;
    }
    
    private async Task<CraftableItem?> ProcessSingleRecipe(RecipeData recipe, InventorySnapshot inventory, ScanOptions options)
    {
        // Generate cache key for memoization
        var cacheKey = GenerateCacheKey(recipe.RecipeId, inventory.StateHash, options);
        
        // Check cache first
        if (_craftabilityCache.TryGetValue(cacheKey, out var cachedResult))
        {
            Interlocked.Increment(ref _cacheHits);
            return CreateCraftableItemFromCache(recipe, cachedResult);
        }
        
        // Perform fresh calculation
        var craftabilityResult = await CalculateCraftability(recipe, inventory, options);
        
        // Cache the result
        CacheResult(cacheKey, craftabilityResult);
        
        // Always return the item, even if not craftable (MaxCraftable = 0)
        return CreateCraftableItem(recipe, craftabilityResult, inventory, options);
    }

    /// <summary>
    /// Check if a recipe can be crafted by the player based on job level requirements.
    /// </summary>
    /// <param name="recipe">Recipe to check</param>
    /// <param name="options">Scan options</param>
    /// <returns>True if the player meets the job level requirements</returns>
    private bool CanPlayerCraftRecipe(RecipeData recipe, ScanOptions options)
    {
        if (!options.FilterByJobLevel || !_jobLevelService.IsJobLevelCheckingAvailable())
            return true; // Job level checking disabled or not available

        return _jobLevelService.CanCraftRecipe(recipe.JobId, recipe.RequiredLevel);
    }
    
    /// <summary>
    /// Determine if a recipe without ingredients is a legitimate gathering/special recipe
    /// or just missing ingredient data
    /// </summary>
    private bool IsGatheringOrSpecialRecipe(RecipeData recipe)
    {
        // Since ingredients are now loading correctly, we should NOT treat
        // recipes with no ingredients as special/gathering recipes.
        // All normal crafting recipes should have ingredients loaded.
        // If a recipe has no ingredients, it's a data loading issue.
        
        return false; // Always return false - no recipe should be treated as special
    }
    
    private async Task<CraftabilityResult> CalculateCraftability(RecipeData recipe, InventorySnapshot inventory, ScanOptions options)
    {
        var result = new CraftabilityResult
        {
            RecipeId = recipe.RecipeId,
            Materials = new List<MaterialCalculation>(),
            IntermediateCrafts = new List<IntermediateCraftCalculation>()
        };
        
        // Enhanced calculation logic
        var materialAvailability = new Dictionary<uint, MaterialCalculation>();
        int maxCraftableFromMaterials = int.MaxValue;
        bool hasAllMaterials = true;
        
        // Phase 1: Calculate base material availability
        foreach (var ingredient in recipe.Ingredients)
        {
            var materialCalc = CalculateEnhancedMaterialAvailability(ingredient, inventory, options);
            materialAvailability[ingredient.ItemId] = materialCalc;
            result.Materials.Add(materialCalc);
            
            if (!materialCalc.IsSatisfied)
            {
                hasAllMaterials = false;
            }
        }
        
        // Phase 2: Resolve intermediate crafts with enhanced logic
        if (options.ResolveIntermediateCrafts)
        {
            await ResolveIntermediateCraftsEnhanced(materialAvailability, result, inventory, options);
        }
        
        // Phase 3: Calculate final max craftable with optimized logic
        maxCraftableFromMaterials = CalculateOptimalMaxCraftable(recipe, materialAvailability, result, options);
        
        // Handle recipes with no ingredients (like gathering recipes)
        if (recipe.Ingredients.Count == 0)
        {
            // IMPORTANT: Only allow high craftable count for specific recipe types
            // Don't default to 999 for all recipes without ingredients as this breaks filtering
            if (IsGatheringOrSpecialRecipe(recipe))
            {
                result.MaxCraftable = 999; // Default high value for recipes without material requirements
                result.HasAllMaterials = true;
            }
            else
            {
                result.MaxCraftable = 0; // Assume not craftable if no ingredients defined
                result.HasAllMaterials = false;
            }
        }
        else
        {
            result.MaxCraftable = Math.Max(0, maxCraftableFromMaterials);
            result.HasAllMaterials = hasAllMaterials && maxCraftableFromMaterials > 0;
        }
        
        return result;
    }
    
    /// <summary>
    /// Enhanced material availability calculation with improved logic
    /// </summary>
    private MaterialCalculation CalculateEnhancedMaterialAvailability(RecipeIngredient ingredient, InventorySnapshot inventory, ScanOptions options)
    {
        var calc = new MaterialCalculation
        {
            ItemId = ingredient.ItemId,
            Required = ingredient.Quantity,
            Available = 0,
            RequiresHq = ingredient.RequiresHq
        };
        
        if (inventory.Items.TryGetValue(ingredient.ItemId, out var itemQuantity))
        {
            if (options.SeparateHqCalculation)
            {
                // Treat HQ and NQ separately
                calc.Available = ingredient.RequiresHq ? itemQuantity.Hq : itemQuantity.Nq;
            }
            else
            {
                // Enhanced logic: HQ materials can substitute for NQ requirements
                // But consider priority - save HQ materials unless specifically needed
                if (ingredient.RequiresHq)
                {
                    calc.Available = itemQuantity.Hq;
                }
                else
                {
                    // Use NQ first, then HQ if enabled and needed
                    calc.Available = itemQuantity.Nq;
                    if (options.IncludeHqMaterials && calc.Available < calc.Required)
                    {
                        var remainingNeeded = calc.Required - calc.Available;
                        var hqAvailable = Math.Min(remainingNeeded, itemQuantity.Hq);
                        calc.Available += hqAvailable;
                    }
                }
            }
        }
        
        calc.IsSatisfied = calc.Available >= calc.Required;
        calc.Needed = calc.IsSatisfied ? 0 : calc.Required - calc.Available;
        
        return calc;
    }
    
    /// <summary>
    /// Enhanced intermediate craft resolution with better logic
    /// </summary>
    private async Task ResolveIntermediateCraftsEnhanced(Dictionary<uint, MaterialCalculation> materialAvailability, 
        CraftabilityResult result, InventorySnapshot inventory, ScanOptions options)
    {
        foreach (var materialCalc in materialAvailability.Values.Where(m => !m.IsSatisfied))
        {
            try
            {
                var intermediateCalc = await TryResolveIntermediateCraft(materialCalc.ItemId, materialCalc.Needed, inventory, options);
                if (intermediateCalc != null)
                {
                    result.IntermediateCrafts.Add(intermediateCalc);
                    
                    // Update material availability if intermediate craft can provide materials
                    if (intermediateCalc.IsRecommended)
                    {
                        var potentialYield = intermediateCalc.MaxCraftable * intermediateCalc.Yield;
                        var actualContribution = Math.Min(potentialYield, materialCalc.Needed);
                        
                        // Update the material calculation to reflect intermediate craft contribution
                        materialCalc.Available += (uint)actualContribution;
                        materialCalc.IsSatisfied = materialCalc.Available >= materialCalc.Required;
                        materialCalc.Needed = Math.Max(0, materialCalc.Required - materialCalc.Available);
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
    }
    
    /// <summary>
    /// Calculate optimal max craftable with enhanced logic
    /// </summary>
    private int CalculateOptimalMaxCraftable(RecipeData recipe, Dictionary<uint, MaterialCalculation> materialAvailability, 
        CraftabilityResult result, ScanOptions options)
    {
        int maxCraftableFromMaterials = int.MaxValue;
        
        foreach (var ingredient in recipe.Ingredients)
        {
            if (materialAvailability.TryGetValue(ingredient.ItemId, out var materialCalc))
            {
                if (materialCalc.Available == 0)
                {
                    // No materials available for this ingredient
                    maxCraftableFromMaterials = 0;
                    break;
                }
                
                // Calculate how many times we can craft based on this ingredient
                var possibleCrafts = (int)(materialCalc.Available / ingredient.Quantity);
                maxCraftableFromMaterials = Math.Min(maxCraftableFromMaterials, possibleCrafts);
            }
            else
            {
                // Material calculation missing - shouldn't happen but handle gracefully
                maxCraftableFromMaterials = 0;
                break;
            }
        }
        
        // Handle edge case where no ingredients means unlimited crafting potential
        if (maxCraftableFromMaterials == int.MaxValue)
        {
            maxCraftableFromMaterials = recipe.Ingredients.Count == 0 ? 999 : 0;
        }
        
        return maxCraftableFromMaterials;
    }
    
    private MaterialCalculation CalculateMaterialAvailability(RecipeIngredient ingredient, InventorySnapshot inventory, ScanOptions options)
    {
        var calc = new MaterialCalculation
        {
            ItemId = ingredient.ItemId,
            Required = ingredient.Quantity,
            Available = 0,
            RequiresHq = ingredient.RequiresHq
        };
        
        if (inventory.Items.TryGetValue(ingredient.ItemId, out var itemQuantity))
        {
            if (options.SeparateHqCalculation)
            {
                // Treat HQ and NQ separately
                calc.Available = ingredient.RequiresHq ? itemQuantity.Hq : itemQuantity.Nq;
            }
            else
            {
                // HQ materials can substitute for NQ requirements
                calc.Available = itemQuantity.Nq + (options.IncludeHqMaterials ? itemQuantity.Hq : 0);
            }
        }
        
        calc.IsSatisfied = calc.Available >= calc.Required;
        calc.Needed = calc.IsSatisfied ? 0 : calc.Required - calc.Available;
        
        return calc;
    }
    
    private async Task<IntermediateCraftCalculation?> TryResolveIntermediateCraft(uint itemId, uint quantityNeeded, 
        InventorySnapshot inventory, ScanOptions options)
    {
        // Get recipes that produce this item
        var intermediateRecipes = GetIntermediateRecipes(itemId);
        if (!intermediateRecipes.Any())
            return null;
        
        // Enhanced recipe evaluation with multiple criteria
        RecipeData? bestRecipe = null;
        var bestCraftability = new CraftabilityResult { MaxCraftable = 0 };
        var bestScore = double.MinValue;
        
        foreach (var intermediateRecipeId in intermediateRecipes)
        {
            var intermediateRecipe = _recipeRepository.GetRecipe(intermediateRecipeId);
            if (intermediateRecipe == null)
                continue;
            
            // Enhanced recursion detection - prevent circular dependencies
            if (IsCircularDependency(intermediateRecipe, itemId))
                continue;
            
            // Calculate craftability with dependency-aware options
            var dependencyOptions = CreateDependencyAwareOptions(options);
            var craftability = await CalculateCraftability(intermediateRecipe, inventory, dependencyOptions);
            
            if (craftability.MaxCraftable > 0)
            {
                // Enhanced scoring algorithm considering multiple factors
                var score = CalculateIntermediateCraftScore(intermediateRecipe, craftability, quantityNeeded);
                
                if (score > bestScore)
                {
                    bestRecipe = intermediateRecipe;
                    bestCraftability = craftability;
                    bestScore = score;
                }
            }
        }
        
        if (bestRecipe == null || bestCraftability.MaxCraftable == 0)
            return null;
        
        var timesNeeded = (int)Math.Ceiling((double)quantityNeeded / bestRecipe.Yield);
        var canCraft = Math.Min(bestCraftability.MaxCraftable, timesNeeded);
        var actualYield = canCraft * bestRecipe.Yield;
        
        return new IntermediateCraftCalculation
        {
            RecipeId = bestRecipe.RecipeId,
            ItemId = itemId,
            QuantityNeeded = quantityNeeded,
            MaxCraftable = canCraft,
            Yield = bestRecipe.Yield,
            IsRecommended = canCraft > 0 && actualYield >= quantityNeeded,
            Materials = bestCraftability.Materials
        };
    }
    
    /// <summary>
    /// Enhanced circular dependency detection
    /// </summary>
    private bool IsCircularDependency(RecipeData recipe, uint targetItemId)
    {
        // Direct circular dependency check
        if (recipe.Ingredients.Any(ing => ing.ItemId == targetItemId))
            return true;
        
        // Check for potential deeper circular dependencies
        // This is a simplified version - in production you might want more sophisticated detection
        foreach (var ingredient in recipe.Ingredients)
        {
            var subRecipes = GetIntermediateRecipes(ingredient.ItemId);
            if (subRecipes.Any(subRecipeId => 
            {
                var subRecipe = _recipeRepository.GetRecipe(subRecipeId);
                return subRecipe != null && subRecipe.Ingredients.Any(subIng => subIng.ItemId == targetItemId);
            }))
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Create dependency-aware scan options to prevent infinite recursion
    /// </summary>
    private ScanOptions CreateDependencyAwareOptions(ScanOptions originalOptions)
    {
        return new ScanOptions
        {
            IncludeRetainers = originalOptions.IncludeRetainers,
            ResolveIntermediateCrafts = false, // Prevent recursive intermediate resolution
            IncludeHqMaterials = originalOptions.IncludeHqMaterials,
            SeparateHqCalculation = originalOptions.SeparateHqCalculation,
            EnableParallelProcessing = false, // Simpler processing for dependencies
            MaxRecipesToProcess = originalOptions.MaxRecipesToProcess,
            JobFilter = originalOptions.JobFilter,
            MinRecipeLevel = originalOptions.MinRecipeLevel,
            MaxRecipeLevel = originalOptions.MaxRecipeLevel,
            IncludeExpertRecipes = originalOptions.IncludeExpertRecipes,
            IncludeSpecializationRecipes = originalOptions.IncludeSpecializationRecipes,
            MaxDegreeOfParallelism = 1
        };
    }
    
    /// <summary>
    /// Calculate a score for intermediate craft options to select the best one
    /// </summary>
    private double CalculateIntermediateCraftScore(RecipeData recipe, CraftabilityResult craftability, uint quantityNeeded)
    {
        var score = 0.0;
        
        // Factor 1: How much can be crafted (higher is better)
        score += craftability.MaxCraftable * 100.0;
        
        // Factor 2: Recipe efficiency (yield vs materials)
        var materialCount = recipe.Ingredients.Count;
        if (materialCount > 0)
        {
            score += (recipe.Yield / (double)materialCount) * 50.0;
        }
        else
        {
            score += 200.0; // Bonus for recipes with no materials
        }
        
        // Factor 3: Level appropriateness (prefer lower level recipes for easier crafting)
        score += Math.Max(0, 100 - recipe.RecipeLevel);
        
        // Factor 4: Yield efficiency for the specific need
        var totalYield = craftability.MaxCraftable * recipe.Yield;
        if (totalYield >= quantityNeeded)
        {
            score += 150.0; // Bonus for meeting the full need
            
            // Small penalty for excessive overproduction
            var wasteRatio = (totalYield - quantityNeeded) / (double)quantityNeeded;
            score -= Math.Min(wasteRatio * 20.0, 50.0);
        }
        else
        {
            // Partial fulfillment is still valuable
            var fulfillmentRatio = totalYield / (double)quantityNeeded;
            score += fulfillmentRatio * 100.0;
        }
        
        // Factor 5: Specialization penalty (prefer non-specialization recipes)
        if (recipe.IsSpecialization)
        {
            score -= 75.0;
        }
        
        // Factor 6: Expert recipe penalty (prefer non-expert recipes)
        if (recipe.IsExpert)
        {
            score -= 100.0;
        }
        
        return score;
    }
    
    private List<uint> GetIntermediateRecipes(uint itemId)
    {
        if (_intermediateRecipeCache.TryGetValue(itemId, out var cachedRecipes))
            return cachedRecipes;
        
        var recipes = _recipeRepository.GetRecipesForItem(itemId).Select(r => r.RecipeId).ToList();
        
        // Cache for future lookups
        if (_intermediateRecipeCache.Count < MaxCacheEntries)
        {
            _intermediateRecipeCache.TryAdd(itemId, recipes);
        }
        
        return recipes;
    }
    
    private CraftableItem CreateCraftableItem(RecipeData recipe, CraftabilityResult result, InventorySnapshot inventory, ScanOptions options)
    {
        var craftableItem = new CraftableItem
        {
            RecipeId = recipe.RecipeId,
            ItemId = recipe.ItemId,
            ItemName = recipe.ItemName,
            JobId = recipe.JobId,
            JobName = recipe.JobName,
            RecipeLevel = recipe.RecipeLevel,
            RequiredLevel = recipe.RequiredLevel,
            Yield = recipe.Yield,
            CanHq = recipe.CanHq,
            MaxCraftable = result.MaxCraftable,
            HasAllMaterials = result.HasAllMaterials,
            IconId = recipe.IconId,
            ItemLevel = recipe.ItemLevel,
            IsSpecialization = recipe.IsSpecialization,
            RequiredCraftsmanship = recipe.RequiredCraftsmanship,
            RequiredControl = recipe.RequiredControl,
            QualityThreshold = recipe.QualityThreshold,
            MeetsJobLevelRequirement = true, // DISABLED: Always true since job level filtering is disabled
            LastCalculated = DateTime.UtcNow
        };
        
        // Convert material calculations to material requirements
        foreach (var materialCalc in result.Materials)
        {
            var ingredient = recipe.Ingredients.First(i => i.ItemId == materialCalc.ItemId);
            craftableItem.Materials.Add(new MaterialRequirement
            {
                ItemId = materialCalc.ItemId,
                ItemName = ingredient.ItemName,
                Required = materialCalc.Required,
                Available = materialCalc.Available,
                RequiresHq = materialCalc.RequiresHq,
                IconId = ingredient.IconId
            });
        }
        
        // Convert intermediate craft calculations
        foreach (var intermediateCalc in result.IntermediateCrafts)
        {
            var intermediateRecipe = _recipeRepository.GetRecipe(intermediateCalc.RecipeId);
            if (intermediateRecipe != null)
            {
                var intermediateCraft = new IntermediateCraft
                {
                    RecipeId = intermediateCalc.RecipeId,
                    ItemId = intermediateCalc.ItemId,
                    ItemName = intermediateRecipe.ItemName,
                    JobId = intermediateRecipe.JobId,
                    QuantityNeeded = intermediateCalc.QuantityNeeded,
                    MaxCraftable = intermediateCalc.MaxCraftable,
                    IsRecommended = intermediateCalc.IsRecommended
                };
                
                // Add materials for the intermediate craft
                foreach (var materialCalc in intermediateCalc.Materials)
                {
                    var ingredient = intermediateRecipe.Ingredients.First(i => i.ItemId == materialCalc.ItemId);
                    intermediateCraft.Materials.Add(new MaterialRequirement
                    {
                        ItemId = materialCalc.ItemId,
                        ItemName = ingredient.ItemName,
                        Required = materialCalc.Required,
                        Available = materialCalc.Available,
                        RequiresHq = materialCalc.RequiresHq,
                        IconId = ingredient.IconId
                    });
                }
                
                craftableItem.IntermediateCrafts.Add(intermediateCraft);
            }
        }
        
        return craftableItem;
    }
    
    private CraftableItem? CreateCraftableItemFromCache(RecipeData recipe, CraftabilityResult cachedResult)
    {
        // Always return the item, even if not craftable (MaxCraftable = 0)
        
        // This is a simplified version that recreates the craftable item from cached data
        // In a production system, you might want to cache the full CraftableItem
        // Note: We need to check job level requirements even for cached items since player levels may have changed
        var defaultOptions = new ScanOptions(); // Use default options for job level check
        return new CraftableItem
        {
            RecipeId = recipe.RecipeId,
            ItemId = recipe.ItemId,
            ItemName = recipe.ItemName,
            JobId = recipe.JobId,
            JobName = recipe.JobName,
            RecipeLevel = recipe.RecipeLevel,
            RequiredLevel = recipe.RequiredLevel,
            Yield = recipe.Yield,
            CanHq = recipe.CanHq,
            MaxCraftable = cachedResult.MaxCraftable,
            HasAllMaterials = cachedResult.HasAllMaterials,
            IconId = recipe.IconId,
            ItemLevel = recipe.ItemLevel,
            IsSpecialization = recipe.IsSpecialization,
            RequiredCraftsmanship = recipe.RequiredCraftsmanship,
            RequiredControl = recipe.RequiredControl,
            QualityThreshold = recipe.QualityThreshold,
            MeetsJobLevelRequirement = true, // DISABLED: Always true since job level filtering is disabled
            LastCalculated = DateTime.UtcNow
        };
    }
    
    private void CacheResult(string cacheKey, CraftabilityResult result)
    {
        // Prevent cache from growing too large
        if (_craftabilityCache.Count >= MaxCacheEntries)
        {
            // Remove oldest entries (simplified LRU)
            var keysToRemove = _craftabilityCache.Keys.Take(_craftabilityCache.Count / 4).ToList();
            foreach (var key in keysToRemove)
            {
                _craftabilityCache.TryRemove(key, out _);
            }
        }
        
        _craftabilityCache.TryAdd(cacheKey, result);
    }
    
    private string GenerateCacheKey(uint recipeId, string inventoryHash, ScanOptions options)
    {
        return $"{recipeId}:{inventoryHash}:{options.IncludeHqMaterials}:{options.SeparateHqCalculation}:{options.ResolveIntermediateCrafts}";
    }
    
    private void CheckCacheInvalidation(InventorySnapshot inventory)
    {
        if (_lastInventoryHash != inventory.StateHash)
        {
            InvalidateCache();
            _lastInventoryHash = inventory.StateHash;
        }
        
        // Also invalidate cache periodically to prevent stale data
        if (DateTime.UtcNow - _lastCacheInvalidation > TimeSpan.FromMinutes(CacheInvalidationMinutes))
        {
            InvalidateCache();
        }
    }
    
    private void UpdatePerformanceMetrics(double solveTimeMs)
    {
        lock (_lockObject)
        {
            _solveTimes.Enqueue(solveTimeMs);
            
            // Keep only the last 10 solve times
            while (_solveTimes.Count > 10)
            {
                _solveTimes.TryDequeue(out _);
            }
            
            Interlocked.Increment(ref _totalSolves);
            
            // Log performance warnings
            if (solveTimeMs > MaxSolveTimeMs)
            {
                _logger.Warning($"Craft solve took {solveTimeMs:F2}ms (exceeds {MaxSolveTimeMs}ms target)");
            }
        }
    }
    
    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        // Invalidate cache when inventory changes significantly
        if (e.Changes.TotalChanges > 5) // Only invalidate for significant changes
        {
            InvalidateCache();
        }
    }
    
    private void OnSolveCompleted(SolveCompletedEventArgs args)
    {
        try
        {
            SolveCompleted?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in SolveCompleted event handler");
        }
    }
    
    private void OnCacheInvalidated(CacheInvalidatedEventArgs args)
    {
        try
        {
            CacheInvalidated?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in CacheInvalidated event handler");
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        _inventoryService.InventoryChanged -= OnInventoryChanged;
        _craftabilityCache.Clear();
        _intermediateRecipeCache.Clear();
        
    }
}

/// <summary>
/// Internal result structure for memoization cache.
/// </summary>
internal sealed class CraftabilityResult
{
    public uint RecipeId { get; set; }
    public int MaxCraftable { get; set; }
    public bool HasAllMaterials { get; set; }
    public List<MaterialCalculation> Materials { get; set; } = new();
    public List<IntermediateCraftCalculation> IntermediateCrafts { get; set; } = new();
}

/// <summary>
/// Internal material calculation for recipes.
/// </summary>
internal sealed class MaterialCalculation
{
    public uint ItemId { get; set; }
    public uint Required { get; set; }
    public uint Available { get; set; }
    public uint Needed { get; set; }
    public bool IsSatisfied { get; set; }
    public bool RequiresHq { get; set; }
}

/// <summary>
/// Internal intermediate craft calculation.
/// </summary>
internal sealed class IntermediateCraftCalculation
{
    public uint RecipeId { get; set; }
    public uint ItemId { get; set; }
    public uint QuantityNeeded { get; set; }
    public int MaxCraftable { get; set; }
    public uint Yield { get; set; }
    public bool IsRecommended { get; set; }
    public List<MaterialCalculation> Materials { get; set; } = new();
}

/// <summary>
/// Event arguments for solve completion.
/// </summary>
public sealed class SolveCompletedEventArgs : EventArgs
{
    public int CraftableItemsFound { get; }
    public int RecipesProcessed { get; }
    public double SolveTimeMs { get; }
    
    public SolveCompletedEventArgs(int craftableItemsFound, int recipesProcessed, double solveTimeMs)
    {
        CraftableItemsFound = craftableItemsFound;
        RecipesProcessed = recipesProcessed;
        SolveTimeMs = solveTimeMs;
    }
}

/// <summary>
/// Event arguments for cache invalidation.
/// </summary>
public sealed class CacheInvalidatedEventArgs : EventArgs
{
    public int EntriesCleared { get; }
    public DateTime Timestamp { get; }
    
    public CacheInvalidatedEventArgs(int entriesCleared, DateTime timestamp)
    {
        EntriesCleared = entriesCleared;
        Timestamp = timestamp;
    }
}

/// <summary>
/// Performance statistics for the craft solver.
/// </summary>
public sealed class CraftSolverStats
{
    public int TotalSolves { get; set; }
    public int CacheHits { get; set; }
    public double CacheHitRate { get; set; }
    public double AverageSolveTimeMs { get; set; }
    public int CacheSize { get; set; }
    public DateTime LastCacheInvalidation { get; set; }
    public bool IsReady { get; set; }
}