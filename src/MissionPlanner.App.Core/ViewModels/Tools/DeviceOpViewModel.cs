using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Reads registers of a sensor on the autopilot's I2C or SPI bus with DEVICE_OP_READ (Mission
/// Planner's Ctrl+J devops screen), for driver debugging.
/// </summary>
public sealed partial class DeviceOpViewModel(AppServices services)
    : ToolPageViewModel("Bus registers",
        "Read registers of a sensor straight from the autopilot's I2C or SPI bus (DEVICE_OP_READ). For driver debugging; the firmware must allow it.",
        services)
{
    public IReadOnlyList<string> BusTypes { get; } = ["I2C", "SPI"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpi))]
    private string _busType = "I2C";

    public bool IsSpi => BusType == "SPI";

    [ObservableProperty]
    private string _spiName = "mpu6000";

    [ObservableProperty]
    private decimal _bus;

    /// <summary>7-bit I2C address, typed in hex or decimal.</summary>
    [ObservableProperty]
    private string _address = "0x1E";

    [ObservableProperty]
    private string _register = "0x00";

    [ObservableProperty]
    private decimal _count = 1;

    [ObservableProperty]
    private string _output = "";

    /// <summary>A byte typed as 0x1E, 1Eh or 30.</summary>
    public static byte? ParseByte(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        if (t.EndsWith('h') || t.EndsWith('H'))
            return byte.TryParse(t[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h2) ? h2 : null;
        return byte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [RelayCommand]
    private async Task ReadAsync()
    {
        if (ParseByte(Address) is not { } address || ParseByte(Register) is not { } reg)
        {
            Report("Enter the address and register as hex (0x1E) or decimal.", true);
            return;
        }

        var spi = IsSpi;
        var name = SpiName.Trim();
        var bus = (byte)Bus;
        var count = (byte)Count;
        byte[] data = [];
        byte result = 0;
        await RunAsync("Reading", (mav, sysid, compid) =>
        {
            result = mav.device_op(sysid, compid, out data, spi ? MAVLink.DEVICE_OP_BUSTYPE.SPI : MAVLink.DEVICE_OP_BUSTYPE.I2C,
                name, bus, address, reg, count);
            return data.Length > 0;
        }, "Read.", "No answer from the autopilot.");

        var where = spi ? $"SPI {name}" : $"I2C bus {bus} 0x{address:X2}";
        var line = data.Length > 0
            ? $"{where} reg 0x{reg:X2}: " + string.Join(' ', data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
            : $"{where} reg 0x{reg:X2}: no answer (result {result})";
        Output = line + Environment.NewLine + Output;
    }
}
