using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MissionPlanner.App.Core.ViewModels;

namespace MissionPlanner.App;

/// <summary>
/// Finds the view for a view-model by name, so new pages need no registration:
/// <c>MissionPlanner.App.Core.ViewModels.X.FooViewModel</c> → <c>MissionPlanner.App.Views.X.FooView</c>.
/// A missing view shows a clear placeholder instead of failing.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private const string VmNamespace = "MissionPlanner.App.Core.ViewModels";
    private const string ViewNamespace = "MissionPlanner.App.Views";

    private static readonly ConcurrentDictionary<Type, Type?> Cache = new();

    public bool Match(object? data) => data is PageViewModel;

    public Control? Build(object? param)
    {
        if (param == null)
            return null;

        var vmType = param.GetType();
        var viewType = Cache.GetOrAdd(vmType, Resolve);
        if (viewType != null)
            return (Control)Activator.CreateInstance(viewType)!;

        return new TextBlock
        {
            Text = $"No view for {vmType.Name} (expected {ViewName(vmType)})",
            Margin = new Avalonia.Thickness(20),
        };
    }

    // a view-model without its own view uses its base type's (e.g. both menu screens share one)
    private static Type? Resolve(Type vmType)
    {
        for (var t = vmType; t != null && t != typeof(PageViewModel); t = t.BaseType)
        {
            if (typeof(ViewLocator).Assembly.GetType(ViewName(t)) is { } view)
                return view;
        }

        return null;
    }

    private static string ViewName(Type vmType)
    {
        var name = vmType.FullName!;
        if (name.StartsWith(VmNamespace, StringComparison.Ordinal))
            name = ViewNamespace + name[VmNamespace.Length..];
        if (name.EndsWith("ViewModel", StringComparison.Ordinal))
            name = name[..^"Model".Length];
        return name;
    }
}
