using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using log4net;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Controls.ConfigScreens;

/// <summary>
/// Typing values in a parameter grid's "Value" column, shared by the parameter pages: one click
/// on a value starts editing it (F2 and typing work too), the editor starts from the shown
/// value, and the typed text is checked and queued by the page when the edit commits. Clicks
/// on the cell's buttons (documented values, Set bitmask) do not start an edit.
/// </summary>
public static class ParamGridEditing
{
    public const string ValueHeader = "Value";

    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamGridEditing));

    /// <param name="grid">The grid; its "Value" column has a TextBox editing template bound to <see cref="ParamRow.EditText"/>.</param>
    /// <param name="page">The page that checks and queues edits.</param>
    /// <param name="rowOf">The parameter of a grid line (null for lines that are not parameters, e.g. tree groups).</param>
    public static void Attach(DataGrid grid, Func<ParamPageViewModel?> page, Func<object?, ParamRow?> rowOf)
    {
        var clickedValue = false;

        grid.BeginningEdit += (_, e) =>
        {
            var row = rowOf(e.Row.DataContext);
            if (row == null)
            {
                e.Cancel = true;
                return;
            }

            row.EditText = row.ValueText;
        };

        grid.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox box)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
        };

        grid.CellEditEnded += async (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit || rowOf(e.Row.DataContext) is not { } row || page() is not { } vm)
                return;
            try
            {
                await vm.CommitEditAsync(row, row.EditText);
            }
            catch (Exception ex)
            {
                Log.Warn("commit " + row.Name, ex);
                vm.StatusText = ex.Message;
            }
        };

        // the edit starts once the grid has moved its current cell on release
        grid.CellPointerPressed += (_, e) =>
            clickedValue = Equals(e.Column.Header, ValueHeader) &&
                           e.PointerPressedEventArgs.GetCurrentPoint(grid).Properties.IsLeftButtonPressed &&
                           !IsInButton(e.PointerPressedEventArgs.Source);
        grid.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (!clickedValue)
                return;
            clickedValue = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (Equals(grid.CurrentColumn?.Header, ValueHeader))
                    grid.BeginEdit();
            }, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool IsInButton(object? source) =>
        source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null;
}
