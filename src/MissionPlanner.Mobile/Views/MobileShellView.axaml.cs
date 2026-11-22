using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.Mobile;
using MissionPlanner.Mobile.Services;

namespace MissionPlanner.Mobile.Views;

public sealed partial class MobileShellView : UserControl
{
    private SheetHost? _sheets;
    private TopLevel? _top;

    public MobileShellView()
    {
        InitializeComponent();
        Slider.Confirmed += (_, _) => (SheetBody.DataContext as Sheet)?.Accept();
        PromptBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && SheetBody.DataContext is Sheet s)
                s.Accept();
        };
    }

    /// <summary>The dialog service's sheets, shown over everything.</summary>
    public SheetHost? Sheets
    {
        get => _sheets;
        set
        {
            if (_sheets != null)
                _sheets.PropertyChanged -= OnSheetChanged;
            _sheets = value;
            if (_sheets != null)
                _sheets.PropertyChanged += OnSheetChanged;
        }
    }

    private MobileShellViewModel? Vm => DataContext as MobileShellViewModel;

    private void OnSheetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SheetHost.Current))
            return;
        var sheet = _sheets?.Current;
        SheetBody.DataContext = sheet;
        SheetLayer.IsVisible = sheet != null;
        Slider.Reset();
        if (sheet == null)
            return;
        Slider.Accent = sheet.IsDanger
            ? (Avalonia.Media.IBrush)this.FindResource("Bad")!
            : (Avalonia.Media.IBrush)this.FindResource("Accent")!;
        if (sheet.IsPrompt)
        {
            Avalonia.Input.TextInput.TextInputOptions.SetContentType(PromptBox, sheet.IsNumeric
                ? Avalonia.Input.TextInput.TextInputContentType.Number
                : Avalonia.Input.TextInput.TextInputContentType.Normal);
            Dispatcher.UIThread.Post(() =>
            {
                PromptBox.Focus();
                PromptBox.SelectAll();
            }, DispatcherPriority.Background);
        }
    }

    private void OnSheetScrimPressed(object? sender, PointerPressedEventArgs e) => _sheets?.CancelOpen();

    private void OnModeScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null)
            Vm.IsModePickerOpen = false;
    }

    private void OnToastPressed(object? sender, PointerPressedEventArgs e) => Vm?.DismissToastCommand.Execute(null);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top != null)
            _top.BackRequested += OnBack;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top != null)
            _top.BackRequested -= OnBack;
        _top = null;
    }

    /// <summary>Android's back: closes a sheet, then the mode picker, then returns to Fly.</summary>
    private void OnBack(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (_sheets?.CancelOpen() == true)
            e.Handled = true;
        else if (vm is { IsModePickerOpen: true })
        {
            vm.IsModePickerOpen = false;
            e.Handled = true;
        }
        else if (vm is { IsSetup: true } && (vm.Setup.Parameters.IsEditing || vm.Setup.Back()))
        {
            if (vm.Setup.Parameters.IsEditing)
                vm.Setup.Parameters.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm is { HasGoHere: true })
        {
            vm.CancelGoHereCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm != null && vm.Tab != MobileTab.Fly)
        {
            vm.Tab = MobileTab.Fly;
            e.Handled = true;
        }
    }
}
