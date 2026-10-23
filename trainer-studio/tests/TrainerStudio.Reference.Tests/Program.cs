using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrainerStudio.Core;

var tests = new (string Name, Action Run)[]
{
    ("Heap addresses are explicitly session-only", HeapSessionReference),
    ("Module offsets stay inside their actual module", ModuleReferences),
    ("Pointer paths re-resolve after module and heap rebase", PointerRebase),
    ("32-bit pointers retain their width", Pointer32),
    ("Reverse pointer search finds bounded module roots across chunks", PointerSearch),
    ("Pointer search reports work and snapshot caps", PointerLimits),
    ("Pointer search cancellation returns no partial result", PointerCancellation),
    ("Moved pointer objects stop freezes without stale restoration", MovedPointerStopsFreeze),
    ("One-shot pointer baselines expire when a path moves", MovedPointerOneShot),
    ("A new process session cannot restore an old baseline", BaselinesAreSessionBound),
    ("Every trainer write requires successful readback", WriteReadback),
    ("Confirmation cannot follow a pointer to a different object", ConfirmationAddressGuard),
    ("Overlapping freezes cannot fight each other", OverlappingFreezes),
    ("Old profiles migrate without misreading new reference kinds", ProfileMigration),
    ("Build changes require explicit reference repair", BuildReview),
    ("DLL changes require review even when the EXE is unchanged", ModuleBuildReview),
    ("Missing module fingerprint requires independent reference repair", MissingModuleFingerprint),
    ("Profiles cannot mix target identities", MixedProfileTargets)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL: {name}: {exception}"); }
}
Console.WriteLine(failures == 0 ? $"PASS: {tests.Length} stable-reference tests" : $"FAIL: {failures} stable-reference tests");
return failures == 0 ? 0 : 1;

static void HeapSessionReference()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.Create(Hit(target), target, "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    Equal(MemoryReferenceKind.SessionAddress, feature.Reference.Kind);
    Equal(target.FinalAddress, feature.Reference.Resolve(target));
    var restarted = new ByteFixture(sessionId: "new-session");
    Throws<InvalidOperationException>(() => feature.Reference.Resolve(restarted));
    Equal(ProfileValidationState.NeedsRepair, ProfileValidator.Validate(TrainerProfile.Create("profile", target.Identity).WithFeature(feature), restarted).State);
}

static void ModuleReferences()
{
    var target = new ByteFixture();
    target.Set(target.ModuleBase + 0x40, ScanDataType.Int32, "50");
    var feature = TrainerBuilder.Create(new ScanHit(target.ModuleBase + 0x40, ScanValue.Parse(ScanDataType.Int32, "50"), null, "module"), target,
        "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "99");
    Equal(MemoryReferenceKind.ModuleRelative, feature.Reference.Kind);
    Equal(0x40L, feature.Reference.Offset);
    var invalid = feature.Reference with { Offset = 4096 };
    Throws<InvalidOperationException>(() => invalid.Resolve(target));
}

static void PointerRebase()
{
    var first = new ByteFixture();
    var reference = Pointer(first);
    Equal(first.FinalAddress, reference.Resolve(first));
    var restarted = new ByteFixture(moduleBase: 0x5000, heapBase: 0xB000, secondHeapBase: 0xD000, sessionId: "new-session");
    Equal(restarted.FinalAddress, reference.Resolve(restarted));
    Equal(1, new PointerScanner().Rescan(restarted, [reference], restarted.FinalAddress).Count);
    Equal(0, new PointerScanner().Rescan(restarted, [reference], restarted.FinalAddress + 4).Count);
}

static void Pointer32()
{
    var target = new ByteFixture(pointerSize: 4);
    Equal(target.FinalAddress, Pointer(target).Resolve(target));
    Throws<InvalidOperationException>(() => (Pointer(target) with { PointerSize = 8 }).Resolve(target));
    Throws<ArgumentException>(() => new PointerScanner().Scan(target, target.FinalAddress, ScanDataType.Int32, new PointerScanOptions(PointerSize: 8)));
}

static void PointerSearch()
{
    var target = new ByteFixture { ChunkBytes = 3 };
    var result = new PointerScanner().Scan(target, target.FinalAddress, ScanDataType.Int32, new PointerScanOptions(MaxDepth: 2, MaxOffset: 32));
    True(result.References.Any(reference => reference.Offset == 0x20 && reference.PointerOffsets!.SequenceEqual(new long[] { 16, 32 })));
    True(result.References.All(reference => reference.Kind == MemoryReferenceKind.PointerPath && reference.Resolve(target) == target.FinalAddress));
    False(result.WasCapped);
    target.SetPointer(target.ModuleBase + 0x20, 0);
    Equal(0, new PointerScanner().Rescan(target, result.References, target.FinalAddress).Count);
}

static void PointerLimits()
{
    var target = new ByteFixture { WasSnapshotTruncated = true };
    True(new PointerScanner().Scan(target, target.FinalAddress, ScanDataType.Int32).WasCapped);
    target.WasSnapshotTruncated = false;
    var limited = new PointerScanner().Scan(target, target.FinalAddress, ScanDataType.Int32, new PointerScanOptions(MaxScannedPointers: 1));
    True(limited.WasCapped);
    Equal(1L, limited.ScannedPointerCount);
}

static void PointerCancellation()
{
    var target = new ByteFixture();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => new PointerScanner().Scan(target, target.FinalAddress, ScanDataType.Int32, cancellationToken: cancellation.Token));
}

static void MovedPointerStopsFreeze()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Freeze", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "999");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, feature, true).Succeeded);
    target.Set(target.SecondHeapBase + 0x40, ScanDataType.Int32, "77");
    target.SetPointer(target.HeapBase + 0x10, target.SecondHeapBase + 0x20);
    False(runtime.Tick(target).Single().Succeeded);
    False(runtime.IsFreezeActive(feature));
    Equal("77", Read(target, target.SecondHeapBase + 0x40));
    var restore = feature with { Id = Guid.NewGuid(), Operation = TrainerOperation.RestoreOriginal, Control = TrainerControlKind.Button, Value = "" };
    False(runtime.Activate(target, restore, true).Succeeded);
    Equal("77", Read(target, target.SecondHeapBase + 0x40));
}

static void BaselinesAreSessionBound()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Set", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, feature, true).Succeeded);
    var restored = feature with { Id = Guid.NewGuid(), Operation = TrainerOperation.RestoreOriginal, Value = "" };
    var restarted = new ByteFixture(sessionId: "restart");
    False(runtime.Activate(restarted, restored, true).Succeeded);
    Equal("100", Read(restarted, restarted.FinalAddress));
    target.IsAlive = false;
    False(runtime.Activate(target, feature, true).Succeeded);
}

static void MovedPointerOneShot()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Set", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, feature, true).Succeeded);
    var restore = feature with { Id = Guid.NewGuid(), Operation = TrainerOperation.RestoreOriginal, Value = "" };
    target.Set(target.SecondHeapBase + 0x40, ScanDataType.Int32, "77");
    target.SetPointer(target.HeapBase + 0x10, target.SecondHeapBase + 0x20);
    False(runtime.Activate(target, restore, true).Succeeded);
    target.SetPointer(target.HeapBase + 0x10, target.SecondHeapBase);
    False(runtime.Activate(target, restore, true).Succeeded);
    Equal("999", Read(target, target.FinalAddress));
}

static void WriteReadback()
{
    var target = new ByteFixture { IgnoreWrites = true };
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Freeze", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "999");
    var runtime = new TrainerRuntime();
    var result = runtime.Activate(target, feature, true);
    False(result.Succeeded);
    False(runtime.IsFreezeActive(feature));
    Equal("100", result.After!.Value.Display);
}

static void ConfirmationAddressGuard()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Set", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var confirmedAddress = feature.Reference.Resolve(target);
    target.Set(target.SecondHeapBase + 0x40, ScanDataType.Int32, "77");
    target.SetPointer(target.HeapBase + 0x10, target.SecondHeapBase + 0x20);
    var runtime = new TrainerRuntime();
    False(runtime.Activate(target, feature, true, expectedResolvedAddress: confirmedAddress).Succeeded);
    Equal(0, target.WriteCount);
    Equal("100", Read(target, target.FinalAddress));
    Equal("77", Read(target, target.SecondHeapBase + 0x40));
    var restore = feature with { Operation = TrainerOperation.RestoreOriginal, Value = "" };
    False(runtime.Activate(target, restore, true).Succeeded);
    Equal(0, target.WriteCount);
}

static void OverlappingFreezes()
{
    var target = new ByteFixture();
    var first = TrainerBuilder.CreateFromReference(Pointer(target), target, "First", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "999");
    var second = first with { Id = Guid.NewGuid(), Name = "Second", Value = "500" };
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, first, true).Succeeded);
    True(runtime.Activate(target, second, true).Succeeded);
    False(runtime.IsFreezeActive(first));
    True(runtime.IsFreezeActive(second));
    Equal("500", Read(target, target.FinalAddress));
    True(runtime.Deactivate(target, second, true).Succeeded);
    Equal("100", Read(target, target.FinalAddress));
}

static void ProfileMigration()
{
    var target = new ByteFixture();
    var reference = new MemoryReference(target.Identity.TargetId, "fixture.exe", 0x40, ScanDataType.Int32, "0");
    var feature = new TrainerFeature(Guid.NewGuid(), "Old", "", "General", TrainerControlKind.Button, TrainerOperation.SetValue, reference, "999", null, null, null, null);
    var profile = TrainerProfile.Create("old", target.Identity).WithFeature(feature) with { SchemaVersion = 2 };
    var root = Path.Combine(Path.GetTempPath(), "trainer-reference-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var file = Path.Combine(root, "old.trainerprofile.json");
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        File.WriteAllText(file, JsonSerializer.Serialize(profile, options));
        var store = new ProfileStore(root);
        var loaded = store.Load(file);
        Equal(ProfileStore.SupportedSchemaVersion, loaded.SchemaVersion);
        Equal(MemoryReferenceKind.ModuleRelative, loaded.Features.Single().Reference.Kind);
        Equal(target.Identity.BuildFingerprint, loaded.Features.Single().Reference.ExpectedBuildFingerprint!);
        var pointerProfile = TrainerProfile.Create("pointer", target.Identity).WithFeature(TrainerBuilder.CreateFromReference(Pointer(target), target,
            "Pointer", TrainerOperation.SetValue, TrainerControlKind.Button, "999"));
        store.Save(pointerProfile);
        Equal(MemoryReferenceKind.PointerPath, store.Load(store.GetPath(pointerProfile.Id)).Features.Single().Reference.Kind);
        True(ProfileStore.SupportedSchemaVersion > 2);
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void BuildReview()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var newer = new ByteFixture(version: "2", fingerprint: "changed");
    var profile = TrainerProfile.Create("profile", target.Identity).WithFeature(feature);
    Equal(ProfileValidationState.NeedsReview, ProfileValidator.Validate(profile, newer).State);
    False(new TrainerRuntime().Activate(newer, feature, true).Succeeded);
    Equal("100", Read(newer, newer.FinalAddress));
}

static void ModuleBuildReview()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    Equal("module-build", feature.Reference.ExpectedModuleFingerprint!);
    var changedDll = new ByteFixture { ModuleFingerprint = "changed-module-build" };
    Equal(target.Identity.BuildFingerprint, changedDll.Identity.BuildFingerprint);
    var profile = TrainerProfile.Create("profile", target.Identity).WithFeature(feature);
    Equal(ProfileValidationState.NeedsReview, ProfileValidator.Validate(profile, changedDll).State);
    False(new TrainerRuntime().Activate(changedDll, feature, true).Succeeded);
    Equal("100", Read(changedDll, changedDll.FinalAddress));
}

static void MissingModuleFingerprint()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var missing = feature with { Reference = feature.Reference with { ExpectedModuleFingerprint = null } };
    var profile = TrainerProfile.Create("profile", target.Identity).WithFeature(missing);
    Equal(ProfileValidationState.NeedsReview, ProfileValidator.Validate(profile, target).State);
    Equal(target.FinalAddress, missing.Reference.Resolve(target, enforceBuild: false));
    var runtime = new TrainerRuntime();
    False(runtime.Activate(target, missing, true).Succeeded);
    Equal(0, target.WriteCount);
    Equal("100", Read(target, target.FinalAddress));
    var repaired = TrainerBuilder.CreateFromReference(missing.Reference, target, "Repaired", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    True(runtime.Activate(target, repaired, true).Succeeded);
    Equal(1, target.WriteCount);
}

static void MixedProfileTargets()
{
    var target = new ByteFixture();
    var feature = TrainerBuilder.CreateFromReference(Pointer(target), target, "Field", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var mixed = feature with { Reference = feature.Reference with { TargetId = "another-target" } };
    var profile = TrainerProfile.Create("profile", target.Identity).WithFeature(mixed);
    var root = Path.Combine(Path.GetTempPath(), "trainer-mixed-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new ProfileStore(Path.Combine(root, "library"));
        Throws<InvalidOperationException>(() => store.Save(profile));
        var path = Path.Combine(root, "mixed.trainerprofile.json");
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
        Throws<InvalidDataException>(() => store.Import(path));
        Equal(0, store.ListPaths().Count);
    }
    finally { Directory.Delete(root, recursive: true); }
}

static MemoryReference Pointer(ByteFixture target) => new(target.Identity.TargetId, "fixture.exe", 0x20, ScanDataType.Int32, "100",
    Kind: MemoryReferenceKind.PointerPath, PointerOffsets: new long[] { 16, 32 }, PointerSize: target.PointerSize,
    ExpectedBuildFingerprint: target.Identity.BuildFingerprint, ExpectedVersion: target.Identity.Version, ExpectedModuleFingerprint: target.ModuleFingerprint);

static ScanHit Hit(ByteFixture target) => new(target.FinalAddress, ScanValue.Parse(ScanDataType.Int32, "100"), null, "heap");
static string Read(ByteFixture target, long address) { True(target.TryRead(address, ScanDataType.Int32, out var value)); return value.Display; }
static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true."); }
static void False(bool condition) => True(!condition);
static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

/// <summary>Only byte buffers for automated reference tests; no game loop/process.</summary>
internal sealed class ByteFixture : IApprovedMemoryTarget, IModuleMemoryTarget, IModuleFingerprintTarget, ISessionMemoryTarget, IStreamingMemoryTarget, ISnapshotDiagnosticsTarget
{
    private readonly MemoryRegion[] _regions;
    public ByteFixture(long moduleBase = 0x1000, long heapBase = 0x8000, long secondHeapBase = 0x9000,
        string sessionId = "first", int pointerSize = 8, string version = "1", string fingerprint = "build")
    {
        ModuleBase = moduleBase; HeapBase = heapBase; SecondHeapBase = secondHeapBase; SessionId = sessionId; PointerSize = pointerSize;
        Identity = new TargetIdentity("byte-fixture", "Byte arrays", version, pointerSize == 4 ? "x86" : "x64", "fixture.exe", fingerprint);
        Modules = [new TargetModule("fixture.exe", moduleBase, 256)];
        _regions = [new MemoryRegion(moduleBase, new byte[256], "module"), new MemoryRegion(heapBase, new byte[256], "heap1"), new MemoryRegion(secondHeapBase, new byte[256], "heap2")];
        SetPointer(moduleBase + 0x20, heapBase);
        SetPointer(heapBase + 0x10, secondHeapBase);
        Set(FinalAddress, ScanDataType.Int32, "100");
    }

    public long ModuleBase { get; }
    public long HeapBase { get; }
    public long SecondHeapBase { get; }
    public long FinalAddress => SecondHeapBase + 0x20;
    public int PointerSize { get; }
    public TargetIdentity Identity { get; }
    public IReadOnlyList<TargetModule> Modules { get; }
    public string SessionId { get; }
    public bool IsAlive { get; set; } = true;
    public bool IgnoreWrites { get; set; }
    public int WriteCount { get; private set; }
    public string ModuleFingerprint { get; set; } = "module-build";
    public int ChunkBytes { get; set; } = 256;
    public bool WasSnapshotTruncated { get; set; }
    public long SnapshotBytesRead => 768;
    public int SkippedReadCount => 0;
    public bool TryResolveModuleBase(string name, out long address) { address = ModuleBase; return name == "fixture.exe"; }
    public bool TryGetModuleFingerprint(string name, out string fingerprint) { fingerprint = ModuleFingerprint; return name == "fixture.exe"; }
    public IReadOnlyList<MemoryRegion> Snapshot() => _regions.Select(region => region with { Bytes = region.Bytes.ToArray() }).ToArray();
    public IEnumerable<MemoryRegion> EnumerateMemory(CancellationToken cancellationToken)
    {
        foreach (var region in Snapshot())
        {
            for (var offset = 0; offset < region.Bytes.Length; offset += ChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MemoryRegion(region.BaseAddress + offset, region.Bytes.AsSpan(offset, Math.Min(ChunkBytes, region.Bytes.Length - offset)).ToArray(), region.Name);
            }
        }
    }
    public bool TryRead(long address, ScanDataType type, out ScanValue value)
    {
        value = default;
        var region = Find(address, ValueCodec.SizeOf(type));
        return IsAlive && region is not null && ValueCodec.TryRead(region.Bytes.AsSpan((int)(address - region.BaseAddress)), type, out value);
    }
    public bool TryWrite(long address, ScanValue value, out string error)
    {
        error = "Fixture address is not readable.";
        var region = Find(address, value.Size);
        if (!IsAlive || region is null) return false;
        WriteCount++;
        if (!IgnoreWrites) value.Bytes.CopyTo(region.Bytes, (int)(address - region.BaseAddress));
        error = ""; return true;
    }
    public void Set(long address, ScanDataType type, string value)
    {
        var parsed = ScanValue.Parse(type, value);
        parsed.Bytes.CopyTo(Find(address, parsed.Size)!.Bytes, (int)(address - Find(address, parsed.Size)!.BaseAddress));
    }
    public void SetPointer(long address, long value) => Set(address, PointerSize == 4 ? ScanDataType.UInt32 : ScanDataType.UInt64, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private MemoryRegion? Find(long address, int size) => _regions.FirstOrDefault(region => address >= region.BaseAddress && address <= region.EndAddress - size);
}
