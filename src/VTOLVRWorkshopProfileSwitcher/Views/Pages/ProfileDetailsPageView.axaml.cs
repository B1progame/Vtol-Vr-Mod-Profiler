using Avalonia.Controls;
using Avalonia.Input;
using VTOLVRWorkshopProfileSwitcher.ViewModels.Pages;

namespace VTOLVRWorkshopProfileSwitcher.Views.Pages;

public partial class ProfileDetailsPageView : UserControl
{
    public ProfileDetailsPageView()
    {
        InitializeComponent();
    }

    private void OnLaunchButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is ProfileDetailsPageViewModel vm)
        {
            vm.Shell.SetLaunchButtonHovered(true);
        }
    }

    private void OnLaunchButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ProfileDetailsPageViewModel vm)
        {
            vm.Shell.SetLaunchButtonHovered(false);
        }
    }
}
