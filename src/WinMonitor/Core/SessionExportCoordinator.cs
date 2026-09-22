namespace WinMonitor.Core;

/// <summary>Owns outstanding exports independently of the lifetime of any window.</summary>
public sealed class SessionExportCoordinator
{
    private int _active;
    public bool IsExportInProgress => Volatile.Read(ref _active) > 0;

    public async Task<string> RunAsync(Func<string> export)
    {
        ArgumentNullException.ThrowIfNull(export);
        Interlocked.Increment(ref _active);
        try
        {
            return await Task.Run(export).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
