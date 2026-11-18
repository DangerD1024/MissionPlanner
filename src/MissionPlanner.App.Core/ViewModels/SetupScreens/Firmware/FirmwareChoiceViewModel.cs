using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// Pick one of several firmware files that fit the board (Mission Planner's FirmwareSelectionForm),
/// shown as a dialog.
/// </summary>
public sealed partial class FirmwareChoiceViewModel : PageViewModel, IDialogPage
{
    public FirmwareChoiceViewModel(string prompt, IReadOnlyList<FirmwareInfo> choices, string? platform = null)
        : base("Choose firmware")
    {
        Prompt = prompt;
        Options = new FirmwareOptionsViewModel(choices, platform);
        Options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirmwareOptionsViewModel.Selected))
                UploadCommand.NotifyCanExecuteChanged();
        };
    }

    public string Prompt { get; }

    public FirmwareOptionsViewModel Options { get; }

    public FirmwareInfo? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    private bool CanUpload() => Options.Selected != null;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private void Upload()
    {
        Result = Options.Selected?.Info;
        if (Result != null)
            CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
