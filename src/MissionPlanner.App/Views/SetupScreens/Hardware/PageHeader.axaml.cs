using Avalonia;
using Avalonia.Controls;

namespace MissionPlanner.App.Views.SetupScreens.Hardware;

/// <summary>Title, description, status and reboot banner of a hardware setup page.</summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Description));

    public PageHeader()
    {
        InitializeComponent();
        DescriptionText.IsVisible = false;
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DescriptionProperty)
        {
            DescriptionText.Text = Description;
            DescriptionText.IsVisible = !string.IsNullOrEmpty(Description);
        }
    }
}
