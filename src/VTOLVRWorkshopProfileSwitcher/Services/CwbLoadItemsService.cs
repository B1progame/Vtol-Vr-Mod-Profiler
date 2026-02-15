using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class CwbLoadItemsService
{
    public const string CustomWeaponsBaseWorkshopId = "3265798414";

    private const string DisabledPrefix = "_OFF_";
    private const string LoadItemsFileName = "loaditems.json";

    public async Task<CwbPackDiscoveryResult> DiscoverPacksAsync(
        string workshopPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workshopPath))
        {
            return CwbPackDiscoveryResult.Empty;
        }

        var packNamesByWorkshopId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var directories = await Task.Run(() => Directory.EnumerateDirectories(workshopPath).ToList(), cancellationToken);

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(directory);
            if (!TryGetWorkshopId(folderName, out var workshopId))
            {
                continue;
            }

            List<string> packNames;
            try
            {
                packNames = Directory.EnumerateFiles(directory, "*.cwb", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                continue;
            }

            if (packNames.Count == 0)
            {
                continue;
            }

            packNamesByWorkshopId[workshopId] = packNames;
        }

        if (packNamesByWorkshopId.Count == 0)
        {
            return CwbPackDiscoveryResult.Empty;
        }

        return new CwbPackDiscoveryResult(
            packNamesByWorkshopId,
            packNamesByWorkshopId.Keys.ToHashSet(StringComparer.Ordinal));
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetPackEnabledStatesAsync(
        string workshopPath,
        CwbPackDiscoveryResult discoveryResult,
        CancellationToken cancellationToken = default)
    {
        if (discoveryResult.PackNamesByWorkshopId.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var flagsByPackName = await ReadPackLoadFlagsAsync(workshopPath, cancellationToken);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var pair in discoveryResult.PackNamesByWorkshopId)
        {
            var hasExplicitFlag = false;
            var shouldLoad = false;

            foreach (var packName in pair.Value)
            {
                if (!flagsByPackName.TryGetValue(packName, out var loadThisPack))
                {
                    continue;
                }

                hasExplicitFlag = true;
                if (loadThisPack)
                {
                    shouldLoad = true;
                    break;
                }
            }

            // If CWB has never persisted this pack yet, treat it as enabled by default.
            result[pair.Key] = hasExplicitFlag ? shouldLoad : true;
        }

        return result;
    }

    public async Task<CwbLoadItemsSyncResult> SyncAsync(
        string workshopPath,
        CwbPackDiscoveryResult discoveryResult,
        IReadOnlySet<string> desiredEnabledWorkshopIds,
        CancellationToken cancellationToken = default)
    {
        if (discoveryResult.PackNamesByWorkshopId.Count == 0)
        {
            return CwbLoadItemsSyncResult.Succeeded("No CWB packs discovered.");
        }

        var loadItemsPath = FindLoadItemsPath(workshopPath);
        if (string.IsNullOrWhiteSpace(loadItemsPath))
        {
            return CwbLoadItemsSyncResult.Failed("Custom Weapons Base folder not found.");
        }

        var document = await ReadDocumentAsync(loadItemsPath, cancellationToken);
        var entriesByPackName = new Dictionary<string, LoadItemsPackEntry>(StringComparer.OrdinalIgnoreCase);
        var orderedNames = new List<string>();

        foreach (var entry in document.Packs)
        {
            var normalized = NormalizePackName(entry.Name);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (entriesByPackName.ContainsKey(normalized))
            {
                continue;
            }

            entry.Name = normalized;
            entriesByPackName[normalized] = entry;
            orderedNames.Add(normalized);
        }

        var changedEntries = 0;

        foreach (var pair in discoveryResult.PackNamesByWorkshopId)
        {
            var shouldLoad = desiredEnabledWorkshopIds.Contains(pair.Key);
            foreach (var packName in pair.Value)
            {
                var normalized = NormalizePackName(packName);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (!entriesByPackName.TryGetValue(normalized, out var entry))
                {
                    entry = new LoadItemsPackEntry
                    {
                        Name = normalized,
                        LoadThisPack = shouldLoad
                    };
                    entriesByPackName[normalized] = entry;
                    orderedNames.Add(normalized);
                    changedEntries++;
                    continue;
                }

                if (entry.LoadThisPack != shouldLoad)
                {
                    entry.LoadThisPack = shouldLoad;
                    changedEntries++;
                }
            }
        }

        if (changedEntries == 0 && File.Exists(loadItemsPath))
        {
            return CwbLoadItemsSyncResult.Succeeded("CWB loaditems.json already matched selected packs.");
        }

        document.Packs = orderedNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => entriesByPackName[name])
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await WriteDocumentAsync(loadItemsPath, document, cancellationToken);
        return CwbLoadItemsSyncResult.Succeeded(
            $"Updated CWB loaditems.json ({changedEntries} pack toggle changes).");
    }

    private static async Task<Dictionary<string, bool>> ReadPackLoadFlagsAsync(
        string workshopPath,
        CancellationToken cancellationToken)
    {
        var loadItemsPath = FindLoadItemsPath(workshopPath);
        if (string.IsNullOrWhiteSpace(loadItemsPath) || !File.Exists(loadItemsPath))
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var document = await ReadDocumentAsync(loadItemsPath, cancellationToken);
        return document.Packs
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().LoadThisPack, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<LoadItemsDocument> ReadDocumentAsync(
        string loadItemsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(loadItemsPath))
        {
            return new LoadItemsDocument();
        }

        var content = await File.ReadAllTextAsync(loadItemsPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LoadItemsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<LoadItemsDocument>(content) ?? new LoadItemsDocument();
        }
        catch
        {
            return new LoadItemsDocument();
        }
    }

    private static async Task WriteDocumentAsync(
        string loadItemsPath,
        LoadItemsDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(loadItemsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(loadItemsPath))
        {
            var backupPath = $"{loadItemsPath}.bak_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            File.Copy(loadItemsPath, backupPath, overwrite: true);
        }

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(loadItemsPath, json, cancellationToken);
    }

    private static string? FindLoadItemsPath(string workshopPath)
    {
        var cwbPath = Path.Combine(workshopPath, CustomWeaponsBaseWorkshopId);
        if (Directory.Exists(cwbPath))
        {
            return Path.Combine(cwbPath, LoadItemsFileName);
        }

        var disabledCwbPath = Path.Combine(workshopPath, $"{DisabledPrefix}{CustomWeaponsBaseWorkshopId}");
        if (Directory.Exists(disabledCwbPath))
        {
            return Path.Combine(disabledCwbPath, LoadItemsFileName);
        }

        return null;
    }

    private static string? NormalizePackName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryGetWorkshopId(string folderName, out string workshopId)
    {
        if (long.TryParse(folderName, out _))
        {
            workshopId = folderName;
            return true;
        }

        if (folderName.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = folderName[DisabledPrefix.Length..];
            if (long.TryParse(suffix, out _))
            {
                workshopId = suffix;
                return true;
            }
        }

        workshopId = string.Empty;
        return false;
    }

    private sealed class LoadItemsDocument
    {
        [JsonPropertyName("packs")]
        public List<LoadItemsPackEntry> Packs { get; set; } = new();
    }

    private sealed class LoadItemsPackEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("loadThisPack")]
        public bool LoadThisPack { get; set; }
    }
}

public sealed record CwbPackDiscoveryResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> PackNamesByWorkshopId,
    IReadOnlySet<string> PackWorkshopIds)
{
    public static CwbPackDiscoveryResult Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public sealed record CwbLoadItemsSyncResult(bool Success, string Message)
{
    public static CwbLoadItemsSyncResult Succeeded(string message) => new(true, message);
    public static CwbLoadItemsSyncResult Failed(string message) => new(false, message);
}
