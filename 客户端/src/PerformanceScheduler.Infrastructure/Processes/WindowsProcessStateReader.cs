using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PerformanceScheduler.Infrastructure.Processes;

internal static class WindowsProcessStateReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumImagePathCharacters = 32768;

    public static ProcessPriorityClass ReadPriority(Process process)
    {
        // PriorityClass also caches values assigned through its setter.
        // Refresh before reading so write verification observes the OS state.
        process.Refresh();
        return process.PriorityClass;
    }

    public static string? TryReadImagePath(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Module enumeration can report ntdll.dll while the process loader
        // initializes. Query the executable image with limited query access.
        using var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, process.Id);
        if (handle.IsInvalid)
        {
            return null;
        }

        var path = new StringBuilder(MaximumImagePathCharacters);
        var length = (uint)path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) && length > 0
            ? path.ToString()
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint length);
}
