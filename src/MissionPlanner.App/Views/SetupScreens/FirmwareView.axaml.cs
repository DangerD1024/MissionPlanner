using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.SetupScreens;

namespace MissionPlanner.App.Views.SetupScreens;

public sealed partial class FirmwareView : UserControl
{
    private FirmwareViewModel? _vm;

    public FirmwareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as FirmwareViewModel);
    }

    private void Bind(FirmwareViewModel? vm)
    {
        if (_vm != null)
            _vm.LogLines.CollectionChanged -= OnLogChanged;
        _vm = vm;
        if (_vm != null)
            _vm.LogLines.CollectionChanged += OnLogChanged;
    }

    // keep the newest log line in view
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _vm == null || _vm.LogLines.Count == 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { LogLines.Count: > 0 } vm)
                LogList.ScrollIntoView(vm.LogLines.Count - 1);
        }, DispatcherPriority.Background);
    }
}
