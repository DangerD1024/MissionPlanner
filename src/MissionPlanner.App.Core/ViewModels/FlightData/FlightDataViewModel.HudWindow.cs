using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// The HUD in its own window (another monitor, fullscreen), and keeping the HUD alive while the
/// DATA screen is not shown: the shell keeps ticking this page while something outside it (the HUD
/// window, the 3D view, a stream) still needs the HUD and the vehicle pose.
/// </summary>
public sealed partial class FlightDataViewModel
{
    public const string HudDetachedKey = "next_hud_detached";
    public const string HudWindowKey = "next_hud_window";

    private readonly HashSet<string> _backgroundUsers = new(StringComparer.Ordinal);

    /// <summary>The HUD is shown in its own window instead of above the tabs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsBackgroundTick))]
    private bool _isHudDetached = Settings.Instance.GetBoolean(HudDetachedKey, false);

    partial void OnIsHudDetachedChanged(bool value)
    {
        Settings.Instance[HudDetachedKey] = value.ToString(CultureInfo.InvariantCulture);
        UseInBackground("hud-window", value);
    }

    [RelayCommand]
    private void DetachHud() => IsHudDetached = true;

    [RelayCommand]
    private void AttachHud() => IsHudDetached = false;

    /// <summary>Where the HUD window was ("x,y,width,height,fullscreen", physical pixels); empty for the default.</summary>
    public string HudWindowPlacement
    {
        get => Settings.Instance[HudWindowKey] ?? "";
        set => Settings.Instance[HudWindowKey] = value;
    }

    /// <summary>Something outside the DATA screen needs <see cref="Hud"/> and <see cref="TelemetryUpdated"/>.</summary>
    public bool NeedsBackgroundTick => _backgroundUsers.Count > 0;

    /// <summary>Registers (or releases) a user that needs the HUD updated while the DATA screen is hidden.</summary>
    public void UseInBackground(string user, bool on)
    {
        var changed = on ? _backgroundUsers.Add(user) : _backgroundUsers.Remove(user);
        if (changed)
            OnPropertyChanged(nameof(NeedsBackgroundTick));
    }

    /// <summary>
    /// The shell calls this at its tick rate while another screen is shown and
    /// <see cref="NeedsBackgroundTick"/> is set: only the HUD (no map, tiles or tabs).
    /// </summary>
    public void BackgroundTick()
    {
        if (_shown)
            return;
        var s = Services.Session;
        var cs = s.Cs;
        if (!ReferenceEquals(cs, _cs))
            OnVehicleChanged(cs);
        UpdateHud(s, cs);
        TelemetryUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void InitHudWindow()
    {
        if (IsHudDetached)
            UseInBackground("hud-window", true);
    }
}
