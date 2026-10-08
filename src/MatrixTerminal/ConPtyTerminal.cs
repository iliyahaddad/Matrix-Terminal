using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MatrixTerminal;

/// <summary>Thin wrapper around the Windows pseudo console (ConPTY).</summary>
internal sealed class ConPtyTerminal : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _pty = IntPtr.Zero;
    private IntPtr _process = IntPtr.Zero;
    private FileStream? _input;
    private FileStream? _output;
    private Task? _reader;
    private Task? _waiter;
    private volatile bool _disposed;

    /// <summary>Raised on a background thread with decoded UTF-8 output.</summary>
    public event Action<string>? OutputReceived;
    /// <summary>Raised on a background thread when the child process has exited.</summary>
    public event Action? Exited;

    public void Start(string shell, string? workingDirectory, short cols, short rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Matrix Terminal requires Windows.");

        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
        var attrList = IntPtr.Zero;
        var attrInitialized = false;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) ThrowLast("CreatePipe(input)");
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) ThrowLast("CreatePipe(output)");

            var hr = CreatePseudoConsole(new COORD(cols, rows), inRead!.DangerousGetHandle(), outWrite!.DangerousGetHandle(), 0, out _pty);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            // The pseudo console now owns its own copies of these two ends. We MUST close ours,
            // otherwise the output pipe never breaks when the console goes away.
            inRead.Dispose(); inRead = null;
            outWrite.Dispose(); outWrite = null;

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // expected to fail: queries required size
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) ThrowLast("InitializeProcThreadAttributeList");
            attrInitialized = true;
            if (!UpdateProcThreadAttribute(attrList, 0, PSEUDOCONSOLE_ATTRIBUTE, _pty, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowLast("UpdateProcThreadAttribute");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            var cmd = new StringBuilder(1024);
            cmd.Append('"').Append(shell).Append('"');
            var cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : null;

            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, cwd, ref si, out var pi))
                ThrowLast("CreateProcessW");

            _process = pi.hProcess;
            CloseHandle(pi.hThread);

            // FileStream takes ownership of the handles.
            _input = new FileStream(inWrite!, FileAccess.Write, 1, false); inWrite = null;
            _output = new FileStream(outRead!, FileAccess.Read, 1, false); outRead = null;

            _reader = Task.Run(ReadLoop);
            _waiter = Task.Run(WaitLoop);
        }
        catch
        {
            // If CreateProcess succeeded but a later pipe/stream setup failed, do not
            // leave the child process or native process handle behind.
            if (_process != IntPtr.Zero)
            {
                try { TerminateProcess(_process, 1); } catch { }
                try { CloseHandle(_process); } catch { }
                _process = IntPtr.Zero;
            }
            if (_pty != IntPtr.Zero) { ClosePseudoConsole(_pty); _pty = IntPtr.Zero; }
            inWrite?.Dispose();
            outRead?.Dispose();
            throw;
        }
        finally
        {
            inRead?.Dispose();
            outWrite?.Dispose();
            if (attrList != IntPtr.Zero)
            {
                if (attrInitialized) DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    private void ReadLoop()
    {
        var stream = _output!;
        var buffer = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder(); // keeps partial UTF-8 sequences between reads
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var count = decoder.GetChars(buffer, 0, n, chars, 0);
                if (count > 0) OutputReceived?.Invoke(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { try { stream.Dispose(); } catch { } }
    }

    private void WaitLoop()
    {
        var handle = _process;
        while (!_disposed)
        {
            var r = WaitForSingleObject(handle, 200);
            if (r == 0) break;            // WAIT_OBJECT_0: process exited
            if (r != 0x102) return;       // anything but WAIT_TIMEOUT is an error
        }
        if (_disposed) return;
        Thread.Sleep(250);                // let the reader drain the last output
        if (!_disposed) Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            if (_disposed || _input is null) return;
            try { _input.Write(bytes, 0, bytes.Length); _input.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(short cols, short rows)
    {
        lock (_gate)
        {
            if (!_disposed && _pty != IntPtr.Zero) ResizePseudoConsole(_pty, new COORD(cols, rows));
        }
    }

    public void Dispose()
    {
        IntPtr pty, process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pty = _pty; _pty = IntPtr.Zero;
            process = _process;
        }

        if (process != IntPtr.Zero) TerminateProcess(process, 0);
        try { _waiter?.Wait(500); } catch { }
        if (process != IntPtr.Zero) { CloseHandle(process); _process = IntPtr.Zero; }

        lock (_gate)
        {
            try { _input?.Dispose(); } catch { }
            _input = null;
        }

        // ClosePseudoConsole can block until output is drained; the reader thread keeps draining,
        // and closing it also breaks the output pipe so the reader ends. Never block the UI on it.
        if (pty != IntPtr.Zero) Task.Run(() => ClosePseudoConsole(pty));
    }

    private static void ThrowLast(string api) => throw new Win32Exception(Marshal.GetLastWin32Error(), api);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PSEUDOCONSOLE_ATTRIBUTE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; public COORD(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
