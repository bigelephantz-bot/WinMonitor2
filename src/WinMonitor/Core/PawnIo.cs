using System.Runtime.InteropServices;

namespace WinMonitor.Core;

/// <summary>
/// Thin managed wrapper over PawnIOLib.dll (the user-mode client for the signed PawnIO
/// kernel driver, https://pawnio.eu). Loads the DLL from the PawnIO install directory or
/// PATH and exposes open / load-blob / execute / close.
///
/// PawnIO is the mechanism LibreHardwareMonitor already uses on this machine; we reuse it to
/// reach the ACPI Embedded Controller, which LHM's Super-I/O path cannot see on LG laptops.
/// All calls return an HRESULT (0 = S_OK). This type is not thread-safe; callers serialize.
/// </summary>
public sealed class PawnIo : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public bool IsOpen => _handle != IntPtr.Zero;

    /// <summary>True when PawnIOLib.dll could be located and loaded at all.</summary>
    public static bool LibraryPresent => EnsureLibrary();

    public int Open()
    {
        if (!EnsureLibrary()) return unchecked((int)0x80004005); // E_FAIL: no library
        int hr = pawnio_open(out _handle);
        if (hr != 0) _handle = IntPtr.Zero;
        return hr;
    }

    public int Load(byte[] blob)
    {
        if (!IsOpen) return unchecked((int)0x80004005);
        return pawnio_load(_handle, blob, (nuint)blob.Length);
    }

    /// <summary>Execute an exported function. Returns HRESULT; <paramref name="returned"/> = out entries written.</summary>
    public int Execute(string name, ulong[] input, ulong[] output, out nuint returned)
        => Execute(name, input, input.Length, output, output.Length, out returned);

    /// <summary>
    /// Executes using only the first <paramref name="inputCount"/>/<paramref name="outputCount"/>
    /// entries, so a caller can reuse one buffer for calls of different arity instead of allocating
    /// per call. A count of 0 still pins a valid non-NULL pointer, which the modules' sized-IOCTL
    /// checks require.
    /// </summary>
    public int Execute(string name, ulong[] input, int inputCount, ulong[] output, int outputCount,
        out nuint returned)
    {
        returned = 0;
        if (!IsOpen) return unchecked((int)0x80004005);
        if ((uint)inputCount > (uint)input.Length || (uint)outputCount > (uint)output.Length)
            return unchecked((int)0x80070057); // E_INVALIDARG: never hand the driver a bogus size
        return pawnio_execute(_handle, name, input, (nuint)inputCount, output, (nuint)outputCount, out returned);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { pawnio_close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }

    // ---------- native library resolution ----------

    private static bool _resolved;
    private static bool _available;

    private static bool EnsureLibrary()
    {
        if (_resolved) return _available;
        _resolved = true;
        try
        {
            // NativeLibrary + our resolver below handle the actual path search; a probe call
            // forces the load and tells us whether the DLL is really usable.
            _ = pawnio_version(out _);
            _available = true;
        }
        catch
        {
            _available = false;
        }
        return _available;
    }

    static PawnIo()
    {
        NativeLibrary.SetDllImportResolver(typeof(PawnIo).Assembly, static (name, asm, path) =>
        {
            if (!string.Equals(name, "PawnIOLib", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "PawnIOLib.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            foreach (string candidate in EnumerateCandidates())
            {
                if (candidate.Length > 0 && File.Exists(candidate)
                    && NativeLibrary.TryLoad(candidate, out var h))
                    return h;
            }
            // Fall back to the default search (PATH / app dir).
            return NativeLibrary.TryLoad("PawnIOLib.dll", out var d) ? d : IntPtr.Zero;
        });
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "PawnIO", "PawnIOLib.dll");
        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86))
            yield return Path.Combine(pf86, "PawnIO", "PawnIOLib.dll");
        // App-dir copy, if a user drops one next to the exe.
        yield return Path.Combine(AppContext.BaseDirectory, "PawnIOLib.dll");
    }

    // ---------- P/Invoke (stdcall HRESULT ABI per PawnIOLib.h) ----------

    [DllImport("PawnIOLib", CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_version(out uint version);

    [DllImport("PawnIOLib", CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_open(out IntPtr handle);

    [DllImport("PawnIOLib", CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_load(IntPtr handle, byte[] blob, nuint size);

    [DllImport("PawnIOLib", CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_execute(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize,
        ulong[] output, nuint outSize,
        out nuint returnSize);

    [DllImport("PawnIOLib", CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_close(IntPtr handle);
}
