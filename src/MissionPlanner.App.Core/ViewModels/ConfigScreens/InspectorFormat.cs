using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>One field of a MAVLink message type, with what the inspector needs to show it.</summary>
public sealed class InspectorField
{
    internal InspectorField(FieldInfo info)
    {
        Info = info;
        Name = info.Name;
        var type = info.FieldType;
        IsArray = type.IsArray;
        var element = IsArray ? type.GetElementType()! : type;
        IsText = IsArray && element == typeof(byte) && InspectorFormat.CharFieldNames.Contains(Name);
        IsNumeric = !IsText && InspectorFormat.IsNumericType(element);
        IsUnixTime = !IsArray && Name == "time_unix_usec" && type == typeof(ulong);
        ArrayLength = IsArray ? info.GetCustomAttribute<MarshalAsAttribute>()?.SizeConst ?? 0 : 0;
        // the generator writes units as "[rad]"
        Units = (info.GetCustomAttribute<MAVLink.Units>()?.Unit ?? "").Trim().TrimStart('[').TrimEnd(']');
        Description = info.GetCustomAttribute<MAVLink.Description>()?.Text ?? "";
        TypeName = (IsText ? "char" : InspectorFormat.WireTypeName(element)) + (IsArray ? $"[{(ArrayLength > 0 ? ArrayLength : "")}]" : "");
    }

    public FieldInfo Info { get; }

    public string Name { get; }

    /// <summary>MAVLink's own spelling: uint8_t, float, char[16], int16_t[4]...</summary>
    public string TypeName { get; }

    public string Units { get; }

    public string Description { get; }

    public bool IsArray { get; }

    public int ArrayLength { get; }

    /// <summary>A char[] field (param_id, text, ...): shown as text.</summary>
    public bool IsText { get; }

    /// <summary>A number or an array of numbers: can be graphed.</summary>
    public bool IsNumeric { get; }

    public bool IsUnixTime { get; }

    public object? GetValue(object data) => Info.GetValue(data);
}

/// <summary>Field reflection (cached per message type) and value formatting for the MAVLink inspector.</summary>
public static class InspectorFormat
{
    /// <summary>
    /// Field names declared as char[] in the MAVLink XML; the generated structs hold them as
    /// byte[], so the name is the only clue. (A few uint8_t[] fields share a name; text is only
    /// shown when the bytes are printable.)
    /// </summary>
    internal static readonly HashSet<string> CharFieldNames = new(StringComparer.Ordinal)
    {
        "ar_c", "band_str", "busname", "c1", "c2", "callsign", "cam_definition_uri", "custom_name", "description",
        "device_name", "error", "file_url", "flight_id", "key", "login", "manufacture_date", "model_name", "mode_name",
        "mode_str", "name", "operator_id", "param_id", "param_value", "passkey", "password", "registration", "s",
        "serial_number", "ssid", "text", "tune", "tune2", "uri", "value", "vendor_name",
    };

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<InspectorField>> FieldsByType = new();
    private static readonly IReadOnlyList<InspectorField> NoFields = [];

    /// <summary>The fields of a message id in MAVLink XML order; empty for unknown ids.</summary>
    public static IReadOnlyList<InspectorField> FieldsOf(uint msgid)
    {
        var type = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(msgid).type;
        return type == null ? NoFields : FieldsOf(type);
    }

    public static IReadOnlyList<InspectorField> FieldsOf(Type messageType) => FieldsByType.GetOrAdd(messageType, LoadFields);

    private static IReadOnlyList<InspectorField> LoadFields(Type type)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

        // the structs are laid out in wire order (largest first); the XML order is how the
        // message is documented, and PopulateXMLOrder's parameters spell it out
        var xmlOrder = type.GetMethod("PopulateXMLOrder", BindingFlags.Public | BindingFlags.Static)?
            .GetParameters().Select(p => p.Name ?? "").ToList();
        IEnumerable<FieldInfo> ordered = fields;
        if (xmlOrder is { Count: > 0 })
        {
            ordered = fields.OrderBy(f =>
            {
                var i = xmlOrder.IndexOf(f.Name);
                return i < 0 ? int.MaxValue : i;
            });
        }

        return ordered.Select(f => new InspectorField(f)).ToList();
    }

    internal static bool IsNumericType(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) ||
        t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t.IsEnum;

    internal static string WireTypeName(Type t) => t switch
    {
        _ when t == typeof(byte) => "uint8_t",
        _ when t == typeof(sbyte) => "int8_t",
        _ when t == typeof(ushort) => "uint16_t",
        _ when t == typeof(short) => "int16_t",
        _ when t == typeof(uint) => "uint32_t",
        _ when t == typeof(int) => "int32_t",
        _ when t == typeof(ulong) => "uint64_t",
        _ when t == typeof(long) => "int64_t",
        _ when t == typeof(float) => "float",
        _ when t == typeof(double) => "double",
        _ => t.Name,
    };

    /// <summary>The field of <paramref name="data"/> as the inspector shows it.</summary>
    public static string FormatField(InspectorField field, object? data)
    {
        if (data == null)
            return "";
        object? value;
        try
        {
            value = field.GetValue(data);
        }
        catch (Exception)
        {
            return "?";
        }

        return FormatValue(field, value);
    }

    public static string FormatValue(InspectorField field, object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case byte[] bytes when field.IsText:
                return TryText(bytes) ?? JoinList(bytes);
            case ulong usec when field.IsUnixTime:
                return FormatUnixTime(usec);
            case Array array:
                return JoinList(array);
            default:
                return FormatScalar(value);
        }
    }

    /// <summary>Text up to the first NUL, or null when the bytes are not printable text.</summary>
    public static string? TryText(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
            end = bytes.Length;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, 0, end);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // nothing after the terminator but padding, and no control characters before it
        for (var i = end; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
                return null;
        }

        return text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? null : text;
    }

    public static string FormatScalar(object value) => value switch
    {
        float f => float.IsFinite(f) ? f.ToString("G7", CultureInfo.InvariantCulture) : f.ToString(CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d.ToString("G10", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Microseconds since 1970 as a UTC time (zero stays zero: the sender has no clock).</summary>
    public static string FormatUnixTime(ulong usec)
    {
        if (usec == 0)
            return "0";
        if (usec >= (ulong)((DateTime.MaxValue - DateTime.UnixEpoch).Ticks / 10))
            return usec.ToString(CultureInfo.InvariantCulture);
        try
        {
            var time = DateTime.UnixEpoch.AddTicks((long)(usec * 10));
            return time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";
        }
        catch (ArgumentOutOfRangeException)
        {
            return usec.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string JoinList(IEnumerable values)
    {
        var sb = new StringBuilder();
        foreach (var v in values)
        {
            if (sb.Length > 0)
                sb.Append(',');
            sb.Append(v == null ? "" : FormatScalar(v));
        }

        return sb.ToString();
    }

    /// <summary>The numbers in a field (one for a scalar, one per element for an array); false if it holds none.</summary>
    public static bool TryGetNumbers(InspectorField field, object? data, List<double> into)
    {
        into.Clear();
        if (data == null || !field.IsNumeric)
            return false;
        object? value;
        try
        {
            value = field.GetValue(data);
        }
        catch (Exception)
        {
            return false;
        }

        switch (value)
        {
            case null:
                return false;
            case Array array:
                foreach (var v in array)
                {
                    if (v is IConvertible c)
                        into.Add(c.ToDouble(CultureInfo.InvariantCulture));
                }

                break;
            case IConvertible c:
                into.Add(c.ToDouble(CultureInfo.InvariantCulture));
                break;
        }

        return into.Count > 0;
    }

    /// <summary>A graphed number: whole values without decimals, others to 7 significant digits.</summary>
    public static string FormatNumber(double v) =>
        Math.Abs(v) < 1e15 && Math.Round(v) == v
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("G7", CultureInfo.InvariantCulture);

    public static string FormatRate(double hz) => hz.ToString(hz < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " Hz";

    public static string FormatBytesPerSecond(double bps) =>
        bps < 1000
            ? bps.ToString("0", CultureInfo.InvariantCulture) + " B/s"
            : (bps / 1000).ToString(bps < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " kB/s";
}
