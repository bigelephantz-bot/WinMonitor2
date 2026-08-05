using System.Drawing.Drawing2D;
using System.Globalization;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

/// <summary>
/// Describes a plotted sensor. Thresholds and history values are already converted to the
/// active display unit by the caller.
/// </summary>
public readonly record struct ChartSeriesSource(
    string Id,
    string Name,
    Color Color,
    float Yellow,
    float Red,
    SensorQuantity Quantity,
    bool IsBoolean);

/// <summary>
/// Pure GDI+ history chart. Quantities with incompatible units are rendered in separate
/// panes with independent Y scales. Percentage-based quantities share one pane. Series use
/// both color and marker shape, and line hit testing exposes the sensor name in a tooltip.
/// MainForm owns the refresh cadence and calls <see cref="RefreshData"/>.
/// </summary>
public sealed class ChartControl : Control
{
    private const int LeftPad = 54;
    private const int RightPad = 8;
    private const int TopPad = 5;
    private const int BottomPad = 6;
    private const int PaneGap = 5;
    private const int PaneHeaderHeight = 17;
    private const int LegendGap = 10;
    private const float MarkerRadius = 3.5f;
    private const float HitTolerance = 7f;

    private enum ChartScaleGroup
    {
        Temperature,
        Power,
        Fan,
        Boolean,
        Percent,
        Frequency,
        Voltage,
        Data,
    }

    private enum MarkerShape
    {
        Circle,
        Square,
        Triangle,
        Diamond,
        Cross,
        X,
    }

    private readonly List<ChartSeriesSource> _sources = new();
    private readonly List<TimedValue[]?> _histories = new();
    private readonly List<long> _historyVersions = new();
    private readonly List<PointF[]> _pointBuffers = new();
    private readonly List<int> _pointCounts = new();
    private readonly List<ChartScaleGroup> _panes = new();
    private readonly PointF[] _trianglePoints = new PointF[3];
    private readonly PointF[] _diamondPoints = new PointF[4];
    private readonly ToolTip _hoverTip;
    private Func<string, long, HistoryReadResult>? _historyProvider;
    private int _windowMinutes = 10;
    private int _hoverSeries = -1;

    private readonly Dictionary<Color, Pen> _seriesPens = new();
    private readonly Dictionary<Color, SolidBrush> _seriesBrushes = new();
    private Pen? _gridPen;
    private Pen? _borderPen;
    private SolidBrush? _subtleBrush;

    public ChartControl()
    {
        SetStyle(ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.ChartBack;
        TabStop = false;
        _hoverTip = new ToolTip
        {
            InitialDelay = 100,
            ReshowDelay = 50,
            AutoPopDelay = 5000,
            ShowAlways = true,
        };
    }

    /// <summary>Replaces the plotted series and rebuilds the quantity-pane layout.</summary>
    public void SetSources(IReadOnlyList<ChartSeriesSource> sources,
                           Func<string, long, HistoryReadResult> historyProvider,
                           int windowMinutes)
    {
        _hoverTip.Hide(this);
        _hoverSeries = -1;
        _sources.Clear();
        _histories.Clear();
        _historyVersions.Clear();
        _pointBuffers.Clear();
        _pointCounts.Clear();
        _panes.Clear();

        for (int i = 0; i < sources.Count; i++)
        {
            ChartSeriesSource source = sources[i];
            _sources.Add(source);
            _histories.Add(null);
            _historyVersions.Add(-1);
            _pointBuffers.Add(Array.Empty<PointF>());
            _pointCounts.Add(0);
        }

        AddPaneIfPresent(ChartScaleGroup.Temperature);
        AddPaneIfPresent(ChartScaleGroup.Power);
        AddPaneIfPresent(ChartScaleGroup.Fan);
        AddPaneIfPresent(ChartScaleGroup.Boolean);
        AddPaneIfPresent(ChartScaleGroup.Percent);
        AddPaneIfPresent(ChartScaleGroup.Frequency);
        AddPaneIfPresent(ChartScaleGroup.Voltage);
        AddPaneIfPresent(ChartScaleGroup.Data);

        _historyProvider = historyProvider;
        _windowMinutes = Math.Clamp(windowMinutes, 1, 240);
        RefreshData();
    }

    /// <summary>Re-fetches changed histories and schedules a repaint.</summary>
    public void RefreshData()
    {
        var provider = _historyProvider;
        if (provider is not null)
        {
            for (int i = 0; i < _sources.Count && i < _histories.Count; i++)
            {
                try
                {
                    HistoryReadResult result = provider(_sources[i].Id, _historyVersions[i]);
                    if (result.Values is not null)
                    {
                        _histories[i] = result.Values;
                        _historyVersions[i] = result.Version;
                    }
                }
                catch
                {
                    // Retain the latest valid history; a provider failure must not break painting.
                }
            }
        }
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint clears the double buffer, so the default erase only adds flicker.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        if (BackColor.ToArgb() != Theme.ChartBack.ToArgb())
            BackColor = Theme.ChartBack;
        g.Clear(Theme.ChartBack);

        for (int i = 0; i < _pointCounts.Count; i++)
            _pointCounts[i] = 0;

        if (_sources.Count == 0 || _panes.Count == 0)
        {
            DrawEmptyText(g);
            return;
        }

        int legendWidth = Math.Clamp(Width / 4, 120, 220);
        int plotWidth = Width - LeftPad - RightPad - LegendGap - legendWidth;
        if (plotWidth < 90)
        {
            legendWidth = Math.Max(80, Width - LeftPad - RightPad - LegendGap - 90);
            plotWidth = Width - LeftPad - RightPad - LegendGap - legendWidth;
        }
        int availableHeight = Height - TopPad - BottomPad - PaneGap * (_panes.Count - 1);
        int paneHeight = availableHeight / _panes.Count;
        if (plotWidth < 40 || paneHeight < 10)
            return;

        DateTime now = DateTime.UtcNow;
        double windowSeconds = _windowMinutes * 60.0;
        DateTime start = now.AddSeconds(-windowSeconds);
        Pen gridPen = EnsurePen(ref _gridPen, Theme.GridLine);
        Pen borderPen = EnsurePen(ref _borderPen, Theme.Border);
        SolidBrush subtleBrush = EnsureBrush(ref _subtleBrush, Theme.SubtleText);

        SmoothingMode oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool anyHistory = false;
        for (int paneIndex = 0; paneIndex < _panes.Count; paneIndex++)
        {
            ChartScaleGroup group = _panes[paneIndex];
            int paneTop = TopPad + paneIndex * (paneHeight + PaneGap);
            int headerHeight = paneHeight >= 36 ? PaneHeaderHeight : 0;
            var plot = new Rectangle(LeftPad, paneTop + headerHeight, plotWidth,
                                     paneHeight - headerHeight);

            if (headerHeight > 0)
            {
                string unit = ScaleUnit(group);
                string paneTitle = Loc.T(ScaleLabelKey(group))
                    + (unit.Length > 0 ? " (" + unit + ")" : string.Empty);
                g.DrawString(paneTitle, Font, subtleBrush, plot.Left, paneTop);
            }

            if (!TryGetRange(group, start, out float min, out float max))
            {
                g.DrawRectangle(borderPen, plot);
                continue;
            }
            anyHistory = true;

            ExpandRange(group, ref min, ref max);
            float range = max - min;
            if (group == ChartScaleGroup.Boolean)
            {
                for (int state = 0; state <= 1; state++)
                {
                    float y = plot.Bottom - (state - min) / range * plot.Height;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    string label = Loc.T(state == 1 ? "boolean.true" : "boolean.false");
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, subtleBrush, plot.Left - size.Width - 3f,
                                 y - size.Height * 0.5f);
                }
            }
            else
            {
                float gridStep = NiceStep(range / 4f);
                for (float value = MathF.Ceiling(min / gridStep) * gridStep;
                     value <= max + gridStep * 0.01f;
                     value += gridStep)
                {
                    float y = plot.Bottom - (value - min) / range * plot.Height;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    string label = FormatAxisValue(value, gridStep);
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, subtleBrush, plot.Left - size.Width - 3f,
                                 y - size.Height * 0.5f);
                }
            }

            int minuteStep = _windowMinutes <= 10 ? 1 : _windowMinutes <= 30 ? 5 : 10;
            for (int minute = 0; minute <= _windowMinutes; minute += minuteStep)
            {
                float x = plot.Right - (float)(minute * 60.0 / windowSeconds) * plot.Width;
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            }
            g.DrawRectangle(borderPen, plot);

            for (int seriesIndex = 0; seriesIndex < _sources.Count; seriesIndex++)
            {
                ChartSeriesSource source = _sources[seriesIndex];
                if (ScaleGroupFor(source) != group)
                    continue;
                DrawSeries(g, seriesIndex, source, plot, start, windowSeconds, min, range);
            }
        }

        DrawLegend(g, LeftPad + plotWidth + LegendGap, TopPad, legendWidth,
                   Height - TopPad - BottomPad);
        g.SmoothingMode = oldMode;

        if (!anyHistory)
            DrawEmptyText(g);
    }

    private void DrawSeries(Graphics g, int seriesIndex, ChartSeriesSource source,
                            Rectangle plot, DateTime start, double windowSeconds,
                            float min, float range)
    {
        TimedValue[]? history = _histories[seriesIndex];
        if (history is null || history.Length == 0)
            return;

        int firstVisible = history.Length;
        for (int i = history.Length - 1; i >= 0; i--)
        {
            if (history[i].Utc < start)
                break;
            firstVisible = i;
        }

        int visible = history.Length - firstVisible;
        if (visible <= 0)
            return;

        int maxPoints = Math.Max(2, plot.Width * 2);
        int stride = visible > maxPoints ? (visible + maxPoints - 1) / maxPoints : 1;
        int capacity = (visible + stride - 1) / stride;
        PointF[] points = GetPointBuffer(seriesIndex, capacity);
        int count = 0;

        for (int i = firstVisible; i < history.Length; i += stride)
        {
            TimedValue sample = history[i];
            if (float.IsNaN(sample.Value) || float.IsInfinity(sample.Value))
                continue;
            float x = plot.Left + (float)((sample.Utc - start).TotalSeconds / windowSeconds) * plot.Width;
            float y = plot.Bottom - (sample.Value - min) / range * plot.Height;
            points[count++] = new PointF(x, y);
        }
        _pointCounts[seriesIndex] = count;
        if (count == 0)
            return;

        bool banded = !source.IsBoolean && source.Yellow < float.MaxValue / 2f;
        float yYellow = 0f;
        float yRed = 0f;
        if (banded)
        {
            yYellow = plot.Bottom - (source.Yellow - min) / range * plot.Height;
            yRed = plot.Bottom - (source.Red - min) / range * plot.Height;
        }

        if (count >= 2)
        {
            if (banded)
                DrawBandedSeries(g, points, count, source.Color, yYellow, yRed);
            else
                g.DrawLines(GetPen(source.Color), points.AsSpan(0, count));
        }

        DrawSeriesMarkers(g, points, count, source.Color, ShapeFor(seriesIndex));
    }

    private void DrawSeriesMarkers(Graphics g, PointF[] points, int count,
                                   Color color, MarkerShape shape)
    {
        float lastX = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            bool isLast = i == count - 1;
            if (!isLast && points[i].X - lastX < 28f)
                continue;
            DrawMarker(g, points[i].X, points[i].Y, color, shape);
            lastX = points[i].X;
        }
    }

    private void DrawMarker(Graphics g, float x, float y, Color color, MarkerShape shape)
    {
        Pen pen = GetPen(color);
        SolidBrush background = GetBrush(Theme.ChartBack);
        float r = MarkerRadius;
        switch (shape)
        {
            case MarkerShape.Circle:
                g.FillEllipse(background, x - r, y - r, r * 2f, r * 2f);
                g.DrawEllipse(pen, x - r, y - r, r * 2f, r * 2f);
                break;
            case MarkerShape.Square:
                g.FillRectangle(background, x - r, y - r, r * 2f, r * 2f);
                g.DrawRectangle(pen, x - r, y - r, r * 2f, r * 2f);
                break;
            case MarkerShape.Triangle:
                _trianglePoints[0] = new PointF(x, y - r - 0.5f);
                _trianglePoints[1] = new PointF(x + r + 0.5f, y + r);
                _trianglePoints[2] = new PointF(x - r - 0.5f, y + r);
                g.FillPolygon(background, _trianglePoints);
                g.DrawPolygon(pen, _trianglePoints);
                break;
            case MarkerShape.Diamond:
                _diamondPoints[0] = new PointF(x, y - r - 0.5f);
                _diamondPoints[1] = new PointF(x + r + 0.5f, y);
                _diamondPoints[2] = new PointF(x, y + r + 0.5f);
                _diamondPoints[3] = new PointF(x - r - 0.5f, y);
                g.FillPolygon(background, _diamondPoints);
                g.DrawPolygon(pen, _diamondPoints);
                break;
            case MarkerShape.Cross:
                g.DrawLine(pen, x - r, y, x + r, y);
                g.DrawLine(pen, x, y - r, x, y + r);
                break;
            case MarkerShape.X:
                g.DrawLine(pen, x - r, y - r, x + r, y + r);
                g.DrawLine(pen, x - r, y + r, x + r, y - r);
                break;
        }
    }

    private void DrawLegend(Graphics g, int left, int top, int width, int height)
    {
        int rowHeight = Math.Max(18, Font.Height + 4);
        int bottom = top + height;
        for (int i = 0; i < _sources.Count; i++)
        {
            int y = top + i * rowHeight;
            if (y + rowHeight > bottom)
                break;

            ChartSeriesSource source = _sources[i];
            g.DrawLine(GetPen(source.Color), left, y + rowHeight / 2f,
                       left + 16f, y + rowHeight / 2f);
            DrawMarker(g, left + 8f, y + rowHeight / 2f, source.Color, ShapeFor(i));

            string value = "—";
            TimedValue[]? history = i < _histories.Count ? _histories[i] : null;
            if (history is { Length: > 0 })
                value = FormatDisplayValue(source, history[^1].Value);
            string text = source.Name + "  " + value;
            var bounds = new Rectangle(left + 21, y, Math.Max(1, width - 21), rowHeight);
            TextRenderer.DrawText(g, text, Font, bounds, Theme.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }
    }

    private void DrawBandedSeries(Graphics g, PointF[] points, int count,
                                  Color baseColor, float yYellow, float yRed)
    {
        int runStart = 0;
        int runBand = SegmentBand(points[0].Y, points[1].Y, yYellow, yRed);
        for (int i = 1; i < count - 1; i++)
        {
            int band = SegmentBand(points[i].Y, points[i + 1].Y, yYellow, yRed);
            if (band == runBand)
                continue;
            g.DrawLines(BandPen(runBand, baseColor), points.AsSpan(runStart, i - runStart + 1));
            runStart = i;
            runBand = band;
        }
        g.DrawLines(BandPen(runBand, baseColor), points.AsSpan(runStart, count - runStart));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int seriesIndex = HitTestSeries(e.X, e.Y);
        if (seriesIndex == _hoverSeries)
            return;

        _hoverTip.Hide(this);
        _hoverSeries = seriesIndex;
        if (seriesIndex >= 0)
            _hoverTip.Show(_sources[seriesIndex].Name, this, e.X + 12, e.Y + 16, 5000);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverTip.Hide(this);
        _hoverSeries = -1;
        base.OnMouseLeave(e);
    }

    private int HitTestSeries(float x, float y)
    {
        float bestDistance = HitTolerance * HitTolerance;
        int bestSeries = -1;
        for (int seriesIndex = 0; seriesIndex < _sources.Count; seriesIndex++)
        {
            int count = seriesIndex < _pointCounts.Count ? _pointCounts[seriesIndex] : 0;
            if (count <= 0)
                continue;
            PointF[] points = _pointBuffers[seriesIndex];
            if (count == 1)
            {
                float dx = x - points[0].X;
                float dy = y - points[0].Y;
                float distance = dx * dx + dy * dy;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestSeries = seriesIndex;
                }
                continue;
            }

            for (int i = 0; i < count - 1; i++)
            {
                PointF a = points[i];
                PointF b = points[i + 1];
                if (x < Math.Min(a.X, b.X) - HitTolerance ||
                    x > Math.Max(a.X, b.X) + HitTolerance ||
                    y < Math.Min(a.Y, b.Y) - HitTolerance ||
                    y > Math.Max(a.Y, b.Y) + HitTolerance)
                    continue;
                float distance = DistanceToSegmentSquared(x, y, a, b);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestSeries = seriesIndex;
                }
            }
        }
        return bestSeries;
    }

    private bool TryGetRange(ChartScaleGroup group, DateTime start, out float min, out float max)
    {
        min = float.MaxValue;
        max = float.MinValue;
        bool any = false;
        for (int seriesIndex = 0; seriesIndex < _sources.Count; seriesIndex++)
        {
            if (ScaleGroupFor(_sources[seriesIndex]) != group)
                continue;
            TimedValue[]? history = _histories[seriesIndex];
            if (history is null)
                continue;
            for (int i = history.Length - 1; i >= 0; i--)
            {
                TimedValue sample = history[i];
                if (sample.Utc < start)
                    break;
                float value = sample.Value;
                if (float.IsNaN(value) || float.IsInfinity(value))
                    continue;
                if (value < min) min = value;
                if (value > max) max = value;
                any = true;
            }
        }
        return any;
    }

    private void AddPaneIfPresent(ChartScaleGroup group)
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            if (ScaleGroupFor(_sources[i]) == group)
            {
                _panes.Add(group);
                return;
            }
        }
    }

    private static ChartScaleGroup ScaleGroupFor(ChartSeriesSource source)
    {
        if (source.IsBoolean)
            return ChartScaleGroup.Boolean;
        return source.Quantity switch
        {
            SensorQuantity.Temperature => ChartScaleGroup.Temperature,
            SensorQuantity.Power => ChartScaleGroup.Power,
            SensorQuantity.Fan => ChartScaleGroup.Fan,
            SensorQuantity.Control or SensorQuantity.Level or SensorQuantity.Load => ChartScaleGroup.Percent,
            SensorQuantity.Frequency => ChartScaleGroup.Frequency,
            SensorQuantity.Voltage => ChartScaleGroup.Voltage,
            SensorQuantity.Data => ChartScaleGroup.Data,
            _ => ChartScaleGroup.Percent,
        };
    }

    private static void ExpandRange(ChartScaleGroup group, ref float min, ref float max)
    {
        if (group == ChartScaleGroup.Boolean)
        {
            min = -0.1f;
            max = 1.1f;
            return;
        }
        float minimumSpan = group switch
        {
            ChartScaleGroup.Temperature => Units.UseFahrenheit ? 9f : 5f,
            ChartScaleGroup.Power => 2f,
            ChartScaleGroup.Fan => 200f,
            ChartScaleGroup.Percent => 5f,
            ChartScaleGroup.Frequency => 200f,
            ChartScaleGroup.Voltage => 0.2f,
            ChartScaleGroup.Data => 2f,
            _ => 5f,
        };
        if (max - min < minimumSpan)
        {
            float middle = (min + max) * 0.5f;
            min = middle - minimumSpan * 0.5f;
            max = middle + minimumSpan * 0.5f;
        }
        float padding = (max - min) * 0.1f;
        min -= padding;
        max += padding;
    }

    private static float NiceStep(float rawStep)
    {
        if (!(rawStep > 0f) || float.IsInfinity(rawStep))
            return 1f;
        float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(rawStep)));
        float normalized = rawStep / magnitude;
        float nice = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 5f ? 5f : 10f;
        return nice * magnitude;
    }

    private static string FormatAxisValue(float value, float step)
    {
        string format = step >= 1f ? "0" : step >= 0.1f ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string FormatDisplayValue(ChartSeriesSource source, float value)
    {
        if (source.IsBoolean)
            return Loc.T(value >= 0.5f ? "boolean.true" : "boolean.false");
        SensorQuantity quantity = source.Quantity;
        string unit = quantity switch
        {
            SensorQuantity.Temperature => Units.TempSuffix,
            SensorQuantity.Fan => "RPM",
            SensorQuantity.Power => "W",
            SensorQuantity.Control or SensorQuantity.Level or SensorQuantity.Load => "%",
            SensorQuantity.Frequency => "MHz",
            SensorQuantity.Voltage => "V",
            SensorQuantity.Data => "GB",
            _ => string.Empty,
        };
        string format = quantity is SensorQuantity.Power or SensorQuantity.Voltage or SensorQuantity.Data
            ? "0.0"
            : "0.#";
        return value.ToString(format, CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : string.Empty);
    }

    private static string ScaleLabelKey(ChartScaleGroup group) => group switch
    {
        ChartScaleGroup.Temperature => "chart.temperature",
        ChartScaleGroup.Power => "chart.power",
        ChartScaleGroup.Fan => "chart.fan",
        ChartScaleGroup.Boolean => "chart.boolean",
        ChartScaleGroup.Percent => "chart.percent",
        ChartScaleGroup.Frequency => "chart.frequency",
        ChartScaleGroup.Voltage => "chart.voltage",
        ChartScaleGroup.Data => "chart.data",
        _ => "main.chart",
    };

    private static string ScaleUnit(ChartScaleGroup group) => group switch
    {
        ChartScaleGroup.Temperature => Units.TempSuffix,
        ChartScaleGroup.Power => "W",
        ChartScaleGroup.Fan => "RPM",
        ChartScaleGroup.Boolean => string.Empty,
        ChartScaleGroup.Percent => "%",
        ChartScaleGroup.Frequency => "MHz",
        ChartScaleGroup.Voltage => "V",
        ChartScaleGroup.Data => "GB",
        _ => string.Empty,
    };

    private static MarkerShape ShapeFor(int seriesIndex) => (MarkerShape)(seriesIndex % 6);

    private PointF[] GetPointBuffer(int seriesIndex, int needed)
    {
        PointF[] buffer = _pointBuffers[seriesIndex];
        if (buffer.Length >= needed)
            return buffer;
        int length = Math.Max(needed, Math.Max(32, buffer.Length * 2));
        buffer = new PointF[length];
        _pointBuffers[seriesIndex] = buffer;
        return buffer;
    }

    private static int SegmentBand(float y0, float y1, float yYellow, float yRed)
    {
        float minY = y0 < y1 ? y0 : y1;
        if (minY <= yRed) return 2;
        if (minY <= yYellow) return 1;
        return 0;
    }

    private Pen BandPen(int band, Color baseColor) => GetPen(band switch
    {
        2 => Theme.Hot,
        1 => Theme.Warn,
        _ => baseColor,
    });

    private static float DistanceToSegmentSquared(float x, float y, PointF a, PointF b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        if (dx == 0f && dy == 0f)
        {
            float px = x - a.X;
            float py = y - a.Y;
            return px * px + py * py;
        }
        float t = ((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0f, 1f);
        float nearestX = a.X + t * dx;
        float nearestY = a.Y + t * dy;
        float offsetX = x - nearestX;
        float offsetY = y - nearestY;
        return offsetX * offsetX + offsetY * offsetY;
    }

    private void DrawEmptyText(Graphics g)
    {
        string text = Loc.T("main.chart") + " —";
        SizeF size = g.MeasureString(text, Font);
        g.DrawString(text, Font, EnsureBrush(ref _subtleBrush, Theme.SubtleText),
                     (Width - size.Width) * 0.5f, (Height - size.Height) * 0.5f);
    }

    private static Pen EnsurePen(ref Pen? pen, Color color)
    {
        if (pen is null || pen.Color.ToArgb() != color.ToArgb())
        {
            pen?.Dispose();
            pen = new Pen(color);
        }
        return pen;
    }

    private static SolidBrush EnsureBrush(ref SolidBrush? brush, Color color)
    {
        if (brush is null || brush.Color.ToArgb() != color.ToArgb())
        {
            brush?.Dispose();
            brush = new SolidBrush(color);
        }
        return brush;
    }

    private Pen GetPen(Color color)
    {
        if (!_seriesPens.TryGetValue(color, out Pen? pen))
        {
            pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round };
            _seriesPens[color] = pen;
        }
        return pen;
    }

    private SolidBrush GetBrush(Color color)
    {
        if (!_seriesBrushes.TryGetValue(color, out SolidBrush? brush))
        {
            brush = new SolidBrush(color);
            _seriesBrushes[color] = brush;
        }
        return brush;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTip.Dispose();
            foreach (Pen pen in _seriesPens.Values) pen.Dispose();
            _seriesPens.Clear();
            foreach (SolidBrush brush in _seriesBrushes.Values) brush.Dispose();
            _seriesBrushes.Clear();
            _pointBuffers.Clear();
            _pointCounts.Clear();
            _gridPen?.Dispose();
            _gridPen = null;
            _borderPen?.Dispose();
            _borderPen = null;
            _subtleBrush?.Dispose();
            _subtleBrush = null;
        }
        base.Dispose(disposing);
    }
}
