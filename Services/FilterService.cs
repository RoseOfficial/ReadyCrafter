using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ReadyCrafter.Models;

namespace ReadyCrafter.Services;

/// <summary>
/// Provides real-time filtering and search functionality for craftable items.
/// Implements advanced search algorithms, caching, and performance optimization.
/// Supports fuzzy matching, advanced search syntax, and multi-criteria filtering.
/// </summary>
public sealed class FilterService : IDisposable
{
    private readonly IPluginLog _logger;
    private readonly object _lockObject = new();
    private readonly Timer _cacheCleanupTimer;
    
    // Search result caching
    private readonly ConcurrentDictionary<string, FilteredResult> _searchCache = new();
    private readonly ConcurrentQueue<string> _searchHistory = new();
    private readonly HashSet<string> _searchSuggestions = new();
    
    // Performance tracking
    private readonly ConcurrentQueue<double> _filterTimes = new();
    private int _totalFilters = 0;
    private int _cacheHits = 0;
    
    // Configuration
    private const int MaxCacheSize = 100;
    private const int MaxSearchHistory = 50;
    private const int CacheCleanupIntervalMinutes = 10;
    private const int MaxFilterTimeMs = 100;
    private const double CacheExpiryMinutes = 5;
    
    // Advanced search patterns
    private static readonly Regex JobSearchPattern = new(@"job:(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LevelSearchPattern = new(@"level:(\d+)(?:-(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CategorySearchPattern = new(@"cat(?:egory)?:(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuantitySearchPattern = new(@"qty:(\d+)(?:-(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    private bool _disposed = false;
    
    /// <summary>
    /// Event raised when search suggestions are updated.
    /// </summary>
    public event EventHandler<SearchSuggestionsUpdatedEventArgs>? SearchSuggestionsUpdated;
    
    /// <summary>
    /// Average filter processing time in milliseconds.
    /// </summary>
    public double AverageFilterTimeMs
    {
        get
        {
            lock (_lockObject)
            {
                return _filterTimes.IsEmpty ? 0 : _filterTimes.Average();
            }
        }
    }
    
    /// <summary>
    /// Cache hit ratio for filter operations.
    /// </summary>
    public double CacheHitRatio => _totalFilters > 0 ? (double)_cacheHits / _totalFilters : 0;
    
    /// <summary>
    /// Number of cached filter results.
    /// </summary>
    public int CachedResultCount => _searchCache.Count;
    
    /// <summary>
    /// Current search suggestions based on history.
    /// </summary>
    public IReadOnlyList<string> SearchSuggestions
    {
        get
        {
            lock (_lockObject)
            {
                return _searchSuggestions.Take(10).ToList();
            }
        }
    }
    
    public FilterService(IPluginLog logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Initialize search suggestions with common terms
        InitializeSearchSuggestions();
        
        // Set up cache cleanup timer
        _cacheCleanupTimer = new Timer(CleanupCache, null, 
            TimeSpan.FromMinutes(CacheCleanupIntervalMinutes),
            TimeSpan.FromMinutes(CacheCleanupIntervalMinutes));
        
        _logger.Information("FilterService initialized with advanced search capabilities");
    }
    
    /// <summary>
    /// Apply comprehensive filtering to a collection of craftable items with performance optimization.
    /// </summary>
    public async Task<FilterResult> FilterItemsAsync(IEnumerable<CraftableItem> items, FilterOptions filter, CancellationToken cancellationToken = default)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (filter == null) throw new ArgumentNullException(nameof(filter));
        
        var startTime = DateTime.UtcNow;
        var cacheKey = GenerateCacheKey(filter);
        
        Interlocked.Increment(ref _totalFilters);
        
        // Check cache first
        if (_searchCache.TryGetValue(cacheKey, out var cachedResult) && 
            (DateTime.UtcNow - cachedResult.Timestamp).TotalMinutes < CacheExpiryMinutes)
        {
            Interlocked.Increment(ref _cacheHits);
            return cachedResult.Result;
        }
        
        try
        {
            // Parse advanced search syntax
            var parsedFilter = await ParseAdvancedSearchAsync(filter, cancellationToken);
            
            // Apply filtering with performance monitoring
            var filteredItems = await Task.Run(() => ApplyFiltering(items, parsedFilter, cancellationToken), cancellationToken);
            
            var result = new FilterResult
            {
                Items = filteredItems.ToList(),
                TotalMatched = filteredItems.Count(),
                FilterApplied = parsedFilter,
                ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds
            };
            
            // Cache the result
            CacheResult(cacheKey, result);
            
            // Update search history and suggestions
            UpdateSearchHistory(filter.SearchText);
            
            // Track performance
            TrackPerformance(result.ProcessingTimeMs);
            
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error filtering items with filter: {Filter}", filter.GetDescription());
            
            // Return empty result on error
            return new FilterResult
            {
                Items = new List<CraftableItem>(),
                TotalMatched = 0,
                FilterApplied = filter,
                ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                HasError = true,
                ErrorMessage = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Apply filtering synchronously for real-time UI updates.
    /// </summary>
    public FilterResult FilterItems(IEnumerable<CraftableItem> items, FilterOptions filter)
    {
        return FilterItemsAsync(items, filter, CancellationToken.None).Result;
    }
    
    /// <summary>
    /// Get search suggestions based on input text and history.
    /// </summary>
    public IEnumerable<string> GetSearchSuggestions(string input, IEnumerable<CraftableItem> items)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return SearchSuggestions;
        }
        
        var lowerInput = input.ToLowerInvariant();
        var suggestions = new HashSet<string>();
        
        // Add matching suggestions from history
        lock (_lockObject)
        {
            foreach (var suggestion in _searchSuggestions.Where(s => s.ToLowerInvariant().Contains(lowerInput)))
            {
                suggestions.Add(suggestion);
            }
        }
        
        // Add matching item names
        foreach (var item in items.Take(1000)) // Limit for performance
        {
            if (item.ItemName.ToLowerInvariant().Contains(lowerInput))
            {
                suggestions.Add(item.ItemName);
            }
            
            if (item.JobName.ToLowerInvariant().Contains(lowerInput))
            {
                suggestions.Add($"job:{item.JobName}");
            }
        }
        
        // Add advanced search syntax suggestions
        if (lowerInput.Contains("job:"))
        {
            foreach (var job in FilterPresets.CraftingJobs.Values)
            {
                if (job.ToLowerInvariant().Contains(lowerInput.Replace("job:", "")))
                {
                    suggestions.Add($"job:{job}");
                }
            }
        }
        
        if (lowerInput.Contains("level:"))
        {
            suggestions.Add("level:1-15");
            suggestions.Add("level:51-60");
            suggestions.Add("level:81-90");
        }
        
        return suggestions.Take(10).OrderBy(s => s.Length);
    }
    
    /// <summary>
    /// Calculate relevance score for search results with fuzzy matching.
    /// </summary>
    public float CalculateRelevanceScore(CraftableItem item, string searchText, FilterOptions filter)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return 1.0f;
        
        var lowerSearch = searchText.ToLowerInvariant();
        var lowerName = item.ItemName.ToLowerInvariant();
        var score = 0.0f;
        
        // Exact match (highest score)
        if (lowerName == lowerSearch)
            score += 1.0f;
        
        // Starts with match
        else if (lowerName.StartsWith(lowerSearch))
            score += 0.9f;
        
        // Contains match
        else if (lowerName.Contains(lowerSearch))
            score += 0.7f;
        
        // Fuzzy matching for partial matches
        else
        {
            var fuzzyScore = CalculateFuzzyScore(lowerName, lowerSearch);
            if (fuzzyScore > 0.5f)
                score += fuzzyScore * 0.6f;
        }
        
        // Job name matching
        var lowerJob = item.JobName.ToLowerInvariant();
        if (lowerJob.Contains(lowerSearch))
            score += 0.4f;
        
        // Material name matching
        foreach (var material in item.Materials)
        {
            if (material.ItemName.ToLowerInvariant().Contains(lowerSearch))
            {
                score += 0.2f;
                break;
            }
        }
        
        // Boost favorites
        if (item.IsFavorite && filter.ShowOnlyFavorites)
            score += 0.1f;
        
        // Boost craftable items
        if (item.MaxCraftable > 0 && filter.ShowOnlyCraftable)
            score += 0.05f;
        
        return Math.Min(score, 1.0f);
    }
    
    /// <summary>
    /// Clear the search cache and reset performance counters.
    /// </summary>
    public void ClearCache()
    {
        lock (_lockObject)
        {
            _searchCache.Clear();
            _filterTimes.Clear();
            _totalFilters = 0;
            _cacheHits = 0;
        }
        
    }
    
    /// <summary>
    /// Get filtering performance statistics.
    /// </summary>
    public FilterPerformanceStats GetPerformanceStats()
    {
        lock (_lockObject)
        {
            return new FilterPerformanceStats
            {
                AverageFilterTimeMs = AverageFilterTimeMs,
                CacheHitRatio = CacheHitRatio,
                TotalFilterOperations = _totalFilters,
                CacheHits = _cacheHits,
                CachedResults = _searchCache.Count,
                SearchHistoryCount = _searchHistory.Count
            };
        }
    }
    
    private async Task<FilterOptions> ParseAdvancedSearchAsync(FilterOptions filter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filter.SearchText))
            return filter;
        
        var parsedFilter = filter.Clone();
        var searchText = filter.SearchText;
        
        // Parse job filter (job:CRP, job:Carpenter)
        var jobMatch = JobSearchPattern.Match(searchText);
        if (jobMatch.Success)
        {
            var jobName = jobMatch.Groups[1].Value;
            var jobId = GetJobIdFromName(jobName);
            if (jobId.HasValue)
            {
                parsedFilter.JobFilter = jobId;
                searchText = JobSearchPattern.Replace(searchText, "").Trim();
            }
        }
        
        // Parse level filter (level:50, level:50-60)
        var levelMatch = LevelSearchPattern.Match(searchText);
        if (levelMatch.Success)
        {
            if (uint.TryParse(levelMatch.Groups[1].Value, out var minLevel))
            {
                parsedFilter.MinLevel = minLevel;
                
                if (levelMatch.Groups[2].Success && uint.TryParse(levelMatch.Groups[2].Value, out var maxLevel))
                {
                    parsedFilter.MaxLevel = maxLevel;
                }
                else
                {
                    parsedFilter.MaxLevel = minLevel;
                }
                
                searchText = LevelSearchPattern.Replace(searchText, "").Trim();
            }
        }
        
        // Parse quantity filter (qty:5, qty:5-10)
        var qtyMatch = QuantitySearchPattern.Match(searchText);
        if (qtyMatch.Success)
        {
            if (int.TryParse(qtyMatch.Groups[1].Value, out var minQty))
            {
                parsedFilter.MinCraftableQuantity = minQty;
                
                if (qtyMatch.Groups[2].Success && int.TryParse(qtyMatch.Groups[2].Value, out var maxQty))
                {
                    parsedFilter.MaxCraftableQuantity = maxQty;
                }
                
                searchText = QuantitySearchPattern.Replace(searchText, "").Trim();
            }
        }
        
        // Update the cleaned search text
        parsedFilter.SearchText = searchText;
        
        return await Task.FromResult(parsedFilter);
    }
    
    private IEnumerable<CraftableItem> ApplyFiltering(IEnumerable<CraftableItem> items, FilterOptions filter, CancellationToken cancellationToken)
    {
        IEnumerable<CraftableItem> filteredItems = items.AsParallel()
            .WithCancellation(cancellationToken)
            .Where(item => item.MatchesFilter(filter));
        
        // Apply advanced search scoring if there's search text
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            filteredItems = filteredItems
                .Select(item => new { Item = item, Score = CalculateRelevanceScore(item, filter.SearchText, filter) })
                .Where(x => x.Score > 0.1f)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item);
        }
        else
        {
            // Apply standard sorting
            var parallelItems = filteredItems.AsParallel();
            filteredItems = filter.SortDirection == SortDirection.Ascending 
                ? ApplySortingAscending(parallelItems, filter) 
                : ApplySortingDescending(parallelItems, filter);
        }
        
        // Apply result limit
        if (filter.MaxResults > 0)
        {
            filteredItems = filteredItems.Take(filter.MaxResults);
        }
        
        return filteredItems.ToList();
    }
    
    private IEnumerable<CraftableItem> ApplySortingAscending(ParallelQuery<CraftableItem> items, FilterOptions filter)
    {
        return filter.SortBy switch
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
    
    private IEnumerable<CraftableItem> ApplySortingDescending(ParallelQuery<CraftableItem> items, FilterOptions filter)
    {
        return filter.SortBy switch
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
    
    private float CalculateFuzzyScore(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            return 0.0f;
        
        // Simple fuzzy matching algorithm
        var matches = 0;
        var textIndex = 0;
        
        foreach (var c in pattern)
        {
            while (textIndex < text.Length && text[textIndex] != c)
                textIndex++;
            
            if (textIndex < text.Length)
            {
                matches++;
                textIndex++;
            }
        }
        
        return (float)matches / pattern.Length;
    }
    
    private uint? GetJobIdFromName(string jobName)
    {
        var lowerJobName = jobName.ToLowerInvariant();
        
        // Check exact matches first
        foreach (var kvp in FilterPresets.CraftingJobs)
        {
            if (kvp.Value.ToLowerInvariant() == lowerJobName)
                return kvp.Key;
        }
        
        // Check abbreviations
        var abbreviations = new Dictionary<string, uint>
        {
            { "crp", 8 }, { "carpenter", 8 },
            { "bsm", 9 }, { "blacksmith", 9 },
            { "arm", 10 }, { "armorer", 10 },
            { "gsm", 11 }, { "goldsmith", 11 },
            { "ltw", 12 }, { "leatherworker", 12 },
            { "wvr", 13 }, { "weaver", 13 },
            { "alc", 14 }, { "alchemist", 14 },
            { "cul", 15 }, { "culinarian", 15 }
        };
        
        return abbreviations.TryGetValue(lowerJobName, out var jobId) ? jobId : null;
    }
    
    private string GenerateCacheKey(FilterOptions filter)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(filter.SearchText ?? "");
        keyBuilder.Append("|").Append(filter.JobFilter?.ToString() ?? "");
        keyBuilder.Append("|").Append(filter.MinLevel?.ToString() ?? "");
        keyBuilder.Append("|").Append(filter.MaxLevel?.ToString() ?? "");
        keyBuilder.Append("|").Append(filter.ShowOnlyCraftable);
        keyBuilder.Append("|").Append(filter.ShowOnlyFavorites);
        keyBuilder.Append("|").Append(filter.ShowOnlyHq);
        keyBuilder.Append("|").Append(filter.HideSpecialization);
        keyBuilder.Append("|").Append(filter.SortBy);
        keyBuilder.Append("|").Append(filter.SortDirection);
        keyBuilder.Append("|").Append(filter.MaxResults);
        
        return keyBuilder.ToString();
    }
    
    private void CacheResult(string cacheKey, FilterResult result)
    {
        if (_searchCache.Count >= MaxCacheSize)
        {
            // Remove oldest entries
            var keysToRemove = _searchCache
                .OrderBy(kvp => kvp.Value.Timestamp)
                .Take(_searchCache.Count - MaxCacheSize + 10)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                _searchCache.TryRemove(key, out _);
            }
        }
        
        _searchCache[cacheKey] = new FilteredResult
        {
            Result = result,
            Timestamp = DateTime.UtcNow
        };
    }
    
    private void UpdateSearchHistory(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return;
        
        _searchHistory.Enqueue(searchText);
        
        while (_searchHistory.Count > MaxSearchHistory)
        {
            _searchHistory.TryDequeue(out _);
        }
        
        // Update suggestions
        lock (_lockObject)
        {
            _searchSuggestions.Add(searchText);
            
            // Keep only recent and relevant suggestions
            if (_searchSuggestions.Count > 50)
            {
                var oldSuggestions = _searchSuggestions.Take(_searchSuggestions.Count - 40).ToList();
                foreach (var old in oldSuggestions)
                {
                    _searchSuggestions.Remove(old);
                }
            }
        }
        
        SearchSuggestionsUpdated?.Invoke(this, new SearchSuggestionsUpdatedEventArgs { Suggestions = SearchSuggestions });
    }
    
    private void TrackPerformance(double processingTimeMs)
    {
        lock (_lockObject)
        {
            _filterTimes.Enqueue(processingTimeMs);
            
            while (_filterTimes.Count > 100)
            {
                _filterTimes.TryDequeue(out _);
            }
        }
        
        if (processingTimeMs > MaxFilterTimeMs)
        {
            _logger.Warning("Filter operation took {Time}ms, exceeding target of {Target}ms", 
                processingTimeMs, MaxFilterTimeMs);
        }
    }
    
    private void InitializeSearchSuggestions()
    {
        lock (_lockObject)
        {
            // Add job-based suggestions
            foreach (var job in FilterPresets.CraftingJobs.Values)
            {
                _searchSuggestions.Add($"job:{job}");
            }
            
            // Add level range suggestions
            foreach (var range in FilterPresets.LevelRanges.Keys)
            {
                _searchSuggestions.Add($"level:{range.Split(' ')[0]}");
            }
            
            // Add quantity suggestions
            _searchSuggestions.Add("qty:1");
            _searchSuggestions.Add("qty:5");
            _searchSuggestions.Add("qty:10");
        }
    }
    
    private void CleanupCache(object? state)
    {
        try
        {
            var expiredKeys = _searchCache
                .Where(kvp => (DateTime.UtcNow - kvp.Value.Timestamp).TotalMinutes > CacheExpiryMinutes)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _searchCache.TryRemove(key, out _);
            }
            
            if (expiredKeys.Count > 0)
            {
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during cache cleanup");
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _cacheCleanupTimer?.Dispose();
        _searchCache.Clear();
        
        _disposed = true;
    }
}

/// <summary>
/// Result of a filter operation.
/// </summary>
public sealed class FilterResult
{
    /// <summary>
    /// Filtered and sorted items.
    /// </summary>
    public IReadOnlyList<CraftableItem> Items { get; set; } = new List<CraftableItem>();
    
    /// <summary>
    /// Total number of items that matched the filter.
    /// </summary>
    public int TotalMatched { get; set; }
    
    /// <summary>
    /// The filter options that were applied.
    /// </summary>
    public FilterOptions FilterApplied { get; set; } = FilterOptions.CreateDefault();
    
    /// <summary>
    /// Time taken to process the filter operation in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; set; }
    
    /// <summary>
    /// Whether the operation encountered an error.
    /// </summary>
    public bool HasError { get; set; }
    
    /// <summary>
    /// Error message if HasError is true.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Cached filter result with timestamp.
/// </summary>
internal sealed class FilteredResult
{
    public FilterResult Result { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Performance statistics for filter operations.
/// </summary>
public sealed class FilterPerformanceStats
{
    public double AverageFilterTimeMs { get; set; }
    public double CacheHitRatio { get; set; }
    public int TotalFilterOperations { get; set; }
    public int CacheHits { get; set; }
    public int CachedResults { get; set; }
    public int SearchHistoryCount { get; set; }
}

/// <summary>
/// Event arguments for search suggestions updates.
/// </summary>
public sealed class SearchSuggestionsUpdatedEventArgs : EventArgs
{
    public IReadOnlyList<string> Suggestions { get; set; } = new List<string>();
}