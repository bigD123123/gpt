using System.Buffers.Binary;
using TrainerStudio.Core;

var tests = new (string Name, Action Run)[]
{
    ("Streaming finds values split over small chunks", ChunkBoundaries),
    ("Native alignment follows absolute addresses", NativeAlignment),
    ("Gaps and different regions never invent boundary values", BoundaryIsolation),
    ("First scan cancellation preserves the previous session", CancelledFirstScan),
    ("Next scan cancellation preserves the previous candidates", CancelledNextScan),
    ("Read failure during streaming preserves the previous session", FailedFirstScan),
    ("Different targets with the same executable identity are rejected", TargetInstanceChanged),
    ("Changed or terminated process sessions are rejected", SessionChanged),
    ("Candidate truncation remains visible after narrowing", CandidateCap),
    ("Snapshot limits and skipped reads remain visible", SnapshotDiagnostics),
    ("Floating-point exact matching preserves signed zero semantics", FloatingZero),
    ("Progress cancellation cannot commit partial results", ProgressCancellation)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {name}: {exception}");
    }
}

Console.WriteLine(failures == 0 ? $"PASS: {tests.Length} streaming scanner tests" : $"FAIL: {failures} streaming scanner tests");
return failures == 0 ? 0 : 1;

static ScanRequest Exact(int value, ScanAlignment alignment = ScanAlignment.Native, int max = 100_000) =>
    new(ScanDataType.Int32, ScanCondition.ExactValue, ScanValue.Parse(ScanDataType.Int32, value.ToString(System.Globalization.CultureInfo.InvariantCulture)), max, alignment);

static ByteTarget Ints(params int[] values)
{
    var bytes = new byte[values.Length * 4];
    for (var index = 0; index < values.Length; index++)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 4), values[index]);
    }

    return new ByteTarget([new MemoryRegion(0x1000, bytes, "buffer")]);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void ChunkBoundaries()
{
    var target = new ByteTarget([
        new(0x1000, [0, 0x78], "buffer"),
        new(0x1002, [0x56, 0x34], "buffer"),
        new(0x1004, [0x12, 0, 0], "buffer")]);
    var scanner = new MemoryScanner();
    var summary = scanner.FirstScan(target, Exact(0x12345678, ScanAlignment.EveryByte));
    Require(summary.CandidateCount == 1 && scanner.Candidates[0].Address == 0x1001, "The split unaligned value was lost.");
    Require(summary.ScannedValueCount == 4, "A boundary position was duplicated or skipped.");
    Require(scanner.NextScan(target, Exact(0x12345678, ScanAlignment.EveryByte)).CandidateCount == 1, "Next scan did not preserve the boundary value.");
}

static void NativeAlignment()
{
    var target = new ByteTarget([
        new(0x1001, [0, 0], "buffer"),
        new(0x1003, [0, 42, 0], "buffer"),
        new(0x1006, [0, 0, 0], "buffer")]);
    var scanner = new MemoryScanner();
    var summary = scanner.FirstScan(target, Exact(42));
    Require(summary.CandidateCount == 1 && scanner.Candidates[0].Address == 0x1004, "Alignment was relative to a chunk instead of the absolute address.");
    Require(summary.ScannedValueCount == 1, "Native scanning inspected an unaligned address.");
}

static void BoundaryIsolation()
{
    var scanner = new MemoryScanner();
    var target = new ByteTarget([new(0x1000, [0x78, 0x56], "buffer"), new(0x1003, [0x34, 0x12], "buffer")]);
    Require(scanner.FirstScan(target, Exact(0x12345678, ScanAlignment.EveryByte)).CandidateCount == 0, "A gap was bridged.");
    target.Regions = [new(0x1000, [0x78, 0x56], "first"), new(0x1002, [0x34, 0x12], "second")];
    Require(scanner.FirstScan(target, Exact(0x12345678, ScanAlignment.EveryByte)).CandidateCount == 0, "Distinct readable regions were bridged.");
}

static void CancelledFirstScan()
{
    var target = Ints(50, 75);
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Exact(50));
    using var cancel = new CancellationTokenSource();
    target.BeforeYield = _ => cancel.Cancel();
    Throws<OperationCanceledException>(() => scanner.FirstScan(target, Exact(75), cancel.Token));
    Require(scanner.HasSession && scanner.Candidates.Count == 1 && scanner.Candidates[0].CurrentValue.Display == "50", "Cancelled first scan replaced the prior candidates.");
}

static void CancelledNextScan()
{
    var target = Ints(50, 50);
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Exact(50));
    BinaryPrimitives.WriteInt32LittleEndian(target.Regions[0].Bytes, 75);
    using var cancel = new CancellationTokenSource();
    target.OnRead = () => cancel.Cancel();
    Throws<OperationCanceledException>(() => scanner.NextScan(target, Exact(75), cancel.Token));
    Require(scanner.Candidates.Count == 2 && scanner.Candidates.All(hit => hit.CurrentValue.Display == "50"), "Cancelled next scan replaced the prior values.");
}

static void FailedFirstScan()
{
    var target = Ints(50, 75);
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Exact(50));
    target.BeforeYield = _ => throw new IOException("Simulated read failure.");
    Throws<IOException>(() => scanner.FirstScan(target, Exact(75)));
    Require(scanner.Candidates.Count == 1 && scanner.Candidates[0].CurrentValue.Display == "50", "Read failure replaced the prior scan.");
}

static void TargetInstanceChanged()
{
    var scanner = new MemoryScanner();
    scanner.FirstScan(Ints(50), Exact(50));
    Throws<InvalidOperationException>(() => scanner.NextScan(Ints(50), Exact(50)));
}

static void SessionChanged()
{
    var target = Ints(50);
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Exact(50));
    target.SessionId = "new-process-lifetime";
    Throws<InvalidOperationException>(() => scanner.NextScan(target, Exact(50)));
    target.BeforeYield = _ => target.IsAlive = false;
    Throws<InvalidOperationException>(() => scanner.FirstScan(target, Exact(50)));
    Require(scanner.Candidates.Count == 1, "Terminated target committed an incomplete first scan.");
}

static void CandidateCap()
{
    var target = Ints(50, 50, 50);
    var scanner = new MemoryScanner();
    var first = scanner.FirstScan(target, Exact(50, max: 2));
    Require(first.CandidateCount == 2 && first.WasCapped, "Candidate limit was not reported.");
    BinaryPrimitives.WriteInt32LittleEndian(target.Regions[0].Bytes, 75);
    var next = scanner.NextScan(target, Exact(75));
    Require(next.CandidateCount == 1 && next.WasCapped, "Narrowing hid the incomplete original candidate scope.");
    var complete = scanner.FirstScan(Ints(50, 50), Exact(50, max: 2));
    Require(!complete.WasCapped, "A completely scanned set exactly equal to the limit was marked incomplete.");
}

static void SnapshotDiagnostics()
{
    var target = Ints(50, 75);
    target.WasSnapshotTruncated = true;
    target.SkippedReadCount = 3;
    var scanner = new MemoryScanner();
    var summary = scanner.FirstScan(target, Exact(50));
    Require(summary.WasSnapshotTruncated && summary.SkippedReadCount == 3, "Snapshot diagnostics were dropped.");
    summary = scanner.NextScan(target, Exact(50));
    Require(summary.WasSnapshotTruncated && summary.SkippedReadCount == 3, "Next scan hid earlier snapshot truncation.");
}

static void FloatingZero()
{
    var bytes = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, int.MinValue);
    var scanner = new MemoryScanner();
    var target = new ByteTarget([new(0x1000, bytes, "float-buffer")]);
    var summary = scanner.FirstScan(target, new(ScanDataType.Float, ScanCondition.ExactValue, ScanValue.Parse(ScanDataType.Float, "0")));
    Require(summary.CandidateCount == 2, "Float -0 should equal +0 without requiring bit equality.");
}

static void ProgressCancellation()
{
    var target = Ints(50);
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Exact(50));
    using var cancel = new CancellationTokenSource();
    var progress = new InlineProgress(value =>
    {
        Require(value.ScannedValueCount > 0 && value.CandidateCount == 0, "Progress did not reflect the pending scan.");
        cancel.Cancel();
    });
    Throws<OperationCanceledException>(() => scanner.FirstScan(target, Exact(75), cancel.Token, progress));
    Require(scanner.Candidates.Count == 1 && scanner.Candidates[0].CurrentValue.Display == "50", "Progress callback cancellation committed results.");
}

sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => report(value);
}

sealed class ByteTarget(IReadOnlyList<MemoryRegion> regions) : IApprovedMemoryTarget, IStreamingMemoryTarget, ISessionMemoryTarget, ISnapshotDiagnosticsTarget
{
    public TargetIdentity Identity { get; } = new("scanner-fixture", "Raw byte fixture", "1", "x64", "fixture", "fixture:1");
    public IReadOnlyList<MemoryRegion> Regions { get; set; } = regions;
    public string SessionId { get; set; } = "first-process-lifetime";
    public bool IsAlive { get; set; } = true;
    public bool WasSnapshotTruncated { get; set; }
    public long SnapshotBytesRead => Regions.Sum(region => region.Bytes.LongLength);
    public int SkippedReadCount { get; set; }
    public Action<int>? BeforeYield { get; set; }
    public Action? OnRead { get; set; }

    public bool TryResolveModuleBase(string moduleName, out long baseAddress)
    {
        baseAddress = 0x1000;
        return moduleName == "fixture";
    }

    public IReadOnlyList<MemoryRegion> Snapshot() => throw new InvalidOperationException("Streaming must not call the whole-snapshot path.");

    public IEnumerable<MemoryRegion> EnumerateMemory(CancellationToken cancellationToken)
    {
        for (var index = 0; index < Regions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeYield?.Invoke(index);
            yield return Regions[index];
        }
    }

    public bool TryRead(long address, ScanDataType type, out ScanValue value)
    {
        OnRead?.Invoke();
        var size = ValueCodec.SizeOf(type);
        Span<byte> bytes = stackalloc byte[8];
        string? name = null;
        for (var offset = 0; offset < size; offset++)
        {
            var location = checked(address + offset);
            var region = Regions.FirstOrDefault(region => location >= region.BaseAddress && location < region.EndAddress);
            if (region is null || (name is not null && name != region.Name))
            {
                value = default;
                return false;
            }

            name = region.Name;
            bytes[offset] = region.Bytes[checked((int)(location - region.BaseAddress))];
        }

        return ValueCodec.TryRead(bytes[..size], type, out value);
    }

    public bool TryWrite(long address, ScanValue value, out string error)
    {
        error = "Raw scanner fixtures are read-only.";
        return false;
    }
}
