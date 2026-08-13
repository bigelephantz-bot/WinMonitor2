using System.Text.Json;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

// Diagnostic tool: dump every sensor WinMonitor's backend sees on this machine.
Loc.Initialize("en");

if (args.Any(a => string.Equals(a, "--acpi-ec-fields", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine(AmlEcFieldScanner.CreateReport());
    return;
}

if (args.Any(a => string.Equals(a, "--ec-probe", StringComparison.OrdinalIgnoreCase)))
{
    Environment.ExitCode = RunEcProbe(args);
    return;
}

var config = new AppConfig { PollIntervalMs = 1000 };
using var svc = new SensorService(config);
svc.Start();

Console.WriteLine(
    $"Elevated: {svc.IsElevated}   PawnIO: {svc.PawnIoDetected}"
    + (svc.PawnIoVersion is { } version ? $" ({version})" : "")
    + $"   CPU telemetry: {svc.CpuTelemetryAvailable}");
Console.WriteLine($"Descriptors: {svc.Descriptors.Count}");
Console.WriteLine();

var latest = new Dictionary<string, float?>();
var gate = new ManualResetEventSlim(false);
int ticks = 0;
svc.SnapshotUpdated += (snaps, _) =>
{
    foreach (var s in snaps) latest[s.Id] = s.Value;
    if (Interlocked.Increment(ref ticks) >= 3) gate.Set();
};
gate.Wait(TimeSpan.FromSeconds(10));
SensorHealthSnapshot health = svc.GetHealthSnapshot();
svc.Stop();

Console.WriteLine();
Console.WriteLine("=== Service health ===");
Console.WriteLine($"Successful polls: {health.SuccessfulPollCount}   Failed polls: {health.FailedPollCount}");
Console.WriteLine($"Node update failures: {health.NodeUpdateFailureCount}   Last tick: {health.LastPollDurationMs} ms");
Console.WriteLine($"Last snapshot: {health.LastSnapshotCount} values   Descriptors: {health.DescriptorCount}");
if (health.LastFailureUtc is { } failureUtc)
    Console.WriteLine($"Last failure: {failureUtc:O}  {health.LastFailure}");

if (args.Any(a => string.Equals(a, "--report", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine();
    Console.WriteLine("=== Backend report ===");
    Console.WriteLine(svc.GetBackendReport());
}

foreach (var group in svc.Descriptors.GroupBy(d => d.Category).OrderBy(g => g.Key))
{
    Console.WriteLine($"=== {group.Key} ===");
    foreach (var d in group)
    {
        latest.TryGetValue(d.Id, out var v);
        string value = string.Equals(d.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal)
            ? v is { } state && !float.IsNaN(state) ? (state >= 0.5f ? "True" : "False") : "—"
            : Units.Format(d.Quantity, v);
        // Print the resolved display name, not the raw one: that is what the UI shows, including
        // the quantity suffix added when one hardware reuses a name across quantities.
        string shown = config.DisplayNameFor(d);
        Console.WriteLine($"  [{d.Quantity,-11}] {d.HardwareName} / {shown,-34} = {value}   ({d.Id})");
    }
}

static int RunEcProbe(string[] args)
{
    int idleSeconds = ReadIntArg(args, "--idle", 20, 5, 300);
    int loadSeconds = ReadIntArg(args, "--load", 60, 10, 600);
    int cooldownSeconds = ReadIntArg(args, "--cooldown", 60, 10, 600);
    int intervalMs = ReadIntArg(args, "--interval", 1000, 500, 5000);
    int[] focusedRegisters = ReadRegisterArg(args);
    string outputPath = ReadStringArg(args, "--out")
        ?? Path.Combine(Environment.CurrentDirectory, "ec-probe.json");
    outputPath = Path.GetFullPath(outputPath);

    var config = new AppConfig { PollIntervalMs = Math.Max(1000, intervalMs) };
    var latest = new Dictionary<string, float?>(StringComparer.Ordinal);
    var latestLock = new object();

    using var service = new SensorService(config);
    service.SnapshotUpdated += (snapshots, _) =>
    {
        lock (latestLock)
        {
            foreach (var snapshot in snapshots)
                latest[snapshot.Id] = snapshot.Value;
        }
    };

    try
    {
        service.Start();
        if (!service.IsElevated)
            return WriteFailure(outputPath, "administrator_required");

        if (!service.Ec.Initialize())
            return WriteFailure(outputPath, "ec_unavailable:" + (service.Ec.UnavailableReason ?? "unknown"));

        SensorDescriptor[] cpuTemps = service.Descriptors
            .Where(d => d.Category == SensorCategory.Cpu && d.Quantity == SensorQuantity.Temperature)
            .ToArray();
        SensorDescriptor[] cpuLoads = service.Descriptors
            .Where(d => d.Category == SensorCategory.Cpu && d.Quantity == SensorQuantity.Load)
            .ToArray();

        var frames = new List<EcProbeFrame>(
            (idleSeconds + loadSeconds + cooldownSeconds) * 1000 / intervalMs + 3);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        CapturePhase("idle", idleSeconds, intervalMs, service.Ec, cpuTemps, cpuLoads,
            latest, latestLock, frames, clock, focusedRegisters);

        using var loadStop = new CancellationTokenSource();
        Task[] loadWorkers = StartCpuLoad(loadStop.Token);
        try
        {
            CapturePhase("load", loadSeconds, intervalMs, service.Ec, cpuTemps, cpuLoads,
                latest, latestLock, frames, clock, focusedRegisters);
        }
        finally
        {
            loadStop.Cancel();
            try { Task.WaitAll(loadWorkers, 5000); } catch { }
        }

        CapturePhase("cooldown", cooldownSeconds, intervalMs, service.Ec, cpuTemps, cpuLoads,
            latest, latestLock, frames, clock, focusedRegisters);

        var report = new EcProbeReport
        {
            CreatedUtc = DateTime.UtcNow,
            Machine = ReadMachineIdentity(),
            IdleSeconds = idleSeconds,
            LoadSeconds = loadSeconds,
            CooldownSeconds = cooldownSeconds,
            IntervalMs = intervalMs,
            FocusedRegisters = focusedRegisters.Select(r => $"0x{r:X2}").ToArray(),
            CpuTemperatureSensors = cpuTemps.Select(d => $"{d.HardwareName} / {d.Name} ({d.Id})").ToArray(),
            CpuLoadSensors = cpuLoads.Select(d => $"{d.HardwareName} / {d.Name} ({d.Id})").ToArray(),
            Frames = frames,
            Candidates = AnalyzeCandidates(frames),
        };

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, EcProbeJson.Options));
        Console.WriteLine(outputPath);
        return 0;
    }
    catch (Exception ex)
    {
        return WriteFailure(outputPath, ex.GetType().Name + ":" + ex.Message);
    }
    finally
    {
        try { service.Stop(); } catch { }
    }
}

static void CapturePhase(
    string phase,
    int durationSeconds,
    int intervalMs,
    EmbeddedController ec,
    SensorDescriptor[] cpuTemps,
    SensorDescriptor[] cpuLoads,
    Dictionary<string, float?> latest,
    object latestLock,
    List<EcProbeFrame> frames,
    System.Diagnostics.Stopwatch clock,
    int[] focusedRegisters)
{
    long phaseEnd = clock.ElapsedMilliseconds + durationSeconds * 1000L;
    while (clock.ElapsedMilliseconds < phaseEnd)
    {
        long sampleStarted = clock.ElapsedMilliseconds;
        byte[] values;
        bool[] ok;
        if (focusedRegisters.Length == 0)
        {
            values = ec.Dump(out ok);
        }
        else
        {
            byte[] sparse = ec.ReadRegisters(focusedRegisters, out bool[] sparseOk, 100);
            values = new byte[256];
            ok = new bool[256];
            for (int i = 0; i < focusedRegisters.Length; i++)
            {
                int register = focusedRegisters[i];
                values[register] = sparse[i];
                ok[register] = sparseOk[i];
            }
        }

        float? cpuTemp;
        float? cpuLoad;
        lock (latestLock)
        {
            cpuTemp = MaxValue(cpuTemps, latest);
            cpuLoad = MaxValue(cpuLoads, latest);
        }

        frames.Add(new EcProbeFrame
        {
            Phase = phase,
            ElapsedMs = sampleStarted,
            CpuTemperature = cpuTemp,
            CpuLoad = cpuLoad,
            Values = values,
            Ok = ok,
        });

        long sleepMs = Math.Min(intervalMs, phaseEnd - clock.ElapsedMilliseconds);
        if (sleepMs > 0) Thread.Sleep((int)sleepMs);
    }
}

static float? MaxValue(SensorDescriptor[] descriptors, Dictionary<string, float?> latest)
{
    float? best = null;
    foreach (var descriptor in descriptors)
    {
        if (!latest.TryGetValue(descriptor.Id, out float? value)
            || value is not { } number
            || float.IsNaN(number))
        {
            continue;
        }
        if (best is null || number > best.Value) best = number;
    }
    return best;
}

static Task[] StartCpuLoad(CancellationToken token)
{
    int workerCount = Math.Max(1, Environment.ProcessorCount);
    var workers = new Task[workerCount];
    for (int worker = 0; worker < workers.Length; worker++)
    {
        int seed = worker + 1;
        workers[worker] = Task.Factory.StartNew(() =>
        {
            ulong value = 0x9E3779B97F4A7C15UL ^ (uint)seed;
            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < 100_000; i++)
                {
                    value ^= value << 13;
                    value ^= value >> 7;
                    value ^= value << 17;
                }
                EcProbeLoadSink.Value = value;
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    return workers;
}

static List<EcProbeCandidate> AnalyzeCandidates(List<EcProbeFrame> frames)
{
    var candidates = new List<EcProbeCandidate>(256 * 3);
    for (int register = 0; register < 256; register++)
        AddCandidate(register, EcProbeEncoding.Byte);
    for (int register = 0; register < 255; register++)
    {
        AddCandidate(register, EcProbeEncoding.Le16);
        AddCandidate(register, EcProbeEncoding.Be16);
    }

    candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
    if (candidates.Count > 80) candidates.RemoveRange(80, candidates.Count - 80);
    return candidates;

    void AddCandidate(int register, EcProbeEncoding encoding)
    {
        var samples = new List<(EcProbeFrame Frame, double Value)>(frames.Count);
        foreach (EcProbeFrame frame in frames)
        {
            if (!TryRead(frame, register, encoding, out double value)) continue;
            samples.Add((frame, value));
        }
        if (samples.Count < Math.Max(10, frames.Count * 3 / 4)) return;

        double[] all = samples.Select(s => s.Value).ToArray();
        double min = all.Min();
        double max = all.Max();
        double range = max - min;
        double minimumRange = encoding == EcProbeEncoding.Byte ? 3 : 30;
        if (range < minimumRange) return;

        double[] idle = SteadyValues(samples, "idle");
        double[] load = SteadyValues(samples, "load");
        double[] cooldown = SteadyValues(samples, "cooldown");
        if (idle.Length < 3 || load.Length < 3 || cooldown.Length < 3) return;

        double idleMedian = Median(idle);
        double loadMedian = Median(load);
        double cooldownMedian = Median(cooldown);
        double phaseDelta = loadMedian - idleMedian;
        double recovery = Math.Abs(loadMedian - idleMedian) < 1e-9
            ? 0
            : 1.0 - Math.Min(1.0,
                Math.Abs(cooldownMedian - idleMedian) / Math.Abs(loadMedian - idleMedian));
        double tempCorrelation = Pearson(samples
            .Where(s => s.Frame.CpuTemperature is not null)
            .Select(s => (s.Value, (double)s.Frame.CpuTemperature!.Value)));
        double loadCorrelation = Pearson(samples
            .Where(s => s.Frame.CpuLoad is not null)
            .Select(s => (s.Value, (double)s.Frame.CpuLoad!.Value)));

        bool plausibleDirectRpm = encoding != EcProbeEncoding.Byte
            && all.Count(v => v == 0 || v is >= 300 and <= 12_000) >= all.Length * 0.9;
        bool plausibleScaledByte = encoding == EcProbeEncoding.Byte
            && max <= 120
            && phaseDelta > 2;
        bool plausiblePeriod = encoding != EcProbeEncoding.Byte
            && all.Count(v => v == 0 || (v > 0 && 1_000_000.0 / v is >= 300 and <= 12_000))
                >= all.Length * 0.9;

        double normalizedDelta = Math.Min(2.0, Math.Abs(phaseDelta) / Math.Max(minimumRange, range * 0.25));
        double correlation = Math.Max(Math.Abs(tempCorrelation), Math.Abs(loadCorrelation));
        double score = normalizedDelta * 2.0 + correlation + Math.Max(0, recovery);
        if (plausibleDirectRpm) score += 2.0;
        if (plausibleScaledByte) score += 1.5;
        if (plausiblePeriod && phaseDelta < 0) score += 1.0;

        string suggestedKind = "RawByte";
        double suggestedScale = 1;
        double? suggestedDivisor = null;
        if (plausibleDirectRpm)
        {
            suggestedKind = "RpmDirect";
        }
        else if (plausibleScaledByte)
        {
            suggestedScale = 100;
        }
        else if (plausiblePeriod && phaseDelta < 0)
        {
            suggestedKind = "RpmDivided";
            suggestedDivisor = 1_000_000;
        }
        else if (encoding != EcProbeEncoding.Byte)
        {
            suggestedKind = "Word";
        }

        candidates.Add(new EcProbeCandidate
        {
            Register = register,
            RegisterHex = $"0x{register:X2}",
            Encoding = encoding.ToString(),
            AvailableRatio = samples.Count / (double)frames.Count,
            Minimum = min,
            Maximum = max,
            IdleMedian = idleMedian,
            LoadMedian = loadMedian,
            CooldownMedian = cooldownMedian,
            PhaseDelta = phaseDelta,
            Recovery = recovery,
            TemperatureCorrelation = tempCorrelation,
            LoadCorrelation = loadCorrelation,
            SuggestedKind = suggestedKind,
            SuggestedScale = suggestedScale,
            SuggestedDivisor = suggestedDivisor,
            Score = score,
        });
    }
}

static bool TryRead(EcProbeFrame frame, int register, EcProbeEncoding encoding, out double value)
{
    value = 0;
    if (register >= frame.Values.Length || register >= frame.Ok.Length || !frame.Ok[register])
        return false;
    int first = frame.Values[register];
    if (encoding == EcProbeEncoding.Byte)
    {
        value = first;
        return true;
    }
    if (register + 1 >= frame.Values.Length
        || register + 1 >= frame.Ok.Length
        || !frame.Ok[register + 1])
    {
        return false;
    }
    int second = frame.Values[register + 1];
    value = encoding == EcProbeEncoding.Le16
        ? first | (second << 8)
        : (first << 8) | second;
    return true;
}

static double[] SteadyValues(List<(EcProbeFrame Frame, double Value)> samples, string phase)
{
    var phaseSamples = samples.Where(s => s.Frame.Phase == phase).ToArray();
    if (phaseSamples.Length == 0) return Array.Empty<double>();
    int skip = phaseSamples.Length / 2;
    return phaseSamples.Skip(skip).Select(s => s.Value).ToArray();
}

static double Median(double[] values)
{
    if (values.Length == 0) return double.NaN;
    double[] sorted = (double[])values.Clone();
    Array.Sort(sorted);
    int middle = sorted.Length / 2;
    return sorted.Length % 2 == 0
        ? (sorted[middle - 1] + sorted[middle]) / 2.0
        : sorted[middle];
}

static double Pearson(IEnumerable<(double X, double Y)> source)
{
    double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
    int count = 0;
    foreach ((double x, double y) in source)
    {
        sumX += x;
        sumY += y;
        sumXX += x * x;
        sumYY += y * y;
        sumXY += x * y;
        count++;
    }
    if (count < 5) return 0;
    double dx = count * sumXX - sumX * sumX;
    double dy = count * sumYY - sumY * sumY;
    if (dx <= 1e-9 || dy <= 1e-9) return 0;
    return (count * sumXY - sumX * sumY) / Math.Sqrt(dx * dy);
}

static string ReadMachineIdentity()
{
    try
    {
        using Microsoft.Win32.RegistryKey? bios =
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string manufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? "";
        string product = bios?.GetValue("SystemProductName")?.ToString() ?? "";
        string board = bios?.GetValue("BaseBoardProduct")?.ToString() ?? "";
        return string.Join(" / ", new[] { manufacturer, product, board }.Where(s => s.Length > 0));
    }
    catch
    {
        return Environment.MachineName;
    }
}

static int WriteFailure(string outputPath, string error)
{
    try
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new { Error = error }, EcProbeJson.Options));
    }
    catch { }
    Console.Error.WriteLine(error);
    return 2;
}

static int ReadIntArg(string[] args, string name, int fallback, int minimum, int maximum)
{
    for (int i = 0; i + 1 < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out int value))
        {
            return Math.Clamp(value, minimum, maximum);
        }
    }
    return fallback;
}

static string? ReadStringArg(string[] args, string name)
{
    for (int i = 0; i + 1 < args.Length; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static int[] ReadRegisterArg(string[] args)
{
    string? text = ReadStringArg(args, "--registers");
    if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
    var registers = new SortedSet<int>();
    foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        string token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
        if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int register)
            && register is >= 0 and <= 0xFF)
        {
            registers.Add(register);
        }
    }
    return registers.ToArray();
}

internal enum EcProbeEncoding
{
    Byte,
    Le16,
    Be16,
}

internal sealed class EcProbeFrame
{
    public string Phase { get; set; } = "";
    public long ElapsedMs { get; set; }
    public float? CpuTemperature { get; set; }
    public float? CpuLoad { get; set; }
    public byte[] Values { get; set; } = Array.Empty<byte>();
    public bool[] Ok { get; set; } = Array.Empty<bool>();
}

internal sealed class EcProbeCandidate
{
    public int Register { get; set; }
    public string RegisterHex { get; set; } = "";
    public string Encoding { get; set; } = "";
    public double AvailableRatio { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double IdleMedian { get; set; }
    public double LoadMedian { get; set; }
    public double CooldownMedian { get; set; }
    public double PhaseDelta { get; set; }
    public double Recovery { get; set; }
    public double TemperatureCorrelation { get; set; }
    public double LoadCorrelation { get; set; }
    public string SuggestedKind { get; set; } = "";
    public double SuggestedScale { get; set; }
    public double? SuggestedDivisor { get; set; }
    public double Score { get; set; }
}

internal sealed class EcProbeReport
{
    public DateTime CreatedUtc { get; set; }
    public string Machine { get; set; } = "";
    public int IdleSeconds { get; set; }
    public int LoadSeconds { get; set; }
    public int CooldownSeconds { get; set; }
    public int IntervalMs { get; set; }
    public string[] FocusedRegisters { get; set; } = Array.Empty<string>();
    public string[] CpuTemperatureSensors { get; set; } = Array.Empty<string>();
    public string[] CpuLoadSensors { get; set; } = Array.Empty<string>();
    public List<EcProbeFrame> Frames { get; set; } = new();
    public List<EcProbeCandidate> Candidates { get; set; } = new();
}

internal static class EcProbeLoadSink
{
    public static ulong Value;
}

internal static class EcProbeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };
}
