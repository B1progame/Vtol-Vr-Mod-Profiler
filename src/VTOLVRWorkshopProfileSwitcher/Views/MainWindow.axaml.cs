using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    private void OnModCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ModItemViewModel mod)
        {
            return;
        }

        if (IsTapFromInteractiveControl(e.Source))
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.OpenModWorkshopPage(mod);
        }
    }

    private static bool IsTapFromInteractiveControl(object? source)
    {
        if (source is not StyledElement start)
        {
            return false;
        }

        StyledElement? current = start;
        while (current is not null)
        {
            if (current is Button || current is ToggleSwitch || current is CheckBox)
            {
                return true;
            }

            current = current.Parent as StyledElement;
        }

        return false;
    }
}
