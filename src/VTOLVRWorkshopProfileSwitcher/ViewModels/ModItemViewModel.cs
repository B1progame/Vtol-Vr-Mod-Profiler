using System;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.ViewModels;

public sealed partial class ModItemViewModel : ObservableObject
{
    public WorkshopMod Source { get; }

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isSelected;

    public string WorkshopId => Source.WorkshopId;
    public string ModName => Source.DisplayName;
    public string FolderName => Source.FolderName;
    public string DownloadCountText => Source.DownloadCount is > 0
        ? $"{Source.DownloadCount.Value.ToString("N0", CultureInfo.InvariantCulture)} downloads"
        : "downloads: n/a";
    public Bitmap? ThumbnailImage { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    private readonly Func<ModItemViewModel, Task>? _onDelete;

    public ModItemViewModel(WorkshopMod source, Func<ModItemViewModel, Task>? onDelete = null)
    {
        Source = source;
        isEnabled = source.IsEnabled;
        ThumbnailImage = TryLoadBitmap(source.ThumbnailPath);
        _onDelete = onDelete;
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
    }

    private async Task DeleteAsync()
    {
        if (_onDelete is not null)
        {
            await _onDelete(this);
        }
    }

    private static Bitmap? TryLoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
