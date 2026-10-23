using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TrainerStudio.Core;
using TrainerStudio.Windows;

namespace TrainerStudio.Windows.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows process adapter tests require Windows.");
            return 1;
        }

        if (args.Contains("--fixture", StringComparer.Ordinal))
        {
            return RunFixture();
        }

        try
        {
            RunTests();
            Console.WriteLine("PASS: 8 Windows process adapter tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            var annotation = exception.ToString().Replace("%", "%25", StringComparison.Ordinal)
                .Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
            Console.Error.WriteLine($"::error title=Windows process test::{annotation}");
            return 1;
        }
    }

    private static void RunTests()
    {
        // A short-lived console fixture owns only three allocated pages. It is
        // ordinary integration-test code, not a game or replacement game test.
        using var fixture = StartFixture();
        try
        {
            var line = fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var allocation = JsonSerializer.Deserialize<FixtureAllocation>(line ?? throw new InvalidOperationException("Fixture did not report its allocation."))
                ?? throw new InvalidOperationException("Fixture allocation is invalid.");
            var descriptor = WindowsProcessCatalog.Describe(fixture.Id);
            Require(descriptor.ProcessId == fixture.Id && File.Exists(descriptor.ExecutablePath), "Discovery must report the selected executable.");
            Require(descriptor.TargetId == WindowsProcessCatalog.GetTargetId(descriptor.ExecutablePath), "Discovery and attachment must share stable target ids.");
            Require(WindowsProcessCatalog.ListProcesses().Any(process => process.ProcessId == fixture.Id), "Fixture must be discoverable.");
            Require(WindowsProcessCatalog.ResolveLegacyArchitecture(9, false) == "x64" &&
                WindowsProcessCatalog.ResolveLegacyArchitecture(9, true) == "x86" &&
                WindowsProcessCatalog.ResolveLegacyArchitecture(12, false) == "ukendt" &&
                WindowsProcessCatalog.ResolveLegacyArchitecture(12, true) == "ukendt", "Legacy architecture detection must not label ambiguous ARM64 emulation as x64 or x86.");
            Console.WriteLine("PASS: Process discovery reports executable, architecture and stable identity");

            using var target = WindowsProcessTarget.Attach(descriptor, new MemorySnapshotOptions(
                MaxBytes: 3 * 4096, ChunkBytes: 4096, WritableOnly: false,
                StartAddress: allocation.Address, EndAddressExclusive: allocation.Address + 3 * 4096));
            var expectedPointerSize = descriptor.Architecture == "x86" ? 4 : 8;
            Require(target.IsAlive && target.Identity.TargetId == descriptor.TargetId && target.PointerSize == expectedPointerSize, "Target must retain selected live process identity and pointer width.");
            Require(target.Modules.Count > 0 && target.TryResolveModuleBase(target.Identity.ModuleName, out var moduleBase) && moduleBase > 0,
                "The primary module must resolve from the process module list.");
            var duplicateModules = new[]
            {
                new TargetModule("shared.dll", 0x1000, 4096, @"C:\one\shared.dll"),
                new TargetModule("SHARED.dll", 0x2000, 4096, @"C:\two\SHARED.dll")
            };
            Require(!WindowsProcessTarget.TryResolveUniqueModule(duplicateModules, "shared.dll", out var ambiguousBase) && ambiguousBase == 0,
                "Duplicate module basenames must fail closed rather than choose the first DLL.");
            Require(!WindowsProcessTarget.TryFindUniqueModule(duplicateModules, "shared.dll", out _),
                "The module fingerprint selector must also reject duplicate DLL basenames.");
            Require(WindowsProcessTarget.TryResolveUniqueModule(duplicateModules.Take(1), "SHARED.dll", out var uniqueBase) && uniqueBase == 0x1000,
                "A single module basename should resolve without case sensitivity.");
            Require(target.TryGetModuleFingerprint(target.Identity.ModuleName, out var moduleFingerprint) && moduleFingerprint == target.Identity.BuildFingerprint,
                "The loaded executable module fingerprint must match its primary executable SHA-256.");
            Require(target.TryGetModuleFingerprint(target.Identity.ModuleName.ToUpperInvariant(), out var cachedFingerprint) && cachedFingerprint == moduleFingerprint,
                "Cached module fingerprints must preserve the same case-insensitive module identity.");
            Require(!target.TryGetModuleFingerprint("nonexistent-trainer-studio-test-module.dll", out _),
                "Unknown module names must not produce a fingerprint.");
            Console.WriteLine($"PASS: Attach resolves {descriptor.Architecture} modules with a read-only process handle");

            var valueAddress = allocation.Address + 16;
            Require(target.TryRead(valueAddress, ScanDataType.Int32, out var initial) && initial.Display == "1234567", "Cross-process read must use the known fixture allocation.");
            Require(!target.WritesEnabled && !target.TryWrite(valueAddress, ScanValue.Parse(ScanDataType.Int32, "777"), out _),
                "New attachments must refuse writes.");
            Require(target.TryRead(valueAddress, ScanDataType.Int32, out initial) && initial.Display == "1234567", "Refused writes must leave allocation unchanged.");
            Console.WriteLine("PASS: Cross-process reads work and read-only writes are refused");

            target.EnableWriting();
            Require(target.WritesEnabled && target.TryWrite(valueAddress, ScanValue.Parse(ScanDataType.Int32, "777"), out _), "Explicit write access must allow the known writable allocation.");
            Require(target.TryRead(valueAddress, ScanDataType.Int32, out var updated) && updated.Display == "777", "Written value must be readable from the process.");
            var readOnlyAddress = allocation.Address + 2 * 4096;
            Require(target.TryRead(readOnlyAddress, ScanDataType.Int32, out var readOnly) && readOnly.Display == "4321", "Read-only pages remain readable.");
            Require(!target.TryWrite(readOnlyAddress, ScanValue.Parse(ScanDataType.Int32, "1"), out _), "Writes must not change read-only page protection.");
            target.DisableWriting();
            Require(!target.WritesEnabled && !target.TryWrite(valueAddress, ScanValue.Parse(ScanDataType.Int32, "1"), out _), "Disabling writes must close write access.");
            Console.WriteLine("PASS: Scoped write access works and read-only regions stay protected");

            var regions = target.EnumerateMemory(CancellationToken.None).ToArray();
            Require(regions.Sum(region => region.Bytes.Length) == 3 * 4096, "All bounded fixture bytes must be streamed.");
            Require(!target.WasSnapshotTruncated && target.SkippedReadCount == 0, "Complete bounded scans must not claim truncation.");
            var scanner = new MemoryScanner();
            var boundaryValue = ScanValue.Parse(ScanDataType.Int32, "987654321");
            scanner.FirstScan(target, new ScanRequest(ScanDataType.Int32, ScanCondition.ExactValue, boundaryValue,
                Alignment: ScanAlignment.EveryByte));
            Require(scanner.Candidates.Any(hit => hit.Address == allocation.Address + 4094), "Scanner must find a value spanning a streaming chunk boundary.");
            target.ScanOptions = target.ScanOptions with { MaxBytes = 4096 };
            Require(target.EnumerateMemory(CancellationToken.None).Sum(region => region.Bytes.Length) == 4096 && target.WasSnapshotTruncated,
                "Configured read caps must bound data and report truncation.");
            var priorityOptions = new MemorySnapshotOptions(MaxBytes: 4096, ChunkBytes: 4096, WritableOnly: false, PrioritizeModules: true);
            var prioritized = target.EnumerateMemory(priorityOptions).ToArray();
            Require(prioritized.Sum(region => region.Bytes.Length) == 4096 && target.WasSnapshotTruncated &&
                target.Modules.Any(module => prioritized[0].BaseAddress >= module.BaseAddress && prioritized[0].BaseAddress - module.BaseAddress < module.Size),
                "Module-priority reads must reach a loaded module first without increasing the shared byte cap.");
            var scopes = WindowsProcessTarget.BuildScanScopes(priorityOptions with { StartAddress = 100 }, 1000,
                [new TargetModule("a", 300, 200), new TargetModule("overlap", 400, 200), new TargetModule("clipped", 900, 200)]);
            Require(scopes.SequenceEqual(new[]
            {
                new WindowsProcessTarget.AddressScope(300, 600), new WindowsProcessTarget.AddressScope(900, 1000),
                new WindowsProcessTarget.AddressScope(100, 300), new WindowsProcessTarget.AddressScope(600, 900)
            }) && scopes.Sum(range => range.End - range.Start) == 900, "Priority scopes must merge overlaps and cover the requested range exactly once.");
            Require(WindowsProcessTarget.BuildScanScopes(priorityOptions with { StartAddress = 100 }, 1000, [])
                .SequenceEqual([new WindowsProcessTarget.AddressScope(100, 1000)]), "An empty module list must preserve the normal scan scope.");
            Console.WriteLine("PASS: Bounded streaming preserves boundary values and reports byte caps");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                RequireThrows<OperationCanceledException>(() => target.EnumerateMemory(cancelled.Token).ToArray(), "Cancelled scans must stop.");
            }
            Require(!target.TryRead(-1, ScanDataType.Int32, out _) && !target.TryRead(long.MaxValue - 1, ScanDataType.Double, out _), "Invalid read ranges must be refused.");
            RequireThrows<InvalidOperationException>(() => WindowsProcessTarget.Attach(descriptor with { StartTimeUtc = descriptor.StartTimeUtc?.AddSeconds(-1) }).Dispose(),
                "A stale process selection must not attach.");
            Console.WriteLine("PASS: Cancellation, invalid ranges and stale process selections are handled");

            fixture.StandardInput.WriteLine("exit");
            fixture.StandardInput.Flush();
            Require(fixture.WaitForExit(10_000), "Fixture should exit after its test command.");
            Require(!target.IsAlive && !target.TryRead(valueAddress, ScanDataType.Int32, out _) &&
                !target.TryWrite(valueAddress, ScanValue.Parse(ScanDataType.Int32, "1"), out _), "An exited process must not accept further operations.");
            Console.WriteLine("PASS: Process exit disables reads and writes");

            target.Dispose();
            Require(!target.IsAlive && !target.WritesEnabled && !target.TryRead(valueAddress, ScanDataType.Int32, out _), "Disposed handles must remain inactive.");
            Console.WriteLine("PASS: Disposal is idempotent and closes access");
        }
        finally
        {
            if (!fixture.HasExited)
            {
                try
                {
                    fixture.StandardInput.WriteLine("exit");
                    fixture.StandardInput.Flush();
                }
                catch (IOException)
                {
                    // The fixture may already have closed its input while exiting.
                }

                if (!fixture.WaitForExit(5000))
                {
                    fixture.Kill(); // Only this test-created fixture process.
                    fixture.WaitForExit();
                }
            }
        }
    }

    private static Process StartFixture()
    {
        var configuredFixture = Environment.GetEnvironmentVariable("TRAINER_STUDIO_FIXTURE_EXE");
        var executable = string.IsNullOrWhiteSpace(configuredFixture)
            ? Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.")
            : Path.GetFullPath(configuredFixture);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The configured Windows test fixture executable does not exist.", executable);
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet"))
        {
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TrainerStudio.Windows.Tests.dll"));
        }

        start.ArgumentList.Add("--fixture");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the test fixture.");
    }

    private static int RunFixture()
    {
        var allocation = VirtualAlloc(0, 3 * 4096, 0x1000 | 0x2000, 0x04);
        if (allocation == 0)
        {
            return 2;
        }

        try
        {
            Marshal.WriteInt32(allocation, 16, 1234567);
            Marshal.WriteInt32(allocation, 4094, 987654321);
            Marshal.WriteInt32(allocation, 2 * 4096, 4321);
            if (!VirtualProtect(allocation + 2 * 4096, 4096, 0x02, out _))
            {
                return 3;
            }

            var unsignedAddress = IntPtr.Size == 4 ? unchecked((uint)allocation.ToInt32()) : allocation.ToInt64();
            Console.WriteLine(JsonSerializer.Serialize(new FixtureAllocation(unsignedAddress)));
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        finally
        {
            VirtualFree(allocation, 0, 0x8000);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed record FixtureAllocation(long Address);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
