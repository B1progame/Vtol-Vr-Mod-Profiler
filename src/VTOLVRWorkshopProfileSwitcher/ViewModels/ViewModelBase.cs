using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VTOLVRWorkshopProfileSwitcher.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    public virtual void Dispose()
    {
    }
}
