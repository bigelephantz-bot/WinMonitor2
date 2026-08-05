using System.Globalization;
using System.Text;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

public sealed partial class SettingsForm
{
    private System.Windows.Forms.Timer? _diagnosticsTimer;
    private TextBox? _diagnosticsText;

    /// <summary>
    /// Builds a read-only support view. It deliberately reads the live service rather than the
    /// Settings draft: polling health must remain accurate even while the user has edits pending.
    /// </summary>
    private TabPage BuildDiagnosticsTab()
    {
        var page = NewTabPage(Loc.T("set.tab.diagnostics"));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = Loc.T("set.diag.hint"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
            ForeColor = Theme.SubtleText,
        };
        _diagnosticsText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = SystemFonts.MessageBoxFont,
            TabStop = false,
        };
        ApplyInputTheme(_diagnosticsText);

        var copy = new Button
        {
            Text = Loc.T("set.diag.copy"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 10, 0, 0),
        };
        copy.Click += (_, _) => CopyDiagnostics();

        SetOptionToolTip("tip.diagnostics.hint", hint, _diagnosticsText);
        SetOptionToolTip("tip.diagnostics.copy", copy);
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(_diagnosticsText, 0, 1);
        layout.Controls.Add(copy, 0, 2);
        page.Controls.Add(layout);

        EnsureDiagnosticsTimer();
        LoadDiagnosticsTab();
        return page;
    }

    private void EnsureDiagnosticsTimer()
    {
        if (_diagnosticsTimer is not null) return;
        _diagnosticsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _diagnosticsTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && _tabs.SelectedTab == _pageDiagnostics)
                LoadDiagnosticsTab();
        };
        _diagnosticsTimer.Start();
    }

    private void DisposeDiagnosticsTimer()
    {
        _diagnosticsTimer?.Stop();
        _diagnosticsTimer?.Dispose();
        _diagnosticsTimer = null;
    }

    private void LoadDiagnosticsTab()
    {
        if (_diagnosticsText is null || _diagnosticsText.IsDisposed) return;

        SensorHealthSnapshot health = _ctx.Sensors.GetHealthSnapshot();
        var sb = new StringBuilder(512);
        sb.Append(Loc.T("set.diag.status")).Append(": ")
            .Append(Loc.T(health.IsRunning ? "set.diag.running" : "set.diag.stopped")).AppendLine();
        sb.Append(Loc.T("set.diag.successful_polls")).Append(": ")
            .Append(health.SuccessfulPollCount.ToString("N0", CultureInfo.CurrentCulture)).AppendLine();
        sb.Append(Loc.T("set.diag.failed_polls")).Append(": ")
            .Append(health.FailedPollCount.ToString("N0", CultureInfo.CurrentCulture)).AppendLine();
        sb.Append(Loc.T("set.diag.node_failures")).Append(": ")
            .Append(health.NodeUpdateFailureCount.ToString("N0", CultureInfo.CurrentCulture)).AppendLine();
        sb.Append(Loc.T("set.diag.last_poll")).Append(": ")
            .Append(health.LastPollDurationMs.ToString("N0", CultureInfo.CurrentCulture))
            .Append(' ').Append(Loc.T("set.diag.milliseconds")).AppendLine();
        sb.Append(Loc.T("set.diag.last_snapshot")).Append(": ")
            .Append(health.LastSnapshotCount.ToString("N0", CultureInfo.CurrentCulture)).AppendLine();
        sb.Append(Loc.T("set.diag.descriptors")).Append(": ")
            .Append(health.DescriptorCount.ToString("N0", CultureInfo.CurrentCulture)).AppendLine();
        sb.Append(Loc.T("set.diag.last_success")).Append(": ")
            .Append(FormatDiagnosticTime(health.LastSuccessfulPollUtc)).AppendLine();
        sb.Append(Loc.T("set.diag.last_failure")).Append(": ")
            .Append(FormatDiagnosticTime(health.LastFailureUtc)).AppendLine();
        sb.Append(Loc.T("set.diag.failure_detail")).Append(": ")
            .Append(string.IsNullOrWhiteSpace(health.LastFailure) ? Loc.T("set.diag.none") : health.LastFailure).AppendLine();
        sb.AppendLine();
        sb.Append(Loc.T("set.diag.elevation")).Append(": ")
            .Append(Loc.T(_ctx.Sensors.IsElevated ? "set.diag.available" : "set.diag.unavailable")).AppendLine();
        sb.Append(Loc.T("set.diag.cpu_telemetry")).Append(": ")
            .Append(Loc.T(_ctx.Sensors.CpuTelemetryAvailable ? "set.diag.available" : "set.diag.unavailable")).AppendLine();
        sb.Append(Loc.T("set.diag.pawnio")).Append(": ")
            .Append(_ctx.Sensors.PawnIoDetected
                ? _ctx.Sensors.PawnIoVersion?.ToString() ?? Loc.T("set.diag.available")
                : Loc.T("set.diag.unavailable"));

        string text = sb.ToString();
        if (!string.Equals(_diagnosticsText.Text, text, StringComparison.Ordinal))
            _diagnosticsText.Text = text;
    }

    private static string FormatDiagnosticTime(DateTime? utc)
        => utc is { } value
            ? value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
            : Loc.T("set.diag.never");

    private void CopyDiagnostics()
    {
        if (_diagnosticsText is null || _diagnosticsText.TextLength == 0) return;
        try { Clipboard.SetText(_diagnosticsText.Text); }
        catch { /* Clipboard can be temporarily unavailable in a remote session. */ }
    }
}
