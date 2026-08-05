using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.Config;

/// <summary>How a raw EC register (or register pair) is interpreted as a sensor value.</summary>
public enum EcValueKind
{
    RawByte,     // value = reg
    Word,        // value = reg + next reg combined (endianness per BigEndian)
    Percent,     // value = reg (0..100 or 0..255 scaled to %, per Scale)
    RpmDirect,   // value = word RPM as-is
    RpmDivided,  // value = Divisor / word  (some ECs store a period; RPM = k / raw)
}

/// <summary>
/// A user-defined sensor mapped onto one or two EC registers, discovered with the EC Explorer.
/// Because EC register maps are model-specific, definitions normally come from the EC Explorer.
/// A small exact-model catalog may supply a firmware-verified default when available.
/// </summary>
public sealed class EcSensorDef
{
    public bool Enabled { get; set; } = true;
    public int Register { get; set; }             // 0..255
    public string Name { get; set; } = "";        // user label, e.g. "CPU Fan"
    public string? NameKey { get; set; }           // optional Loc key for built-in mappings
    public EcValueKind Kind { get; set; } = EcValueKind.RawByte;
    public bool BigEndian { get; set; }           // for Word/Rpm kinds
    public float Scale { get; set; } = 1f;        // multiply raw
    public float Offset { get; set; }             // add after scale
    public float Divisor { get; set; } = 1000000f; // for RpmDivided (RPM = Divisor / raw)
    public SensorQuantity Quantity { get; set; } = SensorQuantity.Fan;

    /// <summary>Stable sensor id used across the app for this EC sensor.</summary>
    public string SensorId => "/ec/reg/" + Register.ToString("X2") + "/" + Kind;
    public string DisplayName => string.IsNullOrWhiteSpace(NameKey) ? Name : Loc.T(NameKey);

    public EcSensorDef Clone() => (EcSensorDef)MemberwiseClone();

    /// <summary>Converts freshly read EC bytes (indexed by register 0..255) into the sensor value.</summary>
    public float? Compute(byte[] regs, bool[] ok)
    {
        if ((uint)Register > 0xFF || !ok[Register]) return null;
        int lo = regs[Register];

        switch (Kind)
        {
            case EcValueKind.RawByte:
                return lo * Scale + Offset;

            case EcValueKind.Percent:
                return lo * Scale + Offset;

            case EcValueKind.Word:
            case EcValueKind.RpmDirect:
            {
                if (Register >= 0xFF || !ok[Register + 1]) return null;
                int hi = regs[Register + 1];
                int word = BigEndian ? (lo << 8) | hi : (hi << 8) | lo;
                return word * Scale + Offset;
            }

            case EcValueKind.RpmDivided:
            {
                if (Register >= 0xFF || !ok[Register + 1]) return null;
                int hi = regs[Register + 1];
                int word = BigEndian ? (lo << 8) | hi : (hi << 8) | lo;
                if (word <= 0) return 0f; // fan stopped / no period
                return Divisor / word;
            }

            default:
                return lo;
        }
    }

    public SensorCategory Category => Quantity switch
    {
        SensorQuantity.Fan or SensorQuantity.Control => SensorCategory.Fan,
        SensorQuantity.Temperature => SensorCategory.Motherboard,
        _ => SensorCategory.Other,
    };
}

/// <summary>EC monitoring config: a master switch plus the user's discovered sensor definitions.</summary>
public sealed class EcConfig
{
    /// <summary>When true, SensorService reads the EC each tick and surfaces the sensors below.</summary>
    public bool Enabled { get; set; }
    public List<EcSensorDef> Sensors { get; set; } = new();
    /// <summary>
    /// One-time known-model default marker. Once set, deleting/disabling the suggested sensor is
    /// respected and the catalog will not silently add it again.
    /// </summary>
    public string? AppliedDefaultProfile { get; set; }

    public EcConfig Clone()
    {
        var c = new EcConfig
        {
            Enabled = Enabled,
            AppliedDefaultProfile = AppliedDefaultProfile,
        };
        foreach (var s in Sensors) c.Sensors.Add(s.Clone());
        return c;
    }
}
