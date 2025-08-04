using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace ReadyCrafter.Utils;

/// <summary>
/// Performance benchmarking utilities for ReadyCrafter.
/// Provides automated benchmarking, baseline management, and regression detection
/// against all PRD performance requirements.
/// </summary>
public sealed class BenchmarkHelper : IDisposable
{
    private readonly IPluginLog _logger;
    private readonly string _benchmarkDataPath;
    private readonly object _lockObject = new();
    private readonly ConcurrentDictionary<string, BenchmarkBaseline> _baselines = new();
    private readonly ConcurrentQueue<BenchmarkResult> _recentResults = new();
    
    private bool _disposed = false;
    private DateTime _lastBaselineUpdate = DateTime.UtcNow;
    
    // PRD Performance Requirements
    private static readonly Dictionary<string, BenchmarkThreshold> _prdThresholds = new()
    {
        ["inventory_scan"] = new BenchmarkThreshold
        {
            TargetMs = 150.0,
            WarningMs = 120.0,
            CriticalMs = 150.0,
            Description = "Individual inventory scan (PRD target: ≤150ms)"
        },
        ["full_rescan"] = new BenchmarkThreshold
        {
            TargetMs = 200.0,
            WarningMs = 160.0,
            CriticalMs = 200.0,
            Description = "Full rescan without retainers (PRD target: ≤200ms)"
        },
        ["full_rescan_with_retainers"] = new BenchmarkThreshold
        {
            TargetMs = 300.0,
            WarningMs = 240.0,
            CriticalMs = 300.0,
            Description = "Full rescan with retainers (PRD target: ≤300ms)"
        },
        ["recipe_cache_load"] = new BenchmarkThreshold
        {
            TargetMs = 1000.0,
            WarningMs = 800.0,
            CriticalMs = 1000.0,
            Description = "Recipe cache loading on startup"
        },
        ["craft_calculation"] = new BenchmarkThreshold
        {
            TargetMs = 50.0,
            WarningMs = 30.0,
            CriticalMs = 50.0,
            Description = "Craft quantity calculation per recipe"
        },
        ["ui_refresh"] = new BenchmarkThreshold
        {
            TargetMs = 16.67, // 60 FPS
            WarningMs = 12.0,
            CriticalMs = 16.67,
            Description = "UI refresh to maintain 60 FPS"
        }
    };
    
    /// <summary>
    /// Event raised when a benchmark completes.
    /// </summary>
    public event EventHandler<BenchmarkCompletedEventArgs>? BenchmarkCompleted;
    
    /// <summary>
    /// Event raised when performance regression is detected.
    /// </summary>
    public event EventHandler<PerformanceRegressionEventArgs>? PerformanceRegression;
    
    /// <summary>
    /// Available benchmark categories and their thresholds.
    /// </summary>
    public IReadOnlyDictionary<string, BenchmarkThreshold> PrdThresholds => _prdThresholds;

    public BenchmarkHelper(IPluginLog logger, string? dataDirectory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Set up benchmark data storage path
        var pluginDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "ReadyCrafter");
        _benchmarkDataPath = Path.Combine(pluginDirectory, "benchmarks.json");
        
        // Create directory if it doesn't exist
        var directory = Path.GetDirectoryName(_benchmarkDataPath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // Load existing baselines
        LoadBaselines();
        
        _logger.Debug($"BenchmarkHelper initialized with data path: {_benchmarkDataPath}");
    }
    
    /// <summary>
    /// Run a benchmark for a specific operation.
    /// </summary>
    /// <param name="benchmarkName">Name of the benchmark</param>
    /// <param name="operation">Operation to benchmark</param>
    /// <param name="iterations">Number of iterations to run</param>
    /// <param name="warmupIterations">Number of warmup iterations</param>
    /// <returns>Benchmark result</returns>
    public BenchmarkResult RunBenchmark<T>(string benchmarkName, Func<T> operation, int iterations = 10, int warmupIterations = 3)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BenchmarkHelper));
        
        if (string.IsNullOrEmpty(benchmarkName))
            throw new ArgumentException("Benchmark name cannot be null or empty", nameof(benchmarkName));
        
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));
        
        if (iterations <= 0)
            throw new ArgumentException("Iterations must be positive", nameof(iterations));
        
        return RunBenchmarkInternal(benchmarkName, () => { operation(); return true; }, iterations, warmupIterations);
    }
    
    /// <summary>
    /// Run an async benchmark for a specific operation.
    /// </summary>
    /// <param name="benchmarkName">Name of the benchmark</param>
    /// <param name="operation">Async operation to benchmark</param>
    /// <param name="iterations">Number of iterations to run</param>
    /// <param name="warmupIterations">Number of warmup iterations</param>
    /// <returns>Benchmark result</returns>
    public async Task<BenchmarkResult> RunBenchmarkAsync<T>(string benchmarkName, Func<Task<T>> operation, int iterations = 10, int warmupIterations = 3)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BenchmarkHelper));
        
        if (string.IsNullOrEmpty(benchmarkName))
            throw new ArgumentException("Benchmark name cannot be null or empty", nameof(benchmarkName));
        
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));
        
        if (iterations <= 0)
            throw new ArgumentException("Iterations must be positive", nameof(iterations));
        
        return await RunBenchmarkInternalAsync(benchmarkName, async () => { await operation(); return true; }, iterations, warmupIterations);
    }
    
    /// <summary>
    /// Create a benchmark scope for automatic timing.
    /// </summary>
    /// <param name="benchmarkName">Name of the benchmark</param>
    /// <param name="autoComplete">Whether to automatically complete and record the benchmark on disposal</param>
    /// <returns>Benchmark scope</returns>
    public IBenchmarkScope CreateBenchmarkScope(string benchmarkName, bool autoComplete = true)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BenchmarkHelper));
        
        if (string.IsNullOrEmpty(benchmarkName))
            throw new ArgumentException("Benchmark name cannot be null or empty", nameof(benchmarkName));
        
        return new BenchmarkScope(benchmarkName, this, autoComplete);
    }
    
    /// <summary>
    /// Run all PRD-defined benchmarks against actual operations.
    /// </summary>
    /// <param name="operations">Dictionary of operations to benchmark</param>
    /// <returns>Comprehensive benchmark results</returns>
    public async Task<ComprehensiveBenchmarkResults> RunPrdBenchmarkSuite(Dictionary<string, Func<Task>> operations)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BenchmarkHelper));
        
        var results = new ComprehensiveBenchmarkResults
        {
            StartTime = DateTime.UtcNow,
            BenchmarkResults = new Dictionary<string, BenchmarkResult>(),
            ThresholdCompliance = new Dictionary<string, bool>(),
            Summary = new BenchmarkSummary()
        };
        
        _logger.Information("Starting PRD benchmark suite");
        
        // Run each available benchmark
        foreach (var kvp in operations)
        {
            var benchmarkName = kvp.Key;
            var operation = kvp.Value;
            
            try
            {
                _logger.Debug($"Running benchmark: {benchmarkName}");
                
                var result = await RunBenchmarkInternalAsync(
                    benchmarkName,
                    async () => { await operation(); return true; },
                    iterations: 10,
                    warmupIterations: 3
                );
                
                results.BenchmarkResults[benchmarkName] = result;
                
                // Check compliance with PRD thresholds
                if (_prdThresholds.TryGetValue(benchmarkName, out var threshold))
                {
                    results.ThresholdCompliance[benchmarkName] = result.AverageMs <= threshold.TargetMs;
                }
                
                _logger.Information($"Benchmark {benchmarkName} completed: {result.AverageMs:F2}ms average");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to run benchmark {benchmarkName}");
                
                // Create failed result
                results.BenchmarkResults[benchmarkName] = new BenchmarkResult
                {
                    BenchmarkName = benchmarkName,
                    Timestamp = DateTime.UtcNow,
                    Iterations = 0,
                    Success = false,
                    ErrorMessage = ex.Message
                };
                
                results.ThresholdCompliance[benchmarkName] = false;
            }
        }
        
        results.EndTime = DateTime.UtcNow;
        results.TotalDuration = results.EndTime - results.StartTime;
        
        // Generate summary
        results.Summary = GenerateBenchmarkSummary(results.BenchmarkResults.Values);
        
        _logger.Information($"PRD benchmark suite completed in {results.TotalDuration.TotalSeconds:F2}s");
        
        return results;
    }
    
    /// <summary>
    /// Update baseline for a specific benchmark.
    /// </summary>
    /// <param name="benchmarkName">Name of the benchmark</param>
    /// <param name="result">Benchmark result to use as baseline</param>
    public void UpdateBaseline(string benchmarkName, BenchmarkResult result)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BenchmarkHelper));
        
        if (string.IsNullOrEmpty(benchmarkName))
            throw new ArgumentException("Benchmark name cannot be null or empty", nameof(benchmarkName));
        
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        
        lock (_lockObject)
        {
            _baselines.AddOrUpdate(benchmarkName, 
                new BenchmarkBaseline
                {
                    BenchmarkName = benchmarkName,
                    BaselineResult = result,
                    EstablishedAt = DateTime.UtcNow,
                    SampleCount = 1
                },
                (key, existing) =>
                {
                    existing.BaselineResult = result;
                    existing.EstablishedAt = DateTime.UtcNow;
                    existing.SampleCount++;
                    return existing;
                });
        }
        
        SaveBaselines();
        _logger.Information($"Updated baseline for {benchmarkName}: {result.AverageMs:F2}ms");
    }
    
    /// <summary>
    /// Get baseline for a specific benchmark.
    /// </summary>
    /// <param name="benchmarkName">Name of the benchmark</param>
    /// <returns>Baseline result or null if no baseline exists</returns>
    public BenchmarkBaseline? GetBaseline(string benchmarkName)
    {
        if (string.IsNullOrEmpty(benchmarkName))
            return null;
        
        return _baselines.TryGetValue(benchmarkName, out var baseline) ? baseline : null;
    }
    
    /// <summary>
    /// Check if a benchmark result represents a performance regression.
    /// </summary>
    /// <param name="result">Benchmark result to check</param>
    /// <param name="regressionThresholdPercent">Regression threshold as percentage (default: 20%)</param>
    /// <returns>True if regression detected</returns>
    public bool IsPerformanceRegression(BenchmarkResult result, double regressionThresholdPercent = 20.0)
    {
        if (result == null || !result.Success)
            return false;
        
        var baseline = GetBaseline(result.BenchmarkName);
        if (baseline?.BaselineResult == null)
            return false;
        
        var regressionThreshold = baseline.BaselineResult.AverageMs * (1.0 + regressionThresholdPercent / 100.0);
        return result.AverageMs > regressionThreshold;
    }
    
    /// <summary>
    /// Generate performance report comparing current results to baselines and PRD thresholds.
    /// </summary>
    /// <returns>Performance report</returns>
    public PerformanceComplianceReport GenerateComplianceReport()
    {
        var report = new PerformanceComplianceReport
        {
            GeneratedAt = DateTime.UtcNow,
            PrdCompliance = new Dictionary<string, PrdComplianceStatus>(),
            BaselineComparisons = new Dictionary<string, BaselineComparison>(),
            OverallCompliance = true
        };
        
        // Check recent results against PRD thresholds
        var recentResults = _recentResults.TakeLast(50).GroupBy(r => r.BenchmarkName).ToList();
        
        foreach (var group in recentResults)
        {
            var benchmarkName = group.Key;
            var latestResult = group.OrderByDescending(r => r.Timestamp).First();
            
            // PRD compliance check
            if (_prdThresholds.TryGetValue(benchmarkName, out var threshold))
            {
                var compliance = new PrdComplianceStatus
                {
                    BenchmarkName = benchmarkName,
                    Threshold = threshold,
                    LatestResult = latestResult,
                    IsCompliant = latestResult.AverageMs <= threshold.TargetMs,
                    CompliancePercentage = Math.Min(100.0, (threshold.TargetMs / latestResult.AverageMs) * 100.0)
                };
                
                report.PrdCompliance[benchmarkName] = compliance;
                
                if (!compliance.IsCompliant)
                {
                    report.OverallCompliance = false;
                }
            }
            
            // Baseline comparison
            var baseline = GetBaseline(benchmarkName);
            if (baseline != null)
            {
                var comparison = new BaselineComparison
                {
                    BenchmarkName = benchmarkName,
                    BaselineResult = baseline.BaselineResult,
                    LatestResult = latestResult,
                    PerformanceChange = ((latestResult.AverageMs - baseline.BaselineResult.AverageMs) / baseline.BaselineResult.AverageMs) * 100.0,
                    IsRegression = IsPerformanceRegression(latestResult)
                };
                
                report.BaselineComparisons[benchmarkName] = comparison;
            }
        }
        
        return report;
    }
    
    /// <summary>
    /// Clear all benchmark data and baselines.
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _baselines.Clear();
            _recentResults.Clear();
        }
        
        // Delete baseline file
        try
        {
            if (File.Exists(_benchmarkDataPath))
            {
                File.Delete(_benchmarkDataPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete benchmark data file");
        }
        
        _logger.Information("Benchmark helper reset");
    }
    
    private BenchmarkResult RunBenchmarkInternal(string benchmarkName, Func<bool> operation, int iterations, int warmupIterations)
    {
        var result = new BenchmarkResult
        {
            BenchmarkName = benchmarkName,
            Timestamp = DateTime.UtcNow,
            Iterations = iterations,
            WarmupIterations = warmupIterations
        };
        
        var measurements = new List<double>();
        var stopwatch = new Stopwatch();
        
        try
        {
            // Warmup iterations
            for (int i = 0; i < warmupIterations; i++)
            {
                operation();
            }
            
            // Measurement iterations
            for (int i = 0; i < iterations; i++)
            {
                GC.Collect(); // Ensure consistent GC state
                GC.WaitForPendingFinalizers();
                
                stopwatch.Restart();
                operation();
                stopwatch.Stop();
                
                measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            
            // Calculate statistics
            result.MinMs = measurements.Min();
            result.MaxMs = measurements.Max();
            result.AverageMs = measurements.Average();
            result.MedianMs = CalculateMedian(measurements);
            result.StandardDeviationMs = CalculateStandardDeviation(measurements, result.AverageMs);
            result.P95Ms = CalculatePercentile(measurements, 0.95);
            result.P99Ms = CalculatePercentile(measurements, 0.99);
            result.Success = true;
            
            // Record result
            RecordBenchmarkResult(result);
            
            _logger.Debug($"Benchmark {benchmarkName} completed: {result.AverageMs:F2}ms average over {iterations} iterations");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.Error(ex, $"Benchmark {benchmarkName} failed");
        }
        
        return result;
    }
    
    private async Task<BenchmarkResult> RunBenchmarkInternalAsync(string benchmarkName, Func<Task<bool>> operation, int iterations, int warmupIterations)
    {
        var result = new BenchmarkResult
        {
            BenchmarkName = benchmarkName,
            Timestamp = DateTime.UtcNow,
            Iterations = iterations,
            WarmupIterations = warmupIterations
        };
        
        var measurements = new List<double>();
        var stopwatch = new Stopwatch();
        
        try
        {
            // Warmup iterations
            for (int i = 0; i < warmupIterations; i++)
            {
                await operation();
            }
            
            // Measurement iterations
            for (int i = 0; i < iterations; i++)
            {
                GC.Collect(); // Ensure consistent GC state
                GC.WaitForPendingFinalizers();
                
                stopwatch.Restart();
                await operation();
                stopwatch.Stop();
                
                measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            
            // Calculate statistics
            result.MinMs = measurements.Min();
            result.MaxMs = measurements.Max();
            result.AverageMs = measurements.Average();
            result.MedianMs = CalculateMedian(measurements);
            result.StandardDeviationMs = CalculateStandardDeviation(measurements, result.AverageMs);
            result.P95Ms = CalculatePercentile(measurements, 0.95);
            result.P99Ms = CalculatePercentile(measurements, 0.99);
            result.Success = true;
            
            // Record result
            RecordBenchmarkResult(result);
            
            _logger.Debug($"Async benchmark {benchmarkName} completed: {result.AverageMs:F2}ms average over {iterations} iterations");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.Error(ex, $"Async benchmark {benchmarkName} failed");
        }
        
        return result;
    }
    
    private void RecordBenchmarkResult(BenchmarkResult result)
    {
        // Add to recent results
        _recentResults.Enqueue(result);
        
        // Maintain recent results size (keep last 100)
        while (_recentResults.Count > 100)
        {
            _recentResults.TryDequeue(out _);
        }
        
        // Check for regression
        if (IsPerformanceRegression(result))
        {
            RaisePerformanceRegression(result);
        }
        
        // Raise completion event
        RaiseBenchmarkCompleted(result);
    }
    
    private void RaiseBenchmarkCompleted(BenchmarkResult result)
    {
        try
        {
            BenchmarkCompleted?.Invoke(this, new BenchmarkCompletedEventArgs { Result = result });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in BenchmarkCompleted event handler");
        }
    }
    
    private void RaisePerformanceRegression(BenchmarkResult result)
    {
        var baseline = GetBaseline(result.BenchmarkName);
        if (baseline?.BaselineResult == null)
            return;
        
        var regressionPercentage = ((result.AverageMs - baseline.BaselineResult.AverageMs) / baseline.BaselineResult.AverageMs) * 100.0;
        
        try
        {
            PerformanceRegression?.Invoke(this, new PerformanceRegressionEventArgs
            {
                BenchmarkName = result.BenchmarkName,
                CurrentResult = result,
                BaselineResult = baseline.BaselineResult,
                RegressionPercentage = regressionPercentage
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in PerformanceRegression event handler");
        }
        
        _logger.Warning($"Performance regression detected in {result.BenchmarkName}: " +
                       $"{result.AverageMs:F2}ms vs baseline {baseline.BaselineResult.AverageMs:F2}ms " +
                       $"({regressionPercentage:+F1}% slower)");
    }
    
    private BenchmarkSummary GenerateBenchmarkSummary(IEnumerable<BenchmarkResult> results)
    {
        var successfulResults = results.Where(r => r.Success).ToList();
        
        return new BenchmarkSummary
        {
            TotalBenchmarks = results.Count(),
            SuccessfulBenchmarks = successfulResults.Count,
            FailedBenchmarks = results.Count() - successfulResults.Count,
            AverageExecutionTimeMs = successfulResults.Any() ? successfulResults.Average(r => r.AverageMs) : 0,
            FastestBenchmark = successfulResults.OrderBy(r => r.AverageMs).FirstOrDefault(),
            SlowestBenchmark = successfulResults.OrderByDescending(r => r.AverageMs).FirstOrDefault()
        };
    }
    
    private static double CalculateMedian(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
    
    private static double CalculateStandardDeviation(List<double> values, double mean)
    {
        var squaredDifferences = values.Select(x => Math.Pow(x - mean, 2));
        var variance = squaredDifferences.Average();
        return Math.Sqrt(variance);
    }
    
    private static double CalculatePercentile(List<double> values, double percentile)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        index = Math.Max(0, Math.Min(sorted.Count - 1, index));
        
        return sorted[index];
    }
    
    private void LoadBaselines()
    {
        try
        {
            if (!File.Exists(_benchmarkDataPath))
                return;
            
            var json = File.ReadAllText(_benchmarkDataPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, BenchmarkBaseline>>(json);
            
            if (data != null)
            {
                foreach (var kvp in data)
                {
                    _baselines.TryAdd(kvp.Key, kvp.Value);
                }
                
                _logger.Debug($"Loaded {data.Count} benchmark baselines");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load benchmark baselines");
        }
    }
    
    private void SaveBaselines()
    {
        try
        {
            var data = _baselines.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_benchmarkDataPath, json);
            
            _lastBaselineUpdate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to save benchmark baselines");
        }
    }
    
    internal void CompleteBenchmarkScope(BenchmarkScope scope)
    {
        if (scope.AutoComplete && scope.IsCompleted)
        {
            var result = new BenchmarkResult
            {
                BenchmarkName = scope.BenchmarkName,
                Timestamp = scope.StartTime,
                Iterations = 1,
                WarmupIterations = 0,
                MinMs = scope.Duration.TotalMilliseconds,
                MaxMs = scope.Duration.TotalMilliseconds,
                AverageMs = scope.Duration.TotalMilliseconds,
                MedianMs = scope.Duration.TotalMilliseconds,
                StandardDeviationMs = 0,
                P95Ms = scope.Duration.TotalMilliseconds,
                P99Ms = scope.Duration.TotalMilliseconds,
                Success = true
            };
            
            RecordBenchmarkResult(result);
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        // Save baselines before disposing
        SaveBaselines();
        
        _baselines.Clear();
        _recentResults.Clear();
        
        _logger.Debug("BenchmarkHelper disposed");
    }
}

/// <summary>
/// Interface for benchmark scopes.
/// </summary>
public interface IBenchmarkScope : IDisposable
{
    string BenchmarkName { get; }
    DateTime StartTime { get; }
    TimeSpan Duration { get; }
    bool IsCompleted { get; }
    bool AutoComplete { get; }
    
    void Complete();
}

/// <summary>
/// Automatic benchmark timing scope.
/// </summary>
internal sealed class BenchmarkScope : IBenchmarkScope
{
    private readonly BenchmarkHelper _benchmarkHelper;
    private readonly Stopwatch _stopwatch;
    private bool _disposed = false;
    
    public string BenchmarkName { get; }
    public DateTime StartTime { get; }
    public TimeSpan Duration => _stopwatch.Elapsed;
    public bool IsCompleted { get; private set; }
    public bool AutoComplete { get; }
    
    public BenchmarkScope(string benchmarkName, BenchmarkHelper benchmarkHelper, bool autoComplete)
    {
        BenchmarkName = benchmarkName ?? throw new ArgumentNullException(nameof(benchmarkName));
        _benchmarkHelper = benchmarkHelper ?? throw new ArgumentNullException(nameof(benchmarkHelper));
        AutoComplete = autoComplete;
        
        StartTime = DateTime.UtcNow;
        _stopwatch = Stopwatch.StartNew();
    }
    
    public void Complete()
    {
        if (IsCompleted)
            return;
        
        _stopwatch.Stop();
        IsCompleted = true;
        
        _benchmarkHelper.CompleteBenchmarkScope(this);
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
/// Benchmark result data.
/// </summary>
public sealed class BenchmarkResult
{
    public string BenchmarkName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int Iterations { get; set; }
    public int WarmupIterations { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double AverageMs { get; set; }
    public double MedianMs { get; set; }
    public double StandardDeviationMs { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Benchmark threshold configuration.
/// </summary>
public sealed class BenchmarkThreshold
{
    public double TargetMs { get; set; }
    public double WarningMs { get; set; }
    public double CriticalMs { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Benchmark baseline data.
/// </summary>
public sealed class BenchmarkBaseline
{
    public string BenchmarkName { get; set; } = string.Empty;
    public BenchmarkResult BaselineResult { get; set; } = new();
    public DateTime EstablishedAt { get; set; }
    public int SampleCount { get; set; }
}

/// <summary>
/// Comprehensive benchmark results.
/// </summary>
public sealed class ComprehensiveBenchmarkResults
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public Dictionary<string, BenchmarkResult> BenchmarkResults { get; set; } = new();
    public Dictionary<string, bool> ThresholdCompliance { get; set; } = new();
    public BenchmarkSummary Summary { get; set; } = new();
}

/// <summary>
/// Benchmark summary statistics.
/// </summary>
public sealed class BenchmarkSummary
{
    public int TotalBenchmarks { get; set; }
    public int SuccessfulBenchmarks { get; set; }
    public int FailedBenchmarks { get; set; }
    public double AverageExecutionTimeMs { get; set; }
    public BenchmarkResult? FastestBenchmark { get; set; }
    public BenchmarkResult? SlowestBenchmark { get; set; }
}

/// <summary>
/// Performance compliance report.
/// </summary>
public sealed class PerformanceComplianceReport
{
    public DateTime GeneratedAt { get; set; }
    public Dictionary<string, PrdComplianceStatus> PrdCompliance { get; set; } = new();
    public Dictionary<string, BaselineComparison> BaselineComparisons { get; set; } = new();
    public bool OverallCompliance { get; set; }
}

/// <summary>
/// PRD compliance status for a specific benchmark.
/// </summary>
public sealed class PrdComplianceStatus
{
    public string BenchmarkName { get; set; } = string.Empty;
    public BenchmarkThreshold Threshold { get; set; } = new();
    public BenchmarkResult LatestResult { get; set; } = new();
    public bool IsCompliant { get; set; }
    public double CompliancePercentage { get; set; }
}

/// <summary>
/// Baseline comparison data.
/// </summary>
public sealed class BaselineComparison
{
    public string BenchmarkName { get; set; } = string.Empty;
    public BenchmarkResult BaselineResult { get; set; } = new();
    public BenchmarkResult LatestResult { get; set; } = new();
    public double PerformanceChange { get; set; }
    public bool IsRegression { get; set; }
}

/// <summary>
/// Benchmark completed event arguments.
/// </summary>
public sealed class BenchmarkCompletedEventArgs : EventArgs
{
    public BenchmarkResult Result { get; set; } = new();
}

/// <summary>
/// Performance regression event arguments.
/// </summary>
public sealed class PerformanceRegressionEventArgs : EventArgs
{
    public string BenchmarkName { get; set; } = string.Empty;
    public BenchmarkResult CurrentResult { get; set; } = new();
    public BenchmarkResult BaselineResult { get; set; } = new();
    public double RegressionPercentage { get; set; }
}