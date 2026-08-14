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
/// also drives these ports on GPE/EC-query events. That exact name is the whole point of the
/// mutex: a session-local "Access_EC" is a different kernel object and would synchronize us
/// against nobody. If it cannot be had, the EC stays unavailable — unsynchronized port access
/// interleaved with ACPI.sys corrupts both sides' handshakes.
///
/// Every read carries a total-time budget covering every wait it performs — the managed lock,
/// the shared mutex, the OBF drain and the register handshakes all draw from the same clock, so
/// a 40 ms call cannot spend 200 ms acquiring a mutex. When the budget is exhausted the remaining
/// registers are reported as "not read" (ok=false → the sensor shows "—") rather than stalling.
///
/// The budget bounds our own waiting; it cannot bound the kernel. The batch therefore runs on a
/// <see cref="NativeCallGate"/>, so a PawnIO IOCTL that never returns costs the poll thread its
/// timeout rather than the rest of the session. A wedged call keeps the PawnIO handle and the
/// mutex forever, so the EC is disabled for the process and those objects are deliberately never
/// disposed.
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
    // Headroom over the budget before a call counts as wedged rather than merely slow: the batch
    // bounds its own waiting, so overshooting this can only mean a native call that did not return.
    private const int GateMarginMs = 250;
    private const int OpenGateTimeoutMs = 5000;  // driver open + module load, once per session

    private const string ModuleFileName = "LpcACPIEC.bin";
    private const string SharedMutexName = "Global\\Access_EC";

    private readonly object _lock = new();
    private readonly NativeCallGate _gate = new("EcAccess");
    private PawnIo? _pawn;
    private Mutex? _ecMutex;
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
            if (_disposed) return false;
            if (Available && _pawn is not null) return true;
            if (_gate.Wedged)
            {
                // A previous call never returned; the gate accepts no work and the driver handle
                // it owns cannot be reopened safely.
                UnavailableReason = "ec_wedged";
                return false;
            }
            ResetConnectionLocked();

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

            // Open and load also cross the driver boundary, and they run on the poll thread during
            // a rescan or a resume — the same isolation applies.
            var pawn = new PawnIo();
            int openHr = 0;
            int loadHr = 0;
            if (!_gate.TryRun(() =>
                {
                    openHr = pawn.Open();
                    if (openHr == 0) loadHr = pawn.Load(blob);
                }, OpenGateTimeoutMs))
            {
                // The call is still running and owns `pawn`; disposing it here would tear the
                // handle out from under it.
                UnavailableReason = "ec_wedged";
                return false;
            }

            if (openHr != 0)
            {
                pawn.Dispose();
                // 0x80070005 = E_ACCESSDENIED (not elevated / driver not permitting).
                UnavailableReason = openHr == unchecked((int)0x80070005) ? "not_elevated" : "open_failed";
                return false;
            }

            if (loadHr != 0)
            {
                pawn.Dispose();
                UnavailableReason = "load_failed";
                return false;
            }

            _ecMutex = TryOpenSharedMutex();
            if (_ecMutex is null)
            {
                // Fail closed. Reading the EC ports without holding the shared mutex races the
                // ACPI driver mid-transaction and corrupts both sides' handshakes; a wrong fan
                // reading is the mild outcome.
                pawn.Dispose();
                UnavailableReason = "ec_mutex_unavailable";
                Diag.Log("ec", "EC disabled: " + SharedMutexName + " could not be created or opened");
                return false;
            }

            _pawn = pawn;
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
        var flags = new bool[data.Length];
        ok = flags;
        if (data.Length == 0) return data;

        var addrs = new int[data.Length];
        for (int i = 0; i < addrs.Length; i++) addrs[i] = start + i;
        RunRead(addrs, data, flags, budgetMs);
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
        var flags = new bool[addrs.Length];
        ok = flags;
        if (addrs.Length == 0) return data;
        RunRead(addrs, data, flags, budgetMs);
        return data;
    }

    /// <summary>Full 256-byte dump for the EC Explorer (longer budget; call off the UI thread).</summary>
    public byte[] Dump(out bool[] ok) => ReadBlock(0, 256, out ok, ExplorerBudgetMs);

    // ---------- internals ----------

    /// <summary>
    /// Serializes, budgets and isolates one read batch. The stopwatch starts before the managed
    /// lock so every wait in the call — not merely the register handshakes — draws from the same
    /// budget. Entries stay ok=false whenever the budget runs out.
    /// </summary>
    private void RunRead(int[] addrs, byte[] data, bool[] flags, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        if (!Monitor.TryEnter(_lock, Remaining(sw, budgetMs))) return;
        try
        {
            if (_disposed || !Available || _pawn is null) return;
            // The gate runs the batch on its own thread, so a PawnIO IOCTL that never returns
            // costs this caller the timeout instead of the rest of the session.
            if (!_gate.TryRun(() => ReadBatch(addrs, data, flags, sw, budgetMs), budgetMs + GateMarginMs))
                MarkWedgedLocked();
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Runs on the gate's thread: acquires the shared mutex and walks the registers.</summary>
    private void ReadBatch(int[] addrs, byte[] data, bool[] flags, Stopwatch sw, int budgetMs)
    {
        // The mutex is thread-affine and is both taken and released here, so the shared lock is
        // never left owned by a thread that has moved on.
        if (!AcquireEcMutex(Remaining(sw, budgetMs))) return;
        try
        {
            DrainObf(sw, budgetMs);
            for (int i = 0; i < addrs.Length; i++)
            {
                int addr = addrs[i];
                if ((uint)addr > 0xFF) continue;
                if (sw.ElapsedMilliseconds >= budgetMs) break;   // budget exhausted -> rest stay ok=false
                if (TryReadRegisterNoMutex(addr, sw, budgetMs, out byte v)) { data[i] = v; flags[i] = true; }
            }
        }
        finally
        {
            ReleaseEcMutex();
        }
    }

    private static int Remaining(Stopwatch sw, int budgetMs)
    {
        long left = budgetMs - sw.ElapsedMilliseconds;
        return left <= 0 ? 0 : (int)left;
    }

    /// <summary>
    /// Records that a native call never returned. The abandoned call still owns the PawnIO handle
    /// and the shared mutex, so neither is disposed here or later — see <see cref="NativeCallGate"/>.
    /// </summary>
    private void MarkWedgedLocked()
    {
        if (!Available) return;
        Available = false;
        UnavailableReason = "ec_wedged";
        Diag.Log("ec", "EC read did not return; EC sensors disabled for this session "
            + "(driver handle and " + SharedMutexName + " left to the abandoned call)");
    }

    /// <summary>
    /// Discards a byte the EC/ACPI driver may have left in the output buffer before we start our
    /// own handshake, so the first register read cannot return a stale query/burst byte.
    /// </summary>
    private void DrainObf(Stopwatch sw, int budgetMs)
    {
        if (sw.ElapsedMilliseconds >= budgetMs) return;
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

    /// <summary>
    /// Create-or-open (CreateMutex semantics) on the shared name. The mutex usually does NOT
    /// pre-exist, so OpenExisting alone would leave us unsynchronized against ACPI.sys; `new Mutex`
    /// creates it if absent and attaches to a peer's if present.
    ///
    /// There is deliberately no fallback. An unprefixed "Access_EC" lives in the session namespace
    /// and is a different kernel object from the Global one every other tool uses, so falling back
    /// to it would look synchronized while synchronizing against nothing. Returning null makes the
    /// caller disable the EC instead.
    /// </summary>
    private static Mutex? TryOpenSharedMutex()
    {
        try { return new Mutex(false, SharedMutexName); }
        catch (Exception ex)
        {
            Diag.Log("ec", "Opening " + SharedMutexName + " failed", ex);
            return null;
        }
    }

    private bool AcquireEcMutex(int waitMs)
    {
        if (_ecMutex is null) return false;
        try { return _ecMutex.WaitOne(waitMs); }
        catch (AbandonedMutexException) { return true; } // previous owner died; we own it now
        catch { return false; }
    }

    private void ReleaseEcMutex()
    {
        try { _ecMutex?.ReleaseMutex(); } catch { }
    }

    /// <summary>Drops native handles so a hardware rescan/resume can establish a fresh session.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed) return;
            ResetConnectionLocked();
            UnavailableReason = null;
        }
    }

    private void ResetConnectionLocked()
    {
        Available = false;
        if (_gate.Wedged)
        {
            // An abandoned native call still owns both. Disposing a handle or a mutex under a live
            // kernel call is not a tidy-up, it is a use-after-free — drop the references only.
            Diag.Log("ec", "EC worker wedged; leaving the driver handle and "
                + SharedMutexName + " to process exit");
            _ecMutex = null;
            _pawn = null;
            return;
        }
        try { _ecMutex?.Dispose(); } catch { }
        _ecMutex = null;
        try { _pawn?.Dispose(); } catch { }
        _pawn = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ResetConnectionLocked();
        }
        _gate.Dispose();
    }
}
