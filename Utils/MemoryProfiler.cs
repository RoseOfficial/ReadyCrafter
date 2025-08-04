using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace ReadyCrafter.Utils;

/// <summary>
/// Memory profiling and leak detection utility targeting the 75MB limit from PRD.
/// Monitors memory allocation patterns, detects potential leaks, and provides optimization recommendations.
/// </summary>
public sealed class MemoryProfiler : IDisposable
{
    private readonly IPluginLog _logger;
    private readonly Timer _profileTimer;
    private readonly object _lockObject = new();
    private readonly ConcurrentQueue<MemorySnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, MemoryAllocationTracker> _allocationTrackers = new();
    private readonly Process _currentProcess;
    
    private bool _disposed = false;
    private DateTime _profilingStartTime = DateTime.UtcNow;
    private long _baselineMemoryUsage = 0;
    private int _snapshotInterval = 5000; // 5 seconds
    
    // Memory thresholds from PRD
    public const long TargetMemoryLimitBytes = 75L * 1024 * 1024; // 75MB
    public const long WarningThresholdBytes = 60L * 1024 * 1024; // 60MB (80% of limit)
    public const long CriticalThresholdBytes = 70L * 1024 * 1024; // 70MB (93% of limit)
    
    // Leak detection parameters
    private const int MinSnapshotsForLeakDetection = 10;
    private const double LeakDetectionThresholdMBPerMinute = 1.0; // 1MB per minute growth
    private const int MaxSnapshotHistory = 200; // ~15 minutes at 5s intervals
    
    /// <summary>
    /// Event raised when memory usage exceeds thresholds.
    /// </summary>
    public event EventHandler<MemoryAlertEventArgs>? MemoryAlert;
    
    /// <summary>
    /// Event raised when a potential memory leak is detected.
    /// </summary>
    public event EventHandler<MemoryLeakDetectedEventArgs>? MemoryLeakDetected;
    
    /// <summary>
    /// Current memory usage in bytes.
    /// </summary>
    public long CurrentMemoryUsage => GetCurrentMemoryUsage();
    
    /// <summary>
    /// Whether memory profiling is active.
    /// </summary>
    public bool IsProfilingActive { get; private set; }
    
    /// <summary>
    /// Memory usage as percentage of target limit.
    /// </summary>
    public double MemoryUsagePercentage => (CurrentMemoryUsage / (double)TargetMemoryLimitBytes) * 100.0;
    
    /// <summary>
    /// Latest memory analysis results.
    /// </summary>
    public MemoryAnalysis? LatestAnalysis { get; private set; }

    public MemoryProfiler(IPluginLog logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _currentProcess = Process.GetCurrentProcess();
        
        // Initialize baseline memory usage
        _baselineMemoryUsage = GetCurrentMemoryUsage();
        
        // Initialize profiling timer (disabled by default)
        _profileTimer = new Timer(CaptureMemorySnapshot, null, Timeout.Infinite, Timeout.Infinite);
        
    }
    
    /// <summary>
    /// Start memory profiling with specified interval.
    /// </summary>
    /// <param name="intervalMs">Snapshot capture interval in milliseconds</param>
    public void StartProfiling(int intervalMs = 5000)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryProfiler));
        
        if (IsProfilingActive)
        {
            _logger.Warning("Memory profiling is already active");
            return;
        }
        
        if (intervalMs < 1000)
            throw new ArgumentException("Interval must be at least 1000ms", nameof(intervalMs));
        
        _snapshotInterval = intervalMs;
        IsProfilingActive = true;
        _profilingStartTime = DateTime.UtcNow;
        
        // Capture initial snapshot
        CaptureMemorySnapshot(null);
        
        // Start timer
        _profileTimer.Change(intervalMs, intervalMs);
        
        _logger.Information($"Memory profiling started with {intervalMs}ms interval");
    }
    
    /// <summary>
    /// Stop memory profiling.
    /// </summary>
    public void StopProfiling()
    {
        if (!IsProfilingActive)
            return;
        
        IsProfilingActive = false;
        _profileTimer.Change(Timeout.Infinite, Timeout.Infinite);
        
        _logger.Information($"Memory profiling stopped after {DateTime.UtcNow - _profilingStartTime:hh\\:mm\\:ss}");
    }
    
    /// <summary>
    /// Create a memory allocation tracker for a specific component.
    /// </summary>
    /// <param name="componentName">Name of the component to track</param>
    /// <returns>Memory allocation tracker</returns>
    public IMemoryAllocationTracker CreateAllocationTracker(string componentName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryProfiler));
        
        if (string.IsNullOrEmpty(componentName))
            throw new ArgumentException("Component name cannot be null or empty", nameof(componentName));
        
        var tracker = new MemoryAllocationTracker(componentName, this);
        _allocationTrackers.TryAdd(tracker.Id, tracker);
        
        return tracker;
    }
    
    /// <summary>
    /// Perform immediate memory analysis and return results.
    /// </summary>
    /// <returns>Memory analysis results</returns>
    public MemoryAnalysis AnalyzeMemoryUsage()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryProfiler));
        
        var currentUsage = GetCurrentMemoryUsage();
        var analysis = new MemoryAnalysis
        {
            Timestamp = DateTime.UtcNow,
            CurrentUsageBytes = currentUsage,
            BaselineUsageBytes = _baselineMemoryUsage,
            GrowthFromBaselineBytes = currentUsage - _baselineMemoryUsage,
            UsagePercentageOfLimit = (currentUsage / (double)TargetMemoryLimitBytes) * 100.0,
            IsOverWarningThreshold = currentUsage > WarningThresholdBytes,
            IsOverCriticalThreshold = currentUsage > CriticalThresholdBytes,
            IsOverLimit = currentUsage > TargetMemoryLimitBytes,
            AllocationTrackers = GetAllocationTrackerSummaries(),
            Recommendations = GenerateOptimizationRecommendations(currentUsage)
        };
        
        // Analyze memory growth trends
        lock (_lockObject)
        {
            var snapshots = _snapshots.ToList();
            if (snapshots.Count >= MinSnapshotsForLeakDetection)
            {
                analysis.GrowthTrend = AnalyzeGrowthTrend(snapshots);
                analysis.PotentialLeakDetected = DetectPotentialLeak(snapshots);
                
                if (analysis.PotentialLeakDetected)
                {
                    analysis.EstimatedLeakRateMBPerMinute = CalculateLeakRate(snapshots);
                }
            }
        }
        
        // Calculate garbage collection statistics
        analysis.GcStats = new GarbageCollectionStats
        {
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalMemory = GC.GetTotalMemory(false),
            EstimatedTotalMemoryAfterGC = GC.GetTotalMemory(true)
        };
        
        LatestAnalysis = analysis;
        
        // Check for alerts
        CheckMemoryAlerts(analysis);
        
        return analysis;
    }
    
    /// <summary>
    /// Force garbage collection and return memory freed.
    /// </summary>
    /// <returns>Amount of memory freed in bytes</returns>
    public long ForceGarbageCollection()
    {
        var beforeGC = GetCurrentMemoryUsage();
        
        // Force full garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var afterGC = GetCurrentMemoryUsage();
        var memoryFreed = beforeGC - afterGC;
        
        _logger.Information($"Forced GC freed {memoryFreed / (1024.0 * 1024.0):F2}MB " +
                           $"(from {beforeGC / (1024.0 * 1024.0):F2}MB to {afterGC / (1024.0 * 1024.0):F2}MB)");
        
        return memoryFreed;
    }
    
    /// <summary>
    /// Get memory usage statistics for all tracked components.
    /// </summary>
    /// <returns>Dictionary of component memory usage</returns>
    public Dictionary<string, ComponentMemoryStats> GetComponentMemoryStats()
    {
        var stats = new Dictionary<string, ComponentMemoryStats>();
        
        foreach (var kvp in _allocationTrackers)
        {
            var tracker = kvp.Value;
            stats[tracker.ComponentName] = new ComponentMemoryStats
            {
                ComponentName = tracker.ComponentName,
                TotalAllocations = tracker.TotalAllocations,
                TotalAllocationBytes = tracker.TotalAllocationBytes,
                ActiveAllocations = tracker.ActiveAllocations,
                ActiveAllocationBytes = tracker.ActiveAllocationBytes,
                AverageAllocationSize = tracker.AverageAllocationSize,
                PeakMemoryUsage = tracker.PeakMemoryUsage,
                LastActivity = tracker.LastActivity
            };
        }
        
        return stats;
    }
    
    /// <summary>
    /// Generate memory optimization report.
    /// </summary>
    /// <returns>Memory optimization recommendations</returns>
    public MemoryOptimizationReport GenerateOptimizationReport()
    {
        var analysis = AnalyzeMemoryUsage();
        var componentStats = GetComponentMemoryStats();
        
        var report = new MemoryOptimizationReport
        {
            GeneratedAt = DateTime.UtcNow,
            CurrentAnalysis = analysis,
            ComponentStats = componentStats,
            HighMemoryComponents = componentStats.Values
                .Where(s => s.ActiveAllocationBytes > 5 * 1024 * 1024) // > 5MB
                .OrderByDescending(s => s.ActiveAllocationBytes)
                .ToList(),
            OptimizationRecommendations = analysis.Recommendations,
            MemoryTrend = GetMemoryTrendSummary()
        };
        
        return report;
    }
    
    /// <summary>
    /// Clear all profiling data and reset baseline.
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _snapshots.Clear();
            _allocationTrackers.Clear();
            _baselineMemoryUsage = GetCurrentMemoryUsage();
            _profilingStartTime = DateTime.UtcNow;
            LatestAnalysis = null;
        }
        
        _logger.Information("Memory profiler reset");
    }
    
    public long GetCurrentMemoryUsage()
    {
        try
        {
            _currentProcess.Refresh();
            return _currentProcess.WorkingSet64;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get current memory usage");
            return 0;
        }
    }
    
    private void CaptureMemorySnapshot(object? state)
    {
        if (_disposed || !IsProfilingActive)
            return;
        
        try
        {
            var snapshot = new MemorySnapshot
            {
                Timestamp = DateTime.UtcNow,
                WorkingSetBytes = GetCurrentMemoryUsage(),
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2)
            };
            
            lock (_lockObject)
            {
                _snapshots.Enqueue(snapshot);
                
                // Maintain snapshot history size
                while (_snapshots.Count > MaxSnapshotHistory)
                {
                    _snapshots.TryDequeue(out _);
                }
            }
            
            // Check for memory threshold violations
            CheckMemoryThresholds(snapshot);
            
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error capturing memory snapshot");
        }
    }
    
    private void CheckMemoryThresholds(MemorySnapshot snapshot)
    {
        var alerts = new List<string>();
        
        if (snapshot.WorkingSetBytes > TargetMemoryLimitBytes)
        {
            alerts.Add($"Memory usage ({snapshot.WorkingSetBytes / (1024.0 * 1024.0):F1}MB) exceeds target limit ({TargetMemoryLimitBytes / (1024.0 * 1024.0):F1}MB)");
        }
        else if (snapshot.WorkingSetBytes > CriticalThresholdBytes)
        {
            alerts.Add($"Memory usage ({snapshot.WorkingSetBytes / (1024.0 * 1024.0):F1}MB) is in critical range (>{CriticalThresholdBytes / (1024.0 * 1024.0):F1}MB)");
        }
        else if (snapshot.WorkingSetBytes > WarningThresholdBytes)
        {
            alerts.Add($"Memory usage ({snapshot.WorkingSetBytes / (1024.0 * 1024.0):F1}MB) is above warning threshold (>{WarningThresholdBytes / (1024.0 * 1024.0):F1}MB)");
        }
        
        foreach (var alert in alerts)
        {
            RaiseMemoryAlert(MemoryAlertType.ThresholdExceeded, alert, snapshot);
        }
    }
    
    private void CheckMemoryAlerts(MemoryAnalysis analysis)
    {
        if (analysis.PotentialLeakDetected)
        {
            var message = $"Potential memory leak detected: {analysis.EstimatedLeakRateMBPerMinute:F2}MB/min growth rate";
            RaiseMemoryLeakAlert(message, analysis);
        }
    }
    
    private MemoryGrowthTrend AnalyzeGrowthTrend(List<MemorySnapshot> snapshots)
    {
        if (snapshots.Count < 2)
            return MemoryGrowthTrend.Stable;
        
        var recentSnapshots = snapshots.TakeLast(10).ToList();
        var growthRates = new List<double>();
        
        for (int i = 1; i < recentSnapshots.Count; i++)
        {
            var previous = recentSnapshots[i - 1];
            var current = recentSnapshots[i];
            var timeDiff = (current.Timestamp - previous.Timestamp).TotalMinutes;
            
            if (timeDiff > 0)
            {
                var memoryDiff = current.WorkingSetBytes - previous.WorkingSetBytes;
                var growthRateMBPerMin = (memoryDiff / (1024.0 * 1024.0)) / timeDiff;
                growthRates.Add(growthRateMBPerMin);
            }
        }
        
        if (!growthRates.Any())
            return MemoryGrowthTrend.Stable;
        
        var averageGrowthRate = growthRates.Average();
        
        if (averageGrowthRate > LeakDetectionThresholdMBPerMinute)
            return MemoryGrowthTrend.Increasing;
        else if (averageGrowthRate < -LeakDetectionThresholdMBPerMinute)
            return MemoryGrowthTrend.Decreasing;
        else
            return MemoryGrowthTrend.Stable;
    }
    
    private bool DetectPotentialLeak(List<MemorySnapshot> snapshots)
    {
        if (snapshots.Count < MinSnapshotsForLeakDetection)
            return false;
        
        var leakRate = CalculateLeakRate(snapshots);
        return leakRate > LeakDetectionThresholdMBPerMinute;
    }
    
    private double CalculateLeakRate(List<MemorySnapshot> snapshots)
    {
        if (snapshots.Count < 2)
            return 0;
        
        var first = snapshots.First();
        var last = snapshots.Last();
        
        var timeDiff = (last.Timestamp - first.Timestamp).TotalMinutes;
        if (timeDiff <= 0)
            return 0;
        
        var memoryDiff = last.WorkingSetBytes - first.WorkingSetBytes;
        return (memoryDiff / (1024.0 * 1024.0)) / timeDiff; // MB per minute
    }
    
    private List<string> GenerateOptimizationRecommendations(long currentUsage)
    {
        var recommendations = new List<string>();
        
        if (currentUsage > CriticalThresholdBytes)
        {
            recommendations.Add("Critical: Consider reducing cache sizes or implementing cache eviction policies");
            recommendations.Add("Critical: Review and optimize large object allocations");
            recommendations.Add("Critical: Consider forcing garbage collection more frequently");
        }
        else if (currentUsage > WarningThresholdBytes)
        {
            recommendations.Add("Warning: Monitor memory growth trends closely");
            recommendations.Add("Warning: Consider implementing memory pooling for frequently allocated objects");
            recommendations.Add("Warning: Review component memory usage patterns");
        }
        
        // Check GC pressure
        var managedMemory = GC.GetTotalMemory(false);
        if (managedMemory > 30 * 1024 * 1024) // 30MB managed memory
        {
            recommendations.Add("Consider optimizing managed memory allocations and object lifetimes");
        }
        
        // Check component-specific recommendations
        var componentStats = GetComponentMemoryStats();
        var heavyComponents = componentStats.Values
            .Where(s => s.ActiveAllocationBytes > 10 * 1024 * 1024) // > 10MB
            .OrderByDescending(s => s.ActiveAllocationBytes)
            .Take(3);
        
        foreach (var component in heavyComponents)
        {
            recommendations.Add($"Review {component.ComponentName} component - using {component.ActiveAllocationBytes / (1024.0 * 1024.0):F1}MB");
        }
        
        return recommendations;
    }
    
    private List<ComponentAllocationSummary> GetAllocationTrackerSummaries()
    {
        return _allocationTrackers.Values
            .Select(t => new ComponentAllocationSummary
            {
                ComponentName = t.ComponentName,
                ActiveAllocations = t.ActiveAllocations,
                ActiveAllocationBytes = t.ActiveAllocationBytes,
                TotalAllocations = t.TotalAllocations,
                TotalAllocationBytes = t.TotalAllocationBytes
            })
            .ToList();
    }
    
    private MemoryTrendSummary GetMemoryTrendSummary()
    {
        lock (_lockObject)
        {
            var snapshots = _snapshots.ToList();
            if (snapshots.Count < 2)
            {
                return new MemoryTrendSummary
                {
                    TrendDirection = MemoryGrowthTrend.Stable,
                    GrowthRateMBPerMinute = 0
                };
            }
            
            return new MemoryTrendSummary
            {
                TrendDirection = AnalyzeGrowthTrend(snapshots),
                GrowthRateMBPerMinute = CalculateLeakRate(snapshots),
                SnapshotCount = snapshots.Count,
                MonitoringDuration = snapshots.Last().Timestamp - snapshots.First().Timestamp
            };
        }
    }
    
    private void RaiseMemoryAlert(MemoryAlertType alertType, string message, MemorySnapshot snapshot)
    {
        var alert = new MemoryAlertEventArgs
        {
            AlertType = alertType,
            Message = message,
            Timestamp = DateTime.UtcNow,
            CurrentMemoryUsageBytes = snapshot.WorkingSetBytes,
            Snapshot = snapshot
        };
        
        try
        {
            MemoryAlert?.Invoke(this, alert);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in MemoryAlert event handler");
        }
        
        _logger.Warning($"Memory Alert: {message}");
    }
    
    private void RaiseMemoryLeakAlert(string message, MemoryAnalysis analysis)
    {
        var alert = new MemoryLeakDetectedEventArgs
        {
            Message = message,
            Timestamp = DateTime.UtcNow,
            LeakRateMBPerMinute = analysis.EstimatedLeakRateMBPerMinute,
            Analysis = analysis
        };
        
        try
        {
            MemoryLeakDetected?.Invoke(this, alert);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in MemoryLeakDetected event handler");
        }
        
        _logger.Error($"Memory Leak Alert: {message}");
    }
    
    internal void CompleteAllocation(MemoryAllocationTracker tracker)
    {
        // Tracker completed, remove from active tracking
        _allocationTrackers.TryRemove(tracker.Id, out _);
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        StopProfiling();
        _profileTimer?.Dispose();
        _currentProcess?.Dispose();
        
        _snapshots.Clear();
        _allocationTrackers.Clear();
        
    }
}

/// <summary>
/// Interface for tracking memory allocations in a specific component.
/// </summary>
public interface IMemoryAllocationTracker : IDisposable
{
    string ComponentName { get; }
    long TotalAllocations { get; }
    long TotalAllocationBytes { get; }
    long ActiveAllocations { get; }
    long ActiveAllocationBytes { get; }
    
    void RecordAllocation(long bytes);
    void RecordDeallocation(long bytes);
}

/// <summary>
/// Tracks memory allocations for a specific component.
/// </summary>
internal sealed class MemoryAllocationTracker : IMemoryAllocationTracker
{
    private readonly MemoryProfiler _profiler;
    private long _totalAllocations = 0;
    private long _totalAllocationBytes = 0;
    private long _activeAllocations = 0;
    private long _activeAllocationBytes = 0;
    private long _peakMemoryUsage = 0;
    private bool _disposed = false;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string ComponentName { get; }
    public long TotalAllocations => _totalAllocations;
    public long TotalAllocationBytes => _totalAllocationBytes;
    public long ActiveAllocations => _activeAllocations;
    public long ActiveAllocationBytes => _activeAllocationBytes;
    public long AverageAllocationSize => _totalAllocations > 0 ? _totalAllocationBytes / _totalAllocations : 0;
    public long PeakMemoryUsage => _peakMemoryUsage;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    
    public MemoryAllocationTracker(string componentName, MemoryProfiler profiler)
    {
        ComponentName = componentName ?? throw new ArgumentNullException(nameof(componentName));
        _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
    }
    
    public void RecordAllocation(long bytes)
    {
        if (_disposed || bytes <= 0)
            return;
        
        Interlocked.Increment(ref _totalAllocations);
        Interlocked.Add(ref _totalAllocationBytes, bytes);
        Interlocked.Increment(ref _activeAllocations);
        var newActiveBytes = Interlocked.Add(ref _activeAllocationBytes, bytes);
        
        // Update peak usage
        long currentPeak = _peakMemoryUsage;
        while (newActiveBytes > currentPeak)
        {
            var originalPeak = Interlocked.CompareExchange(ref _peakMemoryUsage, newActiveBytes, currentPeak);
            if (originalPeak == currentPeak)
                break;
            currentPeak = originalPeak;
        }
        
        LastActivity = DateTime.UtcNow;
    }
    
    public void RecordDeallocation(long bytes)
    {
        if (_disposed || bytes <= 0)
            return;
        
        Interlocked.Decrement(ref _activeAllocations);
        Interlocked.Add(ref _activeAllocationBytes, -bytes);
        
        LastActivity = DateTime.UtcNow;
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        _profiler.CompleteAllocation(this);
    }
}

/// <summary>
/// Memory snapshot at a point in time.
/// </summary>
public sealed class MemorySnapshot
{
    public DateTime Timestamp { get; set; }
    public long WorkingSetBytes { get; set; }
    public long ManagedMemoryBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

/// <summary>
/// Comprehensive memory analysis results.
/// </summary>
public sealed class MemoryAnalysis
{
    public DateTime Timestamp { get; set; }
    public long CurrentUsageBytes { get; set; }
    public long BaselineUsageBytes { get; set; }
    public long GrowthFromBaselineBytes { get; set; }
    public double UsagePercentageOfLimit { get; set; }
    public bool IsOverWarningThreshold { get; set; }
    public bool IsOverCriticalThreshold { get; set; }
    public bool IsOverLimit { get; set; }
    public MemoryGrowthTrend GrowthTrend { get; set; }
    public bool PotentialLeakDetected { get; set; }
    public double EstimatedLeakRateMBPerMinute { get; set; }
    public GarbageCollectionStats GcStats { get; set; } = new();
    public List<ComponentAllocationSummary> AllocationTrackers { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Garbage collection statistics.
/// </summary>
public sealed class GarbageCollectionStats
{
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long TotalMemory { get; set; }
    public long EstimatedTotalMemoryAfterGC { get; set; }
}

/// <summary>
/// Component allocation summary.
/// </summary>
public sealed class ComponentAllocationSummary
{
    public string ComponentName { get; set; } = string.Empty;
    public long ActiveAllocations { get; set; }
    public long ActiveAllocationBytes { get; set; }
    public long TotalAllocations { get; set; }
    public long TotalAllocationBytes { get; set; }
}

/// <summary>
/// Component memory usage statistics.
/// </summary>
public sealed class ComponentMemoryStats
{
    public string ComponentName { get; set; } = string.Empty;
    public long TotalAllocations { get; set; }
    public long TotalAllocationBytes { get; set; }
    public long ActiveAllocations { get; set; }
    public long ActiveAllocationBytes { get; set; }
    public long AverageAllocationSize { get; set; }
    public long PeakMemoryUsage { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Memory optimization report.
/// </summary>
public sealed class MemoryOptimizationReport
{
    public DateTime GeneratedAt { get; set; }
    public MemoryAnalysis CurrentAnalysis { get; set; } = new();
    public Dictionary<string, ComponentMemoryStats> ComponentStats { get; set; } = new();
    public List<ComponentMemoryStats> HighMemoryComponents { get; set; } = new();
    public List<string> OptimizationRecommendations { get; set; } = new();
    public MemoryTrendSummary MemoryTrend { get; set; } = new();
}

/// <summary>
/// Memory trend summary.
/// </summary>
public sealed class MemoryTrendSummary
{
    public MemoryGrowthTrend TrendDirection { get; set; }
    public double GrowthRateMBPerMinute { get; set; }
    public int SnapshotCount { get; set; }
    public TimeSpan MonitoringDuration { get; set; }
}

/// <summary>
/// Memory alert event arguments.
/// </summary>
public sealed class MemoryAlertEventArgs : EventArgs
{
    public MemoryAlertType AlertType { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long CurrentMemoryUsageBytes { get; set; }
    public MemorySnapshot? Snapshot { get; set; }
}

/// <summary>
/// Memory leak detection event arguments.
/// </summary>
public sealed class MemoryLeakDetectedEventArgs : EventArgs
{
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public double LeakRateMBPerMinute { get; set; }
    public MemoryAnalysis? Analysis { get; set; }
}

/// <summary>
/// Types of memory alerts.
/// </summary>
public enum MemoryAlertType
{
    ThresholdExceeded,
    PotentialLeak,
    RapidGrowth,
    GcPressure
}

/// <summary>
/// Memory growth trend directions.
/// </summary>
public enum MemoryGrowthTrend
{
    Stable,
    Increasing,
    Decreasing
}