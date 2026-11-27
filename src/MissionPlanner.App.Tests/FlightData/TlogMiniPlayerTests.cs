using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>
/// Away from DATA a replayed log keeps its time and controls in view (a bar at the bottom of the window):
/// the MAVLink Inspector or the proximity radar beside the playback, as Mission Planner's floating windows.
/// </summary>
[TestClass]
public class TlogMiniPlayerTests
{
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TheBarShowsAwayFromDataAndKeepsTheTimeMoving()
    {
        var services = TlogTestUi.Services(out var ui);
        var shell = new ShellViewModel(services);
        var player = shell.Tlogs;
        Assert.AreSame(shell.GetPage<FlightDataViewModel>(Screen.Data).Tlogs, player);

        shell.Tick();
        Assert.IsFalse(shell.ShowMiniPlayer, "no log");

        shell.NavigateCommand.Execute(Screen.Config);
        shell.Tick();
        Assert.IsFalse(shell.ShowMiniPlayer, "still no log");

        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("minibar"), 60);
        await player.LoadFileAsync(path);
        try
        {
            Assert.AreEqual(0, ui.Messages.Count, string.Join("; ", ui.Messages));
            shell.Tick();
            Assert.IsTrue(shell.ShowMiniPlayer, "a log plays while CONFIG shows");

            // only the shell ticks the player now (DATA is hidden): the elapsed time still moves
            await TlogTestExtensions.WaitUntilAsync(() =>
            {
                shell.Tick();
                return player.ElapsedText != "00:00";
            });

            player.PlayPauseCommand.Execute(null);
            shell.Tick();
            Assert.IsFalse(player.IsPlaying);
            Assert.IsTrue(shell.ShowMiniPlayer, "a paused log keeps the bar");

            shell.NavigateCommand.Execute(Screen.Data);
            shell.Tick();
            Assert.IsFalse(shell.ShowMiniPlayer, "DATA has the full player");
            shell.NavigateCommand.Execute(Screen.Tools);
            shell.Tick();
            Assert.IsTrue(shell.ShowMiniPlayer);
        }
        finally
        {
            player.StopCommand.Execute(null);
        }

        shell.Tick();
        Assert.IsFalse(shell.ShowMiniPlayer, "the log is closed");
    }
}
