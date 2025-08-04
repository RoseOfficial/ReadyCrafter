# ReadyCrafter Performance Monitoring Utilities

This directory contains comprehensive performance monitoring and optimization utilities for ReadyCrafter, designed to validate and maintain all PRD performance requirements.

## Overview

The performance monitoring system consists of four main components:

1. **PerformanceMonitor.cs** - Real-time performance tracking and alerting
2. **MemoryProfiler.cs** - Memory usage monitoring and leak detection
3. **BenchmarkHelper.cs** - Performance benchmarking and regression detection
4. **PerformanceIntegrationExample.cs** - Complete integration example

## PRD Performance Requirements

ReadyCrafter must meet these specific performance targets:

- **Inventory Scan**: ≤150ms individual scan target
- **Full Rescan**: ≤200ms without retainers, ≤300ms with retainers
- **Memory Usage**: ≤75MB total including recipe cache
- **CPU Usage**: ≤1% when idle
- **No FPS Loss**: During normal operation
- **Cache Effectiveness**: High hit rates for repeated operations

## Component Details

### PerformanceMonitor

Centralized performance tracking system that monitors all operations.

**Key Features:**
- Real-time operation timing
- Automatic threshold violation detection
- Performance alerting system
- Integration with all major services

**Usage Example:**
```csharp
using var monitor = new PerformanceMonitor(logger);
monitor.StartMonitoring();

// Track individual operations
using var tracker = monitor.StartOperation("inventory_scan");
// ... perform scan ...
tracker.Complete();

// Get performance statistics
var stats = monitor.GetOperationStats("inventory_scan");
Console.WriteLine($"Average scan time: {stats.AverageDurationMs:F2}ms");
```

**Performance Thresholds:**
- `InventoryScanTargetMs = 150.0` - Individual inventory scan target
- `FullRescanMaxMs = 200.0` - Full rescan without retainers
- `FullRescanWithRetainerMaxMs = 300.0` - Full rescan with retainers
- `MaxIdleCpuPercent = 1.0` - Maximum CPU usage when idle

### MemoryProfiler

Memory usage monitoring and leak detection system targeting the 75MB limit.

**Key Features:**
- Real-time memory usage tracking
- Memory leak detection algorithm
- Component-specific allocation tracking
- Optimization recommendations

**Usage Example:**
```csharp
using var profiler = new MemoryProfiler(logger);
profiler.StartProfiling(2000); // 2-second intervals

// Track allocations for specific components
using var tracker = profiler.CreateAllocationTracker("inventory_service");
tracker.RecordAllocation(1024 * 1024); // 1MB allocation
// ... use memory ...
tracker.RecordDeallocation(1024 * 1024); // Free memory

// Analyze memory usage
var analysis = profiler.AnalyzeMemoryUsage();
Console.WriteLine($"Memory usage: {analysis.CurrentUsageBytes / (1024.0 * 1024.0):F1}MB");
```

**Memory Thresholds:**
- `TargetMemoryLimitBytes = 75MB` - PRD requirement
- `WarningThresholdBytes = 60MB` - 80% of limit
- `CriticalThresholdBytes = 70MB` - 93% of limit
- `LeakDetectionThresholdMBPerMinute = 1.0` - Memory growth rate

### BenchmarkHelper

Performance benchmarking system with baseline management and regression detection.

**Key Features:**
- Automated benchmarking with statistical analysis
- Baseline establishment and comparison
- Performance regression detection
- PRD compliance validation

**Usage Example:**
```csharp
using var benchmarks = new BenchmarkHelper(logger);

// Run individual benchmark
var result = await benchmarks.RunBenchmarkAsync("inventory_scan", async () =>
{
    await inventoryService.ScanAsync(options);
}, iterations: 10);

// Run comprehensive PRD benchmark suite
var operations = new Dictionary<string, Func<Task>>
{
    ["inventory_scan"] = () => SimulateInventoryScan(),
    ["full_rescan"] = () => SimulateFullRescan()
};

var results = await benchmarks.RunPrdBenchmarkSuite(operations);
Console.WriteLine($"PRD Compliance: {results.ThresholdCompliance.Count(kvp => kvp.Value)} / {results.ThresholdCompliance.Count}");
```

**PRD Benchmark Thresholds:**
- `inventory_scan`: Target 150ms, Warning 120ms, Critical 150ms
- `full_rescan`: Target 200ms, Warning 160ms, Critical 200ms
- `full_rescan_with_retainers`: Target 300ms, Warning 240ms, Critical 300ms
- `ui_refresh`: Target 16.67ms (60 FPS), Warning 12ms, Critical 16.67ms

### PerformanceIntegrationExample

Complete integration example showing how to use all components together.

**Key Features:**
- Comprehensive monitoring setup
- Integrated operation tracking
- Complete PRD validation
- Performance report generation

**Usage Example:**
```csharp
using var integration = new PerformanceIntegrationExample(logger, inventoryService);
integration.StartMonitoring();

// Monitor specific operations
var snapshot = await integration.MonitoredInventoryScan(scanOptions);

// Run comprehensive benchmarks
var benchmarkResults = await integration.RunComprehensivePerformanceBenchmarks();

// Validate PRD compliance
bool isCompliant = integration.ValidatePrdCompliance();

// Generate comprehensive report
var report = await integration.GeneratePerformanceReport();
```

## Integration with ReadyCrafter Services

### InventoryService Integration

The InventoryService already includes performance tracking:

```csharp
// Performance metrics are automatically tracked
var stats = inventoryService.GetPerformanceStats();
Console.WriteLine($"Cache hit rate: {stats.CacheHitRate:F1}%");
Console.WriteLine($"Average scan time: {stats.AverageScanTimeMs:F2}ms");
```

### Plugin-Level Integration

Integrate performance monitoring at the plugin level:

```csharp
public class Plugin : IDalamudPlugin
{
    private PerformanceMonitor? _performanceMonitor;
    private MemoryProfiler? _memoryProfiler;
    private BenchmarkHelper? _benchmarkHelper;
    
    public void Initialize()
    {
        _performanceMonitor = new PerformanceMonitor(PluginLog);
        _memoryProfiler = new MemoryProfiler(PluginLog);
        _benchmarkHelper = new BenchmarkHelper(PluginLog);
        
        // Start monitoring in debug builds
        #if DEBUG
        _performanceMonitor.StartMonitoring();
        _memoryProfiler.StartProfiling();
        #endif
        
        // Set up performance alerts
        _performanceMonitor.PerformanceAlert += OnPerformanceAlert;
        _memoryProfiler.MemoryAlert += OnMemoryAlert;
    }
    
    private void OnPerformanceAlert(object? sender, PerformanceAlertEventArgs e)
    {
        PluginLog.Warning($"Performance Alert: {e.Message}");
    }
    
    private void OnMemoryAlert(object? sender, MemoryAlertEventArgs e)
    {
        PluginLog.Warning($"Memory Alert: {e.Message}");
    }
}
```

## Testing

The performance monitoring utilities include comprehensive unit tests in `Tests/PerformanceTests.cs`:

### Running Tests

```bash
# Run all performance tests
dotnet test --filter "Category=Performance"

# Run specific test
dotnet test --filter "TestName=InventoryScan_ShouldMeetPrdPerformanceTarget"
```

### Test Coverage

The tests validate:
- Individual inventory scan ≤150ms
- Full rescan ≤200ms (without retainers)
- Full rescan ≤300ms (with retainers) 
- Memory usage ≤75MB
- CPU usage ≤1% when idle
- Cache effectiveness
- Performance regression detection
- Memory leak detection
- Benchmark baseline management

## Performance Optimization Recommendations

Based on monitoring data, the system provides optimization recommendations:

### Memory Optimization
- Implement cache eviction policies when approaching 70MB
- Use object pooling for frequently allocated objects
- Consider memory mapping for large data sets
- Monitor component-specific memory usage

### Performance Optimization
- Enable parallel processing for container scanning
- Implement intelligent caching strategies
- Optimize hot code paths identified through profiling
- Use asynchronous operations where appropriate

### Monitoring Best Practices
- Monitor performance continuously in debug builds
- Set up automated performance regression detection
- Use benchmarks to validate optimizations
- Generate regular performance reports

## Troubleshooting

### Common Issues

**High Memory Usage:**
```csharp
var analysis = memoryProfiler.AnalyzeMemoryUsage();
if (analysis.IsOverLimit)
{
    // Force garbage collection
    var freedMemory = memoryProfiler.ForceGarbageCollection();
    PluginLog.Information($"Freed {freedMemory / (1024.0 * 1024.0):F2}MB");
}
```

**Slow Performance:**
```csharp
var slowOperations = performanceMonitor.CurrentStats.OperationMetrics
    .Where(kvp => kvp.Value.Any(m => m.Duration.TotalMilliseconds > 150))
    .Select(kvp => kvp.Key);
    
foreach (var operation in slowOperations)
{
    PluginLog.Warning($"Slow operation detected: {operation}");
}
```

**Memory Leaks:**
```csharp
var analysis = memoryProfiler.AnalyzeMemoryUsage();
if (analysis.PotentialLeakDetected)
{
    PluginLog.Error($"Memory leak: {analysis.EstimatedLeakRateMBPerMinute:F2}MB/min growth");
    
    // Get component breakdown
    var componentStats = memoryProfiler.GetComponentMemoryStats();
    var heavyComponents = componentStats.Values
        .Where(s => s.ActiveAllocationBytes > 10 * 1024 * 1024)
        .OrderByDescending(s => s.ActiveAllocationBytes);
        
    foreach (var component in heavyComponents)
    {
        PluginLog.Warning($"High memory component: {component.ComponentName} - {component.ActiveAllocationBytes / (1024.0 * 1024.0):F1}MB");
    }
}
```

## Configuration

Performance monitoring can be configured through various options:

```csharp
// Adjust monitoring intervals
performanceMonitor.StartMonitoring(); // Default 1-second intervals
memoryProfiler.StartProfiling(5000);  // 5-second intervals

// Configure benchmark parameters
benchmarkHelper.RunBenchmark("operation", action, iterations: 20, warmupIterations: 5);

// Set custom thresholds (if needed)
var customThresholds = new Dictionary<string, BenchmarkThreshold>
{
    ["custom_operation"] = new BenchmarkThreshold
    {
        TargetMs = 100.0,
        WarningMs = 80.0,
        CriticalMs = 100.0,
        Description = "Custom operation benchmark"
    }
};
```

## Future Enhancements

Planned improvements to the performance monitoring system:

1. **Real-time Dashboard**: Web-based performance monitoring dashboard
2. **Historical Trending**: Long-term performance trend analysis  
3. **Automated Optimization**: AI-driven performance optimization suggestions
4. **Cross-Session Analytics**: Performance tracking across game sessions
5. **Integration with Dalamud Metrics**: Native Dalamud performance integration
6. **Performance Budgets**: Configurable performance budgets per operation
7. **A/B Testing Framework**: Performance comparison for code changes

## Contributing

When adding new features or modifying existing code:

1. Ensure all operations are properly tracked with `PerformanceMonitor`
2. Use `MemoryProfiler` for components that allocate significant memory
3. Add benchmarks for new operations using `BenchmarkHelper`
4. Update performance tests to validate PRD requirements
5. Document performance characteristics and optimization opportunities

## Support

For performance-related issues or questions:

1. Check the performance monitoring logs first
2. Generate a comprehensive performance report
3. Review the optimization recommendations
4. Validate PRD compliance using the integrated tools
5. Submit detailed performance data with any bug reports