namespace TrainerStudio.Core;

public sealed record TrainerActionResult(bool Succeeded, string Message, ScanValue? Before = null, ScanValue? After = null);

/// <summary>
/// Executes declarative trainer features only against the approved target used
/// by this starter application. Every write originates from a visible action
/// and requires an explicit confirmation supplied by the UI.
/// </summary>
public sealed class TrainerRuntime
{
    private sealed record ReferenceKey(string TargetId, string ModuleName, long Offset, ScanDataType DataType)
    {
        public static ReferenceKey From(MemoryReference reference) => new(
            reference.TargetId,
            reference.ModuleName,
            reference.Offset,
            reference.DataType);
    }

    private sealed record ActiveFreeze(TrainerFeature Feature, ReferenceKey Key, ScanValue Desired);

    // Baselines deliberately belong to an address reference rather than a
    // feature id, so a separate “restore original” button can restore a value
    // changed by another control during the same session.
    private readonly Dictionary<ReferenceKey, ScanValue> _originalValues = new();
    private readonly Dictionary<Guid, ActiveFreeze> _frozenValues = new();

    public IReadOnlyCollection<Guid> ActiveFreezeFeatureIds => _frozenValues.Keys.ToArray();

    public bool IsFreezeActive(TrainerFeature feature) => _frozenValues.ContainsKey(feature.Id);

    /// <summary>
    /// Stops all active freezes and discards session-only baselines without
    /// writing anything. The UI uses this when the owned test target resets.
    /// </summary>
    public int StopAll()
    {
        var stopped = _frozenValues.Count;
        _frozenValues.Clear();
        _originalValues.Clear();
        return stopped;
    }

    public TrainerActionResult Activate(
        IApprovedMemoryTarget target,
        TrainerFeature feature,
        bool userConfirmed,
        string? valueOverride = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(feature);
        if (!userConfirmed)
        {
            return new TrainerActionResult(false, "Ændringen blev ikke udført uden din godkendelse.");
        }

        try
        {
            var address = feature.Reference.Resolve(target);
            if (!target.TryRead(address, feature.Reference.DataType, out var before))
            {
                return new TrainerActionResult(false, "Den gemte reference kan ikke læses. Kør guidet reparation.");
            }

            var key = ReferenceKey.From(feature.Reference);
            if (feature.Operation == TrainerOperation.RestoreOriginal)
            {
                return RestoreOriginal(target, feature, key, address, before);
            }

            _originalValues.TryAdd(key, before);
            var requested = TrainerBuilder.ValidateRequestedValue(feature, valueOverride ?? feature.Value);
            var after = feature.Operation switch
            {
                TrainerOperation.SetValue or TrainerOperation.FreezeValue => requested,
                TrainerOperation.AddValue => ValueCodec.Add(before, requested),
                _ => throw new ArgumentOutOfRangeException(nameof(feature.Operation))
            };

            if (!target.TryWrite(address, after, out var error))
            {
                return new TrainerActionResult(false, error, before);
            }

            if (feature.Operation == TrainerOperation.FreezeValue)
            {
                _frozenValues[feature.Id] = new ActiveFreeze(feature, key, after);
            }

            return new TrainerActionResult(true, $"{feature.Name}: {before.Display} → {after.Display}", before, after);
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

        if (!_frozenValues.TryGetValue(feature.Id, out var active))
        {
            return new TrainerActionResult(true, $"{feature.Name} er ikke aktiv.");
        }

        try
        {
            var address = feature.Reference.Resolve(target);
            if (feature.RestoreWhenDisabled && _originalValues.TryGetValue(active.Key, out var original))
            {
                if (!target.TryRead(address, feature.Reference.DataType, out var before))
                {
                    return new TrainerActionResult(false, "Den gemte reference kan ikke læses.");
                }

                if (!target.TryWrite(address, original, out var error))
                {
                    return new TrainerActionResult(false, error, before);
                }

                RemoveFreezesFor(active.Key);
                _originalValues.Remove(active.Key);
                return new TrainerActionResult(true, $"{feature.Name}: oprindelig værdi gendannet.", before, original);
            }

            _frozenValues.Remove(feature.Id);
            return new TrainerActionResult(true, $"{feature.Name} er deaktiveret uden at ændre værdien.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return new TrainerActionResult(false, exception.Message);
        }
    }

    public IReadOnlyList<TrainerActionResult> Tick(IApprovedMemoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var results = new List<TrainerActionResult>();
        foreach (var (featureId, active) in _frozenValues.ToArray())
        {
            try
            {
                var address = active.Feature.Reference.Resolve(target);
                if (!target.TryRead(address, active.Desired.Type, out var before))
                {
                    _frozenValues.Remove(featureId);
                    results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: reference kan ikke læses; fastholdelse er stoppet."));
                    continue;
                }

                if (before.NumericallyEquals(active.Desired))
                {
                    results.Add(new TrainerActionResult(true, $"{active.Feature.Name}: værdien er fortsat fastholdt.", before, before));
                    continue;
                }

                if (!target.TryWrite(address, active.Desired, out var error))
                {
                    _frozenValues.Remove(featureId);
                    results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: {error}; fastholdelse er stoppet.", before));
                    continue;
                }

                results.Add(new TrainerActionResult(true, $"{active.Feature.Name}: værdien blev fastholdt.", before, active.Desired));
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                _frozenValues.Remove(featureId);
                results.Add(new TrainerActionResult(false, $"{active.Feature.Name}: {exception.Message}; fastholdelse er stoppet."));
            }
        }

        return results;
    }

    private TrainerActionResult RestoreOriginal(
        IApprovedMemoryTarget target,
        TrainerFeature feature,
        ReferenceKey key,
        long address,
        ScanValue before)
    {
        if (!_originalValues.TryGetValue(key, out var original))
        {
            return new TrainerActionResult(false, $"{feature.Name}: der findes ingen oprindelig værdi i denne session at gendanne.", before);
        }

        if (!target.TryWrite(address, original, out var error))
        {
            return new TrainerActionResult(false, error, before);
        }

        RemoveFreezesFor(key);
        _originalValues.Remove(key);
        return new TrainerActionResult(true, $"{feature.Name}: oprindelig værdi gendannet.", before, original);
    }

    private void RemoveFreezesFor(ReferenceKey key)
    {
        foreach (var (featureId, active) in _frozenValues.Where(pair => pair.Value.Key == key).ToArray())
        {
            _frozenValues.Remove(featureId);
        }
    }
}
