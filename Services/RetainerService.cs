using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using ReadyCrafter.Models;

namespace ReadyCrafter.Services;

/// <summary>
/// Handles optional retainer bag scanning using FFXIVClientStructs for containers 1000-1070.
/// This service provides read-only memory access with comprehensive safety checks and performance optimization.
/// Default OFF to minimize risk - requires explicit configuration to enable.
/// </summary>
public sealed class RetainerService : IDisposable
{
    private readonly IClientState _clientState;
    private readonly IPluginLog _logger;
    private readonly object _lockObject = new();
    private readonly Timer _memoryValidationTimer;
    
    // Performance tracking
    private readonly ConcurrentQueue<double> _scanTimes = new();
    private int _totalScans = 0;
    private int _successfulScans = 0;
    private int _failedScans = 0;
    private DateTime _lastScanTime = DateTime.MinValue;
    private bool _isScanning = false;
    private bool _disposed = false;
    
    // Memory safety tracking
    private bool _memoryStructuresValid = false;
    private DateTime _lastValidationTime = DateTime.MinValue;
    private int _consecutiveFailures = 0;
    private readonly object _validationLock = new();
    
    /// <summary>
    /// Event raised when retainer scan completes successfully.
    /// </summary>
    public event EventHandler<RetainerScanCompletedEventArgs>? ScanCompleted;
    
    /// <summary>
    /// Event raised when retainer scan encounters an error.
    /// </summary>
    public event EventHandler<RetainerScanErrorEventArgs>? ScanError;
    
    /// <summary>
    /// Maximum scan time before timing out (300ms target per PRD).
    /// </summary>
    public const int MaxScanTimeMs = 300;
    
    /// <summary>
    /// Maximum consecutive failures before disabling automatic scans.
    /// </summary>
    public const int MaxConsecutiveFailures = 3;
    
    /// <summary>
    /// Memory structure validation interval in milliseconds.
    /// </summary>
    public const int ValidationIntervalMs = 30000; // 30 seconds
    
    /// <summary>
    /// Retainer container ID range (1000-1070).
    /// </summary>
    public static readonly uint[] RetainerContainerIds = 
        Enumerable.Range(1000, 71).Select(x => (uint)x).ToArray();
    
    /// <summary>
    /// Whether retainer scanning is currently enabled.
    /// </summary>
    public bool IsEnabled { get; private set; } = false;
    
    /// <summary>
    /// Whether memory structures are currently valid for scanning.
    /// </summary>
    public bool AreMemoryStructuresValid => _memoryStructuresValid;
    
    /// <summary>
    /// Average scan time over recent scans.
    /// </summary>
    public double AverageScanTimeMs
    {
        get
        {
            lock (_lockObject)
            {
                return _scanTimes.IsEmpty ? 0 : _scanTimes.Average();
            }
        }
    }
    
    /// <summary>
    /// Success rate as a percentage.
    /// </summary>
    public double SuccessRate => _totalScans == 0 ? 0 : (_successfulScans / (double)_totalScans) * 100;
    
    /// <summary>
    /// Whether a scan is currently in progress.
    /// </summary>
    public bool IsScanning => _isScanning;
    
    /// <summary>
    /// Time of the last successful scan.
    /// </summary>
    public DateTime LastScanTime => _lastScanTime;

    public RetainerService(IClientState clientState, IPluginLog logger)
    {
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Initialize memory validation timer
        _memoryValidationTimer = new Timer(ValidateMemoryStructures, null, 
            ValidationIntervalMs, ValidationIntervalMs);
        
    }
    
    /// <summary>
    /// Enable retainer scanning with safety validation.
    /// </summary>
    /// <param name="performValidation">Whether to validate memory structures before enabling</param>
    /// <returns>True if successfully enabled, false if validation failed</returns>
    public async Task<bool> EnableAsync(bool performValidation = true)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RetainerService));
        
        if (IsEnabled)
        {
            _logger.Information("Retainer scanning is already enabled");
            return true;
        }
        
        if (performValidation)
        {
            _logger.Information("Validating memory structures before enabling retainer scanning...");
            
            if (!await ValidateMemoryStructuresAsync())
            {
                _logger.Warning("Memory structure validation failed - retainer scanning cannot be enabled");
                return false;
            }
        }
        
        IsEnabled = true;
        _consecutiveFailures = 0;
        
        _logger.Information("Retainer scanning enabled successfully");
        return true;
    }
    
    /// <summary>
    /// Disable retainer scanning.
    /// </summary>
    public void Disable()
    {
        if (!IsEnabled)
            return;
        
        IsEnabled = false;
        _logger.Information("Retainer scanning disabled");
    }
    
    /// <summary>
    /// Scan retainer inventories and return item quantities.
    /// </summary>
    /// <param name="options">Scan configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of item quantities found in retainers</returns>
    public async Task<Dictionary<uint, ItemQuantity>> ScanRetainersAsync(
        ScanOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RetainerService));
        
        if (!IsEnabled)
        {
            return new Dictionary<uint, ItemQuantity>();
        }
        
        if (!options.IncludeRetainers)
        {
            return new Dictionary<uint, ItemQuantity>();
        }
        
        if (_isScanning)
        {
            _logger.Warning("Retainer scan already in progress - waiting for completion");
            
            var timeout = DateTime.UtcNow.AddMilliseconds(MaxScanTimeMs * 2);
            while (_isScanning && DateTime.UtcNow < timeout)
            {
                await System.Threading.Tasks.Task.Delay(10, cancellationToken);
            }
            
            if (_isScanning)
            {
                throw new TimeoutException("Previous retainer scan did not complete within timeout");
            }
        }
        
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            _isScanning = true;
            
            // Pre-scan validation
            if (!ValidatePreScanConditions())
            {
                throw new InvalidOperationException("Pre-scan validation failed");
            }
            
            var results = await System.Threading.Tasks.Task.Run(() => PerformRetainerScan(cancellationToken), cancellationToken);
            
            stopwatch.Stop();
            var scanTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            
            // Update performance metrics
            UpdatePerformanceMetrics(scanTimeMs, true);
            
            _logger.Information($"Retainer inventory scan completed in {scanTimeMs:F2}ms " +
                         $"({results.Values.Sum(x => x.Total)} total items from retainers)");
            
            OnScanCompleted(new RetainerScanCompletedEventArgs(results, scanTimeMs));
            
            return results;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var scanTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            
            UpdatePerformanceMetrics(scanTimeMs, false);
            
            _logger.Error(ex, $"Retainer scan failed after {scanTimeMs:F2}ms");
            OnScanError(new RetainerScanErrorEventArgs(ex, scanTimeMs));
            
            // Check if we should disable due to consecutive failures
            lock (_validationLock)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.Warning($"Disabling retainer scanning due to {_consecutiveFailures} consecutive failures");
                    IsEnabled = false;
                }
            }
            
            // Return empty results instead of throwing for graceful degradation
            return new Dictionary<uint, ItemQuantity>();
        }
        finally
        {
            _isScanning = false;
        }
    }
    
    /// <summary>
    /// Get comprehensive performance and health statistics.
    /// </summary>
    public RetainerServiceStats GetStats()
    {
        lock (_lockObject)
        {
            return new RetainerServiceStats
            {
                IsEnabled = IsEnabled,
                IsScanning = IsScanning,
                MemoryStructuresValid = _memoryStructuresValid,
                TotalScans = _totalScans,
                SuccessfulScans = _successfulScans,
                FailedScans = _failedScans,
                SuccessRate = SuccessRate,
                AverageScanTimeMs = AverageScanTimeMs,
                LastScanTime = _lastScanTime,
                LastValidationTime = _lastValidationTime,
                ConsecutiveFailures = _consecutiveFailures
            };
        }
    }
    
    private unsafe Dictionary<uint, ItemQuantity> PerformRetainerScan(CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<uint, ItemQuantity>();
        var processedContainers = 0;
        
        // Get Framework instance for safe memory access
        var framework = Framework.Instance();
        if (framework == null)
        {
            throw new InvalidOperationException("Framework instance is not available");
        }
        
        // Get InventoryManager for retainer access
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            throw new InvalidOperationException("InventoryManager instance is not available");
        }
        
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) // Limit for memory safety
        };
        
        try
        {
            // Scan retainer containers in parallel with safety checks
            Parallel.ForEach(RetainerContainerIds, parallelOptions, containerId =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    // Validate memory access before each container
                    if (!ValidateMemoryPointer(inventoryManager))
                    {
                        return;
                    }
                    
                    InventoryContainer* container;
                    unsafe
                    {
                        container = inventoryManager->GetInventoryContainer((InventoryType)containerId);
                    }
                    if (container == null)
                    {
                        // Container doesn't exist - this is normal for unused retainer slots
                        return;
                    }
                    
                    // Additional safety check on container pointer
                    if (!ValidateContainerPointer(container))
                    {
                        return;
                    }
                    
                    // Check if container is loaded and accessible
                    if (container == null)
                    {
                        // Container not loaded - retainer may not be active
                        return;
                    }
                    
                    var containerItems = ScanRetainerContainer(container, containerId);
                    
                    // Merge results thread-safely
                    foreach (var kvp in containerItems)
                    {
                        results.AddOrUpdate(kvp.Key, kvp.Value, (key, existing) => 
                        {
                            lock (existing)
                            {
                                existing.Nq += kvp.Value.Nq;
                                existing.Hq += kvp.Value.Hq;
                                return existing;
                            }
                        });
                    }
                    
                    Interlocked.Increment(ref processedContainers);
                }
                catch (AccessViolationException ex)
                {
                    _logger.Error(ex, $"Memory access violation scanning retainer container {containerId}");
                    // Don't rethrow - continue with other containers
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, $"Error scanning retainer container {containerId}");
                    // Don't rethrow - continue with other containers
                }
            });
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Retainer scan was cancelled");
            throw;
        }
        
        
        return new Dictionary<uint, ItemQuantity>(results);
    }
    
    private unsafe Dictionary<uint, ItemQuantity> ScanRetainerContainer(
        InventoryContainer* container, uint containerId)
    {
        var items = new Dictionary<uint, ItemQuantity>();
        
        if (container == null || container->Size == 0)
            return items;
        
        // Additional bounds checking
        var maxSlots = Math.Min(container->Size, 200); // Reasonable upper limit for safety
        
        for (var i = 0; i < maxSlots; i++)
        {
            try
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;
                
                // Validate item data before processing
                if (!ValidateItemPointer(item))
                {
                    continue;
                }
                
                var itemId = item->ItemId;
                var quantity = item->Quantity;
                var isHq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                
                // Sanity check on quantities to prevent invalid data
                if (quantity == 0 || quantity > 999999)
                {
                    continue;
                }
                
                if (!items.ContainsKey(itemId))
                {
                    items[itemId] = new ItemQuantity();
                }
                
                if (isHq)
                    items[itemId].Hq += (uint)quantity;
                else
                    items[itemId].Nq += (uint)quantity;
            }
            catch (AccessViolationException ex)
            {
                _logger.Error(ex, $"Memory access violation at slot {i} in retainer container {containerId}");
                break; // Stop processing this container
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, $"Error processing slot {i} in retainer container {containerId}");
                continue; // Skip this slot but continue with others
            }
        }
        
        return items;
    }
    
    private bool ValidatePreScanConditions()
    {
        // Check client state
        if (!_clientState.IsLoggedIn)
        {
            return false;
        }
        
        // Check memory structure validity
        lock (_validationLock)
        {
            if (!_memoryStructuresValid)
            {
                return false;
            }
            
            // Check if validation is too old
            if (DateTime.UtcNow - _lastValidationTime > TimeSpan.FromMinutes(5))
            {
                _ = System.Threading.Tasks.Task.Run(async () => await ValidateMemoryStructuresAsync());
                return false;
            }
        }
        
        return true;
    }
    
    private async Task<bool> ValidateMemoryStructuresAsync()
    {
        return await System.Threading.Tasks.Task.Run(() =>
        {
            unsafe
            {
                try
                {
                    // Validate Framework access
                    var framework = Framework.Instance();
                    if (framework == null)
                    {
                        return SetValidationResult(false);
                    }
                    
                    // Validate InventoryManager access
                    var inventoryManager = InventoryManager.Instance();
                    if (inventoryManager == null)
                    {
                        return SetValidationResult(false);
                    }
                    
                    // Test basic memory access with a known safe container
                    if (!ValidateMemoryPointer(inventoryManager))
                    {
                        return SetValidationResult(false);
                    }
                    
                    // Try to access a standard container to validate memory layout
                    var testContainer = inventoryManager->GetInventoryContainer(InventoryType.Inventory1);
                    if (testContainer != null && !ValidateContainerPointer(testContainer))
                    {
                        return SetValidationResult(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Memory structure validation threw exception");
                    return SetValidationResult(false);
                }
                
                return SetValidationResult(true);
            }
        });
    }
    
    private bool SetValidationResult(bool isValid)
    {
        lock (_validationLock)
        {
            _memoryStructuresValid = isValid;
            _lastValidationTime = DateTime.UtcNow;
            
            if (isValid)
            {
                _consecutiveFailures = 0;
            }
            
            return isValid;
        }
    }
    
    private unsafe bool ValidateMemoryPointer(void* pointer)
    {
        if (pointer == null)
            return false;
        
        try
        {
            // Basic pointer validation - check if we can read the first few bytes
            var testPtr = (byte*)pointer;
            _ = testPtr[0]; // This will throw AccessViolationException if invalid
            return true;
        }
        catch (AccessViolationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private unsafe bool ValidateContainerPointer(InventoryContainer* container)
    {
        if (container == null)
            return false;
        
        try
        {
            // Validate container structure fields
            _ = container->Size;
            // Container is accessible if not null
            return container->Size < 10000; // Reasonable upper bound
        }
        catch (AccessViolationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private unsafe bool ValidateItemPointer(InventoryItem* item)
    {
        if (item == null)
            return false;
        
        try
        {
            // Validate item structure fields
            _ = item->ItemId;
            _ = item->Quantity;
            _ = item->Flags;
            return true;
        }
        catch (AccessViolationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private void UpdatePerformanceMetrics(double scanTimeMs, bool success)
    {
        lock (_lockObject)
        {
            _scanTimes.Enqueue(scanTimeMs);
            
            // Keep only recent scan times
            while (_scanTimes.Count > 10)
            {
                _scanTimes.TryDequeue(out _);
            }
            
            Interlocked.Increment(ref _totalScans);
            
            if (success)
            {
                Interlocked.Increment(ref _successfulScans);
                _lastScanTime = DateTime.UtcNow;
            }
            else
            {
                Interlocked.Increment(ref _failedScans);
            }
            
            // Log performance warnings
            if (scanTimeMs > MaxScanTimeMs)
            {
                _logger.Warning($"Retainer scan took {scanTimeMs:F2}ms (exceeds {MaxScanTimeMs}ms target)");
            }
        }
    }
    
    private void ValidateMemoryStructures(object? state)
    {
        if (_disposed || !IsEnabled)
            return;
        
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await ValidateMemoryStructuresAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during periodic memory structure validation");
            }
        });
    }
    
    private void OnScanCompleted(RetainerScanCompletedEventArgs args)
    {
        try
        {
            ScanCompleted?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in RetainerScanCompleted event handler");
        }
    }
    
    private void OnScanError(RetainerScanErrorEventArgs args)
    {
        try
        {
            ScanError?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in RetainerScanError event handler");
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        Disable();
        _memoryValidationTimer?.Dispose();
        
    }
}

/// <summary>
/// Event arguments for successful retainer scan completion.
/// </summary>
public sealed class RetainerScanCompletedEventArgs : EventArgs
{
    public Dictionary<uint, ItemQuantity> Items { get; }
    public double ScanTimeMs { get; }
    
    public RetainerScanCompletedEventArgs(Dictionary<uint, ItemQuantity> items, double scanTimeMs)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        ScanTimeMs = scanTimeMs;
    }
}

/// <summary>
/// Event arguments for retainer scan errors.
/// </summary>
public sealed class RetainerScanErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public double ScanTimeMs { get; }
    
    public RetainerScanErrorEventArgs(Exception exception, double scanTimeMs)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        ScanTimeMs = scanTimeMs;
    }
}

/// <summary>
/// Performance and health statistics for the retainer service.
/// </summary>
public sealed class RetainerServiceStats
{
    public bool IsEnabled { get; set; }
    public bool IsScanning { get; set; }
    public bool MemoryStructuresValid { get; set; }
    public int TotalScans { get; set; }
    public int SuccessfulScans { get; set; }
    public int FailedScans { get; set; }
    public double SuccessRate { get; set; }
    public double AverageScanTimeMs { get; set; }
    public DateTime LastScanTime { get; set; }
    public DateTime LastValidationTime { get; set; }
    public int ConsecutiveFailures { get; set; }
}