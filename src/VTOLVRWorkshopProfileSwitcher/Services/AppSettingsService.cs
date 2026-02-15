using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_paths.SettingsFile);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions);
        return settings ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveLock.WaitAsync();
        try
        {
            await using var stream = File.Create(_paths.SettingsFile);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
