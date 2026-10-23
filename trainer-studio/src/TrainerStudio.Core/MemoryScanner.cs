using System.Buffers.Binary;
using System.Diagnostics;

namespace TrainerStudio.Core;

public sealed record ScanHit(long Address, ScanValue CurrentValue, ScanValue? PreviousValue, string RegionName)
{
    public string AddressText => $"0x{Address:X}";

    public string ChangeText => PreviousValue is null
        ? "Første observation"
        : $"{PreviousValue.Value.Display} → {CurrentValue.Display}";
}

public sealed record ScanRequest(
    ScanDataType DataType,
    ScanCondition Condition,
    ScanValue? ExactValue = null,
    int MaxCandidates = 100_000,
    ScanAlignment Alignment = ScanAlignment.Native)
{
    public void Validate(bool isFirstScan)
    {
        if (!Enum.IsDefined(DataType) || !Enum.IsDefined(Condition) || !Enum.IsDefined(Alignment))
        {
            throw new ArgumentException("Datatypen, søgereglen eller justeringen er ugyldig.");
        }

        if (MaxCandidates is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCandidates), "Kandidatgrænsen skal være mellem 1 og 1.000.000.");
        }

        if (Condition == ScanCondition.ExactValue && ExactValue is null)
        {
            throw new ArgumentException("En præcis søgning kræver en værdi.", nameof(ExactValue));
        }

        if (ExactValue is { } exact &&
            (exact.Type != DataType || exact.Bytes is null || exact.Bytes.Length != ValueCodec.SizeOf(DataType)))
        {
            throw new ArgumentException("Søgeværdien har en anden datatype eller ugyldige bytes.", nameof(ExactValue));
        }

        if (isFirstScan && Condition is not (ScanCondition.ExactValue or ScanCondition.UnknownInitialValue))
        {
            throw new ArgumentException("Første søgning skal være præcis værdi eller ukendt startværdi.", nameof(Condition));
        }

        if (!isFirstScan && Condition == ScanCondition.UnknownInitialValue)
        {
            throw new ArgumentException("Ukendt startværdi bruges kun ved første søgning.", nameof(Condition));
        }
    }
}

public sealed record ScanSummary(
    int CandidateCount,
    long ScannedValueCount,
    TimeSpan Elapsed,
    ScanCondition Condition,
    bool WasCapped,
    bool WasSnapshotTruncated = false,
    int SkippedReadCount = 0);

public sealed record ScanProgress(
    long ScannedValueCount,
    int CandidateCount,
    TimeSpan Elapsed,
    long? TotalValueCount = null);

/// <summary>
/// Read-only scanner for an explicitly supplied target. A first scan streams
/// bounded chunks where supported; next scans only revisit current candidates.
/// A cancelled or failed operation never replaces the previous scan session.
/// Callers must serialize operations on one scanner instance.
/// </summary>
public sealed class MemoryScanner
{
    private Dictionary<long, ScanHit> _candidates = new();
    private IApprovedMemoryTarget? _sessionTarget;
    private string? _sessionId;
    private bool _sessionWasCapped;
    private bool _sessionWasSnapshotTruncated;
    private int _sessionSkippedReadCount;

    public bool HasSession { get; private set; }

    public ScanDataType? DataType { get; private set; }

    public TargetIdentity? Target { get; private set; }

    public IReadOnlyList<ScanHit> Candidates => _candidates.Values.OrderBy(hit => hit.Address).ToArray();

    public ScanSummary FirstScan(
        IApprovedMemoryTarget target,
        ScanRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(isFirstScan: true);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = target.Identity;
        var sessionId = CaptureSession(target);
        var counter = new ScanCounter(cancellationToken, progress);
        var hits = new Dictionary<long, ScanHit>();
        var width = ValueCodec.SizeOf(request.DataType);
        var stride = request.Alignment == ScanAlignment.EveryByte ? 1 : width;
        var carry = new byte[width - 1];
        var carryLength = 0;
        var previousEnd = -1L;
        string? previousName = null;
        var capped = false;
        Span<byte> boundary = stackalloc byte[14];

        var regions = target is IStreamingMemoryTarget streaming
            ? streaming.EnumerateMemory(cancellationToken)
            : target.Snapshot();
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSession(target, identity, sessionId);
            if (region.BaseAddress < 0 || region.Bytes is null)
            {
                throw new InvalidOperationException("Målet leverede en ugyldig hukommelsesblok.");
            }

            var endAddress = region.EndAddress;
            var contiguous = previousEnd == region.BaseAddress &&
                StringComparer.Ordinal.Equals(previousName, region.Name);
            if (!contiguous)
            {
                carryLength = 0;
            }

            // Only boundary bytes are copied. Full chunks are never combined,
            // and non-matching values allocate no ScanValue/byte[] instances.
            if (carryLength > 0 && region.Bytes.Length > 0)
            {
                carry.AsSpan(0, carryLength).CopyTo(boundary);
                var prefixLength = Math.Min(width - 1, region.Bytes.Length);
                region.Bytes.AsSpan(0, prefixLength).CopyTo(boundary[carryLength..]);
                var boundaryBase = checked(region.BaseAddress - carryLength);
                var boundaryLength = carryLength + prefixLength;
                var start = AlignedOffset(boundaryBase, stride);
                for (var offset = start; offset < carryLength && offset <= boundaryLength - width; offset += stride)
                {
                    if (!Inspect(boundary.Slice(offset, width), checked(boundaryBase + offset), region.Name, request, hits, counter))
                    {
                        capped = true;
                        break;
                    }
                }
            }

            if (!capped)
            {
                var start = AlignedOffset(region.BaseAddress, stride);
                for (var offset = start; offset <= region.Bytes.Length - width; offset += stride)
                {
                    if (!Inspect(region.Bytes.AsSpan(offset, width), checked(region.BaseAddress + offset), region.Name, request, hits, counter))
                    {
                        capped = true;
                        break;
                    }
                }
            }

            if (capped)
            {
                break;
            }

            KeepTail(carry, ref carryLength, region.Bytes);
            previousEnd = endAddress;
            previousName = region.Name;
            counter.Report(hits.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureSession(target, identity, sessionId);
        var diagnostics = target as ISnapshotDiagnosticsTarget;
        var truncated = diagnostics?.WasSnapshotTruncated ?? false;
        var skipped = diagnostics?.SkippedReadCount ?? 0;
        var summary = new ScanSummary(hits.Count, counter.Scanned, counter.Elapsed, request.Condition, capped, truncated, skipped);
        counter.Report(hits.Count, force: true);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSession(target, identity, sessionId);
        _candidates = hits;
        _sessionTarget = target;
        _sessionId = sessionId;
        _sessionWasCapped = capped;
        _sessionWasSnapshotTruncated = truncated;
        _sessionSkippedReadCount = skipped;
        HasSession = true;
        DataType = request.DataType;
        Target = identity;
        return summary;
    }

    public ScanSummary NextScan(
        IApprovedMemoryTarget target,
        ScanRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        if (!HasSession || DataType is null || Target is null)
        {
            throw new InvalidOperationException("Kør første søgning før næste søgning.");
        }

        request.Validate(isFirstScan: false);
        if (request.DataType != DataType)
        {
            throw new InvalidOperationException("Datatypen kan ikke ændres midt i en søgesession.");
        }

        if (!ReferenceEquals(_sessionTarget, target))
        {
            throw new InvalidOperationException("Målprogrammet eller tilknytningen er skiftet. Start en ny søgning.");
        }

        EnsureSession(target, Target, _sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var counter = new ScanCounter(cancellationToken, progress, _candidates.Count);
        var next = new Dictionary<long, ScanHit>();
        var capped = _sessionWasCapped;
        var unreadable = 0;
        foreach (var hit in _candidates.Values)
        {
            counter.Step(next.Count);
            if (!target.TryRead(hit.Address, request.DataType, out var current))
            {
                unreadable++;
                continue;
            }

            if (!Matches(request, hit.CurrentValue, current))
            {
                continue;
            }

            if (next.Count == request.MaxCandidates)
            {
                capped = true;
                break;
            }

            next.Add(hit.Address, hit with { PreviousValue = hit.CurrentValue, CurrentValue = current });
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureSession(target, Target, _sessionId);
        var truncated = _sessionWasSnapshotTruncated || unreadable > 0;
        var skipped = checked(_sessionSkippedReadCount + unreadable);
        var summary = new ScanSummary(next.Count, counter.Scanned, counter.Elapsed, request.Condition, capped, truncated, skipped);
        counter.Report(next.Count, force: true);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSession(target, Target, _sessionId);
        _candidates = next;
        _sessionWasCapped = capped;
        _sessionWasSnapshotTruncated = truncated;
        _sessionSkippedReadCount = skipped;
        return summary;
    }

    public void Clear()
    {
        _candidates.Clear();
        _sessionTarget = null;
        _sessionId = null;
        _sessionWasCapped = false;
        _sessionWasSnapshotTruncated = false;
        _sessionSkippedReadCount = 0;
        HasSession = false;
        DataType = null;
        Target = null;
    }

    private static bool Inspect(
        ReadOnlySpan<byte> bytes,
        long address,
        string regionName,
        ScanRequest request,
        Dictionary<long, ScanHit> hits,
        ScanCounter counter)
    {
        counter.Step(hits.Count);
        if (request.Condition != ScanCondition.UnknownInitialValue && !MatchesExact(bytes, request.ExactValue!.Value))
        {
            return true;
        }

        if (hits.ContainsKey(address))
        {
            return true;
        }

        if (hits.Count == request.MaxCandidates)
        {
            return false;
        }

        hits.Add(address, new ScanHit(address, new ScanValue(request.DataType, bytes.ToArray()), null, regionName));
        return true;
    }

    private static bool MatchesExact(ReadOnlySpan<byte> bytes, ScanValue exact) => exact.Type switch
    {
        ScanDataType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
            .Equals(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(exact.Bytes))),
        ScanDataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
            .Equals(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(exact.Bytes))),
        _ => bytes.SequenceEqual(exact.Bytes)
    };

    private static int AlignedOffset(long baseAddress, int stride) => (int)((stride - baseAddress % stride) % stride);

    private static void KeepTail(byte[] carry, ref int carryLength, byte[] bytes)
    {
        if (carry.Length == 0)
        {
            return;
        }

        if (bytes.Length >= carry.Length)
        {
            bytes.AsSpan(bytes.Length - carry.Length).CopyTo(carry);
            carryLength = carry.Length;
            return;
        }

        var previousCount = Math.Min(carryLength, carry.Length - bytes.Length);
        carry.AsSpan(carryLength - previousCount, previousCount).CopyTo(carry);
        bytes.CopyTo(carry, previousCount);
        carryLength = previousCount + bytes.Length;
    }

    private static string? CaptureSession(IApprovedMemoryTarget target)
    {
        if (target is not ISessionMemoryTarget session)
        {
            return null;
        }

        if (!session.IsAlive || string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new InvalidOperationException("Målprogrammet er lukket eller har ingen gyldig session.");
        }

        return session.SessionId;
    }

    private static void EnsureSession(IApprovedMemoryTarget target, TargetIdentity identity, string? sessionId)
    {
        if (target.Identity != identity ||
            (target is ISessionMemoryTarget session && (!session.IsAlive || !StringComparer.Ordinal.Equals(sessionId, session.SessionId))))
        {
            throw new InvalidOperationException("Målprogrammets session er ændret eller lukket. Start en ny søgning.");
        }
    }

    private static bool Matches(ScanRequest request, ScanValue previous, ScanValue current) => request.Condition switch
    {
        ScanCondition.ExactValue => current.NumericallyEquals(request.ExactValue!.Value),
        ScanCondition.Increased => HasRelativeOrder(current, previous, isIncrease: true),
        ScanCondition.Decreased => HasRelativeOrder(current, previous, isIncrease: false),
        ScanCondition.Changed => !current.NumericallyEquals(previous),
        ScanCondition.Unchanged => current.NumericallyEquals(previous),
        _ => false
    };

    private static bool HasRelativeOrder(ScanValue current, ScanValue previous, bool isIncrease) =>
        ValueCodec.TryCompare(current, previous, out var comparison) && (isIncrease ? comparison > 0 : comparison < 0);

    private sealed class ScanCounter(CancellationToken cancellationToken, IProgress<ScanProgress>? progress, long? total = null)
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private long _lastReportMilliseconds;

        public long Scanned { get; private set; }

        public TimeSpan Elapsed => _timer.Elapsed;

        public void Step(int candidates)
        {
            Scanned++;
            if ((Scanned & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(candidates);
            }
        }

        public void Report(int candidates, bool force = false)
        {
            var elapsedMilliseconds = _timer.ElapsedMilliseconds;
            if (progress is not null && (force || elapsedMilliseconds - _lastReportMilliseconds >= 100))
            {
                _lastReportMilliseconds = elapsedMilliseconds;
                progress.Report(new ScanProgress(Scanned, candidates, _timer.Elapsed, total));
            }
        }
    }
}
