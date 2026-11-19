using System.Globalization;
using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Views.SetupScreens.Hardware;

/// <summary>A labelled <see cref="ChannelPicker"/> (set it as DataContext); hidden without one.</summary>
public sealed partial class ChannelPickerRow : UserControl
{
    public ChannelPickerRow()
    {
        InitializeComponent();
        // a binding that yields null from the start raises no DataContextChanged: start hidden
        IsVisible = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        IsVisible = DataContext is ChannelPicker;
        if (DataContext is ChannelPicker p)
            Hint.Text = $"sets {(p.Suffix == "_OPTION" ? "RCn_OPTION" : "SERVOn_FUNCTION")} = {p.Value.ToString(CultureInfo.InvariantCulture)}";
    }
}
