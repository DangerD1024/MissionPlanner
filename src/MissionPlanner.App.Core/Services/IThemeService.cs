namespace MissionPlanner.App.Core.Services;

public sealed record ThemeInfo(string Name, bool IsLight)
{
    public override string ToString() => Name;
}

/// <summary>Colour themes of the app. Implemented by the UI layer; call on the UI thread.</summary>
public interface IThemeService
{
    IReadOnlyList<ThemeInfo> Themes { get; }

    string Current { get; }

    /// <summary>Switches the whole app to a theme and remembers the choice.</summary>
    void Apply(string name);
}

/// <summary>Used where no UI is present (tests): one theme, switching does nothing.</summary>
public sealed class NullThemeService : IThemeService
{
    public static readonly NullThemeService Instance = new();

    public IReadOnlyList<ThemeInfo> Themes { get; } = [new("Dark", false)];

    public string Current => "Dark";

    public void Apply(string name)
    {
    }
}
