using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class WorkshopScanner
{
    private const string DisabledPrefix = "_OFF_";
    private const string SteamWorkshopApi = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private static readonly HttpClient HttpClient = new();
    private static readonly CwbLoadItemsService CwbLoadItemsService = new();
    private static readonly string ThumbnailCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VTOLVRWorkshopProfileSwitcher",
        "thumbnail-cache");

    public async Task<IReadOnlyList<WorkshopMod>> ScanAsync(string workshopPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workshopPath))
        {
            return Array.Empty<WorkshopMod>();
        }

        var dirs = await Task.Run(() => Directory.EnumerateDirectories(workshopPath).ToList(), cancellationToken);
        var result = new List<WorkshopMod>(dirs.Count);

        foreach (var dir in dirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var folderName = Path.GetFileName(dir);
                if (!TryGetWorkshopId(folderName, out var workshopId, out var enabled))
                {
                    continue;
                }

                var metadata = await TryReadMetadataAsync(dir, cancellationToken);
                var displayName = metadata.DisplayName
                    ?? TryDeriveDisplayName(dir)
                    ?? $"Mod {workshopId}";
                var thumbnail = await ResolveThumbnailAsync(dir, workshopId, metadata.PreviewImageUrl, cancellationToken);

                result.Add(new WorkshopMod
                {
                    WorkshopId = workshopId,
                    FolderName = folderName,
                    FullPath = dir,
                    IsEnabled = enabled,
                    DisplayName = displayName,
                    ThumbnailPath = thumbnail
                });
            }
            catch
            {
                // Skip broken entries so one malformed workshop item cannot crash scanning.
            }
        }

        try
        {
            await ApplyCwbPackStatesAsync(workshopPath, result, cancellationToken);
        }
        catch
        {
            // Never fail full mod scanning due to optional CWB state projection.
        }

        await EnrichMissingMetadataFromSteamAsync(result, cancellationToken);

        return result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task ApplyCwbPackStatesAsync(
        string workshopPath,
        List<WorkshopMod> mods,
        CancellationToken cancellationToken)
    {
        if (mods.Count == 0)
        {
            return;
        }

        var discovery = await CwbLoadItemsService.DiscoverPacksAsync(workshopPath, cancellationToken);
        if (discovery.PackWorkshopIds.Count == 0)
        {
            return;
        }

        var cwbPackStates = await CwbLoadItemsService.GetPackEnabledStatesAsync(workshopPath, discovery, cancellationToken);
        if (cwbPackStates.Count == 0)
        {
            return;
        }

        foreach (var mod in mods)
        {
            if (!cwbPackStates.TryGetValue(mod.WorkshopId, out var shouldLoadPack))
            {
                continue;
            }

            mod.IsEnabled = mod.IsEnabled && shouldLoadPack;
        }
    }

    public static bool TryGetWorkshopId(string folderName, out string workshopId, out bool isEnabled)
    {
        if (long.TryParse(folderName, out _))
        {
            workshopId = folderName;
            isEnabled = true;
            return true;
        }

        if (folderName.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = folderName[DisabledPrefix.Length..];
            if (long.TryParse(suffix, out _))
            {
                workshopId = suffix;
                isEnabled = false;
                return true;
            }
        }

        workshopId = string.Empty;
        isEnabled = false;
        return false;
    }

    private static async Task<(string? DisplayName, string? PreviewImageUrl)> TryReadMetadataAsync(string modPath, CancellationToken cancellationToken)
    {
        var manifestCandidates = GetMetadataCandidates(modPath);

        foreach (var manifestPath in manifestCandidates)
        {
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                var displayName = FindStringByKeyRecursive(root, 0,
                    "name", "title", "displayName", "bundleName", "modName", "pluginName");
                var previewImageUrl = FindStringByKeyRecursive(root, 0,
                    "previewImageUrl", "previewUrl", "imageUrl", "thumbnailUrl", "iconUrl", "previewImage", "thumbnail");

                if (!string.IsNullOrWhiteSpace(previewImageUrl) &&
                    !Uri.TryCreate(previewImageUrl, UriKind.Absolute, out _))
                {
                    var possibleLocalPath = Path.Combine(Path.GetDirectoryName(manifestPath) ?? modPath, previewImageUrl);
                    previewImageUrl = File.Exists(possibleLocalPath) ? possibleLocalPath : null;
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = PrettifyName(displayName);
                }

                if (!string.IsNullOrWhiteSpace(displayName) || !string.IsNullOrWhiteSpace(previewImageUrl))
                {
                    return (displayName, previewImageUrl);
                }
            }
            catch
            {
                // Ignore malformed metadata files and continue with fallback naming.
            }
        }

        return (null, null);
    }

    private static List<string> GetMetadataCandidates(string modPath)
    {
        var highPriority = new[] { "manifest.json", "mod.json", "item.json" }
            .Select(name => Path.Combine(modPath, name))
            .Where(File.Exists)
            .ToList();

        var allJson = Directory.EnumerateFiles(modPath, "*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();

        // Preserve priority files first, then other JSONs.
        return highPriority
            .Concat(allJson)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindThumbnailPath(string modPath)
    {
        var known = new[]
            {
                "preview.png", "thumbnail.png", "icon.png",
                "thumb.png",
                "preview.jpg", "thumbnail.jpg", "icon.jpg",
                "thumb.jpg",
                "preview.jpeg", "thumbnail.jpeg", "icon.jpeg"
            }
            .Select(file => Path.Combine(modPath, file))
            .FirstOrDefault(File.Exists);

        if (!string.IsNullOrWhiteSpace(known))
        {
            return known;
        }

        var candidates = Directory
            .EnumerateFiles(modPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer preview-like filenames. Avoid picking random texture atlases.
        var namedPreview = candidates.FirstOrDefault(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            name = name.ToLowerInvariant();
            return name.Contains("preview") ||
                   name.Contains("thumb") ||
                   name.Contains("thumbnail") ||
                   name.Contains("icon") ||
                   name.Contains("logo") ||
                   name.Contains("cover") ||
                   name.Contains("screenshot");
        });

        if (!string.IsNullOrWhiteSpace(namedPreview))
        {
            return namedPreview;
        }

        // Only use arbitrary local image when there is exactly one non-generic choice.
        if (candidates.Count == 1)
        {
            var only = candidates[0];
            var baseName = Path.GetFileNameWithoutExtension(only) ?? string.Empty;
            return IsGenericAssetName(baseName) ? null : only;
        }

        return null;
    }

    private static async Task<string?> ResolveThumbnailAsync(
        string modPath,
        string workshopId,
        string? previewImageUrl,
        CancellationToken cancellationToken)
    {
        var local = FindThumbnailPath(modPath);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        if (string.IsNullOrWhiteSpace(previewImageUrl) ||
            !Uri.TryCreate(previewImageUrl, UriKind.Absolute, out var previewUri))
        {
            return null;
        }

        try
        {
            EnsureHttpDefaults();
            Directory.CreateDirectory(ThumbnailCacheDir);
            var extension = Path.GetExtension(previewUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var cachePath = Path.Combine(ThumbnailCacheDir, $"{workshopId}{extension}");
            if (File.Exists(cachePath))
            {
                var existing = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                if (!LooksLikeImage(existing))
                {
                    File.Delete(cachePath);
                }
            }

            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length == 0)
            {
                var bytes = await HttpClient.GetByteArrayAsync(previewUri, cancellationToken);
                if (!LooksLikeImage(bytes))
                {
                    return null;
                }
                await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            }

            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureHttpDefaults()
    {
        if (HttpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            HttpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("VTOLVRWorkshopProfileSwitcher", "1.0"));
        }
    }

    private static string? FindStringByKeyRecursive(JsonElement element, int depth, params string[] names)
    {
        if (depth > 6)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var nested = FindStringByKeyRecursive(property.Value, depth + 1, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringByKeyRecursive(item, depth + 1, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? TryDeriveDisplayName(string modPath)
    {
        try
        {
            // Most VTOL mods include a primary DLL in the root. Prefer non-dependency names.
            var dllNames = Directory.EnumerateFiles(modPath, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();

            var dllName = dllNames
                .FirstOrDefault(name => !IsLikelyDependencyAssemblyName(name))
                ?? dllNames.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(dllName))
            {
                return PrettifyName(dllName);
            }

            // Fallback: if the folder contains one logical root subfolder, use that.
            var subfolders = Directory.EnumerateDirectories(modPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();

            if (subfolders.Count == 1)
            {
                return PrettifyName(subfolders[0]);
            }
        }
        catch
        {
            // Keep scanner resilient; never fail scan due to naming fallback.
        }

        return null;
    }

    private static string PrettifyName(string raw)
    {
        var value = raw
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        // Split common camel case for better readability.
        value = Regex.Replace(
            value,
            "(?<=[a-z])([A-Z])",
            " $1");

        return value;
    }

    private static bool IsGenericAssetName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(n))
        {
            return true;
        }

        return n is "thumb" or "thumbnail" or "preview" or "icon" ||
               n.StartsWith("mat_") ||
               n.StartsWith("texture") ||
               n.StartsWith("normal") ||
               n.StartsWith("albedo") ||
               n.EndsWith("_main") ||
               n.EndsWith(" main");
    }

    private static bool IsLikelyDerivedName(string displayName)
    {
        var value = displayName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (IsLikelyDependencyAssemblyName(value))
        {
            return true;
        }

        return value.EndsWith(" main") ||
               value.StartsWith("mat ") ||
               value.StartsWith("texture ") ||
               value.StartsWith("icon ") ||
               Regex.IsMatch(value, @"^[a-z]{1,5}\d{0,2}\smain$");
    }

    private static bool IsLikelyBadThumbnail(string? thumbnailPath)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPath))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(thumbnailPath)?.Trim().ToLowerInvariant() ?? string.Empty;
        return name.StartsWith("mat_") ||
               name.EndsWith("_main") ||
               name.EndsWith(" main") ||
               name.StartsWith("texture");
    }

    private static bool IsLikelyDependencyAssemblyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim()
            .ToLowerInvariant()
            .Replace(".", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty);

        if (normalized.StartsWith("system") ||
            normalized.StartsWith("microsoft") ||
            normalized.StartsWith("unity") ||
            normalized.StartsWith("valve") ||
            normalized.StartsWith("steam"))
        {
            return true;
        }

        return normalized is
            "asmresolver" or
            "harmony" or
            "harmonyx" or
            "0harmony" or
            "bepinex" or
            "newtonsoftjson" or
            "monocecil" or
            "monomod" or
            "netstandard" or
            "mscorlib" or
            "unhollowerbaselib" or
            "unhollowerruntime" or
            "dnlib";
    }

    private static async Task EnrichMissingMetadataFromSteamAsync(List<WorkshopMod> mods, CancellationToken cancellationToken)
    {
        var needsEnrichment = mods
            .Where(mod =>
                IsGenericDisplayName(mod.DisplayName, mod.WorkshopId) ||
                IsLikelyDerivedName(mod.DisplayName) ||
                string.IsNullOrWhiteSpace(mod.ThumbnailPath) ||
                IsLikelyBadThumbnail(mod.ThumbnailPath))
            .Select(mod => mod.WorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (needsEnrichment.Count == 0)
        {
            return;
        }

        var steamDetails = await FetchSteamWorkshopDetailsAsync(needsEnrichment, cancellationToken);
        if (steamDetails.Count == 0)
        {
            return;
        }

        foreach (var mod in mods)
        {
            if (!steamDetails.TryGetValue(mod.WorkshopId, out var detail))
            {
                continue;
            }

            if ((IsGenericDisplayName(mod.DisplayName, mod.WorkshopId) || IsLikelyDerivedName(mod.DisplayName)) &&
                !string.IsNullOrWhiteSpace(detail.Title))
            {
                mod.DisplayName = PrettifyName(detail.Title);
            }

            if ((string.IsNullOrWhiteSpace(mod.ThumbnailPath) || IsLikelyBadThumbnail(mod.ThumbnailPath)) &&
                !string.IsNullOrWhiteSpace(detail.PreviewUrl))
            {
                mod.ThumbnailPath = await ResolveThumbnailAsync(mod.FullPath, mod.WorkshopId, detail.PreviewUrl, cancellationToken);
            }

            if (detail.Downloads is > 0)
            {
                mod.DownloadCount = detail.Downloads.Value;
            }
        }
    }

    private static bool IsGenericDisplayName(string displayName, string workshopId)
    {
        return string.Equals(displayName, $"Mod {workshopId}", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(displayName);
    }

    private static async Task<Dictionary<string, (string? Title, string? PreviewUrl, long? Downloads)>> FetchSteamWorkshopDetailsAsync(
        IReadOnlyList<string> workshopIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string? Title, string? PreviewUrl, long? Downloads)>(StringComparer.Ordinal);
        const int batchSize = 40;

        for (var i = 0; i < workshopIds.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = workshopIds.Skip(i).Take(batchSize).ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("itemcount", batch.Count.ToString())
                };

                for (var j = 0; j < batch.Count; j++)
                {
                    form.Add(new($"publishedfileids[{j}]", batch[j]));
                }

                using var content = new FormUrlEncodedContent(form);
                using var response = await HttpClient.PostAsync(SteamWorkshopApi, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (!document.RootElement.TryGetProperty("response", out var responseRoot) ||
                    !responseRoot.TryGetProperty("publishedfiledetails", out var detailsArray) ||
                    detailsArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var detail in detailsArray.EnumerateArray())
                {
                    if (detail.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = FindStringByKeyRecursive(detail, 0, "publishedfileid");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var title = FindStringByKeyRecursive(detail, 0, "title");
                    var preview = FindStringByKeyRecursive(detail, 0, "preview_url");
                    var downloads = FindInt64ByKeyRecursive(detail, 0, "subscriptions", "lifetime_subscriptions");
                    result[id] = (title, preview, downloads);
                }
            }
            catch
            {
                // Network/API failures should never break local scanning.
            }
        }

        return result;
    }

    private static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // JPG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return true;
        }

        // WEBP (RIFF....WEBP)
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    private static long? FindInt64ByKeyRecursive(JsonElement element, int depth, params string[] names)
    {
        if (depth > 6)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var n))
                    {
                        return n;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String &&
                        long.TryParse(property.Value.GetString(), out var parsed))
                    {
                        return parsed;
                    }
                }

                var nested = FindInt64ByKeyRecursive(property.Value, depth + 1, names);
                if (nested.HasValue)
                {
                    return nested.Value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInt64ByKeyRecursive(item, depth + 1, names);
                if (nested.HasValue)
                {
                    return nested.Value;
                }
            }
        }

        return null;
    }
}
