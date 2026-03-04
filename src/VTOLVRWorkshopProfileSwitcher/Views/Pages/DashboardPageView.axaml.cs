using Avalonia.Controls;
using Avalonia.Input;
using VTOLVRWorkshopProfileSwitcher.ViewModels.Pages;

namespace VTOLVRWorkshopProfileSwitcher.Views.Pages;

public partial class DashboardPageView : UserControl
{
    public DashboardPageView()
    {
        InitializeComponent();
    }

    private void OnLaunchButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is DashboardPageViewModel vm)
        {
            vm.Shell.SetLaunchButtonHovered(true);
        }
    }

    private void OnLaunchButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is DashboardPageViewModel vm)
        {
            vm.Shell.SetLaunchButtonHovered(false);
        }
    }
}
