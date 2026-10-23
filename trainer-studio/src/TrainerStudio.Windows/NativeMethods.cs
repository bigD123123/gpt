using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrainerStudio.Windows;

internal static class NativeMethods
{
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemMapped = 0x40000;
    internal const uint MemImage = 0x1000000;
    internal const uint PageGuard = 0x100;
    internal const uint WaitTimeout = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation information, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint bytesWritten);

    [DllImport("kernel32.dll")]
    internal static extern void GetSystemInfo(out SystemInfo information);

    [DllImport("kernel32.dll")]
    internal static extern void GetNativeSystemInfo(out SystemInfo information);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32First(SafeFileHandle snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Module32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32Next(SafeFileHandle snapshot, ref ModuleEntry32 entry);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly long Value => checked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal nint BaseAddress;
        internal nint AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemInfo
    {
        internal ushort ProcessorArchitecture;
        internal ushort Reserved;
        internal uint PageSize;
        internal nint MinimumApplicationAddress;
        internal nint MaximumApplicationAddress;
        internal nuint ActiveProcessorMask;
        internal uint NumberOfProcessors;
        internal uint ProcessorType;
        internal uint AllocationGranularity;
        internal ushort ProcessorLevel;
        internal ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ModuleEntry32
    {
        internal uint Size;
        internal uint ModuleId;
        internal uint ProcessId;
        internal uint GlobalUsage;
        internal uint ProcessUsage;
        internal nint BaseAddress;
        internal uint BaseSize;
        internal nint ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExePath;
    }
}
