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
        if (MaxCandidates is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCandidates), "Kandidatgrænsen skal være mellem 1 og 1.000.000.");
        }

        if (Condition == ScanCondition.ExactValue && ExactValue is null)
        {
            throw new ArgumentException("En præcis søgning kræver en værdi.", nameof(ExactValue));
        }

        if (ExactValue is { } exact && exact.Type != DataType)
        {
            throw new ArgumentException("Søgeværdien har en anden datatype end søgningen.", nameof(ExactValue));
        }

        if (!Enum.IsDefined(Alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(Alignment));
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
    int ScannedValueCount,
    TimeSpan Elapsed,
    ScanCondition Condition,
    bool WasCapped);

/// <summary>
/// Deterministic snapshot scanner. It never selects a process, opens a PID,
/// or writes memory. Targets provide a bounded snapshot through the approved
/// target contract.
/// </summary>
public sealed class MemoryScanner
{
    private readonly Dictionary<long, ScanHit> _candidates = new();

    public bool HasSession { get; private set; }

    public ScanDataType? DataType { get; private set; }

    public TargetIdentity? Target { get; private set; }

    public IReadOnlyList<ScanHit> Candidates => _candidates.Values.OrderBy(hit => hit.Address).ToArray();

    public ScanSummary FirstScan(IApprovedMemoryTarget target, ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        request.Validate(isFirstScan: true);
        var timer = Stopwatch.StartNew();
        var hits = new Dictionary<long, ScanHit>();
        var scanned = 0;
        var capped = false;
        var width = ValueCodec.SizeOf(request.DataType);
        var stride = request.Alignment == ScanAlignment.EveryByte ? 1 : width;

        foreach (var region in target.Snapshot())
        {
            for (var offset = 0; offset <= region.Bytes.Length - width; offset += stride)
            {
                scanned++;
                if (!ValueCodec.TryRead(region.Bytes.AsSpan(offset), request.DataType, out var value))
                {
                    continue;
                }

                var isMatch = request.Condition == ScanCondition.UnknownInitialValue ||
                    value.NumericallyEquals(request.ExactValue!.Value);
                if (!isMatch)
                {
                    continue;
                }

                if (hits.Count == request.MaxCandidates)
                {
                    capped = true;
                    break;
                }

                var address = checked(region.BaseAddress + offset);
                hits[address] = new ScanHit(address, value, null, region.Name);
            }

            if (capped)
            {
                break;
            }
        }

        _candidates.Clear();
        foreach (var pair in hits)
        {
            _candidates.Add(pair.Key, pair.Value);
        }

        HasSession = true;
        DataType = request.DataType;
        Target = target.Identity;
        timer.Stop();
        return new ScanSummary(_candidates.Count, scanned, timer.Elapsed, request.Condition, capped);
    }

    public ScanSummary NextScan(IApprovedMemoryTarget target, ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!HasSession || DataType is null || Target is null)
        {
            throw new InvalidOperationException("Kør første søgning før næste søgning.");
        }

        request.Validate(isFirstScan: false);
        if (request.DataType != DataType)
        {
            throw new InvalidOperationException("Datatypen kan ikke ændres midt i en søgesession.");
        }

        if (!StringComparer.Ordinal.Equals(Target.TargetId, target.Identity.TargetId))
        {
            throw new InvalidOperationException("Målprogrammet er skiftet. Start en ny søgning.");
        }

        var timer = Stopwatch.StartNew();
        var next = new Dictionary<long, ScanHit>();
        var scanned = 0;
        var capped = false;
        foreach (var hit in _candidates.Values)
        {
            scanned++;
            if (!target.TryRead(hit.Address, request.DataType, out var current))
            {
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

        _candidates.Clear();
        foreach (var pair in next)
        {
            _candidates.Add(pair.Key, pair.Value);
        }

        timer.Stop();
        return new ScanSummary(_candidates.Count, scanned, timer.Elapsed, request.Condition, capped);
    }

    public void Clear()
    {
        _candidates.Clear();
        HasSession = false;
        DataType = null;
        Target = null;
    }

    private static bool Matches(ScanRequest request, ScanValue previous, ScanValue current)
    {
        return request.Condition switch
        {
            ScanCondition.ExactValue => current.NumericallyEquals(request.ExactValue!.Value),
            ScanCondition.Increased => HasRelativeOrder(current, previous, isIncrease: true),
            ScanCondition.Decreased => HasRelativeOrder(current, previous, isIncrease: false),
            ScanCondition.Changed => !current.NumericallyEquals(previous),
            ScanCondition.Unchanged => current.NumericallyEquals(previous),
            _ => false
        };
    }

    private static bool HasRelativeOrder(ScanValue current, ScanValue previous, bool isIncrease) =>
        ValueCodec.TryCompare(current, previous, out var comparison) &&
        (isIncrease ? comparison > 0 : comparison < 0);
}
