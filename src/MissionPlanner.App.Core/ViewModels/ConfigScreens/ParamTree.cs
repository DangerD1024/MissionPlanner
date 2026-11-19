using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A node of the parameter tree: a prefix group, or a parameter.</summary>
public sealed class ParamTreeNode
{
    private ParamTreeNode(string label, string? paramName, List<ParamTreeNode> children)
    {
        Label = label;
        ParamName = paramName;
        Children = children;
        Count = paramName != null ? 1 : children.Sum(c => c.Count);
    }

    public static ParamTreeNode Group(string prefix, List<ParamTreeNode> children) => new(prefix, null, children);

    public static ParamTreeNode Param(string name) => new(name, name, []);

    /// <summary>The group's prefix without the trailing '_' ("ATC_RAT"), or the parameter name.</summary>
    public string Label { get; }

    /// <summary>The parameter name; null for a group.</summary>
    public string? ParamName { get; }

    public bool IsGroup => ParamName == null;

    public List<ParamTreeNode> Children { get; }

    /// <summary>Parameters in the node (1 for a parameter).</summary>
    public int Count { get; }

    public override string ToString() => IsGroup ? $"{Label}({string.Join(",", Children)})" : Label;
}

/// <summary>
/// Groups parameter names into a tree by prefix, like Mission Planner's ConfigRawParamsTree:
/// a prefix up to an '_' shared by two or more parameters becomes a group, recursively
/// (ATC → ATC_RAT → ATC_RAT_PIT). A group holding nothing but one sub-group is merged into it.
/// </summary>
public static class ParamTree
{
    public static List<ParamTreeNode> Build(IEnumerable<string> names)
    {
        var sorted = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, NaturalStringComparer.Instance).ToList();
        return Level(sorted, "");
    }

    /// <summary>The next name segment after <paramref name="prefix"/>: up to the next '_' or the end.</summary>
    private static string Segment(string name, string prefix)
    {
        var i = name.IndexOf('_', prefix.Length);
        return i < 0 ? name[prefix.Length..] : name[prefix.Length..i];
    }

    // every name starts with prefix
    private static List<ParamTreeNode> Level(List<string> names, string prefix)
    {
        var nodes = new List<ParamTreeNode>();
        var buckets = new List<(string Segment, List<string> Names)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var seg = Segment(n, prefix);
            if (!index.TryGetValue(seg, out var i))
            {
                i = buckets.Count;
                index[seg] = i;
                buckets.Add((seg, []));
            }

            buckets[i].Names.Add(n);
        }

        foreach (var (seg, bucket) in buckets)
        {
            var groupPrefix = prefix + seg + "_";
            var inside = seg.Length > 0
                ? bucket.Where(n => n.Length > groupPrefix.Length && n.StartsWith(groupPrefix, StringComparison.Ordinal)).ToList()
                : [];

            if (inside.Count < 2)
            {
                nodes.AddRange(bucket.Select(ParamTreeNode.Param));
                continue;
            }

            // a name equal to the prefix (Q_PLT_Y_RATE beside Q_PLT_Y_RATE_TC) stays a parameter of this level
            var grouped = new HashSet<string>(inside, StringComparer.Ordinal);
            nodes.AddRange(bucket.Where(n => !grouped.Contains(n)).Select(ParamTreeNode.Param));

            // ATC holding nothing but ATC_RAT_* shows as ATC_RAT
            var children = Level(inside, groupPrefix);
            nodes.Add(children is [{ IsGroup: true } only] ? only : ParamTreeNode.Group(groupPrefix[..^1], children));
        }

        return nodes;
    }
}

/// <summary>A line of the Full Parameter Tree: a group with an expander, or a parameter row.</summary>
public sealed partial class ParamTreeItem : ObservableObject
{
    public const double IndentStep = 18;

    private ParamTreeItem(string label, int depth, ParamRow? row, IReadOnlyList<ParamTreeItem> children)
    {
        Label = label;
        Depth = depth;
        Row = row;
        Children = children;
        Count = row != null ? 1 : children.Sum(c => c.Count);
    }

    public static ParamTreeItem Group(string label, int depth, IReadOnlyList<ParamTreeItem> children) => new(label, depth, null, children);

    public static ParamTreeItem Param(ParamRow row, int depth) => new(row.Name, depth, row, []);

    /// <summary>The group's prefix, or the parameter name.</summary>
    public string Label { get; }

    public int Depth { get; }

    /// <summary>Width of the indent before the label.</summary>
    public double Indent => Depth * IndentStep;

    /// <summary>The parameter; null for a group.</summary>
    public ParamRow? Row { get; }

    public bool IsGroup => Row == null;

    public bool IsParam => Row != null;

    public IReadOnlyList<ParamTreeItem> Children { get; }

    public int Count { get; }

    /// <summary>Called by the expander; the page updates the visible lines.</summary>
    internal Action<ParamTreeItem>? Toggled { get; set; }

    /// <summary>Whether the group's children are shown.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Parameters matching the search under the group (all of them without a search).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _matchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private bool _isSearching;

    public string CountText => IsSearching && MatchCount != Count ? $"{MatchCount} of {Count}" : Count.ToString();

    [RelayCommand]
    private void Toggle() => Toggled?.Invoke(this);

    public override string ToString() => Label;
}
