using System.Runtime.CompilerServices;

namespace TrainerStudio.Core;

public sealed record TrainerActionResult(bool Succeeded, string Message, ScanValue? Before = null, ScanValue? After = null);

/// <summary>
/// Executes declarative features against the currently approved process.
/// Baselines belong to one concrete address and process session, never just
/// to the text of a pointer path. Every successful write is read back.
/// </summary>
public sealed class TrainerRuntime
{
    private sealed record ReferenceKey(string SessionId, long Address, ScanDataType DataType)
    {
        public int Size => ValueCodec.SizeOf(DataType);
    }

    private sealed record ActiveFreeze(TrainerFeature Feature, ReferenceKey Key, ScanValue Desired);
    private readonly Dictionary<ReferenceKey, ScanValue> _originalValues = new();
    private readonly Dictionary<Guid, ActiveFreeze> _frozenValues = new();
    private readonly Dictionary<string, ReferenceKey> _resolvedReferences = new(StringComparer.Ordinal);
    private IApprovedMemoryTarget? _sessionTarget;
    private string? _sessionId;

    public IReadOnlyCollection<Guid> ActiveFreezeFeatureIds => _frozenValues.Keys.ToArray();
    public bool IsFreezeActive(TrainerFeature feature) => _frozenValues.ContainsKey(feature.Id);

    /// <summary>Stops without another write and discards session baselines.</summary>
    public int StopAll()
    {
        var stopped = _frozenValues.Count;
        _frozenValues.Clear();
        _originalValues.Clear();
        _resolvedReferences.Clear();
        _sessionTarget = null;
        _sessionId = null;
        return stopped;
    }

    public TrainerActionResult Activate(IApprovedMemoryTarget target, TrainerFeature feature, bool userConfirmed, string? valueOverride = null, long? expectedResolvedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(feature);
        if (!userConfirmed)
        {
            return new TrainerActionResult(false, "Ændringen blev ikke udført uden din godkendelse.");
        }

        try
        {
            if (!EnsureSession(target))
            {
                return new TrainerActionResult(false, "Målprocessen er afsluttet. Fastholdelser og sessionsværdier er ryddet.");
            }

            var address = feature.Reference.Resolve(target);
            if (expectedResolvedAddress is { } confirmedAddress && address != confirmedAddress)
            {
                return new TrainerActionResult(false, "Referencen flyttede sig, mens godkendelsesdialogen var åben. Ingen værdi blev ændret. Gennemgå den nye adresse og godkend igen.");
            }

            if (!target.TryRead(address, feature.Reference.DataType, out var before))
            {
                return new TrainerActionResult(false, "Den gemte reference kan ikke læses. Genfind værdien eller reparér referencen.");
            }

            var key = new ReferenceKey(_sessionId!, address, feature.Reference.DataType);
            TrackResolvedReference(feature.Reference, key);
            if (feature.Operation == TrainerOperation.RestoreOriginal)
            {
                return RestoreOriginal(target, feature, key, before);
            }

            var requested = TrainerBuilder.ValidateRequestedValue(feature, valueOverride ?? feature.Value);
            var desired = feature.Operation switch
            {
                TrainerOperation.SetValue or TrainerOperation.FreezeValue => requested,
                TrainerOperation.AddValue => ValueCodec.Add(before, requested),
                _ => throw new ArgumentOutOfRangeException(nameof(feature.Operation))
            };

            // Keep a recovery baseline even if a write occurs but readback fails.
            _originalValues.TryAdd(key, before);
            if (!TryWriteVerified(target, address, desired, out var actual, out var error))
            {
                RemoveOverlappingFreezes(key);
                return new TrainerActionResult(false, error, before, actual);
            }

            var replaced = RemoveOverlappingFreezes(key);
            if (feature.Operation == TrainerOperation.FreezeValue)
            {
                _frozenValues[feature.Id] = new ActiveFreeze(feature, key, desired);
            }

            var conflictMessage = replaced > 0 ? $" {replaced} tidligere fastholdelse(r) på samme byteområde er stoppet." : string.Empty;
            return new TrainerActionResult(true, $"{feature.Name}: {before.Display} → {actual!.Value.Display}. Tilbagelæsning bekræftet.{conflictMessage}", before, actual);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return new TrainerActionResult(false, exception.Message);
        }
    }

    public TrainerActionResult Deactivate(IApprovedMemoryTarget target, TrainerFeature feature, bool userConfirmed)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(feature);
        if (!userConfirmed)
        {
            return new TrainerActionResult(false, "Deaktivering blev ikke udført uden din godkendelse.");
        }

        try
        {
            if (!EnsureSession(target))
            {
                return new TrainerActionResult(false, "Målprocessen er afsluttet. Fastholdelsen er stoppet uden gendannelse.");
            }

            if (!_frozenValues.TryGetValue(feature.Id, out var active))
            {
                return new TrainerActionResult(true, $"{feature.Name} er ikke aktiv.");
            }

            var address = feature.Reference.Resolve(target);
            if (address != active.Key.Address)
            {
                InvalidateMovedFreeze(feature.Id, active);
                return new TrainerActionResult(false, $"{feature.Name}: pointerstien peger nu på en anden adresse. Fastholdelsen er stoppet uden at gendanne en gammel værdi i et nyt objekt.");
            }

            if (feature.RestoreWhenDisabled && _originalValues.TryGetValue(active.Key, out var original))
            {
                if (!target.TryRead(address, feature.Reference.DataType, out var before))
                {
                    return new TrainerActionResult(false, "Den gemte reference kan ikke læses.");
                }

                if (!TryWriteVerified(target, address, original, out var actual, out var error))
                {
                    _frozenValues.Remove(feature.Id);
                    return new TrainerActionResult(false, error, before, actual);
                }

                RemoveOverlappingFreezes(active.Key);
                _originalValues.Remove(active.Key);
                return new TrainerActionResult(true, $"{feature.Name}: oprindelig sessionsværdi gendannet og tilbagelæst.", before, actual);
            }

            _frozenValues.Remove(feature.Id);
            return new TrainerActionResult(true, $"{feature.Name} er deaktiveret uden at ændre værdien.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            if (_frozenValues.TryGetValue(feature.Id, out var active))
            {
                InvalidateMovedFreeze(feature.Id, active);
            }

            return new TrainerActionResult(false, exception.Message);
        }
    }

    public IReadOnlyList<TrainerActionResult> Tick(IApprovedMemoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var previouslyActive = _frozenValues.Count;
        if (!EnsureSession(target))
        {
            return previouslyActive == 0 ? [] : [new TrainerActionResult(false, "Målprocessen er afsluttet. Alle fastholdelser er stoppet uden en ny ændring.")];
        }

        var results = new List<TrainerActionResult>();
        foreach (var (featureId, active) in _frozenValues.ToArray())
        {
            try
            {
                var address = active.Feature.Reference.Resolve(target);
                if (address != active.Key.Address)
                {
                    InvalidateMovedFreeze(featureId, active);
                    results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: pointerstien flyttede sig; fastholdelsen er stoppet. Godkend funktionen igen for det nye objekt."));
                    continue;
                }

                if (!target.TryRead(address, active.Desired.Type, out var before))
                {
                    InvalidateMovedFreeze(featureId, active);
                    results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: reference kan ikke læses; fastholdelse er stoppet."));
                    continue;
                }

                if (before.NumericallyEquals(active.Desired))
                {
                    results.Add(new TrainerActionResult(true, $"{active.Feature.Name}: værdien er fortsat fastholdt.", before, before));
                    continue;
                }

                if (!TryWriteVerified(target, address, active.Desired, out var actual, out var error))
                {
                    _frozenValues.Remove(featureId);
                    results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: {error}; fastholdelse er stoppet.", before, actual));
                    continue;
                }

                results.Add(new TrainerActionResult(true, $"{active.Feature.Name}: værdien blev fastholdt og tilbagelæst.", before, actual));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                InvalidateMovedFreeze(featureId, active);
                results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: {exception.Message}; fastholdelse er stoppet."));
            }
        }

        return results;
    }

    private bool EnsureSession(IApprovedMemoryTarget target)
    {
        if (target is ISessionMemoryTarget { IsAlive: false })
        {
            StopAll();
            return false;
        }

        var sessionId = target is ISessionMemoryTarget session ? session.SessionId : $"instance:{RuntimeHelpers.GetHashCode(target):X}";
        if (!ReferenceEquals(_sessionTarget, target) || !StringComparer.Ordinal.Equals(_sessionId, sessionId))
        {
            StopAll();
            _sessionTarget = target;
            _sessionId = sessionId;
        }

        return true;
    }

    private TrainerActionResult RestoreOriginal(IApprovedMemoryTarget target, TrainerFeature feature, ReferenceKey key, ScanValue before)
    {
        if (!_originalValues.TryGetValue(key, out var original))
        {
            return new TrainerActionResult(false, $"{feature.Name}: der findes ingen oprindelig værdi for denne adresse i den aktive processession.", before);
        }

        if (!TryWriteVerified(target, key.Address, original, out var actual, out var error))
        {
            RemoveOverlappingFreezes(key);
            return new TrainerActionResult(false, error, before, actual);
        }

        RemoveOverlappingFreezes(key);
        _originalValues.Remove(key);
        return new TrainerActionResult(true, $"{feature.Name}: oprindelig sessionsværdi gendannet og tilbagelæst.", before, actual);
    }

    private void TrackResolvedReference(MemoryReference reference, ReferenceKey key)
    {
        var identity = $"{reference.TargetId}:{reference.Kind}:{reference.ModuleName}:{reference.Offset}:{reference.DataType}:{reference.PointerSize}:{reference.SessionAddress}:{string.Join(",", reference.PointerOffsets ?? [])}";
        if (_resolvedReferences.TryGetValue(identity, out var previous) && previous != key)
        {
            // A pointer may move between one-shot actions as well as while a
            // freeze is active. Do not retain the old object's recovery data.
            _originalValues.Remove(previous);
            RemoveOverlappingFreezes(previous);
        }

        _resolvedReferences[identity] = key;
    }

    private static bool TryWriteVerified(IApprovedMemoryTarget target, long address, ScanValue desired, out ScanValue? actual, out string error)
    {
        actual = null;
        if (!target.TryWrite(address, desired, out error))
        {
            return false;
        }

        if (!target.TryRead(address, desired.Type, out var readback))
        {
            error = "Ændringen blev sendt, men resultatet kunne ikke tilbagelæses. Resultatet er usikkert; fastholdelse aktiveres ikke.";
            return false;
        }

        actual = readback;
        if (!readback.NumericallyEquals(desired))
        {
            error = $"Resultatet kunne ikke bekræftes: ønsket {desired.Display}, tilbagelæst {readback.Display}. Spillet kan have ændret værdien igen.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void InvalidateMovedFreeze(Guid featureId, ActiveFreeze active)
    {
        _frozenValues.Remove(featureId);
        _originalValues.Remove(active.Key);
    }

    private int RemoveOverlappingFreezes(ReferenceKey key)
    {
        var matches = _frozenValues.Where(pair =>
            pair.Value.Key.SessionId == key.SessionId &&
            pair.Value.Key.Address < checked(key.Address + key.Size) &&
            key.Address < checked(pair.Value.Key.Address + pair.Value.Key.Size)).Select(pair => pair.Key).ToArray();
        foreach (var id in matches)
        {
            _frozenValues.Remove(id);
        }

        return matches.Length;
    }
}
