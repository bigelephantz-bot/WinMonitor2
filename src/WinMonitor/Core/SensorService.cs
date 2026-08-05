using System.Globalization;
using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using WinMonitor.Config;
using WinMonitor.Localization;

namespace WinMonitor.Core;

/// <summary>
/// A thread-safe, support-oriented snapshot of the sensor polling service. Times are UTC so
/// diagnostics can be copied into logs without depending on the current display locale.
/// </summary>
public readonly record struct SensorHealthSnapshot(
    bool IsRunning,
    long SuccessfulPollCount,
    long FailedPollCount,
    long NodeUpdateFailureCount,
    DateTime? LastSuccessfulPollUtc,
    DateTime? LastFailureUtc,
    long LastPollDurationMs,
    int LastSnapshotCount,
    int DescriptorCount,
    string? LastFailure);

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and polls it on a dedicated
/// low-priority background thread. Emits <see cref="SnapshotUpdated"/> each tick
/// (background thread — consumers marshal). Degrades gracefully when the kernel
/// driver cannot load (non-elevated): reduced hardware set, then WMI-only.
/// </summary>
public sealed class SensorService : IDisposable
{
    private static readonly Version MinPawnIoVersion = new(2, 2);
    private const int MinIntervalMs = 500;
    private const int MaxIntervalMs = 30000;
    private const int FullRefreshMs = 30000; // smart polling: full hardware sweep at least this often
    private const int SlowUpdateMs = 15000;  // Storage/Battery nodes update at most this often
    private const int NodeFailureLimit = 3;  // consecutive Update() failures before a node's sensors read as null

    private readonly Func<AppConfig> _configProvider;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private readonly AutoResetEvent _wakeEvent = new(false);

    private Computer? _computer;
    private ManagementObjectSearcher? _wmiSearcher;
    private Thread? _thread;
    private int _intervalMs;
    private int _polling;                    // Interlocked reentrancy guard
    private volatile bool _rescanRequested;  // consumed on the polling thread
    private volatile bool _wmiDiscoveryPending; // startup deferred WMI zone discovery; consumed on the polling thread
    private bool _skipNextUpdate;            // first tick after Start(): node values are fresh from RebuildDescriptors
    private bool _disposed;
    private long _lastFullTickMs;            // Environment.TickCount64 (monotonic) of last full tick
    private long _lastSlowUpdateMs;          // TickCount64 of last slow-node (Storage/Battery) update
    private long _successfulPollCount;
    private long _failedPollCount;
    private long _nodeUpdateFailureCount;
    private long _lastSuccessfulPollUtcTicks;
    private long _lastFailureUtcTicks;
    private long _lastPollDurationMs;
    private int _lastSnapshotCount;
    private string? _lastFailure;

    // All arrays below are immutable after construction and swapped atomically under _sync.
    private SensorEntry[] _allEntries = Array.Empty<SensorEntry>();
    private IHardware[] _allNodes = Array.Empty<IHardware>();
    private bool[] _allNodesSlow = Array.Empty<bool>();   // parallel to _allNodes
    private WmiZoneEntry[] _allZones = Array.Empty<WmiZoneEntry>();
    private SensorEntry[]? _activeEntries;   // null = poll everything
    private IHardware[]? _activeNodes;
    private bool[]? _activeNodesSlow;        // parallel to _activeNodes
    private WmiZoneEntry[]? _activeZones;
    private HashSet<string>? _activeIds;

    // CPU throttle detection (synthetic /throttle/cpu sensor). The sensor references are
    // swapped under _sync in RebuildDescriptors; the state fields are poll-thread only.
    private ThrottleInput[] _throttleDistanceSensors = Array.Empty<ThrottleInput>();
    private ThrottleInput? _throttleFallbackSensor; // CPU package temp, used when no distance sensors exist
    private IntelThermalStatusReader? _intelThermalStatus;
    private bool _intelThermalStatusInitializationAttempted;
    private bool _throttleState;              // debounced throttling state
    private long _throttleFlipSinceMs;        // TickCount64 since the raw condition disagreed with the state; 0 = agrees

    // Mutated on the polling thread only; replaced under _sync when the node arrays are rebuilt.
    private Dictionary<IHardware, int> _nodeFailures = new();

    // Embedded Controller (LG fan support). Read-only; its own methods are internally locked.
    private readonly EmbeddedController _ec = new();
    // The poll thread reads ONLY this immutable snapshot, never the live Config.Ec.Sensors list
    // (which the EC Explorer mutates on the UI thread) — set atomically via SetEcSensorSnapshot.
    private volatile EcSensorDef[] _ecConfigSnapshot = Array.Empty<EcSensorDef>();
    private EcSensorDef[] _ecSensors = Array.Empty<EcSensorDef>();   // swapped under _sync
    private int[] _ecRegisters = Array.Empty<int>();                 // exact registers to read per tick
    private volatile bool _descriptorRebuildRequested;               // EC config edited -> rebuild descriptors only
    // EC read throttling: only touch the EC every EcReadEveryNTicks ticks; between reads we re-emit
    // the last computed values so tray/stats/history stay continuous. Sized to _ecSensors.Length and
    // reset (all null) whenever _ecSensors is swapped in RebuildDescriptors, all under _sync.
    private int _ecTickCounter;
    private float?[] _ecLastValues = Array.Empty<float?>();

    private volatile IReadOnlyList<SensorDescriptor> _descriptors = Array.Empty<SensorDescriptor>();
    // Exact LHM ids for fixed NVMe warning/critical limits excluded from Descriptors. The config
    // layer uses this only after a successful descriptor rebuild to clean stale chart/tray refs.
    private volatile string[] _suppressedStorageTemperatureLimitSensorIds = Array.Empty<string>();
    private volatile bool _cpuTelemetryAvailable;

    /// <summary>Creates a service against one fixed configuration (diagnostic-tool convenience).</summary>
    public SensorService(AppConfig config) : this(() => config)
    {
    }

    /// <summary>
    /// Creates a service that reads an atomically replaceable configuration snapshot. The provider
    /// must return a fully formed <see cref="AppConfig"/> reference, never a partially edited one.
    /// </summary>
    public SensorService(Func<AppConfig> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        AppConfig config = _configProvider();
        _intervalMs = Math.Clamp(config.PollIntervalMs, MinIntervalMs, MaxIntervalMs);
        IsElevated = ComputeIsElevated();
        PawnIoDetected = DetectPawnIo(out Version? pawnIoVersion);
        PawnIoVersion = pawnIoVersion;
    }

    private AppConfig CurrentConfig => _configProvider();

    public IReadOnlyList<SensorDescriptor> Descriptors => _descriptors;

    /// <summary>
    /// Exact ids for the fixed Storage "Warning Temperature" / "Critical Temperature" sensors
    /// hidden from the live list during the most recent descriptor rebuild.
    /// </summary>
    public IReadOnlyList<string> SuppressedStorageTemperatureLimitSensorIds
        => _suppressedStorageTemperatureLimitSensorIds;

    /// <summary>Shared read-only Embedded Controller accessor (used by the EC Explorer UI too).</summary>
    public EmbeddedController Ec => _ec;

    /// <summary>Fired each poll tick on the polling (background) thread.</summary>
    public event Action<SensorSnapshot[]>? SnapshotUpdated;

    /// <summary>Fired after a hardware rescan rebuilt the descriptor list (background thread).</summary>
    public event Action? DescriptorsChanged;

    /// <summary>Fired when the debounced CPU throttle state flips (polling thread — consumers marshal).</summary>
    public event Action<bool>? ThrottleStateChanged;

    public bool IsElevated { get; }

    public bool PawnIoDetected { get; }

    public Version? PawnIoVersion { get; }

    public bool PawnIoUpdateRequired
        => PawnIoVersion is { } version && version.CompareTo(MinPawnIoVersion) < 0;

    /// <summary>
    /// True after a full poll has produced at least one low-level CPU temperature, power, or
    /// frequency value. CPU load alone does not qualify because Windows can provide it without
    /// PawnIO/kernel access.
    /// </summary>
    public bool CpuTelemetryAvailable => _cpuTelemetryAvailable;

    /// <summary>Returns current polling health without exposing mutable sensor-service state.</summary>
    public SensorHealthSnapshot GetHealthSnapshot()
    {
        return new SensorHealthSnapshot(
            IsRunning: _thread is { IsAlive: true } && !_stopEvent.IsSet,
            SuccessfulPollCount: Interlocked.Read(ref _successfulPollCount),
            FailedPollCount: Interlocked.Read(ref _failedPollCount),
            NodeUpdateFailureCount: Interlocked.Read(ref _nodeUpdateFailureCount),
            LastSuccessfulPollUtc: TicksToUtc(Interlocked.Read(ref _lastSuccessfulPollUtcTicks)),
            LastFailureUtc: TicksToUtc(Interlocked.Read(ref _lastFailureUtcTicks)),
            LastPollDurationMs: Interlocked.Read(ref _lastPollDurationMs),
            LastSnapshotCount: Volatile.Read(ref _lastSnapshotCount),
            DescriptorCount: _descriptors.Count,
            LastFailure: Volatile.Read(ref _lastFailure));
    }

    /// <summary>Full LibreHardwareMonitor diagnostic report. Intended for SensorDump/support use.</summary>
    public string GetBackendReport()
    {
        try { return _computer?.GetReport() ?? string.Empty; }
        catch (Exception ex) { return "Backend report unavailable: " + ex.Message; }
    }

    /// <summary>
    /// Opens the hardware backend, builds descriptors and performs the first poll
    /// synchronously so <see cref="Descriptors"/> is populated when this returns.
    /// </summary>
    public void Start()
    {
        if (_disposed || _thread is not null) return;

        AppConfig config = CurrentConfig;
        _intervalMs = Math.Clamp(config.PollIntervalMs, MinIntervalMs, MaxIntervalMs);
        if (_computer is null) OpenComputer();
        _ecConfigSnapshot = SnapshotEcSensors(config.Ec);
        if (config.Ec.Enabled) { try { _ec.Initialize(); } catch { } }
        // Startup path: WMI zone discovery is deferred to the polling thread (one tick later)
        // and the first Poll skips the per-node Update() sweep — RebuildDescriptors just did it.
        RebuildDescriptors(deferWmiDiscovery: true);
        _skipNextUpdate = true;

        _stopEvent.Reset();
        Poll(); // synchronous first tick: consumers get data immediately

        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "WinMonitor.SensorPoll",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? t = _thread;
        if (t is null) return;
        _thread = null;
        _stopEvent.Set();
        if (!ReferenceEquals(t, Thread.CurrentThread))
        {
            try { t.Join(2000); } catch { /* thread state races are non-fatal on shutdown */ }
        }
    }

    public void SetPollInterval(int ms)
    {
        int clamped = Math.Clamp(ms, MinIntervalMs, MaxIntervalMs);
        // No-op when unchanged: battery StatusChange events fire this repeatedly with the same
        // value; waking the poll thread each time would force off-cadence immediate polls.
        if (clamped == Volatile.Read(ref _intervalMs)) return;
        Volatile.Write(ref _intervalMs, clamped);
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Smart polling: null = poll all hardware every tick; otherwise only the hardware
    /// nodes owning these ids are updated per tick, with a full sweep every 30 s.
    /// </summary>
    public void SetActiveSensorIds(IReadOnlyCollection<string>? ids)
    {
        lock (_sync)
        {
            if (ids is null)
            {
                _activeIds = null;
            }
            else
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in ids) set.Add(id);
                _activeIds = set;
            }
            RecomputeActiveSetsLocked();
        }
    }

    /// <summary>Requests a close/reopen + descriptor rebuild; executed on the polling thread.</summary>
    public void RescanHardware()
    {
        _rescanRequested = true;
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Requests a descriptor-only rebuild on the polling thread (no LHM reopen). Used when a
    /// config toggle changes the synthetic descriptor set (e.g. the CPU throttle indicator).
    /// </summary>
    public void RequestDescriptorRebuild()
    {
        _descriptorRebuildRequested = true;
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Rebuild descriptors after the EC sensor set changed (add/remove in the EC Explorer).
    /// The caller passes an immutable snapshot built on its own thread, so the poll thread never
    /// touches the live Config.Ec.Sensors list. Lighter than RescanHardware (no LHM reopen).
    /// </summary>
    public void RefreshEcSensors(EcConfig ec)
    {
        _ecConfigSnapshot = SnapshotEcSensors(ec);
        _descriptorRebuildRequested = true;
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Deep-copies the enabled EC sensor defs into an immutable array for the poll thread.</summary>
    private static EcSensorDef[] SnapshotEcSensors(EcConfig ec)
    {
        if (!ec.Enabled || ec.Sensors.Count == 0) return Array.Empty<EcSensorDef>();
        var list = new List<EcSensorDef>(ec.Sensors.Count);
        foreach (var s in ec.Sensors)
            if (s is not null) list.Add(s.Clone());
        return list.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
        try { _intelThermalStatus?.Dispose(); } catch { }
        _intelThermalStatus = null;
        try { _ec.Dispose(); } catch { }
        try { _computer?.Close(); } catch { }
        _computer = null;
        try { _wmiSearcher?.Dispose(); } catch { }
        _wmiSearcher = null;
        try { _stopEvent.Dispose(); } catch { }
        try { _wakeEvent.Dispose(); } catch { }
    }

    // ---------- polling thread ----------

    private void PollLoop()
    {
        // Index 0 = stop (checked first by WaitAny), index 1 = wake (interval change / rescan).
        var handles = new WaitHandle[] { _stopEvent.WaitHandle, _wakeEvent };
        while (true)
        {
            int signaled;
            try { signaled = WaitHandle.WaitAny(handles, Volatile.Read(ref _intervalMs)); }
            catch (ObjectDisposedException) { return; }
            if (signaled == 0 || _stopEvent.IsSet) return;
            Poll();
        }
    }

    private void Poll()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        long pollStarted = Environment.TickCount64;
        try
        {
            AppConfig config = CurrentConfig;
            bool skipUpdate = _skipNextUpdate;
            _skipNextUpdate = false;

            if (_rescanRequested)
            {
                _rescanRequested = false;
                DoRescan();
            }
            else if (_descriptorRebuildRequested)
            {
                // EC sensor set changed: rebuild descriptors from the already-open Computer
                // (no Close/Open — far cheaper than a full rescan) and notify consumers.
                _descriptorRebuildRequested = false;
                if (config.Ec.Enabled) { try { _ec.Initialize(); } catch { } }
                RebuildDescriptors();
                _lastFullTickMs = 0;
                DescriptorsChanged?.Invoke();
            }

            // Deferred startup WMI discovery: never on the synchronous first tick (that would
            // defeat the deferral); a rescan/rebuild above already discovered and cleared it.
            if (!skipUpdate && _wmiDiscoveryPending)
            {
                _wmiDiscoveryPending = false;
                MergeDeferredWmiZones();
            }

            SensorEntry[] allEntries;
            IHardware[] allNodes;
            bool[] allNodesSlow;
            WmiZoneEntry[] allZones;
            SensorEntry[]? activeEntries;
            IHardware[]? activeNodes;
            bool[]? activeNodesSlow;
            WmiZoneEntry[]? activeZones;
            Dictionary<IHardware, int> nodeFailures;
            EcSensorDef[] ecSensors;
            int[] ecRegisters;
            float?[] ecLastValues;
            ThrottleInput[] throttleDistance;
            ThrottleInput? throttleFallback;
            lock (_sync)
            {
                allEntries = _allEntries;
                allNodes = _allNodes;
                allNodesSlow = _allNodesSlow;
                allZones = _allZones;
                activeEntries = _activeEntries;
                activeNodes = _activeNodes;
                activeNodesSlow = _activeNodesSlow;
                activeZones = _activeZones;
                nodeFailures = _nodeFailures;
                ecSensors = _ecSensors;
                ecRegisters = _ecRegisters;
                ecLastValues = _ecLastValues;
                throttleDistance = _throttleDistanceSensors;
                throttleFallback = _throttleFallbackSensor;
            }

            long now = Environment.TickCount64;
            bool full = activeEntries is null || _lastFullTickMs == 0 || now - _lastFullTickMs >= FullRefreshMs;

            IHardware[] nodes = full ? allNodes : activeNodes!;
            bool[] nodesSlow = full ? allNodesSlow : activeNodesSlow!;
            if (!skipUpdate)
            {
                // Slow cadence: Storage (SMART) and Battery readings barely change and their
                // Update() is comparatively expensive — run them at most every SlowUpdateMs.
                // One shared timestamp, advanced whenever slow nodes run (30 s full sweeps and
                // rescans included); between runs LHM keeps the last values and snapshots
                // still emit them. Always due on a full sweep so a smart-polled fast node
                // (e.g. an active Battery) advancing the shared timestamp can't starve a
                // full-sweep-only slow node (e.g. Storage) of its cadence.
                bool slowDue = full || now - _lastSlowUpdateMs >= SlowUpdateMs;
                bool ranSlow = false;
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (nodesSlow[i])
                    {
                        // Skipped, not failed: a slow skip must not touch the failure counters.
                        if (!slowDue) continue;
                        ranSlow = true;
                    }
                    // Per-node guard: one flaky device must not kill the whole tick.
                    try
                    {
                        nodes[i].Update();
                        nodeFailures[nodes[i]] = 0;
                    }
                    catch
                    {
                        nodeFailures.TryGetValue(nodes[i], out int fails);
                        nodeFailures[nodes[i]] = fails + 1;
                        Interlocked.Increment(ref _nodeUpdateFailureCount);
                    }
                }
                if (ranSlow) _lastSlowUpdateMs = now;
            }

            if (full)
            {
                RefreshWmiZones(allZones);
                _lastFullTickMs = now;
            }

            bool throttleEnabled = config.ThrottleIndicatorEnabled;
            bool throttleFinalClear = false;
            if (throttleEnabled)
            {
                UpdateThrottleState(now, throttleDistance, throttleFallback, nodeFailures, config.ThrottleSustainSeconds);
            }
            else if (_throttleState || _throttleFlipSinceMs != 0)
            {
                // Indicator turned off while latched throttling: emit one final Value=0 snapshot
                // this tick so tray/stats clear the latched 100/HOT, and notify consumers once.
                // Subsequent ticks fall through (state now clean) and stop emitting, as before.
                throttleFinalClear = _throttleState;
                _throttleState = false; // silent reset so re-enabling starts clean
                _throttleFlipSinceMs = 0;
                if (throttleFinalClear) ThrottleStateChanged?.Invoke(false);
            }

            SensorEntry[] entries = full ? allEntries : activeEntries!;
            WmiZoneEntry[] zones = full ? allZones : activeZones!;

            bool emitEc = ecSensors.Length > 0 && _ec.Available && ecLastValues.Length == ecSensors.Length;

            // EC read throttling: only touch the EC every Nth tick. We emit EC snapshots ONLY on
            // read ticks — re-emitting cached values on skip ticks would feed StatsTracker/history
            // duplicate samples with fresh timestamps, skewing min/max/avg and the chart. The tray
            // already retains the last value between reads (its own _latest cache), so throttling
            // just lowers the EC sample rate, exactly as intended.
            bool ecReadTick = false;
            if (emitEc)
            {
                int everyN = Math.Max(1, config.EcReadEveryNTicks);
                // Read on the first tick after a rebuild (counter 0) and every Nth tick after.
                ecReadTick = _ecTickCounter % everyN == 0;
                _ecTickCounter++;
                if (ecReadTick && ecRegisters.Length > 0)
                {
                    // Read only the exact registers the sensors need, with a small time budget so a
                    // wedged EC can never stall this tick (see EmbeddedController budget).
                    byte[]? ecRaw = _ec.ReadRegisters(ecRegisters, out bool[]? ecRawOk);
                    if (ecRaw is not null && ecRawOk is not null)
                    {
                        // Scatter the sparse read into full-register space so EcSensorDef.Compute,
                        // which addresses regs[0..255], reads the right offsets.
                        var regs = new byte[256];
                        var okFull = new bool[256];
                        for (int i = 0; i < ecRegisters.Length; i++)
                        {
                            int addr = ecRegisters[i];
                            if ((uint)addr <= 0xFF) { regs[addr] = ecRaw[i]; okFull[addr] = ecRawOk[i]; }
                        }
                        for (int i = 0; i < ecSensors.Length; i++)
                            ecLastValues[i] = ecSensors[i].Compute(regs, okFull);
                    }
                }
            }
            int ecEmit = (emitEc && ecReadTick) ? ecSensors.Length : 0;
            int throttleEmit = (throttleEnabled || throttleFinalClear) ? 1 : 0;

            DateTime utc = DateTime.UtcNow;
            var snapshots = new SensorSnapshot[entries.Length + zones.Length + ecEmit + throttleEmit];
            int n = 0;
            bool cpuTelemetryAvailable = false;
            for (int i = 0; i < entries.Length; i++)
            {
                SensorEntry e = entries[i];
                // A node stuck failing Update() keeps its last cached readings; after
                // NodeFailureLimit consecutive failures they are ghost data — emit null ("—").
                bool dead = nodeFailures.TryGetValue(e.Owner, out int fails) && fails >= NodeFailureLimit;
                float? value = dead ? null : e.Sensor.Value;
                snapshots[n++] = new SensorSnapshot { Id = e.Id, Value = value, UtcTimestamp = utc };
                if (full && e.Owner.HardwareType == HardwareType.Cpu
                    && e.Sensor.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Clock
                    && value is { } cpuValue && !float.IsNaN(cpuValue) && cpuValue > 0f)
                {
                    cpuTelemetryAvailable = true;
                }
            }
            if (full) _cpuTelemetryAvailable = cpuTelemetryAvailable;
            for (int i = 0; i < zones.Length; i++)
                snapshots[n++] = new SensorSnapshot { Id = zones[i].Id, Value = zones[i].LastValue, UtcTimestamp = utc };

            if (emitEc && ecReadTick)
            {
                for (int i = 0; i < ecSensors.Length; i++)
                    snapshots[n++] = new SensorSnapshot { Id = ecSensors[i].SensorId, Value = ecLastValues[i], UtcTimestamp = utc };
            }

            if (throttleEnabled)
                snapshots[n++] = new SensorSnapshot { Id = WellKnown.ThrottleSensorId, Value = _throttleState ? 1f : 0f, UtcTimestamp = utc };
            else if (throttleFinalClear)
                snapshots[n++] = new SensorSnapshot { Id = WellKnown.ThrottleSensorId, Value = 0f, UtcTimestamp = utc };

            SnapshotUpdated?.Invoke(snapshots);
            Volatile.Write(ref _lastSnapshotCount, n);
            Interlocked.Increment(ref _successfulPollCount);
            Interlocked.Exchange(ref _lastSuccessfulPollUtcTicks, utc.Ticks);
        }
        catch (Exception ex)
        {
            // A failed tick is dropped; diagnostics retain the reason while monitoring continues.
            Interlocked.Increment(ref _failedPollCount);
            Interlocked.Exchange(ref _lastFailureUtcTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _lastFailure, DescribeFailure(ex));
        }
        finally
        {
            Volatile.Write(ref _lastPollDurationMs, Math.Max(0, Environment.TickCount64 - pollStarted));
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private static DateTime? TicksToUtc(long ticks)
        => ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    private static string DescribeFailure(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        string detail = ex.GetType().Name + (message.Length == 0 ? string.Empty : ": " + message);
        return detail.Length <= 300 ? detail : detail[..300];
    }

    private void DoRescan()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;

        // Dispose can race a rescan (Stop's Join has a timeout). Never reopen a Computer
        // once shutdown began — a resurrected instance keeps the LHM kernel driver loaded.
        if (IsShuttingDown()) return;
        OpenComputer();
        if (IsShuttingDown())
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            return;
        }

        RebuildDescriptors();
        _lastFullTickMs = 0; // force a full tick right after the rescan
        DescriptorsChanged?.Invoke();
    }

    private bool IsShuttingDown()
    {
        if (Volatile.Read(ref _disposed)) return true;
        try { return _stopEvent.IsSet; } catch (ObjectDisposedException) { return true; }
    }

    // ---------- CPU throttle detection (synthetic sensor) ----------

    /// <summary>
    /// Detects CPU thermal throttling on the polling thread. Intel package thermal-status
    /// assertions enter immediately, then use the configured sustain time as the clear delay.
    /// Without direct MSR access, a temperature proxy (Distance to TjMax ≤ 3 °C, or package
    /// temperature ≥ 95 °C) must persist for the sustain time in both directions.
    /// </summary>
    private void UpdateThrottleState(long now, ThrottleInput[] distance, ThrottleInput? fallback,
                                     Dictionary<IHardware, int> nodeFailures, int sustainSeconds)
    {
        bool directStatusAvailable = TryReadIntelThermalStatus(out bool condition);
        if (!directStatusAvailable && distance.Length > 0)
        {
            float min = float.MaxValue;
            for (int i = 0; i < distance.Length; i++)
            {
                // Honor the snapshot loop's node-death rule: a node stuck failing Update() feeds
                // ghost temps, so skip its inputs. With every input dead the min stays MaxValue
                // (→ false) and the debounce cleanly exits throttling.
                if (nodeFailures.TryGetValue(distance[i].Owner, out int fails) && fails >= NodeFailureLimit) continue;
                float? v = distance[i].Sensor.Value;
                if (v is not { } d || float.IsNaN(d)) continue;
                if (d < min) min = d;
            }
            condition = min <= 3f; // stays MaxValue (→ false) when nothing reported this tick
        }
        else if (!directStatusAvailable && fallback is { } fb
            && !(nodeFailures.TryGetValue(fb.Owner, out int fails) && fails >= NodeFailureLimit))
        {
            float? v = fb.Sensor.Value;
            condition = v is { } t && !float.IsNaN(t) && t >= 95f;
        }

        if (condition == _throttleState)
        {
            _throttleFlipSinceMs = 0;
            return;
        }
        // A real hardware assertion can be brief. Enter immediately and use the configured
        // sustain interval as a visible hold/clear delay. Temperature-proxy entry remains
        // debounced below because it is only an inference.
        if (directStatusAvailable && condition)
        {
            _throttleState = true;
            _throttleFlipSinceMs = 0;
            ThrottleStateChanged?.Invoke(true);
            return;
        }
        if (_throttleFlipSinceMs == 0) _throttleFlipSinceMs = now;
        long sustainMs = Math.Max(0, sustainSeconds) * 1000L;
        if (now - _throttleFlipSinceMs < sustainMs) return;

        _throttleState = condition;
        _throttleFlipSinceMs = 0;
        ThrottleStateChanged?.Invoke(condition);
    }

    private bool TryReadIntelThermalStatus(out bool throttling)
    {
        throttling = false;
        if (!_intelThermalStatusInitializationAttempted)
        {
            _intelThermalStatusInitializationAttempted = true;
            _intelThermalStatus = IntelThermalStatusReader.TryCreate();
        }
        IntelThermalStatusReader? reader = _intelThermalStatus;
        return reader is not null && reader.TryRead(out throttling);
    }

    // ---------- hardware backend ----------

    private void OpenComputer()
    {
        var full = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
        };
        try
        {
            full.Open();
            _computer = full;
            return;
        }
        catch
        {
            try { full.Close(); } catch { }
        }

        // A GPU/controller backend can fail independently of the CPU. Retry with the useful
        // low-risk groups before falling all the way back to driver-free sources; otherwise one
        // unrelated device failure makes every CPU descriptor disappear.
        var essentials = new Computer
        {
            IsCpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
        };
        try
        {
            essentials.Open();
            _computer = essentials;
            return;
        }
        catch
        {
            try { essentials.Close(); } catch { }
        }

        // PawnIO/kernel access can still be unavailable; retain driver-free sources rather than
        // failing the whole app. The UI reports the missing CPU telemetry explicitly.
        var reduced = new Computer
        {
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
        };
        try
        {
            reduced.Open();
            _computer = reduced;
        }
        catch
        {
            try { reduced.Close(); } catch { }
            _computer = null; // WMI-thermal-zone-only mode; never crash the app
        }
    }

    private void RebuildDescriptors(bool deferWmiDiscovery = false)
    {
        var descriptors = new List<SensorDescriptor>();
        var entries = new List<SensorEntry>();
        var nodes = new List<IHardware>();
        var tjMaxDistance = new List<ThrottleInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var suppressedStorageTemperatureLimitIds = new HashSet<string>(StringComparer.Ordinal);
        string? cpuName = null;

        Computer? computer = _computer;
        if (computer is not null)
        {
            try
            {
                foreach (IHardware hw in computer.Hardware)
                {
                    if (cpuName is null && hw.HardwareType == HardwareType.Cpu) cpuName = hw.Name.Trim();
                    AddHardwareNode(hw, descriptors, entries, nodes, seen, tjMaxDistance,
                        suppressedStorageTemperatureLimitIds);
                }
            }
            catch { /* enumeration failure -> whatever we collected so far */ }
        }

        // Fallback source for throttle detection when no "Distance to TjMax" sensors exist.
        ThrottleInput? throttleFallback = null;
        if (tjMaxDistance.Count == 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                SensorEntry e = entries[i];
                if (e.Owner.HardwareType == HardwareType.Cpu
                    && e.Sensor.SensorType == SensorType.Temperature
                    && e.Sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                {
                    throttleFallback = new ThrottleInput(e.Sensor, e.Owner);
                    break;
                }
            }
        }

        if (CurrentConfig.ThrottleIndicatorEnabled && seen.Add(WellKnown.ThrottleSensorId))
        {
            descriptors.Add(new SensorDescriptor
            {
                Id = WellKnown.ThrottleSensorId,
                HardwareName = cpuName ?? "CPU",
                Name = Loc.T("throttle.sensor"),
                Category = SensorCategory.Cpu,
                Quantity = SensorQuantity.Level,
            });
        }

        // Startup path: WMI discovery (searcher creation + first query) is slow, so Start()
        // defers it to the polling thread; rescans/rebuilds already run there and stay sync.
        List<WmiZoneEntry> zones;
        if (deferWmiDiscovery)
        {
            zones = new List<WmiZoneEntry>();
            _wmiDiscoveryPending = true;
        }
        else
        {
            zones = DiscoverWmiZones(seen, descriptors);
            _wmiDiscoveryPending = false; // this full build supersedes any pending deferred merge
        }
        EcSensorDef[] ecSensors = BuildEcDescriptors(descriptors, seen, out int[] ecRegisters);

        var nodesSlow = new bool[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) nodesSlow[i] = IsSlowNode(nodes[i]);

        lock (_sync)
        {
            _allEntries = entries.ToArray();
            _allNodes = nodes.ToArray();
            _allNodesSlow = nodesSlow;
            _allZones = zones.ToArray();
            _nodeFailures = new Dictionary<IHardware, int>();
            _ecSensors = ecSensors;
            _ecRegisters = ecRegisters;
            // Keep _ecLastValues in lockstep with the new sensor set (fresh all-null buffer).
            _ecLastValues = ecSensors.Length > 0 ? new float?[ecSensors.Length] : Array.Empty<float?>();
            _ecTickCounter = 0;
            _throttleDistanceSensors = tjMaxDistance.ToArray();
            _throttleFallbackSensor = throttleFallback;
            // AddHardwareNode just Update()d every node (slow ones included) — counts as a slow update.
            _lastSlowUpdateMs = Environment.TickCount64;
            _descriptors = descriptors;
            _suppressedStorageTemperatureLimitSensorIds = suppressedStorageTemperatureLimitIds.ToArray();
            RecomputeActiveSetsLocked();
        }
    }

    /// <summary>
    /// Startup deferred WMI discovery: runs on the polling thread one tick after Start(),
    /// keeping WMI searcher creation and the first (often slow) query off the startup path.
    /// When zones exist they are merged into the swapped arrays + descriptor list.
    /// </summary>
    private void MergeDeferredWmiZones()
    {
        // Copy-on-write: the published descriptor list is read lock-free by consumers.
        IReadOnlyList<SensorDescriptor> current = _descriptors;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new List<SensorDescriptor>(current.Count + 8);
        for (int i = 0; i < current.Count; i++)
        {
            descriptors.Add(current[i]);
            seen.Add(current[i].Id);
        }

        List<WmiZoneEntry> zones = DiscoverWmiZones(seen, descriptors);
        if (zones.Count == 0) return;

        lock (_sync)
        {
            var merged = new WmiZoneEntry[_allZones.Length + zones.Count];
            Array.Copy(_allZones, merged, _allZones.Length);
            for (int i = 0; i < zones.Count; i++) merged[_allZones.Length + i] = zones[i];
            _allZones = merged;
            _descriptors = descriptors;
            RecomputeActiveSetsLocked();
        }
        DescriptorsChanged?.Invoke();
    }

    /// <summary>Storage (SMART) and Battery nodes change slowly; polled at SlowUpdateMs cadence.</summary>
    private static bool IsSlowNode(IHardware hw)
        => hw.HardwareType is HardwareType.Storage or HardwareType.Battery;

    /// <summary>
    /// Appends descriptors for the user's EC sensors (LG fans etc.) and returns the definition
    /// array plus the exact (deduplicated) register list to read each tick. Reads the immutable
    /// snapshot, never the live config list, so it can't race the EC Explorer's edits.
    /// </summary>
    private EcSensorDef[] BuildEcDescriptors(List<SensorDescriptor> descriptors, HashSet<string> seen, out int[] readRegisters)
    {
        readRegisters = Array.Empty<int>();
        if (!CurrentConfig.Ec.Enabled) return Array.Empty<EcSensorDef>();
        if (!_ec.Available && !_ec.Initialize()) return Array.Empty<EcSensorDef>();

        var list = new List<EcSensorDef>();
        var regs = new SortedSet<int>();
        foreach (var def in _ecConfigSnapshot)
        {
            if (def is null || !def.Enabled || (uint)def.Register > 0xFF) continue;
            if (!seen.Add(def.SensorId)) continue;
            list.Add(def);
            descriptors.Add(new SensorDescriptor
            {
                Id = def.SensorId,
                HardwareName = Loc.T("cat.fan"),
                Name = def.DisplayName,
                Category = def.Category,
                Quantity = def.Quantity,
            });
            regs.Add(def.Register);
            // Word/RPM kinds also need the high byte at Register+1.
            if (def.Kind is not (EcValueKind.RawByte or EcValueKind.Percent) && def.Register < 0xFF)
                regs.Add(def.Register + 1);
        }
        if (list.Count == 0) return Array.Empty<EcSensorDef>();
        readRegisters = new int[regs.Count];
        regs.CopyTo(readRegisters);
        return list.ToArray();
    }

    private static void AddHardwareNode(
        IHardware hw,
        List<SensorDescriptor> descriptors,
        List<SensorEntry> entries,
        List<IHardware> nodes,
        HashSet<string> seen,
        List<ThrottleInput> tjMaxDistance,
        HashSet<string> suppressedStorageTemperatureLimitIds)
    {
        if (hw.HardwareType == HardwareType.Network) return;
        // Consumer memory modules have no real temperature sensor; LHM's Memory node only
        // reports load/usage values WinMonitor never displays — skip the whole node.
        if (hw.HardwareType == HardwareType.Memory) return;

        // Update before enumerating: several backends (storage SMART, battery) only
        // register their sensors during the first Update().
        try { hw.Update(); } catch { }
        nodes.Add(hw);

        ISensor[] sensors;
        try { sensors = hw.Sensors; } catch { sensors = Array.Empty<ISensor>(); }
        foreach (ISensor sensor in sensors)
        {
            // NVMe exposes these SMART limits as temperature sensors, but they are fixed
            // warning/critical thresholds rather than live drive-temperature readings.
            if (IsStorageTemperatureLimitSensor(sensor, hw.HardwareType))
            {
                suppressedStorageTemperatureLimitIds.Add(sensor.Identifier.ToString());
                continue;
            }
            if (!TryMap(sensor, hw.HardwareType, out SensorQuantity quantity, out SensorCategory category)) continue;
            // "Distance to TjMax" duplicates every core temp as an inverted value — clutter as
            // a descriptor, but its minimum drives the synthetic CPU throttle sensor.
            if (sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            {
                tjMaxDistance.Add(new ThrottleInput(sensor, hw));
                continue;
            }
            string id = sensor.Identifier.ToString();
            if (!seen.Add(id)) continue;
            descriptors.Add(new SensorDescriptor
            {
                Id = id,
                HardwareName = hw.Name.Trim(),
                Name = sensor.Name.Trim(),
                Category = category,
                Quantity = quantity,
            });
            entries.Add(new SensorEntry(sensor, hw, id));
        }

        foreach (IHardware sub in hw.SubHardware)
            AddHardwareNode(sub, descriptors, entries, nodes, seen, tjMaxDistance,
                suppressedStorageTemperatureLimitIds);
    }

    private static bool IsStorageTemperatureLimitSensor(ISensor sensor, HardwareType hwType)
        => hwType == HardwareType.Storage
            && sensor.SensorType == SensorType.Temperature
            && (string.Equals(sensor.Name, "Warning Temperature", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sensor.Name, "Critical Temperature", StringComparison.OrdinalIgnoreCase));

    /// <summary>Filters LHM sensors down to the quantities WinMonitor displays.</summary>
    private static bool TryMap(ISensor sensor, HardwareType hwType, out SensorQuantity quantity, out SensorCategory category)
    {
        quantity = default;
        category = default;
        switch (sensor.SensorType)
        {
            case SensorType.Fan:
                quantity = SensorQuantity.Fan;
                category = SensorCategory.Fan;
                return true;

            case SensorType.Control: // PWM duty
                quantity = SensorQuantity.Control;
                category = SensorCategory.Fan;
                return true;

            case SensorType.Temperature:
                quantity = SensorQuantity.Temperature;
                category = TemperatureCategory(hwType);
                return true;

            case SensorType.Level: // battery charge/degradation, SSD "Percentage Used" etc.
                if (hwType == HardwareType.Battery)
                {
                    quantity = SensorQuantity.Level;
                    category = SensorCategory.Battery;
                    return true;
                }
                if (hwType == HardwareType.Storage)
                {
                    quantity = SensorQuantity.Level;
                    category = SensorCategory.Storage;
                    return true;
                }
                return false;

            case SensorType.Power:
                if (hwType == HardwareType.Cpu)
                {
                    quantity = SensorQuantity.Power;
                    category = SensorCategory.Cpu;
                    return true;
                }
                if (hwType == HardwareType.Battery) // charge / discharge rate
                {
                    quantity = SensorQuantity.Power;
                    category = SensorCategory.Battery;
                    return true;
                }
                return false;

            case SensorType.Clock:
                if (hwType == HardwareType.Cpu)
                {
                    quantity = SensorQuantity.Frequency;
                    category = SensorCategory.Cpu;
                    return true;
                }
                return false;

            case SensorType.Data:
                if (hwType == HardwareType.Storage &&
                    (sensor.Name.Contains("Data Written", StringComparison.OrdinalIgnoreCase) ||
                     sensor.Name.Contains("Data Read", StringComparison.OrdinalIgnoreCase)))
                {
                    quantity = SensorQuantity.Data;
                    category = SensorCategory.Storage;
                    return true;
                }
                if (hwType == HardwareType.Battery)
                {
                    quantity = SensorQuantity.Data;
                    category = SensorCategory.Battery;
                    return true;
                }
                return false;

            case SensorType.Voltage:
                if (hwType == HardwareType.Battery)
                {
                    quantity = SensorQuantity.Voltage;
                    category = SensorCategory.Battery;
                    return true;
                }
                return false;

            case SensorType.Load:
                if (hwType == HardwareType.Cpu && string.Equals(sensor.Name, "CPU Total", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = SensorQuantity.Load;
                    category = SensorCategory.Cpu;
                    return true;
                }
                return false;

            default: // throughput, energy, factors... not displayed
                return false;
        }
    }

    private static SensorCategory TemperatureCategory(HardwareType hwType) => hwType switch
    {
        HardwareType.Cpu => SensorCategory.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorCategory.Gpu,
        HardwareType.Storage => SensorCategory.Storage,
        HardwareType.Memory => SensorCategory.Memory,
        HardwareType.Battery => SensorCategory.Battery,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => SensorCategory.Motherboard,
        _ => SensorCategory.Other,
    };

    // ---------- smart polling ----------

    /// <summary>Rebuilds the active subsets from _activeIds. Caller holds _sync.</summary>
    private void RecomputeActiveSetsLocked()
    {
        HashSet<string>? ids = _activeIds;
        if (ids is null)
        {
            _activeEntries = null;
            _activeNodes = null;
            _activeNodesSlow = null;
            _activeZones = null;
            return;
        }

        var entries = new List<SensorEntry>();
        var nodes = new List<IHardware>();
        SensorEntry[] all = _allEntries;
        for (int i = 0; i < all.Length; i++)
        {
            SensorEntry e = all[i];
            if (!ids.Contains(e.Id)) continue;
            entries.Add(e);
            if (!nodes.Contains(e.Owner)) nodes.Add(e.Owner); // reference equality, tiny list
        }

        var zones = new List<WmiZoneEntry>();
        WmiZoneEntry[] allZones = _allZones;
        for (int i = 0; i < allZones.Length; i++)
        {
            if (ids.Contains(allZones[i].Id)) zones.Add(allZones[i]);
        }

        // Throttle detection reads live CPU values every tick. Under smart polling the CPU node
        // may back no active sensor id, so force its owner into the active set — otherwise the
        // detector sees stale temps (late detection / false HOT). Not slow, so a false entry.
        if (CurrentConfig.ThrottleIndicatorEnabled)
        {
            IHardware? throttleOwner = _throttleDistanceSensors.Length > 0
                ? _throttleDistanceSensors[0].Owner
                : _throttleFallbackSensor?.Owner;
            if (throttleOwner is not null && !nodes.Contains(throttleOwner)) nodes.Add(throttleOwner);
        }

        var nodesSlow = new bool[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) nodesSlow[i] = IsSlowNode(nodes[i]);

        _activeEntries = entries.ToArray();
        _activeNodes = nodes.ToArray();
        _activeNodesSlow = nodesSlow;
        _activeZones = zones.ToArray();
    }

    // ---------- WMI ACPI thermal zones (best effort; usually needs elevation) ----------

    private List<WmiZoneEntry> DiscoverWmiZones(HashSet<string> seen, List<SensorDescriptor> descriptors)
    {
        var zones = new List<WmiZoneEntry>();
        try
        {
            _wmiSearcher ??= new ManagementObjectSearcher(
                new ManagementScope(@"root\WMI"),
                new ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
            using ManagementObjectCollection results = _wmiSearcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                try
                {
                    string? instance = mo["InstanceName"] as string;
                    if (string.IsNullOrEmpty(instance)) continue;
                    object? raw = mo["CurrentTemperature"];
                    if (raw is null) continue;
                    // WMI reports tenths of Kelvin.
                    float celsius = Convert.ToSingle(raw, CultureInfo.InvariantCulture) / 10f - 273.15f;
                    if (float.IsNaN(celsius) || celsius <= 0.1f) continue; // dead/bogus zone

                    string id = "/wmi/thermalzone/" + instance;
                    if (!seen.Add(id)) continue;
                    zones.Add(new WmiZoneEntry(id, instance) { LastValue = celsius });
                    descriptors.Add(new SensorDescriptor
                    {
                        Id = id,
                        HardwareName = Loc.T("sensor.acpi_zone"),
                        Name = "ACPI " + ShortZoneName(instance),
                        Category = SensorCategory.Motherboard,
                        Quantity = SensorQuantity.Temperature,
                    });
                }
                catch { /* skip this instance */ }
                finally { mo.Dispose(); }
            }
        }
        catch
        {
            // Access denied (non-elevated) or WMI unavailable — LHM data still flows.
        }
        return zones;
    }

    /// <summary>Re-reads all zones. Runs on full ticks only; normal ticks emit cached values.</summary>
    private void RefreshWmiZones(WmiZoneEntry[] zones)
    {
        if (zones.Length == 0) return;
        ManagementObjectSearcher? searcher = _wmiSearcher;
        if (searcher is null) return;
        try
        {
            for (int i = 0; i < zones.Length; i++) zones[i].LastValue = null;
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                try
                {
                    string? instance = mo["InstanceName"] as string;
                    if (instance is null) continue;
                    object? raw = mo["CurrentTemperature"];
                    if (raw is null) continue;
                    float celsius = Convert.ToSingle(raw, CultureInfo.InvariantCulture) / 10f - 273.15f;
                    if (float.IsNaN(celsius) || celsius <= 0.1f) continue;
                    for (int i = 0; i < zones.Length; i++)
                    {
                        if (string.Equals(zones[i].InstanceName, instance, StringComparison.OrdinalIgnoreCase))
                        {
                            zones[i].LastValue = celsius;
                            break;
                        }
                    }
                }
                catch { /* skip this instance */ }
                finally { mo.Dispose(); }
            }
        }
        catch
        {
            // Whole query failed this cycle; zones stay null and render as "—".
        }
    }

    /// <summary>"ACPI\ThermalZone\TZ00_0" → "TZ00".</summary>
    private static string ShortZoneName(string instanceName)
    {
        int slash = instanceName.LastIndexOf('\\');
        string tail = slash >= 0 ? instanceName.Substring(slash + 1) : instanceName;
        if (tail.EndsWith("_0", StringComparison.Ordinal)) tail = tail.Substring(0, tail.Length - 2);
        return tail.Length > 0 ? tail : instanceName;
    }

    // ---------- environment checks ----------

    private static bool ComputeIsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectPawnIo(out Version? version)
    {
        version = null;
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (uninstall is null) continue;
                string? displayVersion = uninstall.GetValue("DisplayVersion") as string;
                if (Version.TryParse(displayVersion, out Version? parsed)) version = parsed;
                return true;
            }
            catch { }
        }
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key is not null) return true;
        }
        catch { }
        try
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles) && Directory.Exists(Path.Combine(programFiles, "PawnIO")))
                return true;
        }
        catch { }
        return false;
    }

    // ---------- internal records ----------

    /// <summary>A throttle-detection input sensor paired with its owning node (for node-death checks).</summary>
    private readonly struct ThrottleInput
    {
        public readonly ISensor Sensor;
        public readonly IHardware Owner;

        public ThrottleInput(ISensor sensor, IHardware owner)
        {
            Sensor = sensor;
            Owner = owner;
        }
    }

    private sealed class SensorEntry
    {
        public readonly ISensor Sensor;
        public readonly IHardware Owner; // direct owner; updating it refreshes the sensor
        public readonly string Id;

        public SensorEntry(ISensor sensor, IHardware owner, string id)
        {
            Sensor = sensor;
            Owner = owner;
            Id = id;
        }
    }

    private sealed class WmiZoneEntry
    {
        public readonly string Id;
        public readonly string InstanceName;
        public float? LastValue; // touched on the polling thread only

        public WmiZoneEntry(string id, string instanceName)
        {
            Id = id;
            InstanceName = instanceName;
        }
    }
}
