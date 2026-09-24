using System.Collections;
using System.ComponentModel;
using System.Reflection;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Tray;

namespace WinMonitor.Tests;

internal static class TraySparklineTests
{
    public static void UnchangedSamplesAdvance()
    {
        const string id = "/test/sparkline";
        var config = new AppConfig();
        config.Active.TrayIcons.Clear();
        config.Active.TrayIcons.Add(new TrayIconConfig { SensorIds = new List<string> { id }, ShowSparkline = true });
        using var stats = new StatsTracker();
        var sync = new QueuedSync();
        using var tray = new TrayIconManager(config, stats, sync);
        var descriptor = new SensorDescriptor
        {
            Id = id, Name = "Test", HardwareName = "Test", Category = SensorCategory.Cpu,
            Quantity = SensorQuantity.Temperature,
        };
        tray.Rebuild(new[] { descriptor });
        int tick = 0;
        void Feed(float value)
        {
            var samples = new[] { new SensorSnapshot { Id = id, Value = value, UtcTimestamp = DateTime.UtcNow.AddSeconds(tick++) } };
            stats.Accept(samples);
            tray.Accept(samples);
        }
        Feed(80f);
        sync.Drain();
        stats.WaitForHistoryBackfillAsync().GetAwaiter().GetResult();
        Feed(40f);
        sync.Drain();
        object slot = ((IList)Field(tray, "_slots"))[0]!;
        Check.True(((float[])Field(slot, "SparkBuffer")).Take((int)Field(slot, "SparkCount")).Contains(80f),
            "The initial sparkline must include the old spike.");
        for (int i = 0; i < 32; i++) Feed(40f);
        Check.Equal(1, sync.Pending, "Equal-valued history ticks must coalesce into one pending redraw.");
        sync.Drain();
        Check.Equal(32, (int)Field(slot, "SparkCount"), "The sparkline should read the latest full window.");
        foreach (float value in ((float[])Field(slot, "SparkBuffer")).Take(32))
            Check.Equal(40f, value, "An old spike must age out even while the displayed number stays constant.");

        object icon = Field(slot, "CurrentIcon");
        Feed(40f);
        sync.Drain();
        Check.True(ReferenceEquals(icon, Field(slot, "CurrentIcon")),
            "An unchanged flat window must reuse the HICON after checking for changes.");
        config.Active.TrayIcons[0].ShowSparkline = false;
        tray.Rebuild(new[] { descriptor });
        Feed(40f);
        Check.Equal(0, sync.Pending, "Numeric-only icons must retain the unchanged-tick fast path.");
        config.Active.TrayIcons[0].ShowSparkline = true;
        tray.Rebuild(new[] { descriptor });
        tray.Accept(Array.Empty<SensorSnapshot>());
        Check.Equal(0, sync.Pending, "A sparse tick without samples must not advance sparkline work.");

        config.Active.TrayIcons[0].SensorIds.Clear();
        tray.Rebuild(new[] { descriptor });
        Feed(40f);
        Check.Equal(1, sync.Pending, "Automatic sensor selection must also advance equal-valued histories.");
        sync.Drain();

        var unrelated = new[] { new SensorSnapshot
        {
            Id = "/test/unrelated", Value = 10f, UtcTimestamp = DateTime.UtcNow,
        } };
        stats.Accept(unrelated);
        tray.Accept(unrelated);
        sync.Drain();
        stats.Accept(unrelated);
        tray.Accept(unrelated);
        Check.Equal(0, sync.Pending, "Unchanged unrelated samples must not schedule sparkline work.");
    }

    private static object Field(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(instance)!;

    private sealed class QueuedSync : ISynchronizeInvoke
    {
        private readonly Queue<Action> _queue = new();
        public int Pending => _queue.Count;
        public bool InvokeRequired => false;
        public IAsyncResult BeginInvoke(Delegate method, object?[]? args)
        {
            _queue.Enqueue(() => method.DynamicInvoke(args));
            return Task.CompletedTask;
        }
        public void Drain() { while (_queue.TryDequeue(out Action? work)) work(); }
        public object? Invoke(Delegate method, object?[]? args) => method.DynamicInvoke(args);
        public object? EndInvoke(IAsyncResult result) => null;
    }
}
