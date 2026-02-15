using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class SteamLibraryDetector
{
    private const string TargetAppId = "3018410";
    private static readonly Regex PathRegex = new("\"path\"\\s+\"(?<path>.+?)\"", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> FindWorkshopPathsAsync(CancellationToken cancellationToken = default)
    {
        var libraryFolders = GetLibraryFoldersFilePath();
        if (string.IsNullOrWhiteSpace(libraryFolders) || !File.Exists(libraryFolders))
        {
            return Array.Empty<string>();
        }

        var content = await File.ReadAllTextAsync(libraryFolders, cancellationToken);
        var matches = PathRegex.Matches(content);

        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetDirectoryName(Path.GetDirectoryName(libraryFolders) ?? string.Empty) ?? string.Empty
        };

        foreach (Match match in matches)
        {
            var rawPath = match.Groups["path"].Value;
            var fixedPath = rawPath.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(fixedPath))
            {
                libraryPaths.Add(fixedPath);
            }
        }

        return libraryPaths
            .Where(Directory.Exists)
            .Select(path => Path.Combine(path, "steamapps", "workshop", "content", TargetAppId))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetLibraryFoldersFilePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "libraryfolders.vdf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "libraryfolders.vdf")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
