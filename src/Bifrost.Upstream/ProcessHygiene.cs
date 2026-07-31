using System.Runtime.InteropServices;

namespace Bifrost.Upstream;

/// <summary>
/// Windows-Prozess-Hygiene (ADR-0005): hängt den Gateway-Prozess an ein Job Object mit
/// KILL_ON_JOB_CLOSE. Alle vom SDK gestarteten stdio-Kindprozesse erben die Job-Mitgliedschaft —
/// stirbt der Gateway (auch hart), räumt das OS alle Kinder ab. Unter Linux übernimmt die
/// stdio-EOF-Semantik diese Rolle (MCP-Server beenden sich bei geschlossenem stdin).
/// </summary>
internal static partial class ProcessHygiene
{
    private static readonly Lock InitLock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateJobObject fehlgeschlagen (Win32-Fehler {Marshal.GetLastPInvokeError()}).");
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                {
                    throw new InvalidOperationException(
                        $"SetInformationJobObject fehlgeschlagen (Win32-Fehler {Marshal.GetLastPInvokeError()}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            using var current = System.Diagnostics.Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(job, current.Handle))
            {
                // Bereits in einem Job ohne Nested-Job-Support (ältere Container-Hosts): dokumentiertes
                // Restrisiko, kein Startabbruch — die stdio-EOF-Semantik bleibt als Fallback.
                _initialized = true;
                return;
            }

            // Job-Handle absichtlich NICHT schließen: sein Lebensende (Prozess-Exit) ist der Kill-Trigger.
            _initialized = true;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

#pragma warning disable SYSLIB1054 // klassisches DllImport genügt hier; LibraryImport bringt für diese 3 Aufrufe nichts
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
