using System.Reflection;

namespace MissionPlanner.App.Core;

/// <summary>The app's version as set in src/Directory.Build.props (MissionPlannerVersion), e.g. "2.0.1b".</summary>
public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "";

    /// <summary>"Mission Planner 2.0.1b".</summary>
    public static string ProductName => "Mission Planner " + Display;
}
