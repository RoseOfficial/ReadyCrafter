using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.Inventory;
using Dalamud.Logging;
using Dalamud.Plugin.Services;
using ReadyCrafter.Models;

namespace ReadyCrafter.Services;

/// <summary>
/// Handles inventory scanning via Dalamud's IGameInventory service.
/// Automatically includes all items (crystals, shards, and regular items).
/// Provides real-time monitoring, change detection, and performance-optimized scanning.
/// </summary>
public sealed class InventoryService : IDisposable
{
    private readonly IGameInventory _gameInventory;
    private readonly IClientState _clientState;
    private readonly IPluginLog _logger;
    private readonly object _lockObject = new();
    private readonly ConcurrentDictionary<string, InventorySnapshot> _snapshotCache = new();
    private readonly Timer _monitoringTimer;
    
    private InventorySnapshot? _lastSnapshot;
    private DateTime _lastScanTime = DateTime.MinValue;
    private bool _isScanning = false;
    private bool _disposed = false;
    
    // Performance tracking
    private readonly ConcurrentQueue<double> _scanTimes = new();
    private int _totalScans = 0;
    private int _cacheHits = 0;
    
    /// <summary>
    /// Event raised when inventory changes are detected.
    /// </summary>
    public event EventHandler<InventoryChangedEventArgs>? InventoryChanged;
    
    /// <summary>
    /// Event raised when a scan completes successfully.
    /// </summary>
    public event EventHandler<InventoryScanCompletedEventArgs>? ScanCompleted;
    
    /// <summary>
    /// Event raised when a scan encounters an error.
    /// </summary>
    public event EventHandler<InventoryScanErrorEventArgs>? ScanError;
    
    /// <summary>
    /// Default scan interval in milliseconds.
    /// </summary>
    public const int DefaultScanIntervalMs = 5000;
    
    /// <summary>
    /// Maximum scan time before considering it a performance issue.
    /// </summary>
    public const int MaxScanTimeMs = 150;
    
    /// <summary>
    /// Current scan interval in milliseconds.
    /// </summary>
    public int ScanIntervalMs { get; set; } = DefaultScanIntervalMs;
    
    /// <summary>
    /// Whether real-time monitoring is enabled.
    /// </summary>
    public bool IsMonitoringEnabled { get; private set; } = false;
    
    /// <summary>
    /// Average scan time over the last 10 scans.
    /// </summary>
    public double AverageScanTimeMs
    {
        get
        {
            lock (_lockObject)
            {
                if (_scanTimes.IsEmpty) return 0;
                return _scanTimes.Average();
            }
        }
    }
    
    /// <summary>
    /// Total number of scans performed.
    /// </summary>
    public int TotalScans => _totalScans;
    
    /// <summary>
    /// Cache hit rate as a percentage.
    /// </summary>
    public double CacheHitRate => _totalScans == 0 ? 0 : (_cacheHits / (double)_totalScans) * 100;
    
    /// <summary>
    /// Whether a scan is currently in progress.
    /// </summary>
    public bool IsScanning => _isScanning;
    
    /// <summary>
    /// Time of the last successful scan.
    /// </summary>
    public DateTime LastScanTime => _lastScanTime;
    
    /// <summary>
    /// The most recent inventory snapshot.
    /// </summary>
    public InventorySnapshot? LastSnapshot => _lastSnapshot;

    public InventoryService(IGameInventory gameInventory, IClientState clientState, IPluginLog logger)
    {
        _gameInventory = gameInventory ?? throw new ArgumentNullException(nameof(gameInventory));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Initialize monitoring timer (disabled by default)
        _monitoringTimer = new Timer(OnMonitoringTick, null, Timeout.Infinite, Timeout.Infinite);
        
    }
    
    /// <summary>
    /// Scan inventory with the specified options.
    /// </summary>
    /// <param name="options">Scan configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Inventory snapshot</returns>
    public async Task<InventorySnapshot> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InventoryService));
        
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        
        // Validate and fix options
        options.Validate();
        
        // Check if we can use cached result
        var cacheKey = GenerateCacheKey(options);
        if (_snapshotCache.TryGetValue(cacheKey, out var cachedSnapshot) && 
            cachedSnapshot.IsRecentEnough(TimeSpan.FromMinutes(options.CacheDurationMinutes)))
        {
            Interlocked.Increment(ref _cacheHits);
            Interlocked.Increment(ref _totalScans);
            return cachedSnapshot;
        }
        
        return await ScanInternalAsync(options, cancellationToken);
    }
    
    /// <summary>
    /// Start real-time inventory monitoring with the specified options.
    /// </summary>
    /// <param name="options">Scan configuration options</param>
    public void StartMonitoring(ScanOptions? options = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InventoryService));
        
        if (IsMonitoringEnabled)
        {
            _logger.Warning("Monitoring is already enabled");
            return;
        }
        
        options ??= ScanOptions.CreatePerformanceOptimized();
        options.Validate();
        
        IsMonitoringEnabled = true;
        _monitoringTimer.Change(0, ScanIntervalMs);
        
        _logger.Information($"Started inventory monitoring with {ScanIntervalMs}ms interval");
    }
    
    /// <summary>
    /// Stop real-time inventory monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        if (!IsMonitoringEnabled)
            return;
        
        IsMonitoringEnabled = false;
        _monitoringTimer.Change(Timeout.Infinite, Timeout.Infinite);
        
        _logger.Information("Stopped inventory monitoring");
    }
    
    /// <summary>
    /// Force an immediate inventory scan with change detection.
    /// </summary>
    /// <param name="options">Scan configuration options</param>
    /// <returns>Change set if monitoring is enabled, null otherwise</returns>
    public async Task<InventoryChangeSet?> RefreshAsync(ScanOptions? options = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InventoryService));
        
        options ??= ScanOptions.CreatePerformanceOptimized();
        
        var newSnapshot = await ScanInternalAsync(options, CancellationToken.None);
        
        if (_lastSnapshot != null && IsMonitoringEnabled)
        {
            var changes = newSnapshot.CompareTo(_lastSnapshot);
            if (changes.HasChanges)
            {
                OnInventoryChanged(new InventoryChangedEventArgs(changes));
            }
            return changes;
        }
        
        return null;
    }
    
    /// <summary>
    /// Clear all cached snapshots.
    /// </summary>
    public void ClearCache()
    {
        _snapshotCache.Clear();
    }
    
    /// <summary>
    /// Get performance statistics for the service.
    /// </summary>
    public InventoryServiceStats GetPerformanceStats()
    {
        lock (_lockObject)
        {
            return new InventoryServiceStats
            {
                TotalScans = _totalScans,
                CacheHits = _cacheHits,
                CacheHitRate = CacheHitRate,
                AverageScanTimeMs = AverageScanTimeMs,
                IsMonitoring = IsMonitoringEnabled,
                ScanIntervalMs = ScanIntervalMs,
                LastScanTime = _lastScanTime,
                CachedSnapshotCount = _snapshotCache.Count
            };
        }
    }
    
    private async Task<InventorySnapshot> ScanInternalAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        if (_isScanning)
        {
            _logger.Warning("Scan already in progress, waiting for completion");
            
            // Wait for current scan to complete (with timeout)
            var timeout = DateTime.UtcNow.AddMilliseconds(MaxScanTimeMs * 2);
            while (_isScanning && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10, cancellationToken);
            }
            
            if (_isScanning)
            {
                throw new InvalidOperationException("Previous scan did not complete within timeout");
            }
        }
        
        var stopwatch = Stopwatch.StartNew();
        InventorySnapshot snapshot;
        
        try
        {
            _isScanning = true;
            
            // Check if client is in valid state
            if (!_clientState.IsLoggedIn)
            {
                throw new InvalidOperationException("Client is not logged in");
            }
            
            snapshot = await Task.Run(() => PerformInventoryScan(options, cancellationToken), cancellationToken);
            
            stopwatch.Stop();
            snapshot.ScanTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            
            // Update performance tracking
            UpdatePerformanceMetrics(snapshot.ScanTimeMs);
            
            // Cache the result
            var cacheKey = GenerateCacheKey(options);
            _snapshotCache.AddOrUpdate(cacheKey, snapshot, (key, old) => snapshot);
            
            // Update last snapshot for change detection
            _lastSnapshot = snapshot;
            _lastScanTime = DateTime.UtcNow;
            
            
            OnScanCompleted(new InventoryScanCompletedEventArgs(snapshot));
            
            return snapshot;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, $"Inventory scan failed after {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
            OnScanError(new InventoryScanErrorEventArgs(ex, stopwatch.Elapsed.TotalMilliseconds));
            throw;
        }
        finally
        {
            _isScanning = false;
        }
    }
    
    private InventorySnapshot PerformInventoryScan(ScanOptions options, CancellationToken cancellationToken)
    {
        var snapshot = new InventorySnapshot
        {
            Timestamp = DateTime.UtcNow,
            ScannedContainers = new HashSet<uint>(options.AllContainerIds),
            IncludedRetainers = options.IncludeRetainers
        };
        
        int totalStacks = 0;
        var processedItems = new Dictionary<uint, ItemQuantity>();
        
        try
        {
            _logger.Information("Starting IGameInventory scan using GetInventoryItems (includes all items including crystals/shards)...");
            
            // Use IGameInventory.GetInventoryItems to scan all inventory types
            // This is the proper way to access all items including crystals/shards
            var inventoryTypesToScan = new[]
            {
                GameInventoryType.Inventory1,
                GameInventoryType.Inventory2, 
                GameInventoryType.Inventory3,
                GameInventoryType.Inventory4,
                GameInventoryType.ArmoryMainHand,
                GameInventoryType.ArmoryHead,
                GameInventoryType.ArmoryBody,
                GameInventoryType.ArmoryHands,
                GameInventoryType.ArmoryLegs,
                GameInventoryType.ArmoryNeck,
                GameInventoryType.ArmoryRings,
                GameInventoryType.SaddleBag1,
                GameInventoryType.SaddleBag2,
                GameInventoryType.PremiumSaddleBag1,
                GameInventoryType.PremiumSaddleBag2,
                GameInventoryType.Crystals
            };
            
            foreach (var inventoryType in inventoryTypesToScan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    var items = _gameInventory.GetInventoryItems(inventoryType);
                    
                    foreach (var gameItem in items)
                    {
                        if (gameItem.ItemId == 0 || gameItem.Quantity == 0)
                            continue;
                        
                        var itemId = (uint)gameItem.ItemId;
                        var quantity = (uint)gameItem.Quantity;
                        // For crystals/shards, HQ doesn't matter anyway (they can't be HQ)
                        var isHq = false;
                        
                        // Log specifically for Wind Shard (ItemID 4) and Maple Log (ItemID 5380)
                        if (itemId == 4)
                        {
                            _logger.Warning($"WIND SHARD FOUND in {inventoryType}: ItemID 4, Quantity={quantity}, HQ={isHq}");
                        }
                        if (itemId == 5380)
                        {
                            _logger.Warning($"MAPLE LOG FOUND in {inventoryType}: ItemID 5380, Quantity={quantity}, HQ={isHq}");
                        }
                        
                        totalStacks++;
                        
                        // Add or update item quantities
                        if (processedItems.TryGetValue(itemId, out var existingQuantity))
                        {
                            if (isHq)
                                existingQuantity.Hq += quantity;
                            else
                                existingQuantity.Nq += quantity;
                        }
                        else
                        {
                            processedItems[itemId] = new ItemQuantity 
                            { 
                                Nq = isHq ? 0u : quantity, 
                                Hq = isHq ? quantity : 0u 
                            };
                        }
                    }
                    
                    _logger.Information($"Scanned {inventoryType}: {items.Length} slots checked");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, $"Could not access inventory type {inventoryType}");
                }
            }
            
            _logger.Information($"IGameInventory scan completed: {totalStacks} total item stacks found");
            
            // Log all items that might be shards/crystals (reasonable quantities)
            _logger.Information("All items with crystal-like quantities (1-999):");
            var crystalLikeItems = processedItems
                .Where(kvp => kvp.Value.Nq > 0 && kvp.Value.Nq <= 999)
                .OrderBy(kvp => kvp.Key)
                .ToList();
            
            foreach (var item in crystalLikeItems)
            {
                _logger.Information($"  ItemID {item.Key}: {item.Value.Nq}");
            }
            
            // Look specifically for an item with quantity 47 (Wind Shards)
            var windShardCandidate = processedItems.FirstOrDefault(kvp => kvp.Value.Nq == 47);
            if (windShardCandidate.Key != 0)
            {
                _logger.Warning($"FOUND ITEM WITH QUANTITY 47: ItemID {windShardCandidate.Key} - This should be your Wind Shard!");
            }
            else
            {
                _logger.Information("No item with quantity 47 found in IGameInventory scan");
            }
            
            // Also check for other reasonable crystal quantities
            var reasonableCrystals = processedItems.Where(kvp => 
                kvp.Value.Nq > 10 && kvp.Value.Nq < 500 && // Reasonable crystal range
                kvp.Key < 100 // Crystals usually have low ItemIDs
            ).ToList();
            
            if (reasonableCrystals.Any())
            {
                _logger.Information("Potential crystal/shard items (ID < 100, quantity 10-500):");
                foreach (var crystal in reasonableCrystals)
                {
                    _logger.Information($"  ItemID {crystal.Key}: {crystal.Value.Nq}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Inventory scan was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error occurred during IGameInventory scanning");
            throw;
        }
        
        // Transfer results to snapshot
        snapshot.Items = processedItems;
        snapshot.TotalStacks = totalStacks;
        
        // Generate state hash for change detection
        snapshot.GenerateStateHash();
        
        // Add metadata
        snapshot.Metadata["scan_method"] = "igameinventory_simple";
        snapshot.Metadata["crystal_scanning_enabled"] = "true";
        
        return snapshot;
    }
    
    
    
    private void UpdatePerformanceMetrics(double scanTimeMs)
    {
        lock (_lockObject)
        {
            _scanTimes.Enqueue(scanTimeMs);
            
            // Keep only the last 10 scan times for average calculation
            while (_scanTimes.Count > 10)
            {
                _scanTimes.TryDequeue(out _);
            }
            
            Interlocked.Increment(ref _totalScans);
            
            // Log performance warnings
            if (scanTimeMs > MaxScanTimeMs)
            {
                _logger.Warning($"Inventory scan took {scanTimeMs:F2}ms (exceeds {MaxScanTimeMs}ms target)");
            }
        }
    }
    
    private string GenerateCacheKey(ScanOptions options)
    {
        var keyBuilder = new System.Text.StringBuilder();
        try
        {
            keyBuilder.Append("scan:");
            keyBuilder.Append(string.Join(",", options.AllContainerIds.OrderBy(x => x)));
            keyBuilder.Append(":retainers:");
            keyBuilder.Append(options.IncludeRetainers);
            keyBuilder.Append(":hq:");
            keyBuilder.Append(options.IncludeHqMaterials);
            
            return keyBuilder.ToString();
        }
        finally
        {
            // StringBuilder is managed by GC, no manual disposal needed
        }
    }
    
    private void OnMonitoringTick(object? state)
    {
        if (!IsMonitoringEnabled || _disposed)
            return;
        
        try
        {
            // Use performance-optimized options for monitoring
            var options = ScanOptions.CreatePerformanceOptimized();
            
            _ = Task.Run(async () =>
            {
                try
                {
                    var newSnapshot = await ScanInternalAsync(options, CancellationToken.None);
                    
                    // Check for changes if we have a previous snapshot
                    if (_lastSnapshot != null)
                    {
                        var changes = newSnapshot.CompareTo(_lastSnapshot);
                        if (changes.HasChanges)
                        {
                            OnInventoryChanged(new InventoryChangedEventArgs(changes));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error during monitoring scan");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in monitoring timer tick");
        }
    }
    
    private void OnInventoryChanged(InventoryChangedEventArgs args)
    {
        try
        {
            InventoryChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in InventoryChanged event handler");
        }
    }
    
    private void OnScanCompleted(InventoryScanCompletedEventArgs args)
    {
        try
        {
            ScanCompleted?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in ScanCompleted event handler");
        }
    }
    
    private void OnScanError(InventoryScanErrorEventArgs args)
    {
        try
        {
            ScanError?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in ScanError event handler");
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        StopMonitoring();
        _monitoringTimer?.Dispose();
        _snapshotCache.Clear();
        
    }
}

/// <summary>
/// Event arguments for inventory change notifications.
/// </summary>
public sealed class InventoryChangedEventArgs : EventArgs
{
    public InventoryChangeSet Changes { get; }
    
    public InventoryChangedEventArgs(InventoryChangeSet changes)
    {
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }
}

/// <summary>
/// Event arguments for successful scan completion.
/// </summary>
public sealed class InventoryScanCompletedEventArgs : EventArgs
{
    public InventorySnapshot Snapshot { get; }
    
    public InventoryScanCompletedEventArgs(InventorySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }
}

/// <summary>
/// Event arguments for scan errors.
/// </summary>
public sealed class InventoryScanErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public double ScanTimeMs { get; }
    
    public InventoryScanErrorEventArgs(Exception exception, double scanTimeMs)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        ScanTimeMs = scanTimeMs;
    }
}

/// <summary>
/// Performance statistics for the inventory service.
/// </summary>
public sealed class InventoryServiceStats
{
    public int TotalScans { get; set; }
    public int CacheHits { get; set; }
    public double CacheHitRate { get; set; }
    public double AverageScanTimeMs { get; set; }
    public bool IsMonitoring { get; set; }
    public int ScanIntervalMs { get; set; }
    public DateTime LastScanTime { get; set; }
    public int CachedSnapshotCount { get; set; }
}