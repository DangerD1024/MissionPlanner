using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using log4net;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Controls.ConfigScreens;

/// <summary>The value cell of the parameter grids; see the XAML for what it shows.</summary>
public sealed partial class ParamValueCell : UserControl
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamValueCell));

    // one value per line: the number, then its label
    private static readonly IDataTemplate ChoiceTemplate = new FuncDataTemplate<ParamChoice>((choice, _) =>
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*") };
        grid.Children.Add(new TextBlock
        {
            Text = choice?.ValueText,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var label = new TextBlock
        {
            Text = choice?.Label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    });

    public ParamValueCell()
    {
        InitializeComponent();
    }

    /// <summary>Lists the documented values under the cell; picking one queues it as an edit.</summary>
    private void OnOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ParamRow row || row.Choices.Count == 0)
            return;

        var current = row.Choices.FirstOrDefault(c => ParamValues.Same(c.Value, row.DisplayValue));
        var list = new ListBox
        {
            Classes = { "choices" },
            ItemsSource = row.Choices,
            ItemTemplate = ChoiceTemplate,
            SelectedItem = current,
            MaxHeight = 420,
            MinWidth = 260,
            MaxWidth = 460,
            Background = Brushes.Transparent,
        };
        var flyout = new Flyout
        {
            Content = list,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        async void Pick()
        {
            if (list.SelectedItem is not ParamChoice choice)
                return;
            flyout.Hide();
            try
            {
                await row.ChooseAsync(choice);
            }
            catch (Exception ex)
            {
                Log.Warn("choose value for " + row.Name, ex);
            }
        }

        // a tap on an entry picks it (also the selected one, which raises no selection change)
        list.Tapped += (_, te) =>
        {
            if ((te.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null)
                Pick();
        };
        list.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                ke.Handled = true;
                Pick();
            }
        };
        flyout.Opened += (_, _) =>
        {
            if (current != null)
                list.ScrollIntoView(current);
            list.Focus();
        };
        flyout.ShowAt(OptionsButton);
    }
}
