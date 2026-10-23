using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using TrainerStudio.Core;

namespace TrainerStudio.Windows;

/// <summary>
/// Explicitly attached, user-selected Windows process. Read access and write
/// access have separate native handles. No privilege changes or protection
/// changes are performed. Reads are live observations, never atomic snapshots.
/// </summary>
public sealed class WindowsProcessTarget : IApprovedMemoryTarget, IStreamingMemoryTarget,
    ISessionMemoryTarget, IModuleMemoryTarget, ISnapshotDiagnosticsTarget, IModuleFingerprintTarget, IDisposable
{
    private readonly object _gate = new();
    private readonly SafeProcessHandle _readHandle;
    private readonly long _creationTime;
    private readonly long _maximumAddress;
    private readonly int _pageSize;
    private readonly Dictionary<ModuleFingerprintKey, string> _moduleFingerprints = [];
    private SafeProcessHandle? _writeHandle;
    private IReadOnlyList<TargetModule> _modules = [];
    private MemorySnapshotOptions _scanOptions = new();
    private bool _disposed;

    private WindowsProcessTarget(WindowsProcessDescriptor descriptor, SafeProcessHandle handle, MemorySnapshotOptions options)
    {
        Process = descriptor;
        _readHandle = handle;
        _creationTime = WindowsProcessCatalog.GetCreationTime(handle);
        var actualPath = WindowsProcessCatalog.GetExecutablePath(handle);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(descriptor.ExecutablePath)) ||
            descriptor.StartTimeUtc is { } expectedTime && expectedTime.ToFileTime() != _creationTime)
        {
            throw new InvalidOperationException("Processen er ændret siden den blev valgt. Opdater proceslisten og vælg den igen.");
        }

        var architecture = WindowsProcessCatalog.GetArchitecture(handle);
        if (!Environment.Is64BitProcess || architecture is not ("x86" or "x64"))
        {
            throw new PlatformNotSupportedException("Denne adapter kræver en 64-bit Trainer Studio og et x86- eller x64-mål.");
        }

        NativeMethods.GetSystemInfo(out var information);
        _maximumAddress = Math.Min(checked(information.MaximumApplicationAddress.ToInt64() + 1), architecture == "x86" ? 0x1_0000_0000L : long.MaxValue);
        _pageSize = checked((int)information.PageSize);
        SessionId = $"{descriptor.ProcessId}:{_creationTime}";
        Identity = new TargetIdentity(
            WindowsProcessCatalog.GetTargetId(actualPath), Path.GetFileNameWithoutExtension(actualPath),
            WindowsProcessCatalog.ReadVersion(actualPath), architecture, Path.GetFileName(actualPath), HashExecutable(actualPath));
        ScanOptions = options;
        RefreshModules();
    }

    public WindowsProcessDescriptor Process { get; }
    public TargetIdentity Identity { get; }
    public string SessionId { get; }
    public int PointerSize => Identity.Architecture == "x86" ? 4 : 8;
    public MemorySnapshotOptions ScanOptions
    {
        get => _scanOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            _scanOptions = value;
        }
    }
    public IReadOnlyList<TargetModule> Modules => _modules;
    public MemorySnapshotReport LastSnapshotReport { get; private set; } = new(0, 0, 0, false, "Ingen søgning endnu.");
    public string? LastModuleError { get; private set; }
    public bool WasSnapshotTruncated => LastSnapshotReport.WasTruncated;
    public long SnapshotBytesRead => LastSnapshotReport.BytesRead;
    public int SkippedReadCount => LastSnapshotReport.SkippedReadCount;

    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                return IsAliveCore();
            }
        }
    }

    public bool WritesEnabled
    {
        get
        {
            lock (_gate)
            {
                return _writeHandle is { IsClosed: false, IsInvalid: false } && IsAliveCore();
            }
        }
    }

    public static WindowsProcessTarget Attach(int processId, MemorySnapshotOptions? options = null) =>
        Attach(WindowsProcessCatalog.Describe(processId), options);

    public static WindowsProcessTarget Attach(WindowsProcessDescriptor descriptor, MemorySnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        WindowsProcessCatalog.EnsureWindows();
        options ??= new MemorySnapshotOptions();
        options.Validate();
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead | NativeMethods.Synchronize, false, descriptor.ProcessId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw WindowsProcessCatalog.WindowsError("Processen kan ikke åbnes til læsning");
        }

        try
        {
            return new WindowsProcessTarget(descriptor, handle, options);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void EnableWriting()
    {
        lock (_gate)
        {
            EnsureAlive();
            if (_writeHandle is { IsClosed: false, IsInvalid: false })
            {
                return;
            }

            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessVmOperation | NativeMethods.ProcessVmWrite |
                NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize, false, Process.ProcessId);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw WindowsProcessCatalog.WindowsError("Windows afviste skriveadgang til den valgte proces");
            }

            try
            {
                if (WindowsProcessCatalog.GetCreationTime(handle) != _creationTime || !IsAliveCore())
                {
                    throw new InvalidOperationException("Procesidentiteten er ændret. Tilknyt processen igen.");
                }

                _writeHandle = handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    public bool TryEnableWriting(out string error)
    {
        try
        {
            EnableWriting();
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public void DisableWriting()
    {
        lock (_gate)
        {
            _writeHandle?.Dispose();
            _writeHandle = null;
        }
    }

    public bool TryResolveModuleBase(string moduleName, out long baseAddress)
    {
        baseAddress = 0;
        if (!IsAlive)
        {
            return false;
        }

        try
        {
            RefreshModules();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            LastModuleError = exception.Message;
            return false;
        }

        var resolved = TryResolveUniqueModule(_modules, moduleName, out baseAddress);
        if (!resolved)
        {
            LastModuleError = $"Modulnavnet '{moduleName}' er ikke fundet entydigt i den valgte proces.";
        }

        return resolved;
    }

    internal static bool TryResolveUniqueModule(IEnumerable<TargetModule> modules, string moduleName, out long baseAddress)
    {
        // Saved references currently identify a module by basename only. A
        // duplicate basename from another path is ambiguous and must not
        // silently redirect a trainer write to whichever module came first.
        baseAddress = 0;
        if (!TryFindUniqueModule(modules, moduleName, out var module))
        {
            return false;
        }

        baseAddress = module!.BaseAddress;
        return true;
    }

    public bool TryGetModuleFingerprint(string moduleName, out string fingerprint)
    {
        fingerprint = string.Empty;
        try
        {
            lock (_gate)
            {
                RefreshModules();
                if (!TryFindUniqueModule(_modules, moduleName, out var module))
                {
                    return false;
                }

                var resolvedModule = module!;
                var modulePath = resolvedModule.Path;
                if (string.IsNullOrWhiteSpace(modulePath))
                {
                    return false;
                }

                var path = Path.GetFullPath(modulePath);
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return false;
                }

                var key = new ModuleFingerprintKey(path.ToUpperInvariant(), resolvedModule.BaseAddress, resolvedModule.Size, file.Length, file.LastWriteTimeUtc.Ticks);
                if (_moduleFingerprints.TryGetValue(key, out var cached))
                {
                    fingerprint = cached;
                    return true;
                }

                var computed = HashExecutable(path);
                file.Refresh();
                if (!file.Exists || file.Length != key.FileLength || file.LastWriteTimeUtc.Ticks != key.LastWriteTicks || !IsAliveCore())
                {
                    return false; // The module file changed while it was hashed.
                }

                _moduleFingerprints[key] = computed;
                fingerprint = computed;
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException or CryptographicException)
        {
            return false;
        }
    }

    internal static bool TryFindUniqueModule(IEnumerable<TargetModule> modules, string moduleName, out TargetModule? module)
    {
        var matches = CollapseIdenticalModuleEntries(modules.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, moduleName))).Take(2).ToArray();
        module = matches.Length == 1 ? matches[0] : null;
        return module is not null;
    }

    private static IEnumerable<TargetModule> CollapseIdenticalModuleEntries(IEnumerable<TargetModule> modules)
    {
        var seen = new HashSet<(string Name, long BaseAddress, long Size, string Path)>();
        foreach (var module in modules)
        {
            // WOW64 snapshots can contain the same record from both snapshot
            // flags. Only identical known paths, bases and sizes are duplicate
            // records; genuinely different same-name DLLs remain ambiguous.
            if (string.IsNullOrWhiteSpace(module.Path) || seen.Add((module.Name.ToUpperInvariant(), module.BaseAddress, module.Size, module.Path.ToUpperInvariant())))
            {
                yield return module;
            }
        }
    }

    public bool TryFindModule(long address, out TargetModule? module)
    {
        try
        {
            RefreshModules();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            module = null;
            return false;
        }

        module = _modules.FirstOrDefault(item => address >= item.BaseAddress && address - item.BaseAddress < item.Size);
        return module is not null;
    }

    public void RefreshModules()
    {
        lock (_gate)
        {
            EnsureAlive();
            const uint flags = 0x00000008 | 0x00000010; // TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32
            SafeFileHandle? snapshot = null;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    snapshot?.Dispose();
                    snapshot = NativeMethods.CreateToolhelp32Snapshot(flags, Process.ProcessId);
                    if (!snapshot.IsInvalid || Marshal.GetLastWin32Error() != 24)
                    {
                        break; // ERROR_BAD_LENGTH can occur while modules are loading.
                    }
                }

                if (snapshot is null || snapshot.IsInvalid)
                {
                    throw WindowsProcessCatalog.WindowsError("Modullisten kan ikke læses");
                }

                var entry = new NativeMethods.ModuleEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>(), ModuleName = string.Empty, ExePath = string.Empty };
                if (!NativeMethods.Module32First(snapshot, ref entry))
                {
                    throw WindowsProcessCatalog.WindowsError("Processens første modul kan ikke læses");
                }

                var modules = new List<TargetModule>();
                do
                {
                    modules.Add(new TargetModule(entry.ModuleName, entry.BaseAddress.ToInt64(), entry.BaseSize, entry.ExePath));
                    entry.Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>();
                }
                while (NativeMethods.Module32Next(snapshot, ref entry));

                if (Marshal.GetLastWin32Error() != 18) // ERROR_NO_MORE_FILES
                {
                    throw WindowsProcessCatalog.WindowsError("Modullisten ændrede sig under læsningen. Opdater den og prøv igen");
                }

                if (!IsAliveCore())
                {
                    throw new InvalidOperationException("Målprocessen er afsluttet.");
                }

                _modules = CollapseIdenticalModuleEntries(modules).OrderBy(item => item.BaseAddress).ToArray();
                LastModuleError = null;
            }
            finally
            {
                snapshot?.Dispose();
            }
        }
    }

    public IReadOnlyList<MemoryRegion> Snapshot()
    {
        // This compatibility path materializes at most 256 MiB. The scanner
        // should use EnumerateMemory so larger scopes remain streaming.
        var options = ScanOptions;
        return EnumerateMemory(options with { MaxBytes = Math.Min(options.MaxBytes, 256L * 1024 * 1024) }, CancellationToken.None).ToArray();
    }

    public IEnumerable<MemoryRegion> EnumerateMemory(CancellationToken cancellationToken) => EnumerateMemory(ScanOptions, cancellationToken);

    public IEnumerable<MemoryRegion> EnumerateMemory(MemorySnapshotOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureAlive();
        long bytesRead = 0;
        var regionsRead = 0;
        var skippedReads = 0;
        var capped = false;
        var complete = false;
        var scope = $"{options.StartAddress:X}–{Math.Min(options.EndAddressExclusive, _maximumAddress):X}; " +
            $"maks. {options.MaxBytes / (1024 * 1024d):0.##} MiB; " + (options.WritableOnly ? "skrivbare regioner" : "læsbare regioner") +
            (options.PrioritizeModules ? "; moduler læses først" : string.Empty);
        LastSnapshotReport = new(0, 0, 0, false, scope);
        var end = Math.Min(options.EndAddressExclusive, _maximumAddress);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.PrioritizeModules)
            {
                RefreshModules();
            }

            foreach (var addressScope in BuildScanScopes(options, end, _modules))
            {
                var cursor = addressScope.Start;
                while (cursor < addressScope.End)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureAlive();
                    if (!TryQuery(cursor, out var information))
                    {
                        skippedReads++;
                        break;
                    }

                    var regionBase = information.BaseAddress.ToInt64();
                    var regionEnd = checked(regionBase + (long)information.RegionSize);
                    if (regionEnd <= cursor)
                    {
                        throw new InvalidOperationException("Windows returnerede et ugyldigt hukommelsesinterval.");
                    }

                    var readEnd = Math.Min(addressScope.End, regionEnd);
                    if (IsReadable(information) && (!options.WritableOnly || IsWritable(information)) && IncludeType(information.Type, options))
                    {
                        regionsRead++;
                        var name = $"{RegionTypeName(information.Type)} 0x{regionBase:X}";
                        while (cursor < readEnd)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (bytesRead == options.MaxBytes)
                            {
                                capped = true;
                                yield break;
                            }

                            var length = checked((int)Math.Min(Math.Min(readEnd - cursor, options.ChunkBytes), options.MaxBytes - bytesRead));
                            var data = ReadChunk(cursor, length);
                            if (data.Length > 0)
                            {
                                var address = cursor;
                                cursor += data.Length;
                                bytesRead += data.Length;
                                yield return new MemoryRegion(address, data, name);
                            }

                            if (data.Length < length)
                            {
                                skippedReads++;
                                if (data.Length == 0)
                                {
                                    cursor += Math.Min(readEnd - cursor, _pageSize - cursor % _pageSize);
                                }
                            }
                        }
                    }

                    cursor = readEnd;
                }
            }

            complete = true;
        }
        finally
        {
            LastSnapshotReport = new(bytesRead, regionsRead, skippedReads, capped || !complete || skippedReads > 0, scope);
        }
    }

    internal readonly record struct AddressScope(long Start, long End);

    internal static IReadOnlyList<AddressScope> BuildScanScopes(MemorySnapshotOptions options, long end, IEnumerable<TargetModule> modules)
    {
        if (options.StartAddress >= end)
        {
            return [];
        }

        if (!options.PrioritizeModules)
        {
            return [new AddressScope(options.StartAddress, end)];
        }

        var clipped = modules.Where(module => module.BaseAddress >= 0 && module.Size > 0)
            .Select(module => new AddressScope(Math.Max(options.StartAddress, module.BaseAddress),
                Math.Min(end, module.BaseAddress > long.MaxValue - module.Size ? long.MaxValue : module.BaseAddress + module.Size)))
            .Where(range => range.Start < range.End).OrderBy(range => range.Start).ToArray();
        var merged = new List<AddressScope>();
        foreach (var range in clipped)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = new AddressScope(merged[^1].Start, Math.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        var complement = new List<AddressScope>();
        var cursor = options.StartAddress;
        foreach (var moduleScope in merged)
        {
            if (cursor < moduleScope.Start)
            {
                complement.Add(new AddressScope(cursor, moduleScope.Start));
            }

            cursor = moduleScope.End;
        }

        if (cursor < end)
        {
            complement.Add(new AddressScope(cursor, end));
        }

        // Scopes are disjoint and together cover precisely the requested
        // interval. Modules are visited before its complement; the caller's
        // single byte budget is shared across every scope.
        return merged.Concat(complement).ToArray();
    }

    public bool TryRead(long address, ScanDataType type, out ScanValue value)
    {
        if (TryReadBytes(address, ValueCodec.SizeOf(type), out var bytes))
        {
            return ValueCodec.TryRead(bytes, type, out value);
        }

        value = default;
        return false;
    }

    public bool TryReadBytes(long address, int count, out byte[] bytes)
    {
        lock (_gate)
        {
            bytes = [];
            if (count is < 1 or > 16 * 1024 * 1024 || !IsAliveCore() || !ValidateRange(address, count, requireWritable: false))
            {
                return false;
            }

            var buffer = new byte[count];
            if (!NativeMethods.ReadProcessMemory(_readHandle, (nint)address, buffer, (nuint)count, out var read) || read != (nuint)count)
            {
                return false;
            }

            bytes = buffer;
            return true;
        }
    }

    public bool TryWrite(long address, ScanValue value, out string error)
    {
        lock (_gate)
        {
            if (!IsAliveCore())
            {
                error = "Målprocessen er afsluttet eller frakoblet.";
                return false;
            }

            if (_writeHandle is null || _writeHandle.IsInvalid || _writeHandle.IsClosed)
            {
                error = "Processen er kun åbnet til læsning. Aktivér skriveadgang udtrykkeligt først.";
                return false;
            }

            if (value.Bytes is null || !Enum.IsDefined(value.Type) || value.Bytes.Length != ValueCodec.SizeOf(value.Type))
            {
                error = "Værdien har ugyldig datatype eller størrelse.";
                return false;
            }

            if (!ValidateRange(address, value.Bytes.Length, requireWritable: true))
            {
                error = "Hele adressens værdi skal ligge i læsbar og skrivbar, committed hukommelse uden guard-sider.";
                return false;
            }

            if (!NativeMethods.WriteProcessMemory(_writeHandle, (nint)address, value.Bytes, (nuint)value.Bytes.Length, out var written) || written != (nuint)value.Bytes.Length)
            {
                error = WindowsProcessCatalog.WindowsError("Windows kunne ikke skrive hele værdien; en delvis ændring kan være sket").Message;
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writeHandle?.Dispose();
            _writeHandle = null;
            _moduleFingerprints.Clear();
            _readHandle.Dispose();
        }
    }

    private byte[] ReadChunk(long address, int length)
    {
        lock (_gate)
        {
            EnsureAlive();
            var buffer = new byte[length];
            NativeMethods.ReadProcessMemory(_readHandle, (nint)address, buffer, (nuint)length, out var read);
            var actual = checked((int)Math.Min((ulong)read, (ulong)length));
            return actual == length ? buffer : buffer.AsSpan(0, actual).ToArray();
        }
    }

    private bool ValidateRange(long address, int count, bool requireWritable)
    {
        if (address < 0 || address >= _maximumAddress || count > _maximumAddress - address)
        {
            return false;
        }

        var end = address + count;
        var cursor = address;
        while (cursor < end)
        {
            if (!TryQuery(cursor, out var information) || !IsReadable(information) || requireWritable && !IsWritable(information))
            {
                return false;
            }

            var next = checked(information.BaseAddress.ToInt64() + (long)information.RegionSize);
            if (next <= cursor)
            {
                return false;
            }

            cursor = next;
        }

        return true;
    }

    private bool TryQuery(long address, out NativeMethods.MemoryBasicInformation information)
    {
        lock (_gate)
        {
            if (!IsAliveCore())
            {
                information = default;
                return false;
            }

            return NativeMethods.VirtualQueryEx(_readHandle, (nint)address, out information,
                (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>()) != 0;
        }
    }

    private bool IsAliveCore() => !_disposed && !_readHandle.IsClosed && !_readHandle.IsInvalid &&
        NativeMethods.WaitForSingleObject(_readHandle, 0) == NativeMethods.WaitTimeout;

    private void EnsureAlive()
    {
        lock (_gate)
        {
            if (!IsAliveCore())
            {
                throw new InvalidOperationException("Målprocessen er afsluttet eller frakoblet. Tilknyt den igen før næste handling.");
            }
        }
    }

    private static bool IsReadable(NativeMethods.MemoryBasicInformation information) =>
        information.State == NativeMethods.MemCommit && (information.Protect & NativeMethods.PageGuard) == 0 &&
        (information.Protect & 0xff) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;

    private static bool IsWritable(NativeMethods.MemoryBasicInformation information) =>
        IsReadable(information) && (information.Protect & 0xff) is 0x04 or 0x08 or 0x40 or 0x80;

    private static bool IncludeType(uint type, MemorySnapshotOptions options) => type switch
    {
        NativeMethods.MemPrivate => options.IncludePrivate,
        NativeMethods.MemImage => options.IncludeImages,
        NativeMethods.MemMapped => options.IncludeMapped,
        _ => false
    };

    private static string RegionTypeName(uint type) => type switch
    {
        NativeMethods.MemPrivate => "Private",
        NativeMethods.MemImage => "Image",
        NativeMethods.MemMapped => "Mapped",
        _ => "Memory"
    };

    private static string HashExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ModuleFingerprintKey(string Path, long BaseAddress, long ModuleSize, long FileLength, long LastWriteTicks);
}
