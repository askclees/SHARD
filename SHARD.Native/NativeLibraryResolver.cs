using System.Reflection;
using System.Runtime.InteropServices;

namespace SHARD.Native;

/// <summary>
/// Fixes native-dependency resolution for Microsoft.Data.Sqlite's bundled SQLite binary
/// (libe_sqlite3.so / e_sqlite3.dll / libe_sqlite3.dylib) when <c>shard_native.so</c> is loaded
/// as a plain shared library into a foreign host process (e.g. Python via ctypes) rather than
/// run as its own self-contained executable.
///
/// The problem (reproduced and confirmed while building this): in that hosting scenario,
/// <c>AppContext.BaseDirectory</c> resolves to the *host* process's own directory (e.g.
/// Python's own install directory), not to wherever <c>shard_native.so</c> itself lives, so the
/// runtime's default native-library search for "e_sqlite3" fails even though the correct file
/// is sitting right next to shard_native.so. Setting LD_LIBRARY_PATH after the host process has
/// already started does *not* help either — glibc's dynamic linker snapshots that variable at
/// process start and does not re-read it for dlopen calls made later in the same process
/// (verified empirically, not just per documentation).
///
/// The actual fix: find *this module's own* on-disk path (via dladdr on POSIX / GetModuleHandleEx
/// on Windows, using the address of a function inside this very module — not the host process's
/// executable path) and resolve the sibling native SQLite library relative to that.
/// </summary>
internal static class NativeLibraryResolver
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        // NativeLibrary.SetDllImportResolver is per-assembly. The actual [DllImport("e_sqlite3")]
        // declarations live in SQLitePCLRaw.provider.e_sqlite3.dll's generated NativeMethods class
        // (SQLitePCL.SQLite3Provider_e_sqlite3), which is a *different* assembly from SQLitePCL.raw
        // itself (SQLitePCLRaw.core.dll) — registering only on SQLitePCL.raw's assembly silently
        // never gets invoked for the P/Invoke that actually fails. Register on every already-loaded
        // assembly whose name starts with "SQLitePCLRaw" so this doesn't depend on knowing which
        // one declares the DllImport, or on that type being public.
        foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (candidate.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true)
                NativeLibrary.SetDllImportResolver(candidate, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "e_sqlite3") return IntPtr.Zero; // not ours — fall back to default resolution

        string? selfDir = GetOwnModuleDirectory();
        if (selfDir is null) return IntPtr.Zero;

        string fileName =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "e_sqlite3.dll" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "libe_sqlite3.dylib" :
                                                                   "libe_sqlite3.so";

        string candidate = Path.Combine(selfDir, fileName);
        return File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle) ? handle : IntPtr.Zero;
    }

    private static unsafe string? GetOwnModuleDirectory()
    {
        // Take the address of a function that's actually compiled into this module (not
        // inlined/optimized away — Marker is a real, addressable static method) and ask the OS
        // which loaded module that address belongs to.
        delegate*<void> marker = &Marker;
        var address = (IntPtr)marker;

        string? path =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetModulePathWindows(address) :
                                                                   GetModulePathPosix(address);

        return path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
    }

    private static void Marker() { }

    // ── POSIX: dladdr ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr dli_fname;
        public IntPtr dli_fbase;
        public IntPtr dli_sname;
        public IntPtr dli_saddr;
    }

    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int dladdr_glibc(IntPtr addr, out DlInfo info);

    [DllImport("libSystem.dylib", EntryPoint = "dladdr")]
    private static extern int dladdr_darwin(IntPtr addr, out DlInfo info);

    private static string? GetModulePathPosix(IntPtr address)
    {
        try
        {
            DlInfo info;
            int result = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? dladdr_darwin(address, out info)
                : dladdr_glibc(address, out info);

            if (result == 0 || info.dli_fname == IntPtr.Zero) return null;
            return Marshal.PtrToStringUTF8(info.dli_fname);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    // ── Windows: GetModuleHandleEx + GetModuleFileName ──────────────────────

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    private static string? GetModulePathWindows(IntPtr address)
    {
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, address, out var hModule))
            return null;

        var buffer = new char[1024];
        uint length = GetModuleFileNameW(hModule, buffer, (uint)buffer.Length);
        return length == 0 ? null : new string(buffer, 0, (int)length);
    }
}
