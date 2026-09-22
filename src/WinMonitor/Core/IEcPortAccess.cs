namespace WinMonitor.Core;

/// <summary>
/// Narrow native execution seam. Production uses PawnIo; regression checks emulate the read
/// handshake without loading a driver. Only EmbeddedController decides which commands to send.
/// </summary>
internal interface IEcPortAccess : IDisposable
{
    int Execute(string name, ulong[] input, int inputCount, ulong[] output, int outputCount,
        out nuint returned);
}
