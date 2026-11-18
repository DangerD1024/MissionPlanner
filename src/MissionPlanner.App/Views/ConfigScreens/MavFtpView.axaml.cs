using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class MavFtpView : UserControl
{
    private MavFtpViewModel? _vm;

    public MavFtpView()
    {
        InitializeComponent();

        Files.SelectionChanged += (_, _) => _vm?.SetSelection(Files.SelectedItems.OfType<FtpEntry>());
        Files.DoubleTapped += OnDoubleTapped;
        Files.Sorting += OnSorting;
        Files.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Files.AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MavFtpViewModel;
            if (_vm != null)
                _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateSortGlyphs();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MavFtpViewModel.SortColumn) or nameof(MavFtpViewModel.SortDescending))
            UpdateSortGlyphs();
    }

    // the view-model sorts (folders stay on top), the grid only reports the click
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_vm != null && e.Column.Tag is string tag && Enum.TryParse<FtpSortColumn>(tag, out var column))
            _vm.SortBy(column);
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in Files.Columns)
        {
            if (column.Tag is not string tag || !Enum.TryParse<FtpSortColumn>(tag, out var c))
                continue;
            var title = tag == nameof(FtpSortColumn.Kind) ? "Type" : tag;
            column.Header = _vm != null && _vm.SortColumn == c ? title + (_vm.SortDescending ? "  ▼" : "  ▲") : title;
        }
    }

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm == null || Files.SelectedItem is not FtpEntry entry || !_vm.IsIdle)
            return;
        // ignore double-clicks on the header
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>(includeSelf: true) == null)
            return;
        await _vm.OpenEntryAsync(entry);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _vm is { IsIdle: true, SelectedFolder: not null } && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not { IsIdle: true })
            return;
        var files = e.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>()
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (files is { Count: > 0 })
            await _vm.UploadFilesAsync(files);
    }
}
