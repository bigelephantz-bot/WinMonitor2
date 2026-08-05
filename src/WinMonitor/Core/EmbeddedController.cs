using System.Diagnostics;

namespace WinMonitor.Core;

/// <summary>
/// Read-only access to the ACPI Embedded Controller (EC) via PawnIO's signed LpcACPIEC module.
///
/// LG gram (and many ultrabooks) route fan tacho / thermal data through the EC rather than a
/// standard Super-I/O chip, so LibreHardwareMonitor sees no fan. The EC exposes a 256-byte
/// register space read through the ACPI hardware protocol on ports 0x66 (status/command) and
/// 0x62 (data). We NEVER write to the EC — writing wrong registers can affect fan/thermal
/// behavior — so this class only implements the read path.
///
/// All access is serialized on the shared "Global\Access_EC" mutex (create-or-open, the
/// WinRing0 / LibreHardwareMonitor convention) so we never race the Windows ACPI driver, which
/// also drives these ports on GPE/EC-query events.
///
/// Every block read carries a small total-time budget: the EC can wedge (ACPI driver contention,
/// S3/Modern-Standby resume), and an unbounded busy-spin would otherwise freeze the poll thread
/// or the UI. When the budget is exhausted the remaining registers are simply reported as
/// "not read" (ok=false → the sensor shows "—") rather than stalling.
/// </summary>
public sealed class EmbeddedController : IDisposable
{
    // ACPI EC interface (ACPI spec §12).
    private const int EC_DATA = 0x62;   // read/write data register
    private const int EC_SC = 0x66;     // status (read) / command (write) register
    private const byte RD_EC = 0x80;    // "read EC" command
    private const byte STATUS_OBF = 0x01; // output buffer full (data ready to read)
    private const byte STATUS_IBF = 0x02; // input buffer full (EC busy)

    private const int WaitStatusMaxMs = 10;      // per-handshake-step ceiling (was 100)
    private const int DefaultBudgetMs = 40;      // per block-read total budget (poll thread)
    private const int ExplorerBudgetMs = 250;    // longer budget for the off-thread EC Explorer dump

    private const string ModuleFileName = "LpcACPIEC.bin";

    private readonly object _lock = new();
    private PawnIo? _pawn;
    private Mutex? _ecMutex;
    private bool _loaded;
    private bool _disposed;

    public bool Available { get; private set; }

    /// <summary>Human-readable reason Available is false, for the UI. Null when available.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Attempts to bring the EC online. Idempotent. Returns Available. Safe to call when not
    /// elevated / PawnIO missing — it just reports the reason and leaves Available=false.
    /// </summary>
    public bool Initialize()
    {
        lock (_lock)
        {
            if (_loaded) return Available;
            _loaded = true;

            if (!PawnIo.LibraryPresent)
            {
                UnavailableReason = "pawnio_missing";
                return false;
            }

            string modulePath = Path.Combine(AppContext.BaseDirectory, "pawnio", ModuleFileName);
            if (!File.Exists(modulePath))
            {
                // Also accept a copy next to the exe.
                string alt = Path.Combine(AppContext.BaseDirectory, ModuleFileName);
                if (File.Exists(alt)) modulePath = alt;
                else { UnavailableReason = "module_missing"; return false; }
            }

            byte[] blob;
            try { blob = File.ReadAllBytes(modulePath); }
            catch { UnavailableReason = "module_unreadable"; return false; }

            var pawn = new PawnIo();
            int hr = pawn.Open();
            if (hr != 0)
            {
                pawn.Dispose();
                // 0x80070005 = E_ACCESSDENIED (not elevated / driver not permitting).
                UnavailableReason = hr == unchecked((int)0x80070005) ? "not_elevated" : "open_failed";
                return false;
            }

            hr = pawn.Load(blob);
            if (hr != 0)
            {
                pawn.Dispose();
                UnavailableReason = "load_failed";
                return false;
            }

            _pawn = pawn;
            TryOpenEcMutex();
            Available = true;
            UnavailableReason = null;
            return true;
        }
    }

    /// <summary>Reads a single EC register (0..255). Returns null on any failure.</summary>
    public byte? Read(int address)
    {
        if ((uint)address > 0xFF) return null;
        var vals = ReadRegisters(new[] { address }, out bool[] ok, DefaultBudgetMs);
        return ok[0] ? vals[0] : null;
    }

    /// <summary>
    /// Reads a contiguous block [start, start+count). Entries that could not be read within the
    /// budget are 0 with ok=false. One mutex acquisition + a total-time budget cover the whole
    /// block so a wedged EC can never stall the caller for more than <paramref name="budgetMs"/>.
    /// </summary>
    public byte[] ReadBlock(int start, int count, out bool[] ok, int budgetMs = DefaultBudgetMs)
    {
        var data = new byte[count < 0 ? 0 : count];
        ok = new bool[data.Length];
        if (data.Length == 0) return data;

        lock (_lock)
        {
            if (!Available || _pawn is null) return data;
            var sw = Stopwatch.StartNew();
            bool held = AcquireEcMutex(out _);
            try
            {
                DrainObf();
                for (int i = 0; i < data.Length; i++)
                {
                    int addr = start + i;
                    if ((uint)addr > 0xFF) continue;
                    if (sw.ElapsedMilliseconds >= budgetMs) break;   // budget exhausted -> rest stay ok=false
                    if (TryReadRegisterNoMutex(addr, sw, budgetMs, out byte v)) { data[i] = v; ok[i] = true; }
                }
            }
            finally
            {
                if (held) ReleaseEcMutex();
            }
        }
        return data;
    }

    /// <summary>
    /// Reads a sparse set of registers (used by the poll thread: only the handful the user's EC
    /// sensors actually need, instead of a wide contiguous sweep). Values are returned parallel
    /// to <paramref name="addrs"/>; <paramref name="ok"/> flags per-entry success.
    /// </summary>
    public byte[] ReadRegisters(int[] addrs, out bool[] ok, int budgetMs = DefaultBudgetMs)
    {
        var data = new byte[addrs.Length];
        ok = new bool[addrs.Length];
        if (addrs.Length == 0) return data;

        lock (_lock)
        {
            if (!Available || _pawn is null) return data;
            var sw = Stopwatch.StartNew();
            bool held = AcquireEcMutex(out _);
            try
            {
                DrainObf();
                for (int i = 0; i < addrs.Length; i++)
                {
                    int addr = addrs[i];
                    if ((uint)addr > 0xFF) continue;
                    if (sw.ElapsedMilliseconds >= budgetMs) break;
                    if (TryReadRegisterNoMutex(addr, sw, budgetMs, out byte v)) { data[i] = v; ok[i] = true; }
                }
            }
            finally
            {
                if (held) ReleaseEcMutex();
            }
        }
        return data;
    }

    /// <summary>Full 256-byte dump for the EC Explorer (longer budget; call off the UI thread).</summary>
    public byte[] Dump(out bool[] ok) => ReadBlock(0, 256, out ok, ExplorerBudgetMs);

    // ---------- internals (all called under _lock) ----------

    /// <summary>
    /// Discards a byte the EC/ACPI driver may have left in the output buffer before we start our
    /// own handshake, so the first register read cannot return a stale query/burst byte.
    /// </summary>
    private void DrainObf()
    {
        if (ReadPort(EC_SC, out byte status) && (status & STATUS_OBF) != 0)
            ReadPort(EC_DATA, out _);
    }

    /// <summary>ACPI EC read protocol. Assumes the Access_EC mutex is already held.</summary>
    private bool TryReadRegisterNoMutex(int address, Stopwatch sw, int budgetMs, out byte value)
    {
        value = 0;
        if (!WaitStatus(STATUS_IBF, desiredSet: false, sw, budgetMs)) return false; // EC not busy
        if (!WritePort(EC_SC, RD_EC)) return false;                                  // command: read
        if (!WaitStatus(STATUS_IBF, desiredSet: false, sw, budgetMs)) return false;
        if (!WritePort(EC_DATA, (byte)address)) return false;                        // send address
        if (!WaitStatus(STATUS_OBF, desiredSet: true, sw, budgetMs)) return false;   // wait for data
        if (!ReadPort(EC_DATA, out value)) return false;
        return true;
    }

    /// <summary>
    /// Polls the EC status port until the flag reaches the desired state. Bounded by both a
    /// short per-step ceiling and the caller's overall budget, so it can never busy-spin long.
    /// </summary>
    private bool WaitStatus(byte flag, bool desiredSet, Stopwatch overall, int budgetMs)
    {
        long stepDeadline = overall.ElapsedMilliseconds + WaitStatusMaxMs;
        while (true)
        {
            if (!ReadPort(EC_SC, out byte status)) return false;
            if (((status & flag) != 0) == desiredSet) return true;
            long now = overall.ElapsedMilliseconds;
            if (now >= stepDeadline || now >= budgetMs) return false;
            Thread.SpinWait(200);
        }
    }

    private bool ReadPort(int port, out byte value)
    {
        value = 0;
        var input = new ulong[] { (ulong)port };
        var output = new ulong[1];
        int hr = _pawn!.Execute("ioctl_pio_read", input, output, out nuint returned);
        if (hr != 0 || returned < 1) return false;
        value = (byte)(output[0] & 0xFF);
        return true;
    }

    private bool WritePort(int port, byte value)
    {
        // Writes here target ONLY the EC command/data ports as part of the read handshake
        // (RD_EC command + address byte). We never issue WR_EC (0x81), so no EC register is modified.
        // ioctl_pio_write requires out_size==0; Array.Empty is a non-null ref so it pins to a
        // valid (non-NULL) zero-length pointer, satisfying the module's sized-IOCTL check.
        var input = new ulong[] { (ulong)port, value };
        int hr = _pawn!.Execute("ioctl_pio_write", input, Array.Empty<ulong>(), out _);
        return hr == 0;
    }

    private void TryOpenEcMutex()
    {
        // Create-or-open (CreateMutex semantics): the shared "Access_EC" mutex usually does NOT
        // pre-exist, so OpenExisting alone would leave us unsynchronized against ACPI.sys. new
        // Mutex creates it if absent and attaches to a peer's if present. Try the Global (session-
        // wide) name first — matches WinRing0 / LibreHardwareMonitor — then a session-local name.
        try { _ecMutex = new Mutex(false, "Global\\Access_EC"); return; }
        catch { /* Global namespace may be denied; fall back */ }
        try { _ecMutex = new Mutex(false, "Access_EC"); }
        catch { _ecMutex = null; } // last resort: proceed unsynchronized (best effort)
    }

    private bool AcquireEcMutex(out bool abandoned)
    {
        abandoned = false;
        if (_ecMutex is null) return false;
        try { return _ecMutex.WaitOne(200); }
        catch (AbandonedMutexException) { abandoned = true; return true; } // we now own it
        catch { return false; }
    }

    private void ReleaseEcMutex()
    {
        try { _ecMutex?.ReleaseMutex(); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Available = false;
            try { _ecMutex?.Dispose(); } catch { }
            _ecMutex = null;
            try { _pawn?.Dispose(); } catch { }
            _pawn = null;
        }
    }
}
