using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ReadyCrafter.Models;

/// <summary>
/// Represents a snapshot of inventory state for change detection and caching.
/// Optimized for fast comparison and minimal memory usage.
/// </summary>
public sealed class InventorySnapshot
{
    /// <summary>
    /// Timestamp when this snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hash of the complete inventory state.
    /// Used for fast change detection without comparing individual items.
    /// </summary>
    public string StateHash { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary mapping item IDs to their total quantities across all scanned containers.
    /// Key: ItemId, Value: ItemQuantity
    /// </summary>
    public Dictionary<uint, ItemQuantity> Items { get; set; } = new();

    /// <summary>
    /// Set of container IDs that were included in this snapshot.
    /// Used to determine if containers have changed between scans.
    /// </summary>
    public HashSet<uint> ScannedContainers { get; set; } = new();

    /// <summary>
    /// Whether retainers were included in this snapshot.
    /// </summary>
    public bool IncludedRetainers { get; set; } = false;

    /// <summary>
    /// Total number of item stacks found during the scan.
    /// Used for performance monitoring.
    /// </summary>
    public int TotalStacks { get; set; }

    /// <summary>
    /// Time taken to generate this snapshot in milliseconds.
    /// Used for performance tracking.
    /// </summary>
    public double ScanTimeMs { get; set; }

    /// <summary>
    /// Version number of the snapshot format.
    /// Used for compatibility checking when loading cached snapshots.
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Additional metadata about the scan.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Get the total quantity of a specific item (NQ + HQ).
    /// </summary>
    public uint GetTotalQuantity(uint itemId)
    {
        return Items.TryGetValue(itemId, out var quantity) ? quantity.Total : 0;
    }

    /// <summary>
    /// Get the NQ quantity of a specific item.
    /// </summary>
    public uint GetNqQuantity(uint itemId)
    {
        return Items.TryGetValue(itemId, out var quantity) ? quantity.Nq : 0;
    }

    /// <summary>
    /// Get the HQ quantity of a specific item.
    /// </summary>
    public uint GetHqQuantity(uint itemId)
    {
        return Items.TryGetValue(itemId, out var quantity) ? quantity.Hq : 0;
    }

    /// <summary>
    /// Check if the inventory contains a specific quantity of an item.
    /// </summary>
    public bool HasQuantity(uint itemId, uint requiredQuantity, bool requireHq = false)
    {
        if (!Items.TryGetValue(itemId, out var quantity))
            return false;

        if (requireHq)
            return quantity.Hq >= requiredQuantity;
        
        return quantity.Total >= requiredQuantity;
    }

    /// <summary>
    /// Add an item to the snapshot.
    /// </summary>
    public void AddItem(uint itemId, uint nqQuantity, uint hqQuantity = 0)
    {
        if (!Items.ContainsKey(itemId))
        {
            Items[itemId] = new ItemQuantity { Nq = nqQuantity, Hq = hqQuantity };
        }
        else
        {
            Items[itemId].Nq += nqQuantity;
            Items[itemId].Hq += hqQuantity;
        }
    }

    /// <summary>
    /// Generate hash for the current inventory state.
    /// </summary>
    public void GenerateStateHash()
    {
        var hashBuilder = new StringBuilder();
        
        // Include container configuration in hash
        hashBuilder.Append($"containers:{string.Join(",", ScannedContainers.OrderBy(x => x))}");
        hashBuilder.Append($"|retainers:{IncludedRetainers}");
        
        // Include item data in hash
        foreach (var kvp in Items.OrderBy(x => x.Key))
        {
            hashBuilder.Append($"|{kvp.Key}:{kvp.Value.Nq}:{kvp.Value.Hq}");
        }
        
        // Generate SHA256 hash for uniqueness
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashBuilder.ToString()));
        StateHash = Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Compare this snapshot with another to detect changes.
    /// </summary>
    public InventoryChangeSet CompareTo(InventorySnapshot other)
    {
        var changes = new InventoryChangeSet
        {
            OldSnapshot = other,
            NewSnapshot = this,
            HasChanges = StateHash != other.StateHash
        };

        if (!changes.HasChanges)
            return changes;

        // Find added items
        foreach (var kvp in Items)
        {
            if (!other.Items.ContainsKey(kvp.Key))
            {
                changes.AddedItems[kvp.Key] = kvp.Value;
            }
        }

        // Find removed items
        foreach (var kvp in other.Items)
        {
            if (!Items.ContainsKey(kvp.Key))
            {
                changes.RemovedItems[kvp.Key] = kvp.Value;
            }
        }

        // Find quantity changes
        foreach (var kvp in Items)
        {
            if (other.Items.TryGetValue(kvp.Key, out var oldQuantity))
            {
                if (kvp.Value.Nq != oldQuantity.Nq || kvp.Value.Hq != oldQuantity.Hq)
                {
                    changes.QuantityChanges[kvp.Key] = new QuantityChange
                    {
                        OldQuantity = oldQuantity,
                        NewQuantity = kvp.Value,
                        NetChange = (int)kvp.Value.Total - (int)oldQuantity.Total
                    };
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Check if this snapshot is compatible with given scan options.
    /// </summary>
    public bool IsCompatibleWith(ScanOptions options)
    {
        // Check if container sets match
        var expectedContainers = new HashSet<uint>(options.AllContainerIds);
        if (!ScannedContainers.SetEquals(expectedContainers))
            return false;

        // Check retainer inclusion
        if (IncludedRetainers != options.IncludeRetainers)
            return false;

        return true;
    }

    /// <summary>
    /// Check if this snapshot is recent enough to be used.
    /// </summary>
    public bool IsRecentEnough(TimeSpan maxAge)
    {
        return DateTime.UtcNow - Timestamp < maxAge;
    }

    /// <summary>
    /// Get a summary of the inventory contents.
    /// </summary>
    [JsonIgnore]
    public InventorySummary Summary
    {
        get
        {
            return new InventorySummary
            {
                TotalUniqueItems = Items.Count,
                TotalItemQuantity = (uint)Items.Values.Sum(x => x.Total),
                TotalHqItems = (uint)Items.Values.Sum(x => x.Hq),
                TotalStacks = TotalStacks,
                ContainerCount = ScannedContainers.Count,
                IncludedRetainers = IncludedRetainers,
                ScanTimeMs = ScanTimeMs,
                Timestamp = Timestamp
            };
        }
    }

    /// <summary>
    /// Create an empty snapshot.
    /// </summary>
    public static InventorySnapshot CreateEmpty()
    {
        var snapshot = new InventorySnapshot();
        snapshot.GenerateStateHash();
        return snapshot;
    }
}

/// <summary>
/// Represents quantity information for a single item type.
/// </summary>
public sealed class ItemQuantity
{
    /// <summary>
    /// Normal quality quantity.
    /// </summary>
    public uint Nq { get; set; }

    /// <summary>
    /// High quality quantity.
    /// </summary>
    public uint Hq { get; set; }

    /// <summary>
    /// Total quantity (NQ + HQ).
    /// </summary>
    [JsonIgnore]
    public uint Total => Nq + Hq;

    /// <summary>
    /// Whether this item has any HQ variants.
    /// </summary>
    [JsonIgnore]
    public bool HasHq => Hq > 0;

    /// <summary>
    /// Create a copy of this quantity.
    /// </summary>
    public ItemQuantity Clone()
    {
        return new ItemQuantity { Nq = Nq, Hq = Hq };
    }
}

/// <summary>
/// Represents changes between two inventory snapshots.
/// </summary>
public sealed class InventoryChangeSet
{
    /// <summary>
    /// Previous inventory snapshot.
    /// </summary>
    public InventorySnapshot OldSnapshot { get; set; } = null!;

    /// <summary>
    /// Current inventory snapshot.
    /// </summary>
    public InventorySnapshot NewSnapshot { get; set; } = null!;

    /// <summary>
    /// Whether any changes were detected.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Items that were added to inventory.
    /// </summary>
    public Dictionary<uint, ItemQuantity> AddedItems { get; set; } = new();

    /// <summary>
    /// Items that were removed from inventory.
    /// </summary>
    public Dictionary<uint, ItemQuantity> RemovedItems { get; set; } = new();

    /// <summary>
    /// Items whose quantities changed.
    /// </summary>
    public Dictionary<uint, QuantityChange> QuantityChanges { get; set; } = new();

    /// <summary>
    /// Total number of changes detected.
    /// </summary>
    [JsonIgnore]
    public int TotalChanges => AddedItems.Count + RemovedItems.Count + QuantityChanges.Count;

    /// <summary>
    /// Whether changes are significant enough to trigger a recipe recalculation.
    /// </summary>
    [JsonIgnore]
    public bool IsSignificant => HasChanges && TotalChanges > 0;
}

/// <summary>
/// Represents a quantity change for a specific item.
/// </summary>
public sealed class QuantityChange
{
    /// <summary>
    /// Previous quantity.
    /// </summary>
    public ItemQuantity OldQuantity { get; set; } = new();

    /// <summary>
    /// New quantity.
    /// </summary>
    public ItemQuantity NewQuantity { get; set; } = new();

    /// <summary>
    /// Net change in total quantity (positive = gained, negative = lost).
    /// </summary>
    public int NetChange { get; set; }

    /// <summary>
    /// Whether the quantity increased.
    /// </summary>
    [JsonIgnore]
    public bool Increased => NetChange > 0;

    /// <summary>
    /// Whether the quantity decreased.
    /// </summary>
    [JsonIgnore]
    public bool Decreased => NetChange < 0;
}

/// <summary>
/// Summary information about an inventory snapshot.
/// </summary>
public sealed class InventorySummary
{
    /// <summary>
    /// Total number of unique item types.
    /// </summary>
    public int TotalUniqueItems { get; set; }

    /// <summary>
    /// Total quantity of all items.
    /// </summary>
    public uint TotalItemQuantity { get; set; }

    /// <summary>
    /// Total quantity of HQ items.
    /// </summary>
    public uint TotalHqItems { get; set; }

    /// <summary>
    /// Total number of item stacks.
    /// </summary>
    public int TotalStacks { get; set; }

    /// <summary>
    /// Number of containers scanned.
    /// </summary>
    public int ContainerCount { get; set; }

    /// <summary>
    /// Whether retainers were included.
    /// </summary>
    public bool IncludedRetainers { get; set; }

    /// <summary>
    /// Time taken for the scan in milliseconds.
    /// </summary>
    public double ScanTimeMs { get; set; }

    /// <summary>
    /// When the snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; set; }
}