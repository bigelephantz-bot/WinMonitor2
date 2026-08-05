using LibreHardwareMonitor.Hardware;

namespace WinMonitor.Core;

/// <summary>
/// Allocation-free polling wrapper for Intel's package thermal-status MSR. It reuses the
/// read-only IntelMSR PawnIO module embedded in LibreHardwareMonitor.
/// </summary>
internal sealed class IntelThermalStatusReader : IDisposable
{
    private const string ModuleResource = "LibreHardwareMonitor.Resources.PawnIo.IntelMSR.bin";
    private const ulong PackageThermalStatusMsr = 0x1B1;
    private const ulong ThermalStatusMask = 1UL << 0;
    private const ulong ProchotStatusMask = 1UL << 2;

    private readonly PawnIo _pawnIo = new();
    private readonly ulong[] _input = { PackageThermalStatusMsr };
    private readonly ulong[] _output = new ulong[1];

    private IntelThermalStatusReader()
    {
    }

    public static IntelThermalStatusReader? TryCreate()
    {
        var reader = new IntelThermalStatusReader();
        try
        {
            using Stream? stream = typeof(Computer).Assembly.GetManifestResourceStream(ModuleResource);
            if (stream is null || stream.Length <= 0 || stream.Length > int.MaxValue)
            {
                reader.Dispose();
                return null;
            }

            var module = new byte[(int)stream.Length];
            stream.ReadExactly(module);
            if (reader._pawnIo.Open() != 0 || reader._pawnIo.Load(module) != 0)
            {
                reader.Dispose();
                return null;
            }
            return reader;
        }
        catch
        {
            reader.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads the current status. Sticky event-log bits are intentionally ignored because the
    /// synthetic sensor represents whether throttling is active now, not whether it ever occurred.
    /// </summary>
    public bool TryRead(out bool throttling)
    {
        throttling = false;
        _output[0] = 0;
        int hr = _pawnIo.Execute("ioctl_read_msr", _input, _output, out nuint returned);
        if (hr != 0 || returned < 1)
            return false;
        throttling = DecodeStatus(_output[0]);
        return true;
    }

    internal static bool DecodeStatus(ulong status)
        => (status & (ThermalStatusMask | ProchotStatusMask)) != 0;

    public void Dispose() => _pawnIo.Dispose();
}
