using System.Reflection;
using WinMonitor.Core;
using WinMonitor.UI;

/// <summary>Exercises rendering against caller-owned history buffers with unused capacity.</summary>
internal static class ChartWindowTests
{
    public static void Run()
    {
        using var chart = new ChartControl { Size = new Size(640, 240) };
        using var bitmap = new Bitmap(chart.Width, chart.Height);
        var sources = new[]
        {
            new ChartSeriesSource("/test/power", "Power", Color.Blue,
                float.MaxValue, float.MaxValue, SensorQuantity.Power, false),
        };
        int stage = 0;
        int calls = 0;
        TimedValue[]? firstBuffer = null;
        DateTime requestedStart = default;

        HistoryWindowReadResult Provide(string id, DateTime fromUtc,
                                         ref TimedValue[] buffer, long knownVersion)
        {
            Check.Equal("/test/power", id, "The window provider should receive the selected sensor.");
            if (calls > 0)
                Check.Equal(7L, knownVersion, "The chart must pass back the last history version.");
            calls++;
            requestedStart = fromUtc;
            if (buffer.Length < 8) buffer = new TimedValue[8];
            if (firstBuffer is null) firstBuffer = buffer;
            else Check.True(ReferenceEquals(firstBuffer, buffer), "Refresh must reuse the chart-owned buffer.");

            // A stale capacity tail must never enter the scale, point geometry, or latest value.
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = new TimedValue(fromUtc.AddHours(1), 1_000_000f);
            if (stage == 0)
            {
                buffer[0] = new TimedValue(fromUtc.AddSeconds(-10), 0f);
                buffer[1] = new TimedValue(fromUtc.AddSeconds(10), 20f);
                buffer[2] = new TimedValue(fromUtc.AddSeconds(20), 30f);
                return new HistoryWindowReadResult(7, 3);
            }
            if (stage == 1)
            {
                buffer[0] = new TimedValue(fromUtc.AddSeconds(20), 25f);
                return new HistoryWindowReadResult(7, 1);
            }
            return new HistoryWindowReadResult(7, 0);
        }

        DateTime before = DateTime.UtcNow.AddMinutes(-1);
        chart.SetSources(sources, Provide, windowMinutes: 1);
        Check.True(requestedStart >= before && requestedStart <= DateTime.UtcNow.AddMinutes(-1),
            "The provider cutoff must be the selected one-minute window, not the full history.");
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
        Check.Equal(3, Field<List<int>>(chart, "_historyCounts")[0], "Only the returned history prefix is valid.");
        Check.Equal(3, Field<List<int>>(chart, "_pointCounts")[0],
            "The entering boundary and two visible samples should produce three points.");
        PointF[] points = Field<List<PointF[]>>(chart, "_pointBuffers")[0];
        Check.True(Math.Abs(points[0].X - 54f) < 0.01f,
            "The pre-window sample must be interpolated at the left pane edge.");
        Check.True(points[0].Y > points[1].Y && points[1].Y > points[2].Y,
            "Boundary interpolation must preserve the rising line instead of flattening it with stale values.");
        Check.True(Math.Abs(points[0].Y - points[2].Y) > chart.Height / 2f,
            "Unused buffer capacity must not expand the Y scale.");

        stage = 1;
        chart.RefreshData();
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
        Check.Equal(1, Field<List<int>>(chart, "_historyCounts")[0], "A shorter same-version window must replace Count.");
        Check.Equal(1, Field<List<int>>(chart, "_pointCounts")[0], "Old buffer entries must not remain plotted.");

        stage = 2;
        chart.RefreshData();
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
        Check.Equal(0, Field<List<int>>(chart, "_historyCounts")[0], "An empty window must clear the valid prefix.");
        Check.Equal(0, Field<List<int>>(chart, "_pointCounts")[0], "An empty window must clear hit-test geometry.");
        Check.Equal(3, calls, "Every refresh must obtain fresh raw values even when the version is unchanged.");
    }

    private static T Field<T>(object instance, string name)
    {
        FieldInfo? field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) throw new InvalidOperationException("Missing chart field: " + name);
        return (T)field.GetValue(instance)!;
    }
}
