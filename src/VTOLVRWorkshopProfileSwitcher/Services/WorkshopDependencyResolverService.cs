using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class WorkshopDependencyResolverService
{
    private const string DisabledPrefix = "_OFF_";
    private const string ItemMetadataFileName = "item.json";
    private const string DependenciesPropertyName = "DependenciesIds";

    public async Task<DependencyResolutionResult> ResolveAsync(
        string workshopPath,
        IReadOnlyCollection<string> requestedEnabledIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequested = requestedEnabledIds
            .Where(IsNumericId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (!Directory.Exists(workshopPath))
        {
            return new DependencyResolutionResult(
                normalizedRequested,
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        var catalog = await BuildCatalogAsync(workshopPath, cancellationToken);
        var resolvedEnabledIds = new HashSet<string>(normalizedRequested, StringComparer.Ordinal);
        var autoEnabledDependencies = new HashSet<string>(StringComparer.Ordinal);
        var missingDependencies = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(normalizedRequested);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentId = queue.Dequeue();
            if (!visited.Add(currentId))
            {
                continue;
            }

            if (!catalog.DependenciesByWorkshopId.TryGetValue(currentId, out var dependencies))
            {
                continue;
            }

            foreach (var dependencyId in dependencies)
            {
                if (!catalog.AllKnownWorkshopIds.Contains(dependencyId))
                {
                    missingDependencies.Add(dependencyId);
                    continue;
                }

                if (resolvedEnabledIds.Add(dependencyId))
                {
                    autoEnabledDependencies.Add(dependencyId);
                    queue.Enqueue(dependencyId);
                }
            }
        }

        return new DependencyResolutionResult(
            resolvedEnabledIds,
            autoEnabledDependencies.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            missingDependencies.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private static async Task<DependencyCatalog> BuildCatalogAsync(string workshopPath, CancellationToken cancellationToken)
    {
        var allKnownWorkshopIds = new HashSet<string>(StringComparer.Ordinal);
        var dependenciesByWorkshopId = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        var directories = await Task.Run(() => Directory.EnumerateDirectories(workshopPath).ToList(), cancellationToken);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(directory);
            if (!TryGetWorkshopId(folderName, out var workshopId))
            {
                continue;
            }

            allKnownWorkshopIds.Add(workshopId);
            var itemJsonPath = Path.Combine(directory, ItemMetadataFileName);
            if (!File.Exists(itemJsonPath))
            {
                continue;
            }

            var dependencies = await ReadDependenciesAsync(itemJsonPath, cancellationToken);
            if (dependencies.Count > 0)
            {
                dependenciesByWorkshopId[workshopId] = dependencies;
            }
        }

        return new DependencyCatalog(allKnownWorkshopIds, dependenciesByWorkshopId);
    }

    private static async Task<IReadOnlySet<string>> ReadDependenciesAsync(string itemJsonPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(itemJsonPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!TryGetPropertyCaseInsensitive(document.RootElement, DependenciesPropertyName, out var dependenciesElement) ||
                dependenciesElement.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in dependenciesElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
                {
                    dependencies.Add(number.ToString());
                    continue;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    var dependencyId = element.GetString();
                    if (IsNumericId(dependencyId))
                    {
                        dependencies.Add(dependencyId!);
                    }
                }
            }

            return dependencies;
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
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

    private static bool IsNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(char.IsDigit);
    }

    private sealed record DependencyCatalog(
        IReadOnlySet<string> AllKnownWorkshopIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> DependenciesByWorkshopId);
}

public sealed record DependencyResolutionResult(
    IReadOnlySet<string> EnabledWorkshopIds,
    IReadOnlyList<string> AutoEnabledDependencyIds,
    IReadOnlyList<string> MissingDependencyIds);
