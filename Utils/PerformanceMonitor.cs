using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace ReadyCrafter.Utils;

/// <summary>
/// Centralized performance monitoring system that tracks all PRD performance requirements.
/// Monitors scan times, memory usage, CPU usage, and system performance metrics.
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    private readonly IPluginLog _logger;
    private readonly Timer _performanceTimer;
    private readonly object _lockObject = new();
    private readonly ConcurrentQueue<PerformanceMetric> _metricsHistory = new();
    private readonly ConcurrentDictionary<string, OperationTracker> _operationTrackers = new();
    private readonly PerformanceCounter? _cpuCounter;
    private readonly Process _currentProcess;
    
    private bool _disposed = false;
    private DateTime _lastMetricsCapture = DateTime.UtcNow;
    
    // Performance thresholds from PRD
    public const double FullRescanMaxMs = 200.0;
    public const double FullRescanWithRetainerMaxMs = 300.0;
    public const double InventoryScanTargetMs = 150.0;
    public const long MaxMemoryUsageBytes = 75L * 1024 * 1024; // 75MB
    public const double MaxIdleCpuPercent = 1.0;
    
    // Metrics collection interval
    private const int MetricsIntervalMs = 1000; // 1 second
    private const int MaxHistoryEntries = 300; // 5 minutes of history
    
    /// <summary>
    /// Event raised when performance thresholds are exceeded.
    /// </summary>
    public event EventHandler<PerformanceAlertEventArgs>? PerformanceAlert;
    
    /// <summary>
    /// Current performance statistics.
    /// </summary>
    public PerformanceStats CurrentStats { get; private set; } = new();
    
    /// <summary>
    /// Whether performance monitoring is active.
    /// </summary>
    public bool IsMonitoring { get; private set; }

    public PerformanceMonitor(IPluginLog logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _currentProcess = Process.GetCurrentProcess();
        
        // Try to initialize CPU counter (may fail on some systems)
        try
        {
            _cpuCounter = new PerformanceCounter("Process", "% Processor Time", _currentProcess.ProcessName);
            _cpuCounter.NextValue(); // Initialize
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to initialize CPU performance counter, CPU monitoring will be limited");
        }
        
        // Initialize performance timer (disabled by default)
        _performanceTimer = new Timer(CaptureMetrics, null, Timeout.Infinite, Timeout.Infinite);
        
        _logger.Debug("PerformanceMonitor initialized");
    }
    
    /// <summary>
    /// Start performance monitoring.
    /// </summary>
    public void StartMonitoring()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceMonitor));
        
        if (IsMonitoring)
        {
            _logger.Warning("Performance monitoring is already active");
            return;
        }
        
        IsMonitoring = true;
        _performanceTimer.Change(0, MetricsIntervalMs);
        
        _logger.Information("Performance monitoring started");
    }
    
    /// <summary>
    /// Stop performance monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        if (!IsMonitoring)
            return;
        
        IsMonitoring = false;
        _performanceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        
        _logger.Information("Performance monitoring stopped");
    }
    
    /// <summary>
    /// Start tracking a specific operation.
    /// </summary>
    /// <param name="operationName">Name of the operation to track</param>
    /// <returns>Operation tracker instance</returns>
    public IOperationTracker StartOperation(string operationName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceMonitor));
        
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name cannot be null or empty", nameof(operationName));
        
        var tracker = new OperationTracker(operationName, this);
        tracker.Start();
        
        _operationTrackers.TryAdd(tracker.Id, tracker);
        
        return tracker;
    }
    
    /// <summary>
    /// Record a completed operation's performance metrics.
    /// </summary>
    /// <param name="operationName">Name of the operation</param>
    /// <param name="duration">Operation duration</param>
    /// <param name="additionalMetrics">Additional operation-specific metrics</param>
    public void RecordOperation(string operationName, TimeSpan duration, Dictionary<string, object>? additionalMetrics = null)
    {
        if (_disposed)
            return;
        
        var metrics = new OperationMetrics
        {
            OperationName = operationName,
            Duration = duration,
            Timestamp = DateTime.UtcNow,
            AdditionalMetrics = additionalMetrics ?? new Dictionary<string, object>()
        };
        
        // Check for performance threshold violations
        CheckPerformanceThresholds(metrics);
        
        // Add to current stats
        lock (_lockObject)
        {
            if (!CurrentStats.OperationMetrics.ContainsKey(operationName))
            {
                CurrentStats.OperationMetrics[operationName] = new List<OperationMetrics>();
            }
            
            CurrentStats.OperationMetrics[operationName].Add(metrics);
            
            // Keep only recent metrics (last 100 operations per type)
            if (CurrentStats.OperationMetrics[operationName].Count > 100)
            {
                CurrentStats.OperationMetrics[operationName].RemoveAt(0);
            }
        }
        
        _logger.Debug($"Recorded operation '{operationName}' completed in {duration.TotalMilliseconds:F2}ms");
    }
    
    /// <summary>
    /// Get performance statistics for a specific operation type.
    /// </summary>
    /// <param name="operationName">Name of the operation</param>
    /// <returns>Operation statistics or null if no data exists</returns>
    public OperationStats? GetOperationStats(string operationName)
    {
        if (string.IsNullOrEmpty(operationName))
            return null;
        
        lock (_lockObject)
        {
            if (!CurrentStats.OperationMetrics.TryGetValue(operationName, out var metrics) || !metrics.Any())
                return null;
            
            var durations = metrics.Select(m => m.Duration.TotalMilliseconds).ToList();
            
            return new OperationStats
            {
                OperationName = operationName,
                TotalOperations = metrics.Count,
                AverageDurationMs = durations.Average(),
                MinDurationMs = durations.Min(),
                MaxDurationMs = durations.Max(),
                MedianDurationMs = GetMedian(durations),
                P95DurationMs = GetPercentile(durations, 0.95),
                P99DurationMs = GetPercentile(durations, 0.99),
                LastOperationTime = metrics.Max(m => m.Timestamp),
                RecentOperations = metrics.TakeLast(10).ToList()
            };
        }
    }
    
    /// <summary>
    /// Get comprehensive performance report.
    /// </summary>
    /// <returns>Performance report with all tracked metrics</returns>
    public PerformanceReport GenerateReport()
    {
        lock (_lockObject)
        {
            var report = new PerformanceReport
            {
                GeneratedAt = DateTime.UtcNow,
                MonitoringDuration = DateTime.UtcNow - _lastMetricsCapture,
                SystemStats = CurrentStats.Clone(),
                Alerts = GetRecentAlerts(),
                OperationStats = CurrentStats.OperationMetrics.Keys
                    .Select(GetOperationStats)
                    .Where(s => s != null)
                    .Cast<OperationStats>()
                    .ToList(),
                ThresholdCompliance = CalculateThresholdCompliance()
            };
            
            return report;
        }
    }
    
    /// <summary>
    /// Check if current performance meets PRD requirements.
    /// </summary>
    /// <returns>True if all thresholds are met</returns>
    public bool IsPerformanceCompliant()
    {
        var compliance = CalculateThresholdCompliance();
        return compliance.All(kvp => kvp.Value);
    }
    
    /// <summary>
    /// Get current memory usage in bytes.
    /// </summary>
    /// <returns>Current memory usage</returns>
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
    
    /// <summary>
    /// Get current CPU usage percentage.
    /// </summary>
    /// <returns>CPU usage percentage or -1 if unavailable</returns>
    public double GetCurrentCpuUsage()
    {
        try
        {
            return _cpuCounter?.NextValue() ?? -1;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get current CPU usage");
            return -1;
        }
    }
    
    /// <summary>
    /// Clear all performance history and reset counters.
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _metricsHistory.Clear();
            CurrentStats = new PerformanceStats();
            _operationTrackers.Clear();
        }
        
        _logger.Information("Performance monitor reset");
    }
    
    private void CaptureMetrics(object? state)
    {
        if (_disposed || !IsMonitoring)
            return;
        
        try
        {
            var now = DateTime.UtcNow;
            var memoryUsage = GetCurrentMemoryUsage();
            var cpuUsage = GetCurrentCpuUsage();
            
            var metric = new PerformanceMetric
            {
                Timestamp = now,
                MemoryUsageBytes = memoryUsage,
                CpuUsagePercent = cpuUsage,
                ThreadCount = _currentProcess.Threads.Count,
                HandleCount = _currentProcess.HandleCount
            };
            
            // Add to history
            _metricsHistory.Enqueue(metric);
            
            // Maintain history size
            while (_metricsHistory.Count > MaxHistoryEntries)
            {
                _metricsHistory.TryDequeue(out _);
            }
            
            // Update current stats
            lock (_lockObject)
            {
                CurrentStats.LastCpuUsagePercent = cpuUsage;
                CurrentStats.LastMemoryUsageBytes = memoryUsage;
                CurrentStats.LastMetricsUpdate = now;
                
                // Calculate averages from recent history
                var recentMetrics = _metricsHistory.TakeLast(10).ToList();
                if (recentMetrics.Any())
                {
                    CurrentStats.AverageCpuUsagePercent = recentMetrics.Average(m => m.CpuUsagePercent);
                    CurrentStats.AverageMemoryUsageBytes = (long)recentMetrics.Average(m => m.MemoryUsageBytes);
                }
            }
            
            // Check for threshold violations
            CheckSystemThresholds(metric);
            
            _lastMetricsCapture = now;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error capturing performance metrics");
        }
    }
    
    private void CheckPerformanceThresholds(OperationMetrics metrics)
    {
        var alerts = new List<string>();
        
        // Check operation-specific thresholds
        switch (metrics.OperationName.ToLowerInvariant())
        {
            case "inventory_scan":
            case "inventoryscan":
                if (metrics.Duration.TotalMilliseconds > InventoryScanTargetMs)
                {
                    alerts.Add($"Inventory scan took {metrics.Duration.TotalMilliseconds:F1}ms (target: {InventoryScanTargetMs}ms)");
                }
                break;
                
            case "full_rescan":
            case "fullrescan":
                var threshold = metrics.AdditionalMetrics.ContainsKey("include_retainers") &&
                               (bool)metrics.AdditionalMetrics["include_retainers"]
                    ? FullRescanWithRetainerMaxMs
                    : FullRescanMaxMs;
                    
                if (metrics.Duration.TotalMilliseconds > threshold)
                {
                    alerts.Add($"Full rescan took {metrics.Duration.TotalMilliseconds:F1}ms (target: {threshold}ms)");
                }
                break;
        }
        
        // Raise alerts if any thresholds were exceeded
        foreach (var alert in alerts)
        {
            RaisePerformanceAlert(PerformanceAlertType.OperationThresholdExceeded, alert, metrics);
        }
    }
    
    private void CheckSystemThresholds(PerformanceMetric metric)
    {
        var alerts = new List<string>();
        
        // Check memory usage
        if (metric.MemoryUsageBytes > MaxMemoryUsageBytes)
        {
            alerts.Add($"Memory usage is {metric.MemoryUsageBytes / (1024.0 * 1024.0):F1}MB (limit: {MaxMemoryUsageBytes / (1024.0 * 1024.0):F1}MB)");
        }
        
        // Check CPU usage (only if we have a valid reading)
        if (metric.CpuUsagePercent >= 0 && metric.CpuUsagePercent > MaxIdleCpuPercent)
        {
            alerts.Add($"CPU usage is {metric.CpuUsagePercent:F1}% (limit: {MaxIdleCpuPercent:F1}% when idle)");
        }
        
        // Raise alerts if any thresholds were exceeded
        foreach (var alert in alerts)
        {
            RaisePerformanceAlert(PerformanceAlertType.SystemThresholdExceeded, alert, metric);
        }
    }
    
    private void RaisePerformanceAlert(PerformanceAlertType alertType, string message, object? context = null)
    {
        var alert = new PerformanceAlertEventArgs
        {
            AlertType = alertType,
            Message = message,
            Timestamp = DateTime.UtcNow,
            Context = context
        };
        
        try
        {
            PerformanceAlert?.Invoke(this, alert);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in PerformanceAlert event handler");
        }
        
        _logger.Warning($"Performance Alert: {message}");
    }
    
    private Dictionary<string, bool> CalculateThresholdCompliance()
    {
        var compliance = new Dictionary<string, bool>();
        
        // Check memory compliance
        compliance["MemoryUsage"] = CurrentStats.LastMemoryUsageBytes <= MaxMemoryUsageBytes;
        
        // Check CPU compliance
        if (CurrentStats.LastCpuUsagePercent >= 0)
        {
            compliance["CpuUsage"] = CurrentStats.LastCpuUsagePercent <= MaxIdleCpuPercent;
        }
        
        // Check operation compliance
        var inventoryScanStats = GetOperationStats("inventory_scan");
        if (inventoryScanStats != null)
        {
            compliance["InventoryScanTime"] = inventoryScanStats.AverageDurationMs <= InventoryScanTargetMs;
        }
        
        var fullRescanStats = GetOperationStats("full_rescan");
        if (fullRescanStats != null)
        {
            compliance["FullRescanTime"] = fullRescanStats.AverageDurationMs <= FullRescanMaxMs;
        }
        
        return compliance;
    }
    
    private List<PerformanceAlertEventArgs> GetRecentAlerts()
    {
        // This would require storing alerts in memory, which we're not doing yet
        // For now, return empty list
        return new List<PerformanceAlertEventArgs>();
    }
    
    private static double GetMedian(List<double> values)
    {
        if (!values.Any()) return 0;
        
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
    
    private static double GetPercentile(List<double> values, double percentile)
    {
        if (!values.Any()) return 0;
        
        var sorted = values.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        index = Math.Max(0, Math.Min(sorted.Count - 1, index));
        
        return sorted[index];
    }
    
    internal void CompleteOperation(OperationTracker tracker)
    {
        _operationTrackers.TryRemove(tracker.Id, out _);
        
        if (tracker.IsCompleted)
        {
            RecordOperation(tracker.OperationName, tracker.Duration, tracker.AdditionalMetrics);
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        StopMonitoring();
        _performanceTimer?.Dispose();
        _cpuCounter?.Dispose();
        _currentProcess?.Dispose();
        
        _metricsHistory.Clear();
        _operationTrackers.Clear();
        
        _logger.Debug("PerformanceMonitor disposed");
    }
}

/// <summary>
/// Interface for tracking individual operations.
/// </summary>
public interface IOperationTracker : IDisposable
{
    string OperationName { get; }
    DateTime StartTime { get; }
    bool IsCompleted { get; }
    TimeSpan Duration { get; }
    Dictionary<string, object> AdditionalMetrics { get; }
    
    void AddMetric(string key, object value);
    void Complete();
}

/// <summary>
/// Tracks individual operation performance.
/// </summary>
internal sealed class OperationTracker : IOperationTracker
{
    private readonly PerformanceMonitor _monitor;
    private readonly Stopwatch _stopwatch;
    private bool _disposed = false;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string OperationName { get; }
    public DateTime StartTime { get; private set; }
    public bool IsCompleted { get; private set; }
    public TimeSpan Duration => _stopwatch.Elapsed;
    public Dictionary<string, object> AdditionalMetrics { get; } = new();
    
    public OperationTracker(string operationName, PerformanceMonitor monitor)
    {
        OperationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _stopwatch = new Stopwatch();
    }
    
    public void Start()
    {
        StartTime = DateTime.UtcNow;
        _stopwatch.Start();
    }
    
    public void AddMetric(string key, object value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
        AdditionalMetrics[key] = value;
    }
    
    public void Complete()
    {
        if (IsCompleted)
            return;
        
        _stopwatch.Stop();
        IsCompleted = true;
        
        _monitor.CompleteOperation(this);
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        if (!IsCompleted)
        {
            Complete();
        }
        
        _stopwatch?.Reset();
    }
}

/// <summary>
/// System performance metric snapshot.
/// </summary>
public sealed class PerformanceMetric
{
    public DateTime Timestamp { get; set; }
    public long MemoryUsageBytes { get; set; }
    public double CpuUsagePercent { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
}

/// <summary>
/// Operation performance metrics.
/// </summary>
public sealed class OperationMetrics
{
    public string OperationName { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object> AdditionalMetrics { get; set; } = new();
}

/// <summary>
/// Aggregated operation statistics.
/// </summary>
public sealed class OperationStats
{
    public string OperationName { get; set; } = string.Empty;
    public int TotalOperations { get; set; }
    public double AverageDurationMs { get; set; }
    public double MinDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public double MedianDurationMs { get; set; }
    public double P95DurationMs { get; set; }
    public double P99DurationMs { get; set; }
    public DateTime LastOperationTime { get; set; }
    public List<OperationMetrics> RecentOperations { get; set; } = new();
}

/// <summary>
/// Current system performance statistics.
/// </summary>
public sealed class PerformanceStats
{
    public double LastCpuUsagePercent { get; set; }
    public long LastMemoryUsageBytes { get; set; }
    public double AverageCpuUsagePercent { get; set; }
    public long AverageMemoryUsageBytes { get; set; }
    public DateTime LastMetricsUpdate { get; set; }
    public Dictionary<string, List<OperationMetrics>> OperationMetrics { get; set; } = new();
    
    public PerformanceStats Clone()
    {
        return new PerformanceStats
        {
            LastCpuUsagePercent = LastCpuUsagePercent,
            LastMemoryUsageBytes = LastMemoryUsageBytes,
            AverageCpuUsagePercent = AverageCpuUsagePercent,
            AverageMemoryUsageBytes = AverageMemoryUsageBytes,
            LastMetricsUpdate = LastMetricsUpdate,
            OperationMetrics = new Dictionary<string, List<OperationMetrics>>(OperationMetrics.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<OperationMetrics>(kvp.Value)
            ))
        };
    }
}

/// <summary>
/// Comprehensive performance report.
/// </summary>
public sealed class PerformanceReport
{
    public DateTime GeneratedAt { get; set; }
    public TimeSpan MonitoringDuration { get; set; }
    public PerformanceStats SystemStats { get; set; } = new();
    public List<PerformanceAlertEventArgs> Alerts { get; set; } = new();
    public List<OperationStats> OperationStats { get; set; } = new();
    public Dictionary<string, bool> ThresholdCompliance { get; set; } = new();
}

/// <summary>
/// Performance alert event arguments.
/// </summary>
public sealed class PerformanceAlertEventArgs : EventArgs
{
    public PerformanceAlertType AlertType { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public object? Context { get; set; }
}

/// <summary>
/// Types of performance alerts.
/// </summary>
public enum PerformanceAlertType
{
    OperationThresholdExceeded,
    SystemThresholdExceeded,
    MemoryLeak,
    PerformanceRegression
}