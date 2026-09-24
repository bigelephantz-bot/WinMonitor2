using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinMonitor;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

internal static class ResourceProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        int seconds = ReadInt(args, "--seconds", 45);
        int warmup = ReadInt(args, "--warmup", 10);
        string output = ReadArg(args, "--out") ?? throw new ArgumentException("--out is required.");
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("Output already exists.");
        string previousConfig = ConfigStore.ConfigDirectory;
        FieldInfo backing = typeof(ConfigStore).GetField("<ConfigDirectory>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Config isolation unavailable.");
        string isolated = Path.Combine(Path.GetTempPath(), "WinMonitor-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        backing.SetValue(null, isolated);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Loc.Initialize("en");
            WinMonitor.UI.Theme.Initialize("light");
            Diag.Initialize(isolated);
            AppConfig config = ConfigStore.Load();
            config.Language = "en";
            config.ThemeMode = "light";
            config.PollIntervalMs = 1000;
            config.BatteryAdaptivePolling = false;
            config.HotkeyEnabled = false;
            config.StartWithWindows = false;
            config.StartMinimized = true;
            config.CloseToTray = true;
            config.ConfirmOnClose = false;
            config.ChartMinutes = 1;
            config.Logging.Enabled = false;
            BatteryReport.RefreshInBackground();
            using var context = new WinMonitorContext(config, true, enableSystemIntegration: false);
            using var probe = new ProbeRun(context, output, seconds, warmup,
                args.Contains("--retention-check", StringComparer.Ordinal));
            probe.Start();
            Application.Run(context);
            return probe.Succeeded ? 0 : 1;
        }
        finally
        {
            backing.SetValue(null, previousConfig);
            // The isolated directory is retained for diagnosis; never touch the user's real config.
        }
    }

    private static string? ReadArg(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ReadInt(string[] args, string key, int fallback)
        => int.TryParse(ReadArg(args, key), out int value) ? Math.Clamp(value, 5, 300) : fallback;

    private sealed class ProbeRun : IDisposable
    {
        private readonly WinMonitorContext _ctx;
        private readonly string _output;
        private readonly int _seconds, _warmup;
        private readonly bool _retentionCheck;
        private object? _retentionResult;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<PhaseResult> _results = new();
        private readonly List<Sample> _samples = new(300);
        private readonly string[] _order = { "tray", "main", "chart", "diagnostics", "diagnostics", "chart", "main", "tray" };
        private int _phase = -1;
        private double _phaseStart, _measurementStart, _previousTime, _initialCpu, _previousCpu;
        private long _previousAlloc;
        private long _initialPolls, _initialFailures;
        private int[] _initialCollections = new int[3];
        private bool _measuring;
        private string[] _selectedKinds = Array.Empty<string>();

        public bool Succeeded { get; private set; }

        public ProbeRun(WinMonitorContext context, string output, int seconds, int warmup, bool retentionCheck)
        {
            _ctx = context; _output = output; _seconds = seconds; _warmup = warmup;
            _retentionCheck = retentionCheck;
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            Console.WriteLine($"Probe starting: elevated={_ctx.Sensors.IsElevated}, logicalCPUs={Environment.ProcessorCount}, PID={Environment.ProcessId}");
            Console.WriteLine("Isolated config; startup registration, global hotkey and activation pipe disabled.");
            _timer.Start(); // Let the actual message loop and hardware warm up for 20 seconds.
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (_phase < 0)
                {
                    if (now < 20) return;
                    ConfigureSeries();
                    NextPhase();
                    return;
                }
                if (!_measuring)
                {
                    if (now - _phaseStart < _warmup) return;
                    _process.Refresh();
                    _measurementStart = _previousTime = now;
                    _initialCpu = _previousCpu = _process.TotalProcessorTime.TotalMilliseconds;
                    _previousAlloc = GC.GetTotalAllocatedBytes();
                    for (int i = 0; i < 3; i++) _initialCollections[i] = GC.CollectionCount(i);
                    SensorHealthSnapshot health = _ctx.Sensors.GetHealthSnapshot();
                    _initialPolls = health.SuccessfulPollCount;
                    _initialFailures = health.FailedPollCount;
                    _measuring = true;
                    return;
                }

                _process.Refresh();
                double cpu = _process.TotalProcessorTime.TotalMilliseconds;
                double elapsed = now - _previousTime;
                long allocated = GC.GetTotalAllocatedBytes();
                SensorHealthSnapshot state = _ctx.Sensors.GetHealthSnapshot();
                _samples.Add(new Sample(now - _measurementStart,
                    (cpu - _previousCpu) / (elapsed * 1000 * Environment.ProcessorCount) * 100,
                    _process.PrivateMemorySize64 / 1048576d, _process.WorkingSet64 / 1048576d,
                    GetGuiResources(_process.Handle, 0), GetGuiResources(_process.Handle, 1),
                    _process.HandleCount, (allocated - _previousAlloc) / elapsed,
                    state.LastPollDurationMs));
                _previousCpu = cpu; _previousTime = now; _previousAlloc = allocated;
                if (now - _measurementStart < _seconds) return;

                var phase = new PhaseResult(_phase / 4 + 1, _order[_phase],
                    now - _measurementStart, _samples.Count,
                    (cpu - _initialCpu) / ((now - _measurementStart) * 1000 * Environment.ProcessorCount) * 100,
                    Median(_samples.Select(s => s.PrivateMiB)),
                    Median(_samples.Select(s => s.WorkingSetMiB)),
                    _samples.Min(s => s.GdiObjects), _samples.Max(s => s.GdiObjects),
                    _samples.Min(s => s.UserObjects), _samples.Max(s => s.UserObjects),
                    _samples.Average(s => s.AllocatedBytesPerSecond),
                    Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - _initialCollections[i]).ToArray(),
                    state.SuccessfulPollCount - _initialPolls, state.FailedPollCount - _initialFailures,
                    SystemInformation.PowerStatus.PowerLineStatus.ToString(), _samples.ToArray());
                _results.Add(phase);
                Console.WriteLine($"Round {phase.Round} {phase.State}: CPU={phase.CpuPercent:F3}%, private={phase.PrivateMiBMedian:F1} MiB, GDI={phase.GdiMin}-{phase.GdiMax}, polls={phase.SuccessfulPolls}, failures={phase.FailedPolls}");
                if (_phase == _order.Length - 1)
                {
                    if (_retentionCheck) CheckRetention();
                    WriteResult();
                    Succeeded = true;
                    Stop();
                }
                else NextPhase();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Stop();
            }
        }

        private void ConfigureSeries()
        {
            SensorDescriptor[] selected = _ctx.Sensors.Descriptors
                .Where(d => _ctx.Stats.GetLatestValue(d.Id) is not null && d.Id != WellKnown.ThrottleSensorId)
                .OrderBy(d => d.Category == SensorCategory.Cpu && d.Quantity == SensorQuantity.Load ? 0 : 1)
                .Take(4).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("No reporting sensors; cannot measure a representative UI.");
            _selectedKinds = selected.Select(d => d.Category + "/" + d.Quantity).ToArray();
            _ctx.Config.ChartSensorIds = selected.Select(d => d.Id).ToList();
            _ctx.Config.Active.TrayIcons.Clear();
            _ctx.Config.Active.TrayIcons.Add(new TrayIconConfig
            {
                SensorIds = new List<string> { selected[0].Id }, ShowSparkline = true,
            });
            _ctx.ApplySettings();
            Console.WriteLine("Selected real reporting series: " + string.Join(", ", _selectedKinds));
        }

        private void NextPhase()
        {
            _phase++;
            if (Field<Form?>(_ctx, "_settingsForm") is { IsDisposed: false } settings) settings.Close();
            // Match the product's close-to-tray behavior: a hidden live form retains GDI/UI
            // resources and would overstate the cost of a tray-only run.
            if (Field<Form?>(_ctx, "_mainForm") is { IsDisposed: false } oldMain) oldMain.Close();
            string state = _order[_phase];
            if (state is "main" or "chart")
            {
                _ctx.ShowMainWindow();
                var main = Field<Form>(_ctx, "_mainForm");
                main.Size = new Size(900, 650);
                Field<ToolStripMenuItem>(main, "_showChartItem").Checked = state == "chart";
                Field<SplitContainer>(main, "_split").Panel2Collapsed = state != "chart";
                main.Activate();
            }
            else if (state == "diagnostics")
            {
                _ctx.ShowSettings();
                var form = Field<Form>(_ctx, "_settingsForm");
                Field<TabControl>(form, "_tabs").SelectedTab = Field<TabPage>(form, "_pageDiagnostics");
                form.Activate();
            }
            _ctx.UpdateActiveSensorSet();
            _samples.Clear();
            _measuring = false;
            _phaseStart = _clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Round {_phase / 4 + 1}, state={state}, warmup={_warmup}s, measure={_seconds}s");
        }

        private void WriteResult()
        {
            var result = new
            {
                CapturedAtUtc = DateTime.UtcNow,
                RuntimeVersion = Environment.Version.ToString(),
                RuntimeSettings = ReadRuntimeSettings(),
                AssemblyVersion = typeof(WinMonitorContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Elevated = _ctx.Sensors.IsElevated,
                LogicalProcessors = Environment.ProcessorCount,
                PollIntervalMilliseconds = _ctx.Config.PollIntervalMs,
                ChartMinutes = _ctx.Config.ChartMinutes,
                TrayIcons = 1,
                Sparkline = true,
                ReportingSeries = _selectedKinds,
                DescriptorCount = _ctx.Sensors.Descriptors.Count,
                WarmupSeconds = _warmup,
                MeasurementSeconds = _seconds,
                SampleIntervalSeconds = 1,
                DisabledIntegration = new[] { "startup registration", "global hotkey", "activation pipe" },
                RetentionDiagnostic = _retentionResult,
                Notes = "Actual polling and WinForms UI in an isolated host. Main/settings windows close between phases, matching close-to-tray. Process counters include sampling overhead. No forced GC; sequential phases retain process caches. State order reversed on round two. Non-elevated results do not represent full hardware-driver workload.",
                Phases = _results,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
            using var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("Result: " + _output);
        }

        private void Stop()
        {
            _timer.Stop();
            foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
                if (form is not SyncWindow && !form.IsDisposed) form.Close();
            _ctx.ExitApp();
        }

        // Explicit, optional diagnostic AFTER all timing samples. Never force collection in
        // the measured workload or in production; this only distinguishes delayed finalizers.
        private void CheckRetention()
        {
            _timer.Stop();
            object Snapshot()
            {
                _process.Refresh();
                return new
                {
                    PrivateMiB = _process.PrivateMemorySize64 / 1048576d,
                    ManagedMiB = GC.GetTotalMemory(false) / 1048576d,
                    Gdi = GetGuiResources(_process.Handle, 0),
                    User = GetGuiResources(_process.Handle, 1),
                    Handles = _process.HandleCount,
                };
            }
            object before = Snapshot();
            for (int i = 0; i < 2; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Application.DoEvents();
            }
            _retentionResult = new { Before = before, After = Snapshot() };
            Console.WriteLine("Post-timing retention diagnostic: " + JsonSerializer.Serialize(_retentionResult));
        }

        public void Dispose() { _timer.Dispose(); _process.Dispose(); }
    }

    private static T Field<T>(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().Name, name);
        return (T)field.GetValue(owner)!;
    }

    private static JsonElement ReadRuntimeSettings()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "WinMonitor.ResourceProbe.runtimeconfig.json")));
        return document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").Clone();
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        int mid = values.Length / 2;
        return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private sealed record Sample(double ElapsedSeconds, double CpuPercent, double PrivateMiB,
        double WorkingSetMiB, uint GdiObjects, uint UserObjects, int Handles,
        double AllocatedBytesPerSecond, long LastPollMilliseconds);
    private sealed record PhaseResult(int Round, string State, double Seconds, int Samples,
        double CpuPercent, double PrivateMiBMedian, double WorkingSetMiBMedian,
        uint GdiMin, uint GdiMax, uint UserMin, uint UserMax, double AllocatedBytesPerSecond,
        int[] GcCollections, long SuccessfulPolls, long FailedPolls, string PowerLineStatus, Sample[] Readings);
}
