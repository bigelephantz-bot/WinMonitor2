using System.Reflection;
using WinMonitor.Config;
using WinMonitor.Core;

namespace WinMonitor.Tests;

internal static class EcLifecycleTests
{
    public static void AvailabilityTransitions()
    {
        var reader = new FakeReader();
        var definitions = new[]
        {
            new EcSensorDef { Register = 0xB0, Kind = EcValueKind.Word, Quantity = SensorQuantity.Fan },
            new EcSensorDef { Register = 0xB2, Kind = EcValueKind.RawByte, Quantity = SensorQuantity.Temperature },
        };
        var sampler = new EcSensorSampler(reader, definitions, new[] { 0xB0, 0xB1, 0xB2 });
        DateTime utc = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        SensorSnapshot[] valid = sampler.Poll(utc, everyNTicks: 3).ToArray();
        Check.Equal(2, valid.Length, "The first EC tick samples every configured sensor.");
        Check.Equal(3000f, valid[0].Value!.Value, "Little-endian fan RPM must pass through the real scatter/compute path.");
        Check.Equal(42f, valid[1].Value!.Value, "The second configured sensor must also be sampled.");
        Check.True(sampler.Complete, "A fresh EC reading is complete.");

        Check.Equal(0, sampler.Poll(utc.AddSeconds(1), 3).Length,
            "A healthy throttled tick must be omitted, not recorded as zero or a duplicate sample.");
        Check.True(!sampler.Complete, "An omitted EC tick cannot complete a background-log row.");
        Check.Equal(1, reader.ReadCount, "Throttled ticks must not touch the EC backend.");

        // Change availability between normal read ticks, as a diagnostic/reset/backend failure can.
        reader.Available = false;
        SensorSnapshot[] missing = sampler.Poll(utc.AddSeconds(2), 3).ToArray();
        Check.Equal(2, missing.Length, "An unavailable transition invalidates ALL configured IDs.");
        for (int i = 0; i < missing.Length; i++)
        {
            Check.Equal(definitions[i].SensorId, missing[i].Id, "Invalidation must target the original sensor ID.");
            Check.True(!missing[i].HasValue, "The old RPM/temperature must not survive as a current reading.");
        }
        Check.True(sampler.Complete, "Explicitly unavailable EC values cannot hold logging completeness hostage.");
        Check.Equal(1, reader.ReadCount, "An unavailable backend must never be called.");
        Check.Equal(2, sampler.Poll(utc.AddSeconds(3), 3).Length,
            "Subsequent full snapshots still carry explicit unavailable values.");

        reader.Available = true;
        SensorSnapshot[] recovered = sampler.Poll(utc.AddSeconds(4), 3).ToArray();
        Check.Equal(3000f, recovered[0].Value!.Value, "Recovery reads immediately, regardless of the old cadence.");
        Check.Equal(2, reader.ReadCount, "Recovery should issue exactly one fresh read.");

        reader.BecomeUnavailableDuringRead = true;
        SensorSnapshot[] timedOut = sampler.Poll(utc.AddSeconds(5), 1).ToArray();
        Check.Equal(2, timedOut.Length, "A timeout during the native read must emit an invalidation batch.");
        Check.True(!timedOut[0].HasValue && !timedOut[1].HasValue,
            "Even partial success is discarded if the backend became unavailable within the read.");

        var initiallyUnavailable = new EcSensorSampler(new FakeReader { Available = false }, definitions,
            new[] { 0xB0, 0xB1, 0xB2 });
        Check.Equal(2, initiallyUnavailable.Poll(utc, 3).Length,
            "Configured sensors remain represented when initialization never succeeded.");
    }

    public static void TimeoutResetLateCompletion()
    {
        var native = new FakePortAccess(blockRead: true);
        var mutex = new Mutex(false);
        var controller = new EmbeddedController(native, mutex, readGateTimeoutMs: 1000);
        byte[]? returned = null;
        bool[]? flags = null;
        Exception? callerError = null;
        var caller = new Thread(() =>
        {
            try { returned = controller.ReadRegisters(new[] { 0xB0 }, out flags, budgetMs: 30_000); }
            catch (Exception ex) { callerError = ex; }
        }) { IsBackground = true };
        Thread? resetter = null;
        try
        {
            caller.Start();
            Check.True(native.Entered.Wait(5000), "The real EC read protocol must reach the injected native call.");
            // Reset enters while the gate is still waiting. It must serialize with RunRead, then
            // abandon the captured session rather than close resources used by the blocked call.
            resetter = new Thread(controller.Reset) { IsBackground = true };
            resetter.Start();
            Check.True(caller.Join(5000), "The caller must stop waiting for a blocked native read.");
            Check.True(resetter.Join(5000), "Reset must finish once the caller has timed out.");
            Check.True(callerError is null, "A native timeout must degrade without throwing to the poll caller.");
            Check.True(!controller.Available, "A timed-out controller stays unavailable after Reset.");
            Check.True(returned is { Length: 1 } && flags is { Length: 1 }, "The caller still receives a normal result shape.");
            Check.Equal((byte)0, returned![0], "A timed-out batch cannot publish worker-owned data.");
            Check.True(!flags![0], "A timed-out batch cannot publish successful flags.");
            Check.Equal(0, native.DisposeCount, "Reset must not close a native object under a live call.");
            Check.True(!mutex.WaitOne(0), "The blocked worker must still own the exact mutex it acquired.");
            Check.True(IsRetained(native) && IsRetained(mutex),
                "Abandoned native objects require process-lifetime strong roots, not only skipped Dispose calls.");
            Check.True(!controller.Initialize(), "Reset must not reopen a session whose gate is permanently wedged.");
            controller.Dispose();
            Check.Equal(0, native.DisposeCount, "Dispose must also leave native resources intact while the call is alive.");
            Check.True(!mutex.WaitOne(0), "Dispose must not close or release a mutex owned by a live worker.");

            native.Release.Set();
            Check.True(native.Returned.Wait(5000), "The delayed native read must be allowed to return.");
            // WaitOne succeeds only after ReadBatch's finally releases the mutex on its gate thread.
            // An abandoned-thread release would throw AbandonedMutexException and fail this check.
            Check.True(mutex.WaitOne(5000), "Late completion must release the captured mutex despite Reset clearing fields.");
            mutex.ReleaseMutex();
            Check.Equal((byte)0, returned[0], "Late successful output must never mutate arrays returned at timeout.");
            Check.True(!flags[0], "Late completion must never change the caller's failed-read flags.");
            Check.True(native.ExecutionThreadId != caller.ManagedThreadId,
                "The native call must run on the gate worker, not on the poll caller.");
            controller.Dispose();
            Check.Equal(0, native.DisposeCount, "A wedged session deliberately remains retained even after a late return.");
        }
        finally
        {
            native.Release.Set();
            caller.Join(5000);
            resetter?.Join(5000);
            controller.Dispose();
            // The retained native objects intentionally follow the production process-exit rule.
        }
    }

    public static void CompletedReadAndDispose()
    {
        var native = new FakePortAccess(blockRead: false);
        var mutex = new Mutex(false);
        var controller = new EmbeddedController(native, mutex);
        byte[] values = controller.ReadRegisters(new[] { 0xB0 }, out bool[] flags, budgetMs: 5000);
        Check.True(flags[0], "A completed fake native handshake must publish success.");
        Check.Equal((byte)123, values[0], "A completed fake native handshake must publish its output.");
        Check.True(mutex.WaitOne(0), "A successful batch releases its mutex before returning to the caller.");
        mutex.ReleaseMutex();
        controller.Dispose();
        controller.Dispose();
        Check.Equal(1, native.DisposeCount, "A completed session must release its native object exactly once.");
        bool closed = false;
        try { mutex.WaitOne(0); }
        catch (ObjectDisposedException) { closed = true; }
        Check.True(closed, "A completed controller must dispose its owned mutex.");
    }

    private static bool IsRetained(object instance)
    {
        var retained = (List<object>)typeof(EmbeddedController)
            .GetField("AbandonedNativeObjects", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        lock (retained) return retained.Contains(instance);
    }

    private sealed class FakeReader : IEcSensorReader
    {
        public bool Available { get; set; } = true;
        public bool BecomeUnavailableDuringRead { get; set; }
        public int ReadCount { get; private set; }

        public byte[] ReadRegisters(int[] addrs, out bool[] ok, int budgetMs = 40)
        {
            ReadCount++;
            var data = new byte[addrs.Length];
            ok = new bool[addrs.Length];
            for (int i = 0; i < addrs.Length; i++)
            {
                data[i] = addrs[i] switch { 0xB0 => 0xB8, 0xB1 => 0x0B, 0xB2 => 42, _ => 0 };
                ok[i] = true;
            }
            if (BecomeUnavailableDuringRead) Available = false;
            return data;
        }
    }

    private sealed class FakePortAccess(bool blockRead) : IEcPortAccess
    {
        private bool _addressWritten;
        private int _disposeCount;
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim Returned = new(false);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int ExecutionThreadId { get; private set; }

        public int Execute(string name, ulong[] input, int inputCount, ulong[] output, int outputCount,
            out nuint returned)
        {
            ExecutionThreadId = Environment.CurrentManagedThreadId;
            returned = 0;
            if (name == "ioctl_pio_write")
            {
                Check.Equal(2, inputCount, "Protocol writes contain a port and one byte.");
                Check.Equal(0, outputCount, "Protocol writes do not request output bytes.");
                if (input[0] == 0x66)
                    Check.Equal(0x80UL, input[1], "The EC seam must never issue a write-register command.");
                else if (input[0] == 0x62) _addressWritten = true;
                else throw new InvalidOperationException("Unexpected EC port.");
                return 0;
            }
            Check.Equal("ioctl_pio_read", name, "Only read-handshake native calls are supported.");
            returned = 1;
            if (input[0] == 0x66) output[0] = _addressWritten ? 1UL : 0UL;
            else
            {
                Check.Equal(0x62UL, input[0], "Data must come from the EC data port.");
                Entered.Set();
                if (blockRead && !Release.Wait(15_000)) throw new TimeoutException("Test did not release its fake native call.");
                output[0] = 123;
                _addressWritten = false;
                Returned.Set();
            }
            return 0;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
