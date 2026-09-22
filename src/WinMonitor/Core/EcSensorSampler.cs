using WinMonitor.Config;

namespace WinMonitor.Core;

/// <summary>The minimal read-only backend contract needed by the poll sampler.</summary>
internal interface IEcSensorReader
{
    bool Available { get; }
    byte[] ReadRegisters(int[] addrs, out bool[] ok, int budgetMs = 40);
}

/// <summary>
/// Owns EC cadence and invalidation on the poll thread. An omitted healthy tick means "not
/// sampled"; unavailable sensors instead produce explicit nulls so consumers clear old values.
/// </summary>
internal sealed class EcSensorSampler
{
    private readonly IEcSensorReader _reader;
    private readonly EcSensorDef[] _sensors;
    private readonly int[] _registers;
    private readonly string[] _ids;
    private readonly SensorSnapshot[] _samples;
    private readonly byte[] _values = new byte[256];
    private readonly bool[] _valid = new bool[256];
    private int _tick;

    internal EcSensorSampler(IEcSensorReader reader, EcSensorDef[] sensors, int[] registers)
    {
        _reader = reader;
        _sensors = sensors;
        _registers = registers;
        _ids = new string[sensors.Length];
        _samples = new SensorSnapshot[sensors.Length];
        for (int i = 0; i < sensors.Length; i++) _ids[i] = sensors[i].SensorId;
    }

    internal bool Complete { get; private set; } = true;

    /// <summary>Returned storage belongs to the sampler; copy it before the next poll.</summary>
    internal ReadOnlySpan<SensorSnapshot> Poll(DateTime utc, int everyNTicks)
    {
        if (_sensors.Length == 0)
        {
            Complete = true;
            return ReadOnlySpan<SensorSnapshot>.Empty;
        }

        if (!_reader.Available)
        {
            return Invalidate(utc);
        }

        int everyN = Math.Max(1, everyNTicks);
        _tick %= everyN;
        bool due = _tick == 0;
        _tick = (_tick + 1) % everyN;
        if (!due)
        {
            Complete = false;
            return ReadOnlySpan<SensorSnapshot>.Empty;
        }

        Array.Clear(_valid);
        try
        {
            byte[] raw = _reader.ReadRegisters(_registers, out bool[] ok);
            // A driver timeout changes availability inside ReadRegisters. Discard even a partial
            // batch in that case, and invalidate every configured sensor in the same snapshot.
            if (!_reader.Available) return Invalidate(utc);
            int count = Math.Min(_registers.Length, Math.Min(raw.Length, ok.Length));
            for (int i = 0; i < count; i++)
            {
                int address = _registers[i];
                if ((uint)address > 255) continue;
                _values[address] = raw[i];
                _valid[address] = ok[i];
            }
            for (int i = 0; i < _sensors.Length; i++)
            {
                float? value = _sensors[i].Compute(_values, _valid);
                if (value is { } number && !float.IsFinite(number)) value = null;
                _samples[i] = new SensorSnapshot { Id = _ids[i], Value = value, UtcTimestamp = utc };
            }
            Complete = true;
            return _samples;
        }
        catch (Exception ex)
        {
            Diag.Log("ec", "EC sample failed", ex);
            return Invalidate(utc);
        }
    }

    private ReadOnlySpan<SensorSnapshot> Invalidate(DateTime utc)
    {
        _tick = 0; // Recovery must sample immediately rather than wait out an old cadence.
        for (int i = 0; i < _samples.Length; i++)
            _samples[i] = new SensorSnapshot { Id = _ids[i], Value = null, UtcTimestamp = utc };
        Complete = true;
        return _samples;
    }
}
