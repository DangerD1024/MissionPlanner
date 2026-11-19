using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Views.SetupScreens;

public sealed partial class CompassMotView : UserControl
{
    private CompassMotViewModel? _vm;

    public CompassMotView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as CompassMotViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnVmChanged;
        UpdateRating();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // keep the newest message in view
            case nameof(CompassMotViewModel.MessagesText):
                Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
                break;
            case nameof(CompassMotViewModel.Rating):
                UpdateRating();
                break;
        }
    }

    private void UpdateRating()
    {
        var key = _vm?.Rating switch
        {
            InterferenceRating.Good => "Accent",
            InterferenceRating.Acceptable => "Warn",
            InterferenceRating.Bad => "Danger",
            _ => "Text",
        };
        Rating.Bind(TextBlock.ForegroundProperty, Rating.GetResourceObservable(key));
    }
}
