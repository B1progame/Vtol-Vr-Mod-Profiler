using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.ViewModels;

public sealed partial class ProfileItemViewModel : ObservableObject
{
    public ModProfile Source { get; }

    public string Name => Source.Name;
    public string Notes => Source.Notes;
    public string Summary => $"{Source.EnabledMods.Count} enabled";

    public ObservableCollection<string> EnabledMods { get; }

    public ProfileItemViewModel(ModProfile source)
    {
        Source = source;
        EnabledMods = new ObservableCollection<string>(source.EnabledMods);
    }
}
