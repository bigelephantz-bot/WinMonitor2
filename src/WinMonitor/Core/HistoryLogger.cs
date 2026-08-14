using System.Globalization;
using System.Text;
using WinMonitor.Config;
using WinMonitor.Localization;

namespace WinMonitor.Core;

/// <summary>
/// Optional background CSV logging plus one-shot CSV exports.
/// Background log: one file per local day (winmonitor-yyyyMMdd.csv), one column per
/// descriptor, one row per logging interval (independent of the poll interval, and deferred to
/// the first complete snapshot at or after it so no column is blank for want of sampling),
/// buffered writer flushed every 30 s. Every IO operation is wrapped — logging must never crash
/// monitoring. Accept runs on the polling background thread; Dispose on the UI thread.
/// </summary>
public sealed class HistoryLogger : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly char[] CsvSpecialChars = { ',', '"', '\r', '\n' };
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private readonly Func<AppConfig> _configProvider;
    private readonly Func<IReadOnlyList<SensorDescriptor>> _descriptorProvider;
    private readonly object _gate = new();

    // Reused per logging tick (throttled to >= 1 s), never per poll tick.
    private readonly StringBuilder _row = new(512);
    private readonly Dictionary<string, float> _tickValues = new(StringComparer.Ordinal);

    private StreamWriter? _writer;
    private DateTime _fileDate;                        // local date the open file belongs to
    // Header text alone is not enough to identify a column layout: two different sensors can
    // have the same display name. Keep the ordered ids and names alongside every CSV header.
    private string? _layoutFingerprint;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private bool _disposed;

    public string LogDirectory { get; }

    public HistoryLogger(AppConfig config, Func<IReadOnlyList<SensorDescriptor>> descriptorProvider,
        string? logDirectory = null)
        : this(() => config, descriptorProvider, logDirectory)
    {
    }

    /// <summary>Uses a fully replaced configuration snapshot for each non-hot logging decision.</summary>
    /// <param name="logDirectory">
    /// Overrides the log location. The application always uses the default; the regression harness
    /// passes a temporary directory so the writer path can be exercised without touching real logs.
    /// </param>
    public HistoryLogger(Func<AppConfig> configProvider, Func<IReadOnlyList<SensorDescriptor>> descriptorProvider,
        string? logDirectory = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _descriptorProvider = descriptorProvider;
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(ConfigStore.ConfigDirectory, "logs")
            : logDirectory;
    }

    /// <summary>
    /// Background thread. No-op unless logging is enabled and the interval elapsed.
    ///
    /// <paramref name="complete"/> reports whether the tick sampled every descriptor. Smart polling
    /// narrows most ticks to the active set, so a row written from a partial snapshot would leave
    /// unrelated columns blank — indistinguishable, in the CSV, from a sensor that failed. When a
    /// row is due on a partial tick the logger asks for one full sweep instead and writes from the
    /// snapshot that produces, which costs one complete sweep per logging interval rather than
    /// keeping every SMART sensor active on every poll.
    /// </summary>
    /// <param name="requestCompleteSnapshot">
    /// Invoked when a due row cannot be written yet. Must be idempotent: it is called on every due
    /// tick until a complete snapshot arrives, which is what makes a lost or mistimed request
    /// self-correcting rather than a stalled log.
    /// </param>
    public void Accept(SensorSnapshot[] snapshots, bool complete, Action? requestCompleteSnapshot = null)
    {
        AppConfig config = _configProvider();
        if (_disposed) return;
        if (!config.Logging.Enabled)
        {
            lock (_gate)
            {
                if (!_disposed) SafeCloseWriter();
            }
            return;
        }

        var nowUtc = DateTime.UtcNow;
        int interval = Math.Max(1, config.Logging.IntervalSeconds);
        if ((nowUtc - _lastWriteUtc).TotalSeconds < interval) return;

        if (!complete)
        {
            requestCompleteSnapshot?.Invoke();
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var descriptors = _descriptorProvider();
                if (descriptors is null || descriptors.Count == 0) return;

                EnsureWriter(descriptors);
                if (_writer is null) return;

                _tickValues.Clear();
                for (int i = 0; i < snapshots.Length; i++)
                {
                    var s = snapshots[i];
                    if (s.HasValue) _tickValues[s.Id] = s.Value.GetValueOrDefault();
                }

                _row.Clear();
                _row.Append(DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                for (int i = 0; i < descriptors.Count; i++)
                {
                    _row.Append(',');
                    if (_tickValues.TryGetValue(descriptors[i].Id, out var v))
                    {
                        if (string.Equals(descriptors[i].Id, WellKnown.ThrottleSensorId,
                                          StringComparison.Ordinal))
                            _row.Append(v >= 0.5f ? "True" : "False");
                        else
                            _row.Append(v.ToString(CultureInfo.InvariantCulture));
                    }
                    // missing sample -> empty field
                }
                _writer.WriteLine(_row.ToString());
                _lastWriteUtc = nowUtc;

                if (nowUtc - _lastFlushUtc >= FlushInterval)
                {
                    _writer.Flush();
                    _lastFlushUtc = nowUtc;
                }
            }
            catch
            {
                // Disk full, locked file, provider hiccup... drop the writer and retry on
                // the next interval; monitoring must go on.
                SafeCloseWriter();
            }
        }
    }

    /// <summary>Deletes winmonitor-*.csv older than Logging.RetentionDays. Best-effort.</summary>
    public void CleanupRetention()
    {
        try
        {
            int days = _configProvider().Logging.RetentionDays;
            if (days <= 0 || !Directory.Exists(LogDirectory)) return;

            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "winmonitor-*.csv"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        TryDelete(GetLayoutPath(file));
                    }
                }
                catch { /* in use or already gone — skip this one */ }
            }
        }
        catch { /* retention cleanup is best-effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            SafeCloseWriter();
        }
    }

    // ---------- one-shot exports (exceptions propagate to the caller) ----------

    // A provider-based overload used to live here, materializing every sensor's full session
    // history in memory before writing. The disk-spool snapshot below replaced it: memory now
    // scales with descriptor count instead of session duration, so the old path was removed
    // rather than left as a same-named trap for callers reaching for the obvious overload.

    /// <summary>
    /// Streams a disk-backed session snapshot as a wide time series. Memory use depends only on
    /// descriptor count, not session duration; records are already in polling-time order.
    /// </summary>
    internal static string ExportTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors,
        SessionHistoryReadSnapshot snapshot)
    {
        // An unreadable spool must not look like an empty session: both produce a header and no
        // rows, and only one of them is a correct answer.
        if (!snapshot.IsReadable)
            throw new IOException(Loc.T("export.history_unavailable"));

        // Write beside the destination and swap. Truncating the target first — as `append: false`
        // does — destroys the user's previous export the moment the dialog is confirmed, so a
        // failure part-way through a long session leaves them with neither file.
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteTimeSeriesCsv(temp, descriptors, snapshot);
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
            return path;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void WriteTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors,
        SessionHistoryReadSnapshot snapshot)
    {
        var descriptorIndexById = new Dictionary<string, int>(descriptors.Count, StringComparer.Ordinal);
        for (int i = 0; i < descriptors.Count; i++)
            descriptorIndexById[descriptors[i].Id] = i;

        var columnsBySensorIndex = new int[snapshot.SensorIds.Length];
        Array.Fill(columnsBySensorIndex, -1);
        for (int i = 0; i < snapshot.SensorIds.Length; i++)
            if (descriptorIndexById.TryGetValue(snapshot.SensorIds[i], out int column))
                columnsBySensorIndex[i] = column;

        var values = new float[descriptors.Count];
        var present = new bool[descriptors.Count];
        var touchedColumns = new List<int>(descriptors.Count);
        using var writer = new StreamWriter(path, append: false, Utf8Bom);
        var sb = new StringBuilder(Math.Max(256, descriptors.Count * 40));
        sb.Append("Timestamp");
        for (int i = 0; i < descriptors.Count; i++)
        {
            sb.Append(',');
            AppendCsvField(sb, ExportColumnName(descriptors[i]));
        }
        writer.WriteLine(sb.ToString());

        long currentTicks = long.MinValue;
        foreach (SessionHistoryRecord record in snapshot.ReadRecords())
        {
            if (record.UtcTicks != currentTicks)
            {
                if (currentTicks != long.MinValue)
                    WriteSessionRow(writer, sb, currentTicks, descriptors, values, present);
                for (int i = 0; i < touchedColumns.Count; i++)
                    present[touchedColumns[i]] = false;
                touchedColumns.Clear();
                currentTicks = record.UtcTicks;
            }

            if ((uint)record.SensorIndex >= (uint)columnsBySensorIndex.Length) continue;
            int column = columnsBySensorIndex[record.SensorIndex];
            if (column < 0) continue;
            if (!present[column]) touchedColumns.Add(column);
            present[column] = true;
            values[column] = record.Value;
        }
        if (currentTicks != long.MinValue)
            WriteSessionRow(writer, sb, currentTicks, descriptors, values, present);

        writer.Flush();
    }

    private static void WriteSessionRow(StreamWriter writer, StringBuilder sb, long timestamp,
        IReadOnlyList<SensorDescriptor> descriptors, float[] values, bool[] present)
    {
        sb.Clear();
        sb.Append(new DateTime(timestamp, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        for (int i = 0; i < descriptors.Count; i++)
        {
            sb.Append(',');
            if (!present[i]) continue;
            if (string.Equals(descriptors[i].Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal))
                sb.Append(values[i] >= 0.5f ? "True" : "False");
            else
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteLine(sb.ToString());
    }

    private static string ExportColumnName(SensorDescriptor descriptor)
    {
        string quantity = string.Equals(descriptor.Id, WellKnown.ThrottleSensorId,
                                        StringComparison.Ordinal)
            ? "Boolean"
            : descriptor.Quantity.ToString();
        return descriptor.HardwareName + " / " + DisplayNameOf(descriptor) + " [" + quantity + "]";
    }

    // ---------- internals ----------

    /// <summary>
    /// (Re)opens the day file. A new file is started on date rollover or when the descriptor
    /// ids/names change. A sidecar fingerprint distinguishes layouts that happen to render the
    /// same CSV header, and suffixed files keep layouts separate across restarts.
    /// </summary>
    private void EnsureWriter(IReadOnlyList<SensorDescriptor> descriptors)
    {
        var today = DateTime.Now.Date;
        string layoutFingerprint = BuildLayoutFingerprint(descriptors);
        if (_writer is not null && _fileDate == today
            && string.Equals(_layoutFingerprint, layoutFingerprint, StringComparison.Ordinal))
            return;

        SafeCloseWriter();
        Directory.CreateDirectory(LogDirectory);

        string header = BuildHeader(descriptors);
        string baseName = "winmonitor-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        for (int suffix = 1; suffix <= 99; suffix++)
        {
            string path = Path.Combine(LogDirectory, suffix == 1
                ? baseName + ".csv"
                : baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture) + ".csv");
            string layoutPath = GetLayoutPath(path);
            bool emptyFile = false;

            if (File.Exists(path))
            {
                string? firstLine = ReadFirstLine(path);
                if (firstLine is null)
                {
                    // Unreadable or empty: only claim truly empty files.
                    long length;
                    try { length = new FileInfo(path).Length; } catch { length = -1; }
                    if (length != 0) continue;
                    emptyFile = true;
                }
                else if (firstLine != header
                    || !string.Equals(ReadLayoutFingerprint(layoutPath), layoutFingerprint, StringComparison.Ordinal))
                {
                    continue; // column layout differs — try the next suffix
                }
                else if (!EndsAtLineBoundary(path))
                {
                    // The previous run died mid-row (a crash, or a write that failed after a
                    // partial flush). Appending here would glue the next complete row onto that
                    // fragment and lose both. Leave the file exactly as it is — a torn last row
                    // is still readable evidence — and start the next suffix.
                    Diag.Log("logging", "CSV " + Path.GetFileName(path)
                        + " ends mid-row; continuing in the next file rather than appending");
                    continue;
                }
            }
            else if (File.Exists(layoutPath)
                && !string.Equals(ReadLayoutFingerprint(layoutPath), layoutFingerprint, StringComparison.Ordinal))
            {
                // A previous write may have stopped between writing the sidecar and opening
                // the CSV. Never reuse that name for a different layout.
                continue;
            }

            // Write the sidecar before creating a fresh CSV. If this fails, the caller drops
            // the writer and retries later rather than producing a data file with no identity.
            if (!File.Exists(path) || emptyFile)
                File.WriteAllText(layoutPath, layoutFingerprint, Utf8Bom);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, Utf8Bom);
            if (stream.Length == 0)
                _writer.WriteLine(header);
            _fileDate = today;
            _layoutFingerprint = layoutFingerprint;
            _lastFlushUtc = DateTime.UtcNow;
            return;
        }
        // 99 layout changes in one day: give up silently until tomorrow.
    }

    private static string BuildHeader(IReadOnlyList<SensorDescriptor> descriptors)
    {
        var sb = new StringBuilder(256);
        sb.Append("Timestamp");
        for (int i = 0; i < descriptors.Count; i++)
        {
            sb.Append(',');
            AppendCsvField(sb, DisplayNameOf(descriptors[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Produces an unambiguous, ordered layout identity. Length-prefixed fields avoid collisions
    /// from delimiters in a hardware name or user rename without adding ids to the human-facing
    /// CSV header.
    /// </summary>
    private static string BuildLayoutFingerprint(IReadOnlyList<SensorDescriptor> descriptors)
    {
        var sb = new StringBuilder(descriptors.Count * 64);
        for (int i = 0; i < descriptors.Count; i++)
        {
            SensorDescriptor d = descriptors[i];
            AppendFingerprintField(sb, d.Id);
            AppendFingerprintField(sb, d.HardwareName);
            AppendFingerprintField(sb, d.Name);
            AppendFingerprintField(sb, DisplayNameOf(d));
            // Quantity is what the numbers in the column MEAN, and it is not implied by the id.
            // An EC sensor id is "/ec/reg/XX/{Kind}", so switching one from Fan to Temperature in
            // the EC Explorer keeps the id, the name and the header text identical while the
            // column silently changes from RPM to °C. Identity has to include the unit.
            AppendFingerprintField(sb, d.Quantity.ToString());
        }
        return sb.ToString();
    }

    private static void AppendFingerprintField(StringBuilder sb, string value)
    {
        value ??= string.Empty;
        sb.Append(value.Length);
        sb.Append(':');
        sb.Append(value);
        sb.Append('|');
    }

    private static string DisplayNameOf(SensorDescriptor d)
        => string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName;

    /// <summary>RFC-4180 escaping: quote fields containing comma/quote/newline, double inner quotes.</summary>
    private static void AppendCsvField(StringBuilder sb, string field)
    {
        if (field.IndexOfAny(CsvSpecialChars) < 0)
        {
            sb.Append(field);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>
    /// True when the file is empty or its last byte ends a line. Only a byte check is safe here:
    /// the file is UTF-8 with a BOM and may end mid-multi-byte-character, which a text reader
    /// would silently paper over.
    /// </summary>
    private static bool EndsAtLineBoundary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return true;
            stream.Position = stream.Length - 1;
            return stream.ReadByte() == '\n';
        }
        catch
        {
            // Unreadable: treat it as unsafe to append to rather than assuming it is intact.
            return false;
        }
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    private static string GetLayoutPath(string csvPath) => csvPath + ".layout";

    private static string? ReadLayoutFingerprint(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private void SafeCloseWriter()
    {
        if (_writer is null) return;
        try { _writer.Flush(); } catch { }
        try { _writer.Dispose(); } catch { }
        _writer = null;
        _layoutFingerprint = null;
    }
}
