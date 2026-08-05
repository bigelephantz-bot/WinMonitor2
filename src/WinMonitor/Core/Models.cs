using System.Globalization;

namespace WinMonitor.Core;

/// <summary>Logical grouping used by the UI and tray configuration.</summary>
public enum SensorCategory
{
    Cpu,
    Gpu,
    Storage,
    Memory,
    Battery,
    Fan,
    Motherboard,
    Other,
}

/// <summary>What kind of value the sensor reports. Temperatures are always °C internally.</summary>
public enum SensorQuantity
{
    Temperature,   // °C
    Fan,           // RPM
    Control,       // % (PWM duty)
    Level,         // % (charge, health, SMART used...)
    Power,         // W
    Data,          // GB (SMART data written/read)
    Voltage,       // V
    Load,          // %
    Frequency,     // MHz
}

/// <summary>Static description of a sensor. Stable across polls; rebuilt on hardware rescan.</summary>
public sealed class SensorDescriptor
{
    public required string Id { get; init; }            // LHM sensor identifier, stable across runs
    public required string HardwareName { get; init; }  // e.g. "13th Gen Intel Core i7-1360P"
    public required string Name { get; init; }          // e.g. "CPU Package"
    public required SensorCategory Category { get; init; }
    public required SensorQuantity Quantity { get; init; }

    /// <summary>Display name honoring user rename override; set by consumers, not by SensorService.</summary>
    public string DisplayName { get; set; } = "";

    public override string ToString() => $"{HardwareName} / {Name} ({Quantity})";
}

/// <summary>One polled value. Value is null when the sensor did not report this tick.</summary>
public readonly struct SensorSnapshot
{
    public required string Id { get; init; }
    public required float? Value { get; init; }
    public required DateTime UtcTimestamp { get; init; }

    public bool HasValue => Value.HasValue && !float.IsNaN(Value.Value);
}

/// <summary>Session (since app start / last reset) statistics for one sensor.</summary>
public sealed class SessionStats
{
    public float Min = float.MaxValue;
    public float Max = float.MinValue;
    public double Sum;
    public long Count;

    public float Avg => Count > 0 ? (float)(Sum / Count) : float.NaN;
    public bool HasData => Count > 0;

    public void Accept(float value)
    {
        if (value < Min) Min = value;
        if (value > Max) Max = value;
        Sum += value;
        Count++;
    }

    public void Reset()
    {
        Min = float.MaxValue;
        Max = float.MinValue;
        Sum = 0;
        Count = 0;
    }
}

public readonly record struct TimedValue(DateTime Utc, float Value);

/// <summary>Fixed-capacity ring buffer for history samples. Not thread-safe; callers lock.</summary>
public sealed class RingBuffer<T> where T : struct
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        _items = new T[capacity];
    }

    public int Count => _count;
    public int Capacity => _items.Length;

    public T this[int index] => _items[(_start + index) % _items.Length];

    public void Add(T item)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = item;
            _count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>Copies current content oldest-first into a new array.</summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        for (int i = 0; i < _count; i++)
            result[i] = this[i];
        return result;
    }
}

/// <summary>Ids of synthetic sensors the app generates itself (not from LHM/WMI/EC).</summary>
public static class WellKnown
{
    /// <summary>CPU thermal-throttle state: value 0 = false, 1 = true.</summary>
    public const string ThrottleSensorId = "/throttle/cpu";
}

/// <summary>Display-time unit formatting. All storage/math is metric (°C, RPM, W, %).</summary>
public static class Units
{
    public static bool UseFahrenheit;

    public static float ToDisplayTemp(float celsius) => UseFahrenheit ? celsius * 9f / 5f + 32f : celsius;

    public static string TempSuffix => UseFahrenheit ? "°F" : "°C";

    /// <summary>"45°C" / "113°F"; integer precision — this is a monitor, not a lab instrument.</summary>
    public static string FormatTemp(float? celsius)
    {
        if (celsius is not { } c || float.IsNaN(c)) return "—";
        return ((int)MathF.Round(ToDisplayTemp(c))).ToString(CultureInfo.InvariantCulture) + TempSuffix;
    }

    /// <summary>Short numeric for tray icons: no unit, rounded integer in the display scale.</summary>
    public static string FormatTempShort(float? celsius)
    {
        if (celsius is not { } c || float.IsNaN(c)) return "—";
        return ((int)MathF.Round(ToDisplayTemp(c))).ToString(CultureInfo.InvariantCulture);
    }

    public static string Format(SensorQuantity quantity, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return "—";
        return quantity switch
        {
            SensorQuantity.Temperature => FormatTemp(v),
            SensorQuantity.Fan => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + " RPM",
            SensorQuantity.Control => v.ToString("0", CultureInfo.InvariantCulture) + " %",
            SensorQuantity.Level => v.ToString("0.#", CultureInfo.InvariantCulture) + " %",
            SensorQuantity.Load => v.ToString("0", CultureInfo.InvariantCulture) + " %",
            SensorQuantity.Power => v.ToString("0.0", CultureInfo.InvariantCulture) + " W",
            SensorQuantity.Frequency => v >= 1000f
                ? (v / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
                : v.ToString("0", CultureInfo.InvariantCulture) + " MHz",
            SensorQuantity.Voltage => v.ToString("0.00", CultureInfo.InvariantCulture) + " V",
            SensorQuantity.Data => v >= 1024f
                ? (v / 1024f).ToString("0.00", CultureInfo.InvariantCulture) + " TB"
                : v.ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            _ => v.ToString("0.#", CultureInfo.InvariantCulture),
        };
    }
}
