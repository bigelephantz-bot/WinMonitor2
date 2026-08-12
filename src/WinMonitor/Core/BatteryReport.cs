using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace WinMonitor.Core;

/// <summary>Static battery facts that live sensors do not expose. All capacities are mWh.</summary>
public sealed record BatteryHealth(
    int DesignCapacityMwh,
    int FullChargeCapacityMwh,
    int CycleCount,
    string Chemistry,
    string Manufacturer)
{
    /// <summary>Remaining share of the original capacity, or null when the data is unusable.</summary>
    public double? HealthFraction =>
        DesignCapacityMwh > 0 && FullChargeCapacityMwh > 0
            ? Math.Clamp((double)FullChargeCapacityMwh / DesignCapacityMwh, 0d, 1d)
            : null;

    public double DesignWh => DesignCapacityMwh / 1000d;
    public double FullChargeWh => FullChargeCapacityMwh / 1000d;

    /// <summary>Many laptops (this LG gram included) report 0; treat that as "not provided".</summary>
    public bool HasCycleCount => CycleCount > 0;
}

/// <summary>
/// Reads the battery's design capacity, current full-charge capacity and cycle count.
///
/// LibreHardwareMonitor exposes a degradation percentage but not the absolute capacities behind
/// it, and Windows only surfaces those through the battery report. Rather than take a dependency
/// or P/Invoke the battery IOCTLs, this shells the built-in <c>powercfg /batteryreport /xml</c>
/// and parses it — the same numbers, no new components.
///
/// The report takes a second or two to generate, so it never runs on the startup path or the
/// poll thread: <see cref="RefreshAsync"/> is fired once in the background and the result cached.
/// Every failure path yields null; a machine with no battery simply has no data to show.
/// </summary>
public static class BatteryReport
{
    private static readonly object Gate = new();
    private static BatteryHealth? _cached;
    private static DateTime _lastAttemptUtc;

    /// <summary>Capacity changes over months, not minutes; re-reading more often is waste.</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>Last successful reading, or null until one completes (or if none is possible).</summary>
    public static BatteryHealth? Current
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>
    /// Generates and parses a battery report on a worker thread unless one was read recently.
    /// Fire-and-forget: callers read <see cref="Current"/> whenever they next render.
    /// </summary>
    public static void RefreshInBackground()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastAttemptUtc < MinRefreshInterval) return;
            _lastAttemptUtc = DateTime.UtcNow;
        }
        ThreadPool.QueueUserWorkItem(static _ =>
        {
            BatteryHealth? health = Read();
            if (health is null) return;
            lock (Gate) _cached = health;
            Diag.Log("battery", $"Health {health.HealthFraction:P1} " +
                $"(design {health.DesignWh:0.0} Wh, full {health.FullChargeWh:0.0} Wh, " +
                $"cycles {(health.HasCycleCount ? health.CycleCount.ToString(CultureInfo.InvariantCulture) : "n/a")})");
        });
    }

    private static BatteryHealth? Read()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinMonitor-battery-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", "/batteryreport /xml /output \"" + path + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process? process = Process.Start(psi))
            {
                if (process is null) return null;
                // Drain both pipes so a chatty powercfg cannot fill a buffer and deadlock.
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(20_000)) { try { process.Kill(true); } catch { } return null; }
            }

            if (!File.Exists(path)) return null;
            return Parse(XDocument.Load(path));
        }
        catch (Exception ex)
        {
            Diag.Log("battery", "Battery report unavailable", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Parses the first battery entry. The report is namespaced, so match on local names.</summary>
    private static BatteryHealth? Parse(XDocument doc)
    {
        XElement? battery = doc.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Battery", StringComparison.Ordinal));
        if (battery is null) return null;

        int design = ReadInt(battery, "DesignCapacity");
        int full = ReadInt(battery, "FullChargeCapacity");
        if (design <= 0 && full <= 0) return null;   // desktop, or a report without capacities

        return new BatteryHealth(
            design,
            full,
            ReadInt(battery, "CycleCount"),
            ReadText(battery, "Chemistry"),
            ReadText(battery, "Manufacturer"));

        static int ReadInt(XElement parent, string name)
        {
            string text = ReadText(parent, name);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        static string ReadText(XElement parent, string name)
        {
            XElement? child = parent.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal));
            return child?.Value.Trim() ?? "";
        }
    }
}
