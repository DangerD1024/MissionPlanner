using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

public enum MotorSpinResult
{
    Spinning,

    /// <summary>The autopilot refused the motor test (armed, safety switch, not landed, RC not calibrated...).</summary>
    Denied,

    /// <summary>No answer or the link failed.</summary>
    Error,
}

/// <summary>
/// Keeps one motor spinning slowly with DO_MOTOR_TEST until told to stop: the test runs with a
/// short timeout and is re-sent while it should continue, so the motor stops by itself within
/// <see cref="TestTimeoutSeconds"/> if the link or the app goes away. Only one motor spins at a
/// time; switching stops the previous one first (ArduCopter leaves a motor at its last output when
/// the test moves to another, so the test is ended rather than redirected).
/// </summary>
public sealed class MotorSpinner : IAsyncDisposable
{
    /// <summary>DO_MOTOR_TEST timeout of each command (seconds).</summary>
    public const float TestTimeoutSeconds = 1.5f;

    /// <summary>The highest throttle the wizard allows (percent).</summary>
    public const float MaxThrottle = 20;

    private static readonly ILog Log = LogManager.GetLogger(typeof(MotorSpinner));

    private readonly ICalibrationLink _link;
    private readonly TimeSpan _keepAlive;
    private readonly TimeSpan _switchPause;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private volatile int _testOrder;

    public MotorSpinner(ICalibrationLink link, TimeSpan? keepAlive = null, TimeSpan? switchPause = null)
    {
        _link = link;
        _keepAlive = keepAlive ?? TimeSpan.FromMilliseconds(500);
        _switchPause = switchPause ?? TimeSpan.FromMilliseconds(300);
    }

    /// <summary>The test order (DO_MOTOR_TEST param1) of the spinning motor, or null.</summary>
    public int? TestOrder => _testOrder > 0 ? _testOrder : null;

    public bool IsSpinning => _testOrder > 0;

    /// <summary>The keep-alive failed and the spin ended (raised on a worker thread).</summary>
    public event EventHandler<string>? Failed;

    public static float ClampThrottle(double percent) => (float)Math.Clamp(percent, 0, MaxThrottle);

    /// <summary>Spins the motor with this test order (stopping another one first).</summary>
    public async Task<MotorSpinResult> SpinAsync(int testOrder, double throttlePercent)
    {
        var throttle = ClampThrottle(throttlePercent);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_testOrder > 0)
            {
                var same = _testOrder == testOrder;
                await StopCoreAsync().ConfigureAwait(false);
                if (!same)
                    await Task.Delay(_switchPause).ConfigureAwait(false);
            }

            bool ok;
            try
            {
                ok = await SendAsync(testOrder, throttle, TestTimeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("motor spin " + testOrder, ex);
                return MotorSpinResult.Error;
            }

            if (!ok)
                return MotorSpinResult.Denied;

            _testOrder = testOrder;
            var cts = new CancellationTokenSource();
            _loop = cts;
            _loopTask = Task.Run(() => KeepAliveAsync(testOrder, throttle, cts.Token));
            return MotorSpinResult.Spinning;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the spinning motor (throttle 0, timeout 0 ends the test). Safe to call at any time.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends any motor test, spinning or not (the Stop button).</summary>
    public async Task StopAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var order = _testOrder > 0 ? _testOrder : 1;
            await StopCoreAsync().ConfigureAwait(false);
            await SendStopAsync(order).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var order = _testOrder;
        _testOrder = 0;
        var loop = Interlocked.Exchange(ref _loop, null);
        loop?.Cancel();
        if (_loopTask is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _loopTask = null;
        }

        loop?.Dispose();
        if (order > 0)
            await SendStopAsync(order).ConfigureAwait(false);
    }

    private async Task SendStopAsync(int order)
    {
        try
        {
            await SendAsync(order, 0, 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("motor stop", ex);
        }
    }

    private async Task KeepAliveAsync(int testOrder, float throttle, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_keepAlive, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool ok;
            try
            {
                ok = await SendAsync(testOrder, throttle, TestTimeoutSeconds, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("motor keep-alive", ex);
                ok = false;
            }

            failures = ok ? 0 : failures + 1;
            if (failures >= 2 && !ct.IsCancellationRequested)
            {
                // the test has timed out on the vehicle by now
                if (Interlocked.CompareExchange(ref _testOrder, 0, testOrder) == testOrder)
                    Failed?.Invoke(this, "The autopilot stopped accepting the motor test, so the motor has stopped.");
                return;
            }
        }
    }

    /// <summary>DO_MOTOR_TEST: p1 test order, p2 throttle type (percent), p3 throttle, p4 timeout, p5 count (0 = this motor only).</summary>
    private async Task<bool> SendAsync(int testOrder, float throttle, float timeout, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _link.CommandAsync(MAVLink.MAV_CMD.DO_MOTOR_TEST, testOrder,
                (float)MAVLink.MOTOR_TEST_THROTTLE_TYPE.MOTOR_TEST_THROTTLE_PERCENT, throttle, timeout, 0).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
        _commands.Dispose();
    }
}
