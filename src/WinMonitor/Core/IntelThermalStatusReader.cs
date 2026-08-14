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

    // The MSR read is a synchronous driver IOCTL on the poll thread. It is normally microseconds,
    // but nothing in the managed layer can cancel one that does not return, so it runs on a gate
    // that bounds the caller's wait instead. See NativeCallGate for what a timeout costs.
    private const int ReadTimeoutMs = 1000;
    private const int OpenTimeoutMs = 5000;

    private readonly PawnIo _pawnIo = new();
    private readonly NativeCallGate _gate = new("MsrAccess");
    private readonly ulong[] _input = { PackageThermalStatusMsr };
    private readonly ulong[] _output = new ulong[1];
    private readonly Action _readAction;      // cached: the per-tick path stays allocation-free
    private int _lastHr;
    private nuint _lastReturned;

    private IntelThermalStatusReader() => _readAction = ExecuteRead;

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
            // Open and load also cross the driver boundary, on the poll thread during a rescan.
            int hr = 0;
            if (!reader._gate.TryRun(() =>
                {
                    hr = reader._pawnIo.Open();
                    if (hr == 0) hr = reader._pawnIo.Load(module);
                }, OpenTimeoutMs))
            {
                // Still running and still owning the handle: close the gate, leave the handle.
                reader._gate.Dispose();
                return null;
            }
            if (hr != 0)
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
        _lastHr = 0;
        _lastReturned = 0;
        if (!_gate.TryRun(_readAction, ReadTimeoutMs)) return false;
        if (_lastHr != 0 || _lastReturned < 1)
            return false;
        throttling = DecodeStatus(_output[0]);
        return true;
    }

    /// <summary>Runs on the gate's thread; reports through fields so the caller allocates nothing.</summary>
    private void ExecuteRead()
        => _lastHr = _pawnIo.Execute("ioctl_read_msr", _input, _output, out _lastReturned);

    internal static bool DecodeStatus(ulong status)
        => (status & (ThermalStatusMask | ProchotStatusMask)) != 0;

    public void Dispose()
    {
        _gate.Dispose();
        // A wedged call still owns the driver handle; closing it under a live IOCTL is worse than
        // leaving it to process exit.
        if (_gate.Wedged) return;
        _pawnIo.Dispose();
    }
}
