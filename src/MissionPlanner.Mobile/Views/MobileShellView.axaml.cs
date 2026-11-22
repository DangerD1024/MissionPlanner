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

    private bool? _landscape;

    /// <summary>Landscape: the tabs become a rail on the left, the pages get the full height.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var w = availableSize.Width;
        var h = availableSize.Height;
        if (w > 0 && h > 0 && !double.IsInfinity(w) && !double.IsInfinity(h))
        {
            var landscape = w > h * 1.05;
            if (landscape != _landscape)
            {
                _landscape = landscape;
                ApplyOrientation(landscape);
            }
        }

        return base.MeasureOverride(availableSize);
    }

    private void ApplyOrientation(bool landscape)
    {
        if (Vm != null)
            Vm.IsLandscape = landscape;
        Root.ColumnDefinitions = new ColumnDefinitions(landscape ? "Auto,*" : "*");
        var content = landscape ? 1 : 0;
        foreach (var c in new Control[] { TopBar, ReadyStrip, Pages })
            Grid.SetColumn(c, content);
        foreach (var c in new Control[] { ModeLayer, SheetLayer })
        {
            Grid.SetColumn(c, 0);
            Grid.SetColumnSpan(c, landscape ? 2 : 1);
        }

        if (landscape)
        {
            Grid.SetRow(NavBar, 0);
            Grid.SetRowSpan(NavBar, 4);
            Grid.SetColumn(NavBar, 0);
            NavBar.Width = 80;
            NavBar.BorderThickness = new Thickness(0, 0, 1, 0);
            NavGrid.Columns = 1;
            NavGrid.Rows = 5;
        }
        else
        {
            Grid.SetRow(NavBar, 3);
            Grid.SetRowSpan(NavBar, 1);
            Grid.SetColumn(NavBar, 0);
            NavBar.Width = double.NaN;
            NavBar.BorderThickness = new Thickness(0, 1, 0, 0);
            NavGrid.Columns = 5;
            NavGrid.Rows = 1;
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
