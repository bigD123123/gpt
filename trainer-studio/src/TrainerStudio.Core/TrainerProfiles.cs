using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record MemoryReference(
    string TargetId,
    string ModuleName,
    long Offset,
    ScanDataType DataType,
    string ExpectedValueHint)
{
    public long Resolve(IApprovedMemoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!StringComparer.Ordinal.Equals(TargetId, target.Identity.TargetId))
        {
            throw new InvalidOperationException("Profilen hører til et andet godkendt mål.");
        }

        if (!StringComparer.Ordinal.Equals(ModuleName, target.Identity.ModuleName))
        {
            throw new InvalidOperationException("Profilen hører til et andet modul.");
        }

        if (!target.TryResolveModuleBase(ModuleName, out var moduleBase))
        {
            throw new InvalidOperationException("Modulet kan ikke opløses i det valgte mål.");
        }

        return checked(moduleBase + Offset);
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

        if (!target.TryResolveModuleBase(target.Identity.ModuleName, out var moduleBase) || hit.Address < moduleBase ||
            !target.TryRead(hit.Address, hit.CurrentValue.Type, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(hit), "Adressen er ikke en stabil reference i testmålet.");
        }

        ValidateControlOperation(control, operation);
        var normalizedValue = (value ?? string.Empty).Trim();

        var reference = new MemoryReference(
            target.Identity.TargetId,
            target.Identity.ModuleName,
            checked(hit.Address - moduleBase),
            hit.CurrentValue.Type,
            hit.CurrentValue.Display);
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

    private static void ValidateControlOperation(TrainerControlKind control, TrainerOperation operation)
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

        if (!StringComparer.Ordinal.Equals(profile.Target.ModuleName, target.Identity.ModuleName))
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
                var address = feature.Reference.Resolve(target);
                if (!target.TryRead(address, feature.Reference.DataType, out _))
                {
                    repairMessages.Add($"{feature.Name}: den gemte reference kan ikke læses.");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
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
    public const int SupportedSchemaVersion = 2;

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

    public void Save(TrainerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidOperationException($"Kun profilformat {SupportedSchemaVersion} kan gemmes.");
        }

        Directory.CreateDirectory(RootDirectory);
        var path = GetPath(profile.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Options));
        File.Move(temporary, path, overwrite: true);
    }

    public TrainerProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var profile = JsonSerializer.Deserialize<TrainerProfile>(File.ReadAllText(path), Options);
        if (profile is null)
        {
            throw new InvalidDataException("Profilfilen indeholder ikke en gyldig trainerprofil.");
        }

        if (profile.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException($"Profilformat {profile.SchemaVersion} understøttes ikke. Forventede format {SupportedSchemaVersion}.");
        }

        return profile;
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
