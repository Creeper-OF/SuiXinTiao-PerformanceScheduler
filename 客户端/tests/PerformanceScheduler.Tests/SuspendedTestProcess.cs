using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerformanceScheduler.Tests;

internal sealed class SuspendedTestProcess : IDisposable
{
    private SuspendedTestProcess(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public static SuspendedTestProcess Start(string executablePath)
    {
        var startupInfo = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>()
        };
        if (!CreateProcess(
                executablePath,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0x00000004 | 0x08000000, // CREATE_SUSPENDED | CREATE_NO_WINDOW
                IntPtr.Zero,
                null,
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var processHandle = new SafeProcessHandle(processInformation.ProcessHandle, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(processInformation.ThreadHandle, ownsHandle: true);
        try
        {
            return new SuspendedTestProcess(Process.GetProcessById(processInformation.ProcessId));
        }
        catch
        {
            TerminateProcess(processHandle, 1);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit();
            }
        }
        finally
        {
            Process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        string? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
