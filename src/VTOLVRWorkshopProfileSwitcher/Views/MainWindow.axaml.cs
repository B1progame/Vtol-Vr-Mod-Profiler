using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using VTOLVRWorkshopProfileSwitcher.ViewModels;

namespace VTOLVRWorkshopProfileSwitcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;

            if (this.FindControl<Grid>("MainContentHost") is { } host)
            {
                host.Opacity = 1;
                if (host.RenderTransform is TranslateTransform transform)
                {
                    transform.Y = 0;
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyDesignPreset(vm.SelectedDesign);
            ApplyIconPreset(vm.SelectedIcon);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedDesign))
        {
            ApplyDesignPreset(vm.SelectedDesign);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedIcon))
        {
            ApplyIconPreset(vm.SelectedIcon);
        }
    }

    private void ApplyDesignPreset(string design)
    {
        var isBlue = design.Equals("STEEL BLUE", StringComparison.OrdinalIgnoreCase);
        Classes.Set("design-blue", isBlue);
        Classes.Set("design-red", !isBlue);
    }

    private void ApplyIconPreset(string iconPreset)
    {
        var fileName = iconPreset.Equals("BLUE GHOST", StringComparison.OrdinalIgnoreCase)
            ? "AppIconBlue.ico"
            : "AppIcon.ico";

        var iconPath = ResolveAssetPath(fileName);
        if (iconPath is null || !File.Exists(iconPath))
        {
            return;
        }

        try
        {
            Icon = new WindowIcon(iconPath);
        }
        catch
        {
            // Ignore icon load failures.
        }
    }

    private static string? ResolveAssetPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", fileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
