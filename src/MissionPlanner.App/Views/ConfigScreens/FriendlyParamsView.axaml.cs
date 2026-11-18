using Avalonia.Controls;
using Avalonia.Input;

namespace MissionPlanner.App.Views.ConfigScreens;

/// <summary>Standard Params and Advanced Params.</summary>
public sealed partial class FriendlyParamsView : UserControl
{
    public FriendlyParamsView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
    }
}
