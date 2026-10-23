using System.Buffers.Binary;
using System.Diagnostics;

namespace TrainerStudio.Core;

public sealed record PointerScanOptions(
    int PointerSize = 0,
    int MaxDepth = 3,
    long MaxOffset = 4096,
    int MaxResults = 1000,
    long MaxScannedPointers = 4_000_000,
    int MaxPathExpansions = 100_000)
{
    public int ValidateAndGetPointerSize(TargetIdentity identity)
    {
        var width = PointerSize == 0 ? identity.Architecture switch
        {
            "x86" => 4,
            "x64" => 8,
            _ => throw new ArgumentException("Vælg 4- eller 8-byte pointere for målarkitekturen.")
        } : PointerSize;
        if (width is not (4 or 8) || MaxDepth is < 1 or > 8 || MaxOffset is < 0 or > 0x1000_0000L ||
            MaxResults is < 1 or > 10_000 || MaxScannedPointers is < 1 or > 16_000_000 ||
            MaxPathExpansions is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PointerScanOptions), "Pointergrænserne er ugyldige (bredde 4/8, dybde 1–8 og begrænset arbejde/resultatantal).");
        }

        if ((identity.Architecture == "x86" && width != 4) || (identity.Architecture == "x64" && width != 8))
        {
            throw new ArgumentException("Pointerbredden passer ikke til målprocessen.");
        }

        return width;
    }
}

public sealed record PointerScanResult(
    IReadOnlyList<MemoryReference> References,
    long ScannedPointerCount,
    int ExpandedPathCount,
    bool WasCapped,
    TimeSpan Elapsed);

/// <summary>
/// Bounded reverse pointer search. Only roots inside a known module become
/// saved paths. Results are candidates, not proof that a path has gameplay
/// meaning; they must be rescanned after a new process start.
/// </summary>
public sealed class PointerScanner
{
    private readonly record struct PointerEntry(long Value, long Address);
    private sealed record PathState(long Address, long[] Offsets, long[] VisitedAddresses);

    public PointerScanResult Scan(
        IApprovedMemoryTarget target,
        long targetAddress,
        ScanDataType dataType,
        PointerScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new PointerScanOptions();
        var width = options.ValidateAndGetPointerSize(target.Identity);
        if (targetAddress <= 0 || !target.TryRead(targetAddress, dataType, out var observed))
        {
            throw new ArgumentException("Vælg først en læsbar værdi som mål for pointersøgningen.", nameof(targetAddress));
        }

        if (target is not IModuleMemoryTarget moduleTarget || moduleTarget.Modules.Count == 0)
        {
            throw new InvalidOperationException("Målet skal oplyse modulgrænser, før der kan findes vedvarende pointerrødder.");
        }

        var timer = Stopwatch.StartNew();
        var sessionId = (target as ISessionMemoryTarget)?.SessionId;
        var modules = moduleTarget.Modules.ToArray();
        var entries = new List<PointerEntry>();
        var scanned = 0L;
        var capped = false;
        foreach (var entry in EnumeratePointers(target, width, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned == options.MaxScannedPointers)
            {
                capped = true;
                break;
            }

            scanned++;
            if (entry.Value > 0)
            {
                entries.Add(entry);
            }
        }

        if (target is ISnapshotDiagnosticsTarget diagnostics &&
            (diagnostics.WasSnapshotTruncated || diagnostics.SkippedReadCount > 0))
        {
            capped = true;
        }

        entries.Sort((left, right) =>
        {
            var order = left.Value.CompareTo(right.Value);
            return order != 0 ? order : left.Address.CompareTo(right.Address);
        });
        cancellationToken.ThrowIfCancellationRequested();

        var queue = new Queue<PathState>();
        queue.Enqueue(new PathState(targetAddress, [], [targetAddress]));
        var references = new List<MemoryReference>();
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStates = new HashSet<string>(StringComparer.Ordinal);
        var expansions = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = queue.Dequeue();
            var minimumValue = Math.Max(1, state.Address - options.MaxOffset);
            var first = LowerBound(entries, minimumValue);
            for (var index = first; index < entries.Count && entries[index].Value <= state.Address; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expansions == options.MaxPathExpansions)
                {
                    capped = true;
                    queue.Clear();
                    break;
                }

                expansions++;
                var entry = entries[index];
                if (state.VisitedAddresses.Contains(entry.Address))
                {
                    continue;
                }

                var offsets = new[] { checked(state.Address - entry.Value) }.Concat(state.Offsets).ToArray();
                var module = modules.FirstOrDefault(candidate => ContainsPointer(candidate, entry.Address, width));
                if (module is not null)
                {
                    var reference = new MemoryReference(
                        target.Identity.TargetId,
                        module.Name,
                        checked(entry.Address - module.BaseAddress),
                        dataType,
                        observed.Display,
                        Kind: MemoryReferenceKind.PointerPath,
                        PointerOffsets: offsets,
                        PointerSize: width,
                        ExpectedBuildFingerprint: target.Identity.BuildFingerprint,
                        ExpectedVersion: target.Identity.Version,
                        ExpectedModuleFingerprint: target is IModuleFingerprintTarget moduleIdentity &&
                            moduleIdentity.TryGetModuleFingerprint(module.Name, out var fingerprint) ? fingerprint : null);
                    if (TryResolveTo(reference, target, targetAddress))
                    {
                        var signature = $"{reference.ModuleName}:{reference.Offset}:{string.Join(",", offsets)}";
                        if (seenReferences.Add(signature))
                        {
                            references.Add(reference);
                            if (references.Count == options.MaxResults)
                            {
                                capped = true;
                                queue.Clear();
                                break;
                            }
                        }
                    }
                }

                if (offsets.Length < options.MaxDepth)
                {
                    var signature = $"{entry.Address}:{string.Join(",", offsets)}";
                    if (seenStates.Add(signature))
                    {
                        queue.Enqueue(new PathState(entry.Address, offsets, state.VisitedAddresses.Append(entry.Address).ToArray()));
                    }
                }
            }
        }

        if (target is ISessionMemoryTarget session && (!session.IsAlive || !StringComparer.Ordinal.Equals(sessionId, session.SessionId)))
        {
            throw new InvalidOperationException("Målprocessens session blev ændret under pointersøgningen. Resultaterne er kasseret.");
        }

        timer.Stop();
        return new PointerScanResult(references, scanned, expansions, capped, timer.Elapsed);
    }

    /// <summary>Retains paths that still resolve to the independently found value.</summary>
    public IReadOnlyList<MemoryReference> Rescan(
        IApprovedMemoryTarget target,
        IEnumerable<MemoryReference> references,
        long targetAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(references);
        if (targetAddress <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetAddress));
        }

        var survivors = new List<MemoryReference>();
        var inspected = 0;
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > 10_000)
            {
                throw new ArgumentException("En pointer-genkontrol kan højst indeholde 10.000 stier.", nameof(references));
            }

            if (reference.Kind == MemoryReferenceKind.PointerPath && TryResolveTo(reference, target, targetAddress))
            {
                survivors.Add(reference);
            }
        }

        return survivors;
    }

    private static bool TryResolveTo(MemoryReference reference, IApprovedMemoryTarget target, long expectedAddress)
    {
        try
        {
            return reference.Resolve(target) == expectedAddress && target.TryRead(expectedAddress, reference.DataType, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private static bool ContainsPointer(TargetModule module, long address, int width) =>
        module.BaseAddress > 0 && module.Size >= width && address >= module.BaseAddress &&
        address - module.BaseAddress <= module.Size - width;

    private static int LowerBound(List<PointerEntry> entries, long value)
    {
        var low = 0;
        var high = entries.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (entries[middle].Value < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static IEnumerable<PointerEntry> EnumeratePointers(IApprovedMemoryTarget target, int width, CancellationToken cancellationToken)
    {
        var regions = target is IStreamingMemoryTarget streaming
            ? streaming.EnumerateMemory(cancellationToken)
            : target.Snapshot();
        byte[] tail = [];
        long previousEnd = -1;
        string? previousName = null;
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = region.Bytes;
            var start = region.BaseAddress;
            if (tail.Length > 0 && previousEnd == start && StringComparer.Ordinal.Equals(previousName, region.Name))
            {
                data = new byte[checked(tail.Length + region.Bytes.Length)];
                tail.CopyTo(data, 0);
                region.Bytes.CopyTo(data, tail.Length);
                start = checked(start - tail.Length);
            }

            var firstOffset = (int)((width - start % width) % width);
            for (var offset = firstOffset; offset <= data.Length - width; offset += width)
            {
                if ((offset & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var value = width == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, width))
                    : BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, width));
                yield return new PointerEntry(value <= long.MaxValue ? (long)value : 0, checked(start + offset));
            }

            var tailLength = Math.Min(width - 1, data.Length);
            tail = data.AsSpan(data.Length - tailLength).ToArray();
            previousEnd = region.EndAddress;
            previousName = region.Name;
        }
    }
}
