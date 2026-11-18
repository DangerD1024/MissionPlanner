using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.SetupScreens;

namespace MissionPlanner.App.Views.SetupScreens;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();

        // Enter sends, Up/Down walk the history (tunnel: before the TextBox moves its caret)
        CommandBox.AddHandler(KeyDownEvent, OnCommandKey, RoutingStrategies.Tunnel);

        // keep the newest output in view
        OutputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                Dispatcher.UIThread.Post(() => OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0, DispatcherPriority.Background);
        };
    }

    private TerminalViewModel? Vm => DataContext as TerminalViewModel;

    private void OnCommandKey(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SendCommand.CanExecute(null))
                    vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryUp();
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDown();
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && string.IsNullOrEmpty(CommandBox.SelectedText):
                // Ctrl-C with nothing selected interrupts the remote command, as in a terminal
                if (vm.SendCtrlCCommand.CanExecute(null))
                    vm.SendCtrlCCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
    }
}
