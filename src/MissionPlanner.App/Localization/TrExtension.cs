using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Localization;

/// <summary>
/// <c>Text="{l:Tr 'Load File...'}"</c>: English text shown in the current language, updated live when
/// the language changes. Quote the text; escape a quote inside it as \'.
/// </summary>
public sealed class TrExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public BindingBase ProvideValue(IServiceProvider serviceProvider) => TrBinding.For(Text);
}

/// <summary>
/// <c>Text="{l:TrBind {Binding Title}}"</c>: translates English text that comes from a view-model
/// (menu titles, labels), again when the language changes.
/// </summary>
public sealed class TrBindExtension
{
    public TrBindExtension()
    {
    }

    public TrBindExtension(BindingBase binding) => Binding = binding;

    [AssignBinding]
    public BindingBase? Binding { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider) => TrBinding.Of(Binding ?? throw new InvalidOperationException("TrBind needs a binding"));
}

/// <summary>Bindings that follow <see cref="Localizer.Current"/>, for XAML and code-behind.</summary>
public static class TrBinding
{
    /// <summary>A one-way binding to the translation of fixed English text.</summary>
    public static BindingBase For(string english) =>
        CompiledBinding.Create<Localizer, string>(l => l.Culture, source: Localizer.Current, converter: TextConverter.Instance,
            converterParameter: english, mode: BindingMode.OneWay);

    /// <summary>A one-way binding to the translation of whatever another binding produces.</summary>
    public static BindingBase Of(BindingBase english)
    {
        var multi = new MultiBinding { Converter = TranslateValueConverter.Instance, Mode = BindingMode.OneWay };
        multi.Bindings.Add(english);
        multi.Bindings.Add(CompiledBinding.Create<Localizer, string>(l => l.Culture, source: Localizer.Current, mode: BindingMode.OneWay));
        return multi;
    }

    /// <summary>Binds a property of a control to the translation of fixed English text.</summary>
    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string english) =>
        target.Bind(property, For(english));

    // the language is the bound value; the English text is the parameter
    private sealed class TextConverter : IValueConverter
    {
        public static readonly TextConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Localizer.Current.Tr(parameter as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}

/// <summary>
/// Translates a bound English string: <c>{Binding Status, Converter={x:Static l:TranslateConverter.Instance}}</c>.
/// Use <see cref="TrBindExtension"/> instead where the text must change with the language while shown.
/// </summary>
public sealed class TranslateConverter : IValueConverter
{
    public static readonly TranslateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? Localizer.Current.Tr(s) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

internal sealed class TranslateValueConverter : IMultiValueConverter
{
    public static readonly TranslateValueConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var value = values.Count > 0 ? values[0] : null;
        return value switch
        {
            string s => Localizer.Current.Tr(s),
            null => null,
            _ when value == AvaloniaProperty.UnsetValue || value is BindingNotification => value,
            _ => Localizer.Current.Tr(value.ToString()),
        };
    }
}
