using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;

namespace TrainerStudio.Core;

public enum TrainerControlKind
{
    Button,
    Toggle,
    Slider,
    NumberInput
}

public enum TrainerOperation
{
    SetValue,
    AddValue,
    FreezeValue,
    RestoreOriginal
}

public enum ProfileValidationState
{
    Ready,
    NeedsReview,
    NeedsRepair,
    NotAttached
}

public enum MemoryReferenceKind
{
    ModuleRelative,
    PointerPath,
    SessionAddress
}

/// <summary>Optional identity for a loaded root module, including DLL roots.</summary>
public interface IModuleFingerprintTarget
{
    bool TryGetModuleFingerprint(string moduleName, out string fingerprint);
}

public sealed record MemoryReference(
    string TargetId,
    string ModuleName,
    long Offset,
    ScanDataType DataType,
    string ExpectedValueHint,
    MemoryReferenceKind Kind = MemoryReferenceKind.ModuleRelative,
    IReadOnlyList<long>? PointerOffsets = null,
    int PointerSize = 0,
    string? SessionId = null,
    long? SessionAddress = null,
    string? ExpectedBuildFingerprint = null,
    string? ExpectedVersion = null,
    string? ExpectedModuleFingerprint = null)
{
    public bool IsPersistent => Kind != MemoryReferenceKind.SessionAddress;

    public string Description => Kind switch
    {
        MemoryReferenceKind.SessionAddress => $"Kun denne processtart · 0x{SessionAddress:X}",
        MemoryReferenceKind.PointerPath => $"{ModuleName}+0x{Offset:X} → {string.Join(" → ", PointerOffsets ?? [])}",
        _ => $"{ModuleName}+0x{Offset:X}"
    };

    public void ValidateDefinition()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(DataType) || string.IsNullOrWhiteSpace(TargetId))
        {
            throw new ArgumentException("Hukommelsesreferencens type eller mål er ugyldigt.");
        }

        if ((ExpectedBuildFingerprint is not null && string.IsNullOrWhiteSpace(ExpectedBuildFingerprint)) ||
            (ExpectedVersion is not null && string.IsNullOrWhiteSpace(ExpectedVersion)) ||
            (ExpectedModuleFingerprint is not null && string.IsNullOrWhiteSpace(ExpectedModuleFingerprint)))
        {
            throw new ArgumentException("Versions- og buildoplysninger må ikke være tomme strenge.");
        }

        if (Kind == MemoryReferenceKind.SessionAddress)
        {
            if (string.IsNullOrWhiteSpace(SessionId) || SessionAddress is null or <= 0)
            {
                throw new ArgumentException("En sessionsadresse kræver processtart-id og en gyldig adresse.");
            }

            if (PointerOffsets is { Count: > 0 } || PointerSize != 0)
            {
                throw new ArgumentException("En sessionsadresse må ikke indeholde en pointersti.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(ModuleName) || Offset < 0)
        {
            throw new ArgumentException("En modulreference kræver et modulnavn og et positivt relativt offset.");
        }

        if (Kind == MemoryReferenceKind.PointerPath)
        {
            if (PointerSize is not (4 or 8) || PointerOffsets is not { Count: >= 1 and <= 8 })
            {
                throw new ArgumentException("En pointersti kræver 4- eller 8-byte pointere og mellem ét og otte led.");
            }

            if (PointerOffsets.Any(offset => offset is < -0x1000_0000L or > 0x1000_0000L))
            {
                throw new ArgumentException("Pointeroffset er uden for den understøttede grænse på ±256 MiB.");
            }
        }
        else if (PointerOffsets is { Count: > 0 } || PointerSize != 0)
        {
            throw new ArgumentException("En direkte modulreference må ikke indeholde pointerled.");
        }

        if (SessionId is not null || SessionAddress is not null)
        {
            throw new ArgumentException("En modul- eller pointerreference må ikke samtidig være en sessionsadresse.");
        }
    }

    public long Resolve(IApprovedMemoryTarget target, bool enforceBuild = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateDefinition();
        if (!StringComparer.Ordinal.Equals(TargetId, target.Identity.TargetId))
        {
            throw new InvalidOperationException("Profilen hører til et andet godkendt mål.");
        }

        if (target is ISessionMemoryTarget { IsAlive: false })
        {
            throw new InvalidOperationException("Målprocessen er afsluttet. Tilknyt den nye processtart igen.");
        }

        if (enforceBuild &&
            ((!string.IsNullOrWhiteSpace(ExpectedBuildFingerprint) && !StringComparer.Ordinal.Equals(ExpectedBuildFingerprint, target.Identity.BuildFingerprint)) ||
             (!string.IsNullOrWhiteSpace(ExpectedVersion) && !StringComparer.Ordinal.Equals(ExpectedVersion, target.Identity.Version))))
        {
            throw new InvalidOperationException("Spillets version eller build er ændret. Genfind og godkend referencen før en ny ændring.");
        }

        if (Kind == MemoryReferenceKind.SessionAddress)
        {
            if (target is not ISessionMemoryTarget session || !StringComparer.Ordinal.Equals(SessionId, session.SessionId))
            {
                throw new InvalidOperationException("Denne adresse gælder kun den tidligere processtart. Genfind værdien eller brug en valideret pointersti.");
            }

            return SessionAddress!.Value;
        }

        if (enforceBuild && target is IModuleFingerprintTarget && string.IsNullOrWhiteSpace(ExpectedModuleFingerprint))
        {
            throw new InvalidOperationException($"Referencen mangler et gemt build-fingeraftryk for rodmodulet '{ModuleName}'. Genfind og godkend referencen før ændring.");
        }

        if (enforceBuild && !string.IsNullOrWhiteSpace(ExpectedModuleFingerprint) &&
            (target is not IModuleFingerprintTarget moduleIdentity ||
             !moduleIdentity.TryGetModuleFingerprint(ModuleName, out var moduleFingerprint) ||
             !StringComparer.Ordinal.Equals(ExpectedModuleFingerprint, moduleFingerprint)))
        {
            throw new InvalidOperationException($"Rodmodulet '{ModuleName}' har ændret build eller kan ikke verificeres. Genfind og godkend referencen før ændring.");
        }

        if (!target.TryResolveModuleBase(ModuleName, out var moduleBase))
        {
            throw new InvalidOperationException("Modulet kan ikke opløses i det valgte mål.");
        }

        var address = checked(moduleBase + Offset);
        if (target is IModuleMemoryTarget modules)
        {
            var module = modules.Modules.FirstOrDefault(module => StringComparer.OrdinalIgnoreCase.Equals(module.Name, ModuleName));
            var width = Kind == MemoryReferenceKind.PointerPath ? PointerSize : ValueCodec.SizeOf(DataType);
            if (module is null || address < module.BaseAddress || address > checked(module.BaseAddress + module.Size - width))
            {
                throw new InvalidOperationException("Det gemte moduloffset ligger ikke længere inde i modulet.");
            }
        }

        if (Kind != MemoryReferenceKind.PointerPath)
        {
            return address;
        }

        if ((target.Identity.Architecture == "x86" && PointerSize != 4) ||
            (target.Identity.Architecture == "x64" && PointerSize != 8))
        {
            throw new InvalidOperationException("Pointerbredden passer ikke til målprocessens arkitektur.");
        }

        foreach (var offset in PointerOffsets!)
        {
            var pointerType = PointerSize == 4 ? ScanDataType.UInt32 : ScanDataType.UInt64;
            if (!target.TryRead(address, pointerType, out var pointer) || pointer.Bytes is null || pointer.Bytes.Length != PointerSize)
            {
                throw new InvalidOperationException("Et led i pointerstien kan ikke læses.");
            }

            var pointerValue = PointerSize == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(pointer.Bytes)
                : BinaryPrimitives.ReadUInt64LittleEndian(pointer.Bytes);
            if (pointerValue == 0 || pointerValue > long.MaxValue)
            {
                throw new InvalidOperationException("Pointerstien indeholder en tom eller ugyldig pointer.");
            }

            address = checked((long)pointerValue + offset);
            if (address <= 0)
            {
                throw new InvalidOperationException("Pointerstien opløses til en ugyldig adresse.");
            }
        }

        return address;
    }
}

public sealed record TrainerFeature(
    Guid Id,
    string Name,
    string Description,
    string Category,
    TrainerControlKind Control,
    TrainerOperation Operation,
    MemoryReference Reference,
    string Value,
    string? Minimum,
    string? Maximum,
    string? Step,
    string? Hotkey,
    bool RestoreWhenDisabled = true);

public sealed record TrainerProfile(
    int SchemaVersion,
    Guid Id,
    string Name,
    string Notes,
    TargetIdentity Target,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<TrainerFeature> Features)
{
    public static TrainerProfile Create(string name, TargetIdentity target, string notes = "") => new(
        SchemaVersion: ProfileStore.SupportedSchemaVersion,
        Id: Guid.NewGuid(),
        Name: name,
        Notes: notes,
        Target: target,
        CreatedUtc: DateTimeOffset.UtcNow,
        UpdatedUtc: DateTimeOffset.UtcNow,
        Features: []);

    public TrainerProfile WithFeature(TrainerFeature feature) => this with
    {
        UpdatedUtc = DateTimeOffset.UtcNow,
        Features = Features.Append(feature).ToArray()
    };
}

public sealed record ProfileValidationResult(ProfileValidationState State, IReadOnlyList<string> Messages)
{
    public bool IsReady => State == ProfileValidationState.Ready;

    public bool NeedsUserReview => State == ProfileValidationState.NeedsReview;
}

public static class TrainerBuilder
{
    public static TrainerFeature Create(
        ScanHit hit,
        IApprovedMemoryTarget target,
        string name,
        TrainerOperation operation,
        TrainerControlKind control,
        string value,
        string category = "Generelt",
        string description = "",
        string? hotkey = null,
        string? minimum = null,
        string? maximum = null,
        string? step = null)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Funktionen skal have et navn.", nameof(name));
        }

        if (!target.TryRead(hit.Address, hit.CurrentValue.Type, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(hit), "Adressen kan ikke læses i det valgte mål.");
        }

        MemoryReference reference;
        var module = (target as IModuleMemoryTarget)?.Modules.FirstOrDefault(module =>
            hit.Address >= module.BaseAddress && hit.Address <= checked(module.BaseAddress + module.Size - hit.CurrentValue.Size));
        if (module is not null)
        {
            reference = new MemoryReference(target.Identity.TargetId, module.Name, checked(hit.Address - module.BaseAddress), hit.CurrentValue.Type, hit.CurrentValue.Display);
        }
        else if (target is OwnedTestTarget owned && hit.Address >= owned.ModuleBaseAddress)
        {
            reference = new MemoryReference(target.Identity.TargetId, target.Identity.ModuleName, checked(hit.Address - owned.ModuleBaseAddress), hit.CurrentValue.Type, hit.CurrentValue.Display);
        }
        else if (target is ISessionMemoryTarget session)
        {
            reference = new MemoryReference(target.Identity.TargetId, target.Identity.ModuleName, 0, hit.CurrentValue.Type, hit.CurrentValue.Display,
                Kind: MemoryReferenceKind.SessionAddress, SessionId: session.SessionId, SessionAddress: hit.Address);
        }
        else
        {
            throw new InvalidOperationException("Målet oplyser hverken modulgrænser eller en entydig processession. Referencen kan ikke gemmes forsvarligt.");
        }

        return CreateFromReference(reference, target, name, operation, control, value, category, description, hotkey, minimum, maximum, step);
    }

    public static TrainerFeature CreateFromReference(
        MemoryReference reference,
        IApprovedMemoryTarget target,
        string name,
        TrainerOperation operation,
        TrainerControlKind control,
        string value,
        string category = "Generelt",
        string description = "",
        string? hotkey = null,
        string? minimum = null,
        string? maximum = null,
        string? step = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var expectedModuleFingerprint = reference.ExpectedModuleFingerprint;
        if (reference.Kind != MemoryReferenceKind.SessionAddress && target is IModuleFingerprintTarget moduleIdentity)
        {
            if (!moduleIdentity.TryGetModuleFingerprint(reference.ModuleName, out expectedModuleFingerprint))
            {
                throw new InvalidOperationException($"Rodmodulet '{reference.ModuleName}' kan ikke verificeres. Funktionen blev ikke oprettet.");
            }
        }

        reference = reference with
        {
            ExpectedBuildFingerprint = target.Identity.BuildFingerprint,
            ExpectedVersion = target.Identity.Version,
            ExpectedModuleFingerprint = expectedModuleFingerprint
        };
        var address = reference.Resolve(target);
        if (!target.TryRead(address, reference.DataType, out _))
        {
            throw new ArgumentException("Den valgte reference kan ikke læses i målet.");
        }

        ValidateControlOperation(control, operation);
        var normalizedValue = (value ?? string.Empty).Trim();
        var feature = new TrainerFeature(
            Guid.NewGuid(),
            name.Trim(),
            description.Trim(),
            string.IsNullOrWhiteSpace(category) ? "Generelt" : category.Trim(),
            control,
            operation,
            reference,
            normalizedValue,
            minimum?.Trim(),
            maximum?.Trim(),
            step?.Trim(),
            string.IsNullOrWhiteSpace(hotkey) ? null : hotkey.Trim(),
            RestoreWhenDisabled: operation == TrainerOperation.FreezeValue);
        ValidateRequestedValue(feature, normalizedValue, allowRestoreWithoutValue: true);
        return feature;
    }

    public static ScanValue ValidateRequestedValue(TrainerFeature feature, string value, bool allowRestoreWithoutValue = false)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Operation == TrainerOperation.RestoreOriginal && allowRestoreWithoutValue)
        {
            return default;
        }

        var requested = ScanValue.Parse(feature.Reference.DataType, value);
        if (feature.Control != TrainerControlKind.Slider)
        {
            return requested;
        }

        var minimum = ScanValue.Parse(feature.Reference.DataType, feature.Minimum ?? throw new ArgumentException("Slider kræver minimum."));
        var maximum = ScanValue.Parse(feature.Reference.DataType, feature.Maximum ?? throw new ArgumentException("Slider kræver maksimum."));
        var step = ScanValue.Parse(feature.Reference.DataType, feature.Step ?? throw new ArgumentException("Slider kræver trin."));
        if (feature.Reference.DataType is ScanDataType.Int64 or ScanDataType.UInt64)
        {
            const decimal maxExactSliderInteger = 9_007_199_254_740_992m;
            if (Math.Abs(decimal.Parse(minimum.Display, System.Globalization.CultureInfo.InvariantCulture)) > maxExactSliderInteger ||
                Math.Abs(decimal.Parse(maximum.Display, System.Globalization.CultureInfo.InvariantCulture)) > maxExactSliderInteger)
                throw new ArgumentException("Dette heltalsinterval kan ikke vises præcist på en slider. Brug talindtastning til store 64-bit-værdier.");
        }
        var zero = ScanValue.Parse(feature.Reference.DataType, "0");
        if (!ValueCodec.TryCompare(minimum, maximum, out var rangeComparison) || rangeComparison > 0)
        {
            throw new ArgumentException("Sliderens minimum må ikke være større end maksimum.");
        }

        if (!ValueCodec.TryCompare(step, zero, out var stepComparison) || stepComparison <= 0)
        {
            throw new ArgumentException("Sliderens trin skal være større end nul.");
        }

        if (!ValueCodec.TryCompare(requested, minimum, out var lowerComparison) ||
            !ValueCodec.TryCompare(requested, maximum, out var upperComparison) ||
            lowerComparison < 0 || upperComparison > 0)
        {
            throw new ArgumentException("Sliderens værdi skal ligge mellem minimum og maksimum.");
        }

        if (!ValueCodec.IsAlignedToStep(requested, minimum, step))
        {
            throw new ArgumentException("Sliderens værdi skal passe til det valgte trin.");
        }

        return requested;
    }

    internal static void ValidateControlOperation(TrainerControlKind control, TrainerOperation operation)
    {
        if (operation == TrainerOperation.FreezeValue && control != TrainerControlKind.Toggle)
        {
            throw new ArgumentException("'Fasthold værdi' kræver en kontakt, så den kan deaktiveres tydeligt.");
        }

        if (operation == TrainerOperation.RestoreOriginal && control != TrainerControlKind.Button)
        {
            throw new ArgumentException("'Gendan oprindelig' oprettes som en knap.");
        }

        if (control == TrainerControlKind.Toggle && operation != TrainerOperation.FreezeValue)
        {
            throw new ArgumentException("En kontakt bruges til 'Fasthold værdi'.");
        }

        if (control == TrainerControlKind.Slider && operation != TrainerOperation.SetValue)
        {
            throw new ArgumentException("En slider bruges til 'Indstil værdi'.");
        }

        if (control == TrainerControlKind.NumberInput && operation is not (TrainerOperation.SetValue or TrainerOperation.AddValue))
        {
            throw new ArgumentException("Talindtastning bruges til 'Indstil værdi' eller 'Tilføj værdi'.");
        }
    }
}

public static class ProfileValidator
{
    public static ProfileValidationResult Validate(TrainerProfile profile, IApprovedMemoryTarget? target)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (target is null)
        {
            return new ProfileValidationResult(ProfileValidationState.NotAttached, ["Vælg eller start det godkendte testmål først."]);
        }

        if (profile.SchemaVersion != ProfileStore.SupportedSchemaVersion)
        {
            return new ProfileValidationResult(
                ProfileValidationState.NeedsRepair,
                [$"Profilformat {profile.SchemaVersion} understøttes ikke af denne version."]);
        }

        var repairMessages = new List<string>();
        var reviewMessages = new List<string>();
        if (!StringComparer.Ordinal.Equals(profile.Target.TargetId, target.Identity.TargetId))
        {
            repairMessages.Add("Profilen hører til et andet testmål.");
        }

        if (!StringComparer.Ordinal.Equals(profile.Target.Architecture, target.Identity.Architecture))
        {
            repairMessages.Add("Målets arkitektur passer ikke til profilen.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(profile.Target.ModuleName, target.Identity.ModuleName))
        {
            repairMessages.Add("Profilens hovedmodul passer ikke til målet.");
        }

        if (!StringComparer.Ordinal.Equals(profile.Target.Version, target.Identity.Version))
        {
            reviewMessages.Add($"Profilen er lavet til version {profile.Target.Version}, men målet er version {target.Identity.Version}. Gennemgå og test funktionerne før brug.");
        }

        if (!StringComparer.Ordinal.Equals(profile.Target.BuildFingerprint, target.Identity.BuildFingerprint))
        {
            reviewMessages.Add("Målets build-fingeraftryk er ændret. Gennemgå og test funktionerne før brug.");
        }

        foreach (var feature in profile.Features)
        {
            try
            {
                if (feature.Reference.IsPersistent && target is IModuleFingerprintTarget &&
                    string.IsNullOrWhiteSpace(feature.Reference.ExpectedModuleFingerprint))
                {
                    reviewMessages.Add($"{feature.Name}: der mangler et gemt build-fingeraftryk for rodmodulet '{feature.Reference.ModuleName}'. Genfind og godkend referencen.");
                }

                if (feature.Reference.Kind != MemoryReferenceKind.SessionAddress &&
                    !string.IsNullOrWhiteSpace(feature.Reference.ExpectedModuleFingerprint) &&
                    (target is not IModuleFingerprintTarget moduleIdentity ||
                     !moduleIdentity.TryGetModuleFingerprint(feature.Reference.ModuleName, out var fingerprint) ||
                     !StringComparer.Ordinal.Equals(feature.Reference.ExpectedModuleFingerprint, fingerprint)))
                {
                    reviewMessages.Add($"{feature.Name}: rodmodulet '{feature.Reference.ModuleName}' har ændret build eller kan ikke verificeres.");
                }

                // Version differences remain a visible review state, while
                // runtime writes enforce the saved fingerprint/version.
                var address = feature.Reference.Resolve(target, enforceBuild: false);
                if (!target.TryRead(address, feature.Reference.DataType, out _))
                {
                    repairMessages.Add($"{feature.Name}: den gemte reference kan ikke læses.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                repairMessages.Add($"{feature.Name}: {exception.Message}");
            }
        }

        if (repairMessages.Count > 0)
        {
            return new ProfileValidationResult(ProfileValidationState.NeedsRepair, repairMessages);
        }

        return reviewMessages.Count > 0
            ? new ProfileValidationResult(ProfileValidationState.NeedsReview, reviewMessages)
            : new ProfileValidationResult(ProfileValidationState.Ready, ["Profilen er klar til det godkendte testmål."]);
    }
}

public sealed class ProfileStore
{
    // Version 3 prevents an older app from treating a pointer/session record
    // as a plain module offset. Version 2 files are migrated when loaded.
    public const int SupportedSchemaVersion = 3;
    public const long MaximumImportBytes = 2 * 1024 * 1024;
    public const int BackupRetentionCount = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProfileStore(string rootDirectory)
    {
        RootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
    }

    public string RootDirectory { get; }

    public string GetPath(Guid profileId) => Path.Combine(RootDirectory, $"{profileId:D}.trainerprofile.json");

    public string GetBackupDirectory(Guid profileId) => Path.Combine(RootDirectory, "Backups", profileId.ToString("D"));

    public void Save(TrainerProfile profile)
    {
        ValidateProfile(profile, InvalidProfileExceptionKind.Save);

        Directory.CreateDirectory(RootDirectory);
        var path = GetPath(profile.Id);
        if (File.Exists(path))
        {
            CreateBackup(path, profile.Id);
        }

        WriteAtomically(profile, path);
    }

    public TrainerProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadProfile(path, maximumBytes: null);
    }

    /// <summary>
    /// Writes a portable JSON copy of a profile. It never changes the local
    /// profile library or triggers a trainer action.
    /// </summary>
    public void Export(TrainerProfile profile, string destinationPath)
    {
        ValidateProfile(profile, InvalidProfileExceptionKind.Save);
        EnsureProfileExtension(destinationPath, "Eksportfilen skal slutte med .trainerprofile.json.");

        var sourcePath = GetPath(profile.Id);
        if (PathsEqual(sourcePath, destinationPath))
        {
            throw new ArgumentException("Eksportfilen må ikke være den samme som profilens lokale bibliotekskopi.", nameof(destinationPath));
        }

        WriteAtomically(profile, destinationPath);
    }

    /// <summary>
    /// Imports profile data only. The result always receives fresh profile and
    /// feature ids before it is stored locally, so it cannot overwrite an
    /// existing trainer profile.
    /// </summary>
    public TrainerProfile Import(string sourcePath)
    {
        EnsureProfileExtension(sourcePath, "Kun .trainerprofile.json-filer kan importeres.");
        var source = ReadProfile(sourcePath, MaximumImportBytes);
        var imported = CloneForNewProfile(source, $"Importeret kopi af {source.Name}");
        Save(imported);
        return imported;
    }

    /// <summary>
    /// Creates and stores an independent local copy. This is useful for
    /// experimenting without changing the original profile.
    /// </summary>
    public TrainerProfile Duplicate(TrainerProfile profile, string? name = null)
    {
        ValidateProfile(profile, InvalidProfileExceptionKind.Save);
        var copy = CloneForNewProfile(profile, name);
        Save(copy);
        return copy;
    }

    public IReadOnlyList<string> ListBackupPaths(Guid profileId)
    {
        var directory = GetBackupDirectory(profileId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.trainerprofile.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
    }

    private static TrainerProfile CloneForNewProfile(TrainerProfile source, string? name)
    {
        var now = DateTimeOffset.UtcNow;
        return source with
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Kopi af {source.Name}" : name.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now,
            Features = source.Features.Select(feature => feature with { Id = Guid.NewGuid() }).ToArray()
        };
    }

    private void CreateBackup(string currentPath, Guid profileId)
    {
        var backupDirectory = GetBackupDirectory(profileId);
        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDirectory, $"{stamp}.trainerprofile.json");
        var sequence = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(backupDirectory, $"{stamp}-{sequence++:D2}.trainerprofile.json");
        }

        File.Copy(currentPath, backupPath, overwrite: false);
        foreach (var expiredPath in ListBackupPaths(profileId).Skip(BackupRetentionCount))
        {
            File.Delete(expiredPath);
        }
    }

    private static TrainerProfile ReadProfile(string path, long? maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Profilfilen findes ikke.", path);
        }

        if (maximumBytes is { } maximum && file.Length > maximum)
        {
            throw new InvalidDataException($"Profilfilen er for stor. Maksimum er {MaximumImportBytes / (1024 * 1024)} MB.");
        }

        try
        {
            var profile = JsonSerializer.Deserialize<TrainerProfile>(File.ReadAllText(path), Options);
            if (profile is null)
            {
                throw new InvalidDataException("Profilfilen indeholder ikke en gyldig trainerprofil.");
            }

            profile = UpgradeProfile(profile);
            ValidateProfile(profile, InvalidProfileExceptionKind.Load);
            return profile;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Profilfilen indeholder ugyldig JSON.", exception);
        }
    }

    private static void WriteAtomically(TrainerProfile profile, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Options));
            File.Move(temporary, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static TrainerProfile UpgradeProfile(TrainerProfile profile)
    {
        if (profile.SchemaVersion is not (2 or SupportedSchemaVersion) || profile.Target is null || profile.Features is null)
        {
            return profile;
        }

        return profile with
        {
            SchemaVersion = SupportedSchemaVersion,
            Features = profile.Features.Select(feature => feature is null || feature.Reference is null ? feature! : feature with
            {
                Reference = feature.Reference with
                {
                    ExpectedBuildFingerprint = feature.Reference.ExpectedBuildFingerprint ?? profile.Target.BuildFingerprint,
                    ExpectedVersion = feature.Reference.ExpectedVersion ?? profile.Target.Version
                }
            }).ToArray()
        };
    }

    private static void EnsureProfileExtension(string path, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(".trainerprofile.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(message, nameof(path));
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateProfile(TrainerProfile profile, InvalidProfileExceptionKind exceptionKind)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != SupportedSchemaVersion)
        {
            ThrowInvalidProfile(exceptionKind, $"Profilformat {profile.SchemaVersion} understøttes ikke. Forventede format {SupportedSchemaVersion}.");
        }

        if (profile.Id == Guid.Empty)
        {
            ThrowInvalidProfile(exceptionKind, "Profilen mangler et gyldigt id.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            ThrowInvalidProfile(exceptionKind, "Profilen mangler et navn.");
        }

        if (profile.Target is null ||
            string.IsNullOrWhiteSpace(profile.Target.TargetId) ||
            string.IsNullOrWhiteSpace(profile.Target.DisplayName) ||
            string.IsNullOrWhiteSpace(profile.Target.Version) ||
            string.IsNullOrWhiteSpace(profile.Target.Architecture) ||
            string.IsNullOrWhiteSpace(profile.Target.ModuleName) ||
            string.IsNullOrWhiteSpace(profile.Target.BuildFingerprint))
        {
            ThrowInvalidProfile(exceptionKind, "Profilen mangler gyldige mål-oplysninger.");
        }

        if (profile.Features is not { } features)
        {
            ThrowInvalidProfile(exceptionKind, "Profilen mangler sin funktionsliste.");
            return;
        }

        var knownFeatureIds = new HashSet<Guid>();
        foreach (var feature in features)
        {
            if (feature is null)
            {
                ThrowInvalidProfile(exceptionKind, "Profilen indeholder en tom funktion.");
                continue;
            }

            if (feature.Id == Guid.Empty || !knownFeatureIds.Add(feature.Id))
            {
                ThrowInvalidProfile(exceptionKind, "Profilen indeholder en funktion med et ugyldigt eller gentaget id.");
            }

            var featureValue = feature.Value;
            if (string.IsNullOrWhiteSpace(feature.Name) || featureValue is null || feature.Reference is null ||
                string.IsNullOrWhiteSpace(feature.Reference.TargetId) ||
                string.IsNullOrWhiteSpace(feature.Reference.ModuleName) ||
                !Enum.IsDefined(feature.Reference.DataType) ||
                !Enum.IsDefined(feature.Control) || !Enum.IsDefined(feature.Operation))
            {
                ThrowInvalidProfile(exceptionKind, $"Funktionen '{feature.Name ?? "(uden navn)"}' er ikke gyldigt formateret.");
                continue;
            }

            try
            {
                feature.Reference.ValidateDefinition();
                if (!StringComparer.Ordinal.Equals(feature.Reference.TargetId, profile.Target!.TargetId))
                {
                    ThrowInvalidProfile(exceptionKind, $"Funktionen '{feature.Name}' hører til et andet mål end profilen.");
                }

                TrainerBuilder.ValidateControlOperation(feature.Control, feature.Operation);
                TrainerBuilder.ValidateRequestedValue(feature, featureValue, allowRestoreWithoutValue: true);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                ThrowInvalidProfile(exceptionKind, $"Funktionen '{feature.Name}' har ugyldige indstillinger: {exception.Message}");
            }
        }
    }

    private static void ThrowInvalidProfile(InvalidProfileExceptionKind kind, string message)
    {
        if (kind == InvalidProfileExceptionKind.Load)
        {
            throw new InvalidDataException(message);
        }

        throw new InvalidOperationException(message);
    }

    private enum InvalidProfileExceptionKind
    {
        Save,
        Load
    }

    public IReadOnlyList<string> ListPaths()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(RootDirectory, "*.trainerprofile.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
