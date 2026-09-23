using System.Text.Json;
using WinMonitor.Config;
using WinMonitor.Core;

namespace WinMonitor.Tests;

internal static class AppLifecycleTests
{
    private static EcConfig FanConfig() => new()
    {
        Enabled = true,
        Sensors = new List<EcSensorDef>
        {
            new() { Register = 0xB0, Kind = EcValueKind.RpmDirect, Quantity = SensorQuantity.Fan, Name = "Fan" },
        },
    };

    public static void EcSettingsPublication()
    {
        Action<EcSensorDef>[] edits =
        {
            s => s.Scale = 2f, s => s.Offset = 5f, s => s.BigEndian = true,
            s => s.Divisor = 12345f, s => s.Enabled = false, s => s.Register = 0xB2,
            s => s.Kind = EcValueKind.Word, s => s.Quantity = SensorQuantity.Temperature,
            s => s.Name = "Renamed", s => s.NameKey = "ec.default_name",
        };
        foreach (Action<EcSensorDef> edit in edits)
        {
            EcConfig live = FanConfig();
            EcConfig? published = null;
            int calls = 0;
            var publisher = new EcSettingsPublisher(live, value => { published = value; calls++; });
            Check.True(!publisher.Apply(live.Clone()), "An identical EC definition should not force a rescan.");
            edit(live.Sensors[0]);
            Check.True(publisher.Apply(live), "Every changed EC field must reach the runtime publisher.");
            Check.Equal(1, calls, "Each changed definition should publish exactly once.");
            Check.Equal(JsonSerializer.Serialize(live), JsonSerializer.Serialize(published),
                "The runtime must receive the complete definition, not a subset of fields.");
            Check.True(!ReferenceEquals(live.Sensors[0], published!.Sensors[0]),
                "The runtime publication must not alias UI-owned sensor definitions.");
            Check.True(!publisher.Apply(live), "An already applied definition must be a no-op.");
        }

        EcConfig config = FanConfig();
        int attempts = 0;
        var retry = new EcSettingsPublisher(config, _ =>
        {
            if (++attempts == 1) throw new IOException("simulated publication failure");
        });
        config.Sensors[0].Scale = 3f;
        bool threw = false;
        try { retry.Apply(config); } catch (IOException) { threw = true; }
        Check.True(threw && retry.Apply(config), "Failed publication must not advance the accepted baseline.");
        Check.Equal(2, attempts, "A failed publication must be retried.");

        string key = config.Sensors[0].MeasurementKey;
        config.Sensors[0].Name = "Cosmetic";
        Check.Equal(key, config.Sensors[0].MeasurementKey, "A label edit must not split measurement history.");
        config.Sensors[0].Scale = 4f;
        Check.True(key != config.Sensors[0].MeasurementKey, "A calibration edit must split measurement history.");
        Check.True(!JsonSerializer.Serialize(config).Contains("MeasurementKey", StringComparison.Ordinal),
            "Runtime history identity must not change the persisted config schema.");
    }

    public static void SettingsDraftTransaction()
    {
        var live = new AppConfig { Ec = FanConfig() };
        live.Active.TrayIcons.Clear();
        live.Active.TrayIcons.Add(new TrayIconConfig { SensorIds = new List<string> { "A" } });
        var transaction = new SettingsDraft(live);
        transaction.Draft.Ec.Sensors[0].Scale = 2f;
        transaction.Draft.Active.TrayIcons[0].Bold = true;
        live.Language = "zh-TW";
        live.Active.TrayIcons.Add(new TrayIconConfig { SensorIds = new List<string> { "B" } });
        Check.Equal(1f, live.Ec.Sensors[0].Scale, "Draft edits must not touch the live EC definitions.");
        Check.True(transaction.Rebase(live), "Independent live edits should rebase an open draft.");

        AppConfig candidate = transaction.BuildMergedConfig(live);
        Check.Equal(2f, candidate.Ec.Sensors[0].Scale, "Apply should include the draft's calibration.");
        Check.Equal("zh-TW", candidate.Language, "Apply must retain an unrelated live language change.");
        Check.Equal(2, candidate.Active.TrayIcons.Count, "Apply must retain the live-added tray icon.");
        Check.True(candidate.Active.TrayIcons[0].Bold, "Apply must retain the draft's icon style.");
        candidate.Ec.Sensors[0].Scale = 9f;
        Check.Equal(1f, live.Ec.Sensors[0].Scale, "An uncommitted candidate must remain isolated.");

        transaction.Reset(live); // Cancel discards the draft without a publication callback.
        Check.Equal(1f, transaction.Draft.Ec.Sensors[0].Scale, "Cancel must discard pending calibration.");
        transaction.RestoreDefaults();
        Check.Equal(1, live.Ec.Sensors.Count, "Restoring draft defaults must not erase live mappings.");
        transaction.Reset(live);
        Check.Equal(2, transaction.Draft.Active.TrayIcons.Count, "Cancel must retain independent live tray changes.");
    }

    public static void ExportLifetime()
    {
        var coordinator = new SessionExportCoordinator();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var window = new Form();
        Task<string> task = coordinator.RunAsync(() =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Test export was not released.");
            return "complete.csv";
        });
        try
        {
            Check.True(entered.Wait(5000), "The export should reach the background worker.");
            window.Dispose();
            Check.True(coordinator.IsExportInProgress, "Disposing the initiating window must not lose export state.");
            release.Set();
            Check.Equal("complete.csv", task.GetAwaiter().GetResult(), "The export should finish after its window closes.");
            Check.True(!coordinator.IsExportInProgress, "Worker completion must clear the export state.");
        }
        finally
        {
            release.Set();
            task.GetAwaiter().GetResult();
        }

        bool threw = false;
        try { coordinator.RunAsync(() => throw new IOException("export failed")).GetAwaiter().GetResult(); }
        catch (IOException) { threw = true; }
        Check.True(threw && !coordinator.IsExportInProgress, "A failed export must also clear activity state.");
    }

    public static void DisplayEventWiring()
    {
        var source = new FakeDisplaySource();
        var queued = new Queue<Action>();
        bool metricChanged = false;
        int refreshes = 0, redraws = 0;
        using var subscription = new DisplayMetricsSubscription(queued.Enqueue,
            () => { refreshes++; return metricChanged; }, () => redraws++, source);

        source.Raise();
        Check.Equal(0, refreshes, "A display event must marshal work instead of refreshing on the event thread.");
        queued.Dequeue()();
        Check.Equal(1, refreshes, "A display event must reach the metrics refresh.");
        Check.Equal(0, redraws, "An unchanged size must not rebuild tray icons.");
        metricChanged = true;
        source.Raise();
        queued.Dequeue()();
        Check.Equal(1, redraws, "A changed size must rebuild tray icons through the actual event pipeline.");
        source.Raise();
        subscription.Dispose();
        queued.Dequeue()();
        Check.Equal(1, redraws, "Queued callbacks must not redraw a disposed subscription.");
        source.Raise();
        Check.Equal(0, queued.Count, "Disposal must unsubscribe the display event.");
    }

    private sealed class FakeDisplaySource : IDisplaySettingsSource
    {
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
