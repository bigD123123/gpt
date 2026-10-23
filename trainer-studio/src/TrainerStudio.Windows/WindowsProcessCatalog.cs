using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TrainerStudio.Windows;

public sealed record WindowsProcessDescriptor(
    int ProcessId,
    string Name,
    string ExecutablePath,
    string Version,
    string Architecture,
    DateTimeOffset? StartTimeUtc)
{
    public string Display => $"{Name} · PID {ProcessId} · {Architecture}";
    public string TargetId => WindowsProcessCatalog.GetTargetId(ExecutablePath);
    public string EngineHint => WindowsProcessCatalog.DetectEngine(ExecutablePath);
}

/// <summary>Read-only Windows process discovery. Enumeration does not request memory write access.</summary>
public static class WindowsProcessCatalog
{
    public static IReadOnlyList<WindowsProcessDescriptor> ListProcesses()
    {
        EnsureWindows();
        var result = new List<WindowsProcessDescriptor>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    result.Add(Describe(process.Id));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
                {
                    // Protected processes and processes that exit during discovery are not attachable here.
                }
            }
        }

        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.ProcessId).ToArray();
    }

    public static WindowsProcessDescriptor Describe(int processId)
    {
        EnsureWindows();
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            throw WindowsError($"Kan ikke læse oplysninger om proces {processId}");
        }

        var path = GetExecutablePath(handle);
        var creation = GetCreationTime(handle);
        return new WindowsProcessDescriptor(
            processId, Path.GetFileNameWithoutExtension(path), path, ReadVersion(path), GetArchitecture(handle), DateTimeOffset.FromFileTime(creation));
    }

    public static string GetTargetId(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var normalized = Path.GetFullPath(executablePath).ToUpperInvariant();
        return "windows:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string DetectEngine(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return "Ukendt";
        }

        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) ||
            Directory.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(executablePath) + "_Data")))
        {
            return File.Exists(Path.Combine(directory, "GameAssembly.dll"))
                ? "Unity / IL2CPP (filmarkører)"
                : "Unity (filmarkører; runtime ikke verificeret)";
        }

        if (Path.GetFileNameWithoutExtension(executablePath).Contains("-Shipping", StringComparison.OrdinalIgnoreCase) &&
            directory.Contains("Binaries", StringComparison.OrdinalIgnoreCase))
        {
            return "Mulig Unreal (filmarkører)";
        }

        return "Ukendt / generisk Windows-program";
    }

    internal static string GetExecutablePath(SafeProcessHandle process)
    {
        var path = new StringBuilder(32_768);
        uint size = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(process, 0, path, ref size))
        {
            throw WindowsError("Programfilens sti kan ikke læses");
        }

        return path.ToString();
    }

    internal static long GetCreationTime(SafeProcessHandle process)
    {
        if (!NativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
        {
            throw WindowsError("Procesidentiteten kan ikke kontrolleres");
        }

        return created.Value;
    }

    internal static string GetArchitecture(SafeProcessHandle process)
    {
        try
        {
            if (!NativeMethods.IsWow64Process2(process, out var processMachine, out var nativeMachine))
            {
                throw WindowsError("Procesarkitekturen kan ikke læses");
            }

            return (processMachine == 0 ? nativeMachine : processMachine) switch
            {
                0x014c => "x86",
                0x8664 => "x64",
                0xAA64 => "arm64",
                _ => "ukendt"
            };
        }
        catch (EntryPointNotFoundException)
        {
            if (!NativeMethods.IsWow64Process(process, out var wow64))
            {
                throw WindowsError("Procesarkitekturen kan ikke læses");
            }

            NativeMethods.GetNativeSystemInfo(out var nativeSystem);
            return ResolveLegacyArchitecture(nativeSystem.ProcessorArchitecture, wow64);
        }
    }

    // IsWow64Process alone cannot distinguish every ARM64 emulation case.
    // Refuse to guess on non-x86/x64 systems if the newer API is unavailable.
    internal static string ResolveLegacyArchitecture(ushort nativeArchitecture, bool wow64) =>
        (nativeArchitecture, wow64) switch
        {
            (0, false) => "x86", // PROCESSOR_ARCHITECTURE_INTEL
            (9, true) => "x86",  // PROCESSOR_ARCHITECTURE_AMD64 + WOW64
            (9, false) => "x64",
            _ => "ukendt"
        };

    internal static string ReadVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? "uden filversion" : version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return "ukendt filversion";
        }
    }

    internal static Win32Exception WindowsError(string context)
    {
        var code = Marshal.GetLastWin32Error();
        var detail = new Win32Exception(code).Message;
        return new Win32Exception(code, $"{context}: {detail} (Windows {code}).");
    }

    internal static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Procestilknytning kræver Windows.");
        }
    }
}
