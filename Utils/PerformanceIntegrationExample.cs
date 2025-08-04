using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ReadyCrafter.Models;
using ReadyCrafter.Services;

namespace ReadyCrafter.Utils;

/// <summary>
/// Example integration showing how to use all performance monitoring utilities together.
/// This demonstrates the complete performance monitoring pipeline for ReadyCrafter.
/// </summary>
public sealed class PerformanceIntegrationExample : IDisposable
{
    private readonly IPluginLog _logger;
    private readonly PerformanceMonitor _performanceMonitor;
    private readonly MemoryProfiler _memoryProfiler;
    private readonly BenchmarkHelper _benchmarkHelper;
    private readonly InventoryService _inventoryService;
    
    private bool _disposed = false;

    public PerformanceIntegrationExample(IPluginLog logger, InventoryService inventoryService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        
        // Initialize performance monitoring utilities
        _performanceMonitor = new PerformanceMonitor(_logger);
        _memoryProfiler = new MemoryProfiler(_logger);
        _benchmarkHelper = new BenchmarkHelper(_logger);
        
        // Set up event handlers
        SetupPerformanceAlerts();
        
        _logger.Information("Performance monitoring integration example initialized");
    }
    
    /// <summary>
    /// Start comprehensive performance monitoring for ReadyCrafter.
    /// </summary>
    public void StartMonitoring()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceIntegrationExample));
        
        _logger.Information("Starting comprehensive performance monitoring");
        
        // Start all monitoring systems
        _performanceMonitor.StartMonitoring();
        _memoryProfiler.StartProfiling(2000); // 2-second intervals
        
        _logger.Information("Performance monitoring started successfully");
    }
    
    /// <summary>
    /// Stop all performance monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        _performanceMonitor?.StopMonitoring();
        _memoryProfiler?.StopProfiling();
        
        _logger.Information("Performance monitoring stopped");
    }
    
    /// <summary>
    /// Example of monitoring an inventory scan operation.
    /// Demonstrates integration with all monitoring utilities.
    /// </summary>
    public async Task<InventorySnapshot> MonitoredInventoryScan(ScanOptions scanOptions)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceIntegrationExample));
        
        // Create operation tracker for detailed performance monitoring
        using var operationTracker = _performanceMonitor.StartOperation("inventory_scan");
        
        // Create memory allocation tracker for this specific operation
        using var memoryTracker = _memoryProfiler.CreateAllocationTracker("inventory_scan");
        
        // Create benchmark scope for automatic timing
        using var benchmarkScope = _benchmarkHelper.CreateBenchmarkScope("inventory_scan");
        
        try
        {
            // Record memory allocation for the operation setup
            memoryTracker.RecordAllocation(1024); // Estimate for operation setup
            
            // Add operation metadata
            operationTracker.AddMetric("container_count", scanOptions.AllContainerIds.Count());
            operationTracker.AddMetric("include_retainers", scanOptions.IncludeRetainers);
            operationTracker.AddMetric("parallel_processing", scanOptions.EnableParallelProcessing);
            
            // Perform the actual inventory scan
            var snapshot = await _inventoryService.ScanAsync(scanOptions);
            
            // Record additional metrics
            operationTracker.AddMetric("items_found", snapshot.Items.Count);
            operationTracker.AddMetric("total_stacks", snapshot.TotalStacks);
            operationTracker.AddMetric("scan_time_ms", snapshot.ScanTimeMs);
            
            // Check if performance meets PRD requirements
            var targetTime = scanOptions.IncludeRetainers 
                ? PerformanceMonitor.FullRescanWithRetainerMaxMs 
                : PerformanceMonitor.InventoryScanTargetMs;
                
            if (snapshot.ScanTimeMs > targetTime)
            {
                _logger.Warning($"Inventory scan took {snapshot.ScanTimeMs:F2}ms, exceeds PRD target of {targetTime}ms");
            }
            
            // Complete tracking
            operationTracker.Complete();
            benchmarkScope.Complete();
            
            
            return snapshot;
        }
        catch (Exception ex)
        {
            // Record error metrics
            operationTracker.AddMetric("error", ex.Message);
            _logger.Error(ex, "Error during monitored inventory scan");
            throw;
        }
        finally
        {
            // Always clean up memory tracking
            memoryTracker.RecordDeallocation(1024);
        }
    }
    
    /// <summary>
    /// Run comprehensive performance benchmarks for all major operations.
    /// This validates all PRD performance requirements.
    /// </summary>
    public async Task<ComprehensiveBenchmarkResults> RunComprehensivePerformanceBenchmarks()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceIntegrationExample));
        
        _logger.Information("Starting comprehensive performance benchmarks");
        
        // Define benchmark operations that mirror actual ReadyCrafter usage
        var benchmarkOperations = new Dictionary<string, Func<Task>>
        {
            // PRD Requirement: Inventory scan ≤150ms
            ["inventory_scan"] = async () =>
            {
                var options = ScanOptions.CreatePerformanceOptimized();
                await _inventoryService.ScanAsync(options);
            },
            
            // PRD Requirement: Full rescan ≤200ms (without retainers)
            ["full_rescan"] = async () =>
            {
                var options = ScanOptions.CreateDefault();
                options.IncludeRetainers = false;
                await _inventoryService.ScanAsync(options);
            },
            
            // PRD Requirement: Full rescan ≤300ms (with retainers)
            ["full_rescan_with_retainers"] = async () =>
            {
                var options = ScanOptions.CreateDefault();
                options.IncludeRetainers = true;
                await _inventoryService.ScanAsync(options);
            },
            
            // Cache performance test
            ["cached_scan"] = async () =>
            {
                var options = ScanOptions.CreatePerformanceOptimized();
                // This should hit cache after first scan
                await _inventoryService.ScanAsync(options);
            },
            
            // Recipe processing simulation
            ["recipe_processing"] = async () =>
            {
                // Simulate recipe processing work
                await Task.Delay(Random.Shared.Next(20, 50));
                
                // Simulate some CPU work
                var result = 0;
                for (int i = 0; i < 10000; i++)
                {
                    result += i % 100;
                }
            }
        };
        
        // Run benchmark suite
        var results = await _benchmarkHelper.RunPrdBenchmarkSuite(benchmarkOperations);
        
        // Log results summary
        _logger.Information($"Benchmark suite completed: {results.Summary.SuccessfulBenchmarks}/{results.Summary.TotalBenchmarks} successful");
        
        foreach (var kvp in results.BenchmarkResults)
        {
            var benchmark = kvp.Value;
            var isCompliant = results.ThresholdCompliance.GetValueOrDefault(kvp.Key, false);
            
            _logger.Information($"  {kvp.Key}: {benchmark.AverageMs:F2}ms avg " +
                               $"(min: {benchmark.MinMs:F2}ms, max: {benchmark.MaxMs:F2}ms) " +
                               $"- {(isCompliant ? "PASS" : "FAIL")}");
        }
        
        return results;
    }
    
    /// <summary>
    /// Generate a comprehensive performance report including all monitoring data.
    /// </summary>
    public async Task<ComprehensivePerformanceReport> GeneratePerformanceReport()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceIntegrationExample));
        
        _logger.Information("Generating comprehensive performance report");
        
        // Collect data from all monitoring systems
        var performanceReport = _performanceMonitor.GenerateReport();
        var memoryReport = _memoryProfiler.GenerateOptimizationReport();
        var complianceReport = _benchmarkHelper.GenerateComplianceReport();
        
        // Run fresh benchmarks if needed
        ComprehensiveBenchmarkResults? benchmarkResults = null;
        try
        {
            benchmarkResults = await RunComprehensivePerformanceBenchmarks();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to run fresh benchmarks for report");
        }
        
        var report = new ComprehensivePerformanceReport
        {
            GeneratedAt = DateTime.UtcNow,
            PerformanceMonitoringReport = performanceReport,
            MemoryOptimizationReport = memoryReport,
            ComplianceReport = complianceReport,
            BenchmarkResults = benchmarkResults,
            SystemMetrics = CollectSystemMetrics(),
            PrdComplianceSummary = GeneratePrdComplianceSummary(complianceReport, memoryReport),
            Recommendations = GenerateOptimizationRecommendations(performanceReport, memoryReport)
        };
        
        _logger.Information("Performance report generated successfully");
        
        return report;
    }
    
    /// <summary>
    /// Check if current performance meets all PRD requirements.
    /// </summary>
    public bool ValidatePrdCompliance()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceIntegrationExample));
        
        var issues = new List<string>();
        
        // Check memory usage
        var currentMemoryMB = _memoryProfiler.CurrentMemoryUsage / (1024.0 * 1024.0);
        var memoryLimitMB = MemoryProfiler.TargetMemoryLimitBytes / (1024.0 * 1024.0);
        
        if (currentMemoryMB > memoryLimitMB)
        {
            issues.Add($"Memory usage {currentMemoryMB:F1}MB exceeds PRD limit of {memoryLimitMB:F1}MB");
        }
        
        // Check CPU usage
        var currentCpu = _performanceMonitor.GetCurrentCpuUsage();
        if (currentCpu > PerformanceMonitor.MaxIdleCpuPercent && currentCpu >= 0)
        {
            issues.Add($"CPU usage {currentCpu:F1}% exceeds PRD idle limit of {PerformanceMonitor.MaxIdleCpuPercent:F1}%");
        }
        
        // Check performance compliance
        var isPerformanceCompliant = _performanceMonitor.IsPerformanceCompliant();
        if (!isPerformanceCompliant)
        {
            issues.Add("Operation performance does not meet PRD timing requirements");
        }
        
        // Check for memory leaks
        var memoryAnalysis = _memoryProfiler.AnalyzeMemoryUsage();
        if (memoryAnalysis.PotentialLeakDetected)
        {
            issues.Add($"Potential memory leak detected: {memoryAnalysis.EstimatedLeakRateMBPerMinute:F2}MB/min growth");
        }
        
        // Log results
        if (issues.Any())
        {
            _logger.Warning($"PRD compliance validation failed with {issues.Count} issues:");
            foreach (var issue in issues)
            {
                _logger.Warning($"  - {issue}");
            }
            return false;
        }
        else
        {
            _logger.Information("PRD compliance validation passed - all requirements met");
            return true;
        }
    }
    
    private void SetupPerformanceAlerts()
    {
        // Set up performance alerts
        _performanceMonitor.PerformanceAlert += (sender, args) =>
        {
            _logger.Warning($"Performance Alert [{args.AlertType}]: {args.Message}");
        };
        
        // Set up memory alerts
        _memoryProfiler.MemoryAlert += (sender, args) =>
        {
            _logger.Warning($"Memory Alert [{args.AlertType}]: {args.Message}");
        };
        
        _memoryProfiler.MemoryLeakDetected += (sender, args) =>
        {
            _logger.Error($"Memory Leak Detected: {args.Message}");
        };
        
        // Set up benchmark alerts
        _benchmarkHelper.PerformanceRegression += (sender, args) =>
        {
            _logger.Warning($"Performance Regression in {args.BenchmarkName}: " +
                           $"{args.RegressionPercentage:+F1}% slower than baseline");
        };
    }
    
    private SystemMetrics CollectSystemMetrics()
    {
        return new SystemMetrics
        {
            Timestamp = DateTime.UtcNow,
            MemoryUsageMB = _memoryProfiler.CurrentMemoryUsage / (1024.0 * 1024.0),
            CpuUsagePercent = _performanceMonitor.GetCurrentCpuUsage(),
            TotalOperations = _performanceMonitor.CurrentStats.OperationMetrics.Values
                .Sum(list => list.Count),
            UpTime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime
        };
    }
    
    private PrdComplianceSummary GeneratePrdComplianceSummary(PerformanceComplianceReport complianceReport, MemoryOptimizationReport memoryReport)
    {
        var summary = new PrdComplianceSummary
        {
            OverallCompliant = true,
            ComplianceDetails = new Dictionary<string, bool>()
        };
        
        // Memory compliance
        var memoryCompliant = memoryReport.CurrentAnalysis.CurrentUsageBytes <= MemoryProfiler.TargetMemoryLimitBytes;
        summary.ComplianceDetails["Memory Usage (≤75MB)"] = memoryCompliant;
        
        // Performance compliance
        foreach (var kvp in complianceReport.PrdCompliance)
        {
            summary.ComplianceDetails[kvp.Value.Threshold.Description] = kvp.Value.IsCompliant;
        }
        
        // Memory leak compliance
        summary.ComplianceDetails["No Memory Leaks"] = !memoryReport.CurrentAnalysis.PotentialLeakDetected;
        
        // Overall compliance
        summary.OverallCompliant = summary.ComplianceDetails.Values.All(compliant => compliant);
        
        return summary;
    }
    
    private List<string> GenerateOptimizationRecommendations(PerformanceReport performanceReport, MemoryOptimizationReport memoryReport)
    {
        var recommendations = new List<string>();
        
        // Add performance recommendations
        if (performanceReport.OperationStats.Any(op => op.AverageDurationMs > 100))
        {
            recommendations.Add("Consider optimizing slow operations or implementing caching");
        }
        
        // Add memory recommendations
        recommendations.AddRange(memoryReport.OptimizationRecommendations);
        
        // Add benchmark-specific recommendations
        var complianceReport = _benchmarkHelper.GenerateComplianceReport();
        var nonCompliantBenchmarks = complianceReport.PrdCompliance
            .Where(kvp => !kvp.Value.IsCompliant)
            .Select(kvp => kvp.Key);
        
        foreach (var benchmark in nonCompliantBenchmarks)
        {
            recommendations.Add($"Optimize {benchmark} performance to meet PRD requirements");
        }
        
        return recommendations;
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        StopMonitoring();
        
        _performanceMonitor?.Dispose();
        _memoryProfiler?.Dispose();
        _benchmarkHelper?.Dispose();
        
    }
}

/// <summary>
/// Comprehensive performance report combining all monitoring systems.
/// </summary>
public sealed class ComprehensivePerformanceReport
{
    public DateTime GeneratedAt { get; set; }
    public PerformanceReport PerformanceMonitoringReport { get; set; } = new();
    public MemoryOptimizationReport MemoryOptimizationReport { get; set; } = new();
    public PerformanceComplianceReport ComplianceReport { get; set; } = new();
    public ComprehensiveBenchmarkResults? BenchmarkResults { get; set; }
    public SystemMetrics SystemMetrics { get; set; } = new();
    public PrdComplianceSummary PrdComplianceSummary { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// System metrics snapshot.
/// </summary>
public sealed class SystemMetrics
{
    public DateTime Timestamp { get; set; }
    public double MemoryUsageMB { get; set; }
    public double CpuUsagePercent { get; set; }
    public int TotalOperations { get; set; }
    public TimeSpan UpTime { get; set; }
}

/// <summary>
/// PRD compliance summary.
/// </summary>
public sealed class PrdComplianceSummary
{
    public bool OverallCompliant { get; set; }
    public Dictionary<string, bool> ComplianceDetails { get; set; } = new();
}