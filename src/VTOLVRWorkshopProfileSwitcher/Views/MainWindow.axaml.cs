using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VTOLVRWorkshopProfileSwitcher.ViewModels;

namespace VTOLVRWorkshopProfileSwitcher.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribedViewModel;

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
        }, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyDesignPreset(vm.SelectedDesign);
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
    }

    private void ApplyDesignPreset(string? design)
    {
        var isBlue = string.Equals(design, "STEEL BLUE", StringComparison.OrdinalIgnoreCase);
        Classes.Set("design-blue", isBlue);
        Classes.Set("design-red", !isBlue);
    }

    private void OnLaunchButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        _subscribedViewModel?.SetLaunchButtonHovered(true);
    }

    private void OnLaunchButtonPointerExited(object? sender, PointerEventArgs e)
    {
        _subscribedViewModel?.SetLaunchButtonHovered(false);
    }

    private void OnLaunchFlyoutOpened(object? sender, EventArgs e)
    {
        LaunchArrowButton.Classes.Set("open", true);
        LaunchArrowGlyph.Classes.Set("open", true);
    }

    private void OnLaunchFlyoutClosed(object? sender, EventArgs e)
    {
        LaunchArrowButton.Classes.Set("open", false);
        LaunchArrowGlyph.Classes.Set("open", false);
    }
}
