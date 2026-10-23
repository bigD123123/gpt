using TrainerStudio.Core;

var tests = new (string Name, Action Run)[]
{
    ("Exact first scan and narrowed next scan", ExactFirstScanAndNarrowedNextScan),
    ("Unknown, increased, decreased, changed and unchanged filters", RelativeScanFilters),
    ("Supported value codecs round-trip", ValueCodecsRoundTrip),
    ("Trainer builder persists a validated profile", TrainerProfileRoundTrip),
    ("Profile library exports, imports, duplicates and retains backups", ProfileLibraryRoundTrip),
    ("Profile import rejects invalid or oversized files", ProfileImportRejectsUnsafeFiles),
    ("Freeze writes only after approval and restores original", FreezeAndRestore),
    ("Declined freeze deactivation leaves it active", DeclinedFreezeDeactivation),
    ("Stop all ends freezes without another write", StopAllEndsFreezes),
    ("Separate restore feature uses the session baseline", SeparateRestoreFeature),
    ("Module-relative profiles survive an owned-target restart", RebasedRestartProfile),
    ("Profile version mismatch requires review", ProfileVersionMismatch),
    ("Invalid target requires profile repair", InvalidTargetRequiresRepair),
    ("Slider definitions and typed arithmetic are validated", SliderAndTypedArithmetic),
    ("Audit log appends visible outcomes", AuditLogRoundTrip),
    ("Ten repeat scan-and-trainer runs", TenRepeatRuns)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine($"PASS: {tests.Length} Trainer Studio core tests");
return 0;

static void ExactFirstScanAndNarrowedNextScan()
{
    var target = new OwnedTestTarget();
    var scanner = new MemoryScanner();
    var first = scanner.FirstScan(target, Request(ScanCondition.ExactValue, "50"));
    Equal(3, first.CandidateCount);
    target.CollectCurrency();
    var next = scanner.NextScan(target, Request(ScanCondition.ExactValue, "75"));
    Equal(2, next.CandidateCount);
    target.CollectCurrency();
    var final = scanner.NextScan(target, Request(ScanCondition.ExactValue, "100"));
    Equal(1, final.CandidateCount);
    Equal(target.CurrencyAddress, scanner.Candidates.Single().Address);
}

static void RelativeScanFilters()
{
    var target = new OwnedTestTarget();
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Request(ScanCondition.UnknownInitialValue));
    Equal(OwnedTestTarget.CellCount, scanner.Candidates.Count);
    target.CollectCurrency();
    var increased = scanner.NextScan(target, Request(ScanCondition.Increased));
    True(increased.CandidateCount >= 2, "Expected the resource and first distractor to increase.");

    scanner.Clear();
    scanner.FirstScan(target, Request(ScanCondition.UnknownInitialValue));
    target.SpendCurrency();
    var decreased = scanner.NextScan(target, Request(ScanCondition.Decreased));
    True(decreased.CandidateCount >= 1, "Expected at least the resource to decrease.");

    scanner.Clear();
    scanner.FirstScan(target, Request(ScanCondition.UnknownInitialValue));
    target.TickTimer();
    var changed = scanner.NextScan(target, Request(ScanCondition.Changed));
    Equal(1, changed.CandidateCount);
    Equal(target.TimerAddress, scanner.Candidates.Single().Address);

    scanner.Clear();
    scanner.FirstScan(target, Request(ScanCondition.UnknownInitialValue));
    target.TickTimer();
    var unchanged = scanner.NextScan(target, Request(ScanCondition.Unchanged));
    Equal(OwnedTestTarget.CellCount - 1, unchanged.CandidateCount);
}

static void ValueCodecsRoundTrip()
{
    var samples = new Dictionary<ScanDataType, string>
    {
        [ScanDataType.Int8] = "-12",
        [ScanDataType.UInt8] = "240",
        [ScanDataType.Int16] = "-12345",
        [ScanDataType.UInt16] = "65000",
        [ScanDataType.Int32] = "-1234567",
        [ScanDataType.UInt32] = "4000000000",
        [ScanDataType.Int64] = "-1234567890123",
        [ScanDataType.UInt64] = "18000000000000000000",
        [ScanDataType.Float] = "12.5",
        [ScanDataType.Double] = "123456.25",
        [ScanDataType.Boolean] = "true"
    };

    foreach (var (type, source) in samples)
    {
        var value = ScanValue.Parse(type, source);
        True(ValueCodec.TryRead(value.Bytes, type, out var reread), $"Could not read {type}.");
        True(value.NumericallyEquals(reread), $"Codec did not round-trip {type}.");
    }
}

static void TrainerProfileRoundTrip()
{
    var target = new OwnedTestTarget();
    var scanner = new MemoryScanner();
    scanner.FirstScan(target, Request(ScanCondition.ExactValue, "50"));
    target.CollectCurrency();
    scanner.NextScan(target, Request(ScanCondition.ExactValue, "75"));
    target.CollectCurrency();
    scanner.NextScan(target, Request(ScanCondition.ExactValue, "100"));
    var feature = TrainerBuilder.Create(scanner.Candidates.Single(), target, "Sæt mønter", TrainerOperation.SetValue, TrainerControlKind.NumberInput, "999", "Ressourcer", "Testværdi", "F1");
    var profile = TrainerProfile.Create("Min testtrainer", target.Identity, "Kun eget testspil").WithFeature(feature);
    var path = Path.Combine(Path.GetTempPath(), "trainer-studio-test-" + Guid.NewGuid().ToString("N"));
    var store = new ProfileStore(path);
    store.Save(profile);
    var reloaded = store.Load(store.GetPath(profile.Id));
    Equal(profile.Name, reloaded.Name);
    Equal(feature.Name, reloaded.Features.Single().Name);
    Equal(ProfileValidationState.Ready, ProfileValidator.Validate(reloaded, target).State);
}

static void ProfileLibraryRoundTrip()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.CurrencyAddress, ScanValue.Parse(ScanDataType.Int32, "50"), null, "OwnedTestGame.data");
    var feature = TrainerBuilder.Create(hit, target, "Sæt mønter", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var profile = TrainerProfile.Create("Bærbar profil", target.Identity, "Eksporttest").WithFeature(feature);
    var root = Path.Combine(Path.GetTempPath(), "trainer-studio-library-" + Guid.NewGuid().ToString("N"));
    var store = new ProfileStore(root);
    store.Save(profile);

    var exportPath = Path.Combine(root, "portable", "baerbar.trainerprofile.json");
    store.Export(profile, exportPath);
    True(File.Exists(exportPath), "Eksportfilen blev ikke oprettet.");
    Equal(profile.Id, store.Load(exportPath).Id);

    var revised = profile with { Notes = "Opdateret lokal kopi", UpdatedUtc = DateTimeOffset.UtcNow };
    store.Save(revised);
    Equal(1, store.ListBackupPaths(profile.Id).Count);

    for (var revision = 0; revision < ProfileStore.BackupRetentionCount + 3; revision++)
    {
        revised = revised with { Notes = $"Revision {revision}", UpdatedUtc = DateTimeOffset.UtcNow.AddTicks(revision + 1) };
        store.Save(revised);
    }

    Equal(ProfileStore.BackupRetentionCount, store.ListBackupPaths(profile.Id).Count);

    var imported = store.Import(exportPath);
    True(imported.Id != profile.Id, "Importen skal få et nyt profil-id.");
    True(imported.Features.Single().Id != feature.Id, "Importerede funktioner skal få nye ids.");
    True(File.Exists(store.GetPath(imported.Id)), "Den importerede kopi blev ikke gemt lokalt.");
    Equal(ProfileValidationState.Ready, ProfileValidator.Validate(imported, target).State);

    var duplicated = store.Duplicate(imported);
    True(duplicated.Id != imported.Id, "En duplikatprofil skal få et nyt profil-id.");
    True(duplicated.Name.StartsWith("Kopi af ", StringComparison.Ordinal));
    True(File.Exists(store.GetPath(duplicated.Id)), "Duplikatprofilen blev ikke gemt lokalt.");
}

static void ProfileImportRejectsUnsafeFiles()
{
    var root = Path.Combine(Path.GetTempPath(), "trainer-studio-invalid-import-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var store = new ProfileStore(Path.Combine(root, "library"));
    var badExtension = Path.Combine(root, "profile.txt");
    File.WriteAllText(badExtension, "{}");
    Throws<ArgumentException>(() => store.Import(badExtension));
    Equal(0, store.ListPaths().Count);

    var malformed = Path.Combine(root, "bad.trainerprofile.json");
    File.WriteAllText(malformed, "{ dette er ikke JSON }");
    Throws<InvalidDataException>(() => store.Import(malformed));
    Equal(0, store.ListPaths().Count);

    var unsupportedSchema = Path.Combine(root, "old.trainerprofile.json");
    File.WriteAllText(unsupportedSchema, "{\"SchemaVersion\":999}");
    Throws<InvalidDataException>(() => store.Import(unsupportedSchema));
    Equal(0, store.ListPaths().Count);

    var oversized = Path.Combine(root, "large.trainerprofile.json");
    File.WriteAllText(oversized, new string('x', checked((int)ProfileStore.MaximumImportBytes + 1)));
    Throws<InvalidDataException>(() => store.Import(oversized));
    Equal(0, store.ListPaths().Count);
}

static void FreezeAndRestore()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.HealthAddress, ScanValue.Parse(ScanDataType.Int32, "100"), null, "OwnedTestGame.data");
    var feature = TrainerBuilder.Create(hit, target, "Fasthold liv", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "100", "Spiller", "Gendanner testliv", "F2");
    var runtime = new TrainerRuntime();
    False(runtime.Activate(target, feature, userConfirmed: false).Succeeded);
    Equal(100, target.Health);
    True(runtime.Activate(target, feature, userConfirmed: true).Succeeded);
    target.TakeDamage();
    Equal(85, target.Health);
    True(runtime.Tick(target).Single().Succeeded);
    Equal(100, target.Health);
    True(runtime.Deactivate(target, feature, userConfirmed: true).Succeeded);
    Equal(100, target.Health);
}

static void DeclinedFreezeDeactivation()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.HealthAddress, ScanValue.Parse(ScanDataType.Int32, "100"), null, "OwnedTestGame.data");
    var feature = TrainerBuilder.Create(hit, target, "Fasthold liv", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "100");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, feature, userConfirmed: true).Succeeded);
    False(runtime.Deactivate(target, feature, userConfirmed: false).Succeeded);
    True(runtime.IsFreezeActive(feature), "Fastholdelse må ikke stoppe, når brugeren afviser dialogen.");
    target.TakeDamage();
    runtime.Tick(target);
    Equal(100, target.Health);
}

static void StopAllEndsFreezes()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.HealthAddress, ScanValue.Parse(ScanDataType.Int32, "100"), null, "OwnedTestGame.data");
    var feature = TrainerBuilder.Create(hit, target, "Fasthold liv", TrainerOperation.FreezeValue, TrainerControlKind.Toggle, "100");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, feature, userConfirmed: true).Succeeded);
    Equal(1, runtime.StopAll());
    target.TakeDamage();
    Equal(0, runtime.Tick(target).Count);
    Equal(85, target.Health);
}

static void SeparateRestoreFeature()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.CurrencyAddress, ScanValue.Parse(ScanDataType.Int32, "50"), null, "OwnedTestGame.data");
    var setFeature = TrainerBuilder.Create(hit, target, "Sæt mønter", TrainerOperation.SetValue, TrainerControlKind.Button, "999");
    var restoreFeature = TrainerBuilder.Create(hit, target, "Gendan mønter", TrainerOperation.RestoreOriginal, TrainerControlKind.Button, "");
    var runtime = new TrainerRuntime();
    True(runtime.Activate(target, setFeature, userConfirmed: true).Succeeded);
    Equal(999, target.Currency);
    True(runtime.Activate(target, restoreFeature, userConfirmed: true).Succeeded);
    Equal(50, target.Currency);
    False(runtime.Activate(target, restoreFeature, userConfirmed: true).Succeeded, "En baseline må kun kunne gendannes én gang.");
}

static void RebasedRestartProfile()
{
    var firstSession = new OwnedTestTarget(moduleBaseAddress: OwnedTestTarget.BaseAddress);
    var scanner = new MemoryScanner();
    scanner.FirstScan(firstSession, Request(ScanCondition.ExactValue, "50"));
    firstSession.CollectCurrency();
    scanner.NextScan(firstSession, Request(ScanCondition.ExactValue, "75"));
    firstSession.CollectCurrency();
    scanner.NextScan(firstSession, Request(ScanCondition.ExactValue, "100"));
    var feature = TrainerBuilder.Create(scanner.Candidates.Single(), firstSession, "Sæt mønter", TrainerOperation.SetValue, TrainerControlKind.NumberInput, "999");
    var profile = TrainerProfile.Create("Genstartsprofil", firstSession.Identity).WithFeature(feature);

    var restartedSession = new OwnedTestTarget(moduleBaseAddress: OwnedTestTarget.BaseAddress + 0x0020_0000);
    Equal(ProfileValidationState.Ready, ProfileValidator.Validate(profile, restartedSession).State);
    True(new TrainerRuntime().Activate(restartedSession, feature, userConfirmed: true).Succeeded);
    Equal(999, restartedSession.Currency);
}

static void ProfileVersionMismatch()
{
    var original = new OwnedTestTarget("1.0.0");
    var profile = TrainerProfile.Create("Version test", original.Identity);
    var updated = new OwnedTestTarget("2.0.0");
    Equal(ProfileValidationState.NeedsReview, ProfileValidator.Validate(profile, updated).State);
}

static void InvalidTargetRequiresRepair()
{
    var original = new OwnedTestTarget();
    var profile = TrainerProfile.Create("Måltest", original.Identity);
    var otherFingerprint = new OwnedTestTarget(buildFingerprint: "other-build");
    Equal(ProfileValidationState.NeedsReview, ProfileValidator.Validate(profile, otherFingerprint).State);

    var feature = new TrainerFeature(
        Guid.NewGuid(),
        "Brudt",
        "",
        "",
        TrainerControlKind.Button,
        TrainerOperation.SetValue,
        new MemoryReference("andet-mål", OwnedTestTarget.ModuleName, 0, ScanDataType.Int32, "0"),
        "1",
        null,
        null,
        null,
        null);
    var broken = profile.WithFeature(feature);
    Equal(ProfileValidationState.NeedsRepair, ProfileValidator.Validate(broken, original).State);
}

static void SliderAndTypedArithmetic()
{
    var target = new OwnedTestTarget();
    var hit = new ScanHit(target.CurrencyAddress, ScanValue.Parse(ScanDataType.Int32, "50"), null, "OwnedTestGame.data");
    var slider = TrainerBuilder.Create(
        hit,
        target,
        "Vælg mønter",
        TrainerOperation.SetValue,
        TrainerControlKind.Slider,
        "75",
        minimum: "0",
        maximum: "100",
        step: "5");
    True(new TrainerRuntime().Activate(target, slider, userConfirmed: true).Succeeded);
    Equal(75, target.Currency);
    Throws<ArgumentException>(() => TrainerBuilder.Create(
        hit,
        target,
        "Ugyldig slider",
        TrainerOperation.SetValue,
        TrainerControlKind.Slider,
        "101",
        minimum: "0",
        maximum: "100",
        step: "5"));
    Throws<ArgumentException>(() => TrainerBuilder.Create(
        hit,
        target,
        "Forkert trin",
        TrainerOperation.SetValue,
        TrainerControlKind.Slider,
        "77",
        minimum: "0",
        maximum: "100",
        step: "5"));

    var high = ScanValue.Parse(ScanDataType.UInt64, "18000000000000000000");
    var increment = ScanValue.Parse(ScanDataType.UInt64, "1");
    Equal("18000000000000000001", ValueCodec.Add(high, increment).Display);
    True(ValueCodec.TryCompare(ValueCodec.Add(high, increment), high, out var comparison) && comparison > 0);
    True(ValueCodec.IsAlignedToStep(ScanValue.Parse(ScanDataType.Float, "0.3"),
        ScanValue.Parse(ScanDataType.Float, "0"), ScanValue.Parse(ScanDataType.Float, "0.1")));
    True(!ValueCodec.IsAlignedToStep(ScanValue.Parse(ScanDataType.Float, "0.35"),
        ScanValue.Parse(ScanDataType.Float, "0"), ScanValue.Parse(ScanDataType.Float, "0.1")));
}

static void AuditLogRoundTrip()
{
    var path = Path.Combine(Path.GetTempPath(), "trainer-studio-audit-" + Guid.NewGuid().ToString("N"), "events.jsonl");
    var writer = new AuditLogWriter(path);
    writer.Append(new TrainerAuditEvent(
        DateTimeOffset.UtcNow,
        "trainer_action",
        true,
        OwnedTestTarget.TargetId,
        "feature-1",
        "Test handling",
        "50",
        "999"));
    var line = File.ReadAllText(path);
    True(line.Contains("trainer_action", StringComparison.Ordinal));
    True(line.Contains("\"Before\":\"50\"", StringComparison.Ordinal));
}

static void TenRepeatRuns()
{
    for (var run = 0; run < 10; run++)
    {
        var target = new OwnedTestTarget();
        var scanner = new MemoryScanner();
        scanner.FirstScan(target, Request(ScanCondition.ExactValue, "12"));
        target.Fire();
        scanner.NextScan(target, Request(ScanCondition.ExactValue, "11"));
        target.Fire();
        scanner.NextScan(target, Request(ScanCondition.ExactValue, "10"));
        Equal(target.AmmoAddress, scanner.Candidates.Single().Address);
    }
}

static ScanRequest Request(ScanCondition condition, string? value = null) => new(
    ScanDataType.Int32,
    condition,
    value is null ? null : ScanValue.Parse(ScanDataType.Int32, value));

static void True(bool condition, string message = "Expected true.")
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void False(bool condition, string message = "Expected false.") => True(!condition, message);

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
