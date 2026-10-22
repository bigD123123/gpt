using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrainerStudio.Core;

namespace TrainerStudio.App;

public partial class MainWindow : Window
{
    private readonly OwnedTestTarget _target = new();
    private readonly MemoryScanner _scanner = new();
    private readonly TrainerRuntime _runtime = new();
    private readonly ProfileStore _store;
    private readonly AuditLogWriter _auditLog;
    private readonly DispatcherTimer _freezeTimer;
    private TrainerProfile _profile;
    private TestGameWindow? _testGameWindow;
    private bool _updatingRuntimeControl;

    public MainWindow()
    {
        InitializeComponent();
        Hits = [];
        Features = [];
        SavedProfiles = [];
        DataContext = this;

        _profile = TrainerProfile.Create("Mit eget testspil", _target.Identity, "Første lokale Trainer Studio-profil.");
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrainerStudio");
        _store = new ProfileStore(Path.Combine(localRoot, "Profiles"));
        _auditLog = new AuditLogWriter(Path.Combine(localRoot, "events.jsonl"));
        ProfileNameBox.Text = _profile.Name;
        ProfileNotesBox.Text = _profile.Notes;

        DataTypeBox.SelectionChanged += DataTypeBox_SelectionChanged;
        ConditionBox.SelectionChanged += ConditionBox_SelectionChanged;
        ControlBox.SelectionChanged += ControlBox_SelectionChanged;
        OperationBox.SelectionChanged += OperationBox_SelectionChanged;
        UpdateSearchValueAvailability();
        UpdateBuilderControlRules();
        RefreshSavedProfiles();

        _freezeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _freezeTimer.Tick += FreezeTimer_Tick;
        _freezeTimer.Start();
        UpdateTargetState();
        ConfigureSelectedFeature();
        SetStatus("Klar. Denne første version arbejder kun med det indbyggede, ejede testspil. Esc stopper alle aktive fastholdelser.");
    }

    public ObservableCollection<HitRow> Hits { get; }

    public ObservableCollection<TrainerFeature> Features { get; }

    public ObservableCollection<StoredProfileItem> SavedProfiles { get; }

    protected override void OnClosed(EventArgs e)
    {
        _freezeTimer.Stop();
        _runtime.StopAll();
        base.OnClosed(e);
    }

    private void ResetTarget_Click(object sender, RoutedEventArgs e)
    {
        var stopped = _runtime.StopAll();
        _target.Reset();
        _scanner.Clear();
        Hits.Clear();
        UpdateTargetState();
        ConfigureSelectedFeature();
        ScanStatusText.Text = "Søgningen er ryddet, fordi testspillet blev nulstillet.";
        WriteAudit("target_reset", true, "Det ejede testspil blev nulstillet.");
        SetStatus(stopped > 0
            ? $"Testspillet er nulstillet, og {stopped} aktiv(e) fastholdelse(r) blev stoppet. Start en ny første søgning."
            : "Testspillet er nulstillet. Start en ny første søgning.");
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => StopAll();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            StopAll();
            e.Handled = true;
        }
    }

    private void ValidateProfile_Click(object sender, RoutedEventArgs e)
    {
        SyncProfile();
        var result = ProfileValidator.Validate(_profile, _target);
        WriteAudit("profile_validation", result.IsReady, string.Join(" ", result.Messages));
        TrainerStatusText.Text = string.Join(Environment.NewLine, result.Messages);
        SetStatus($"Profilstatus: {DescribeValidationState(result.State)}.");
    }

    private void TopmostCheckBox_Checked(object sender, RoutedEventArgs e) => Topmost = true;

    private void TopmostCheckBox_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

    private void OpenTestGame_Click(object sender, RoutedEventArgs e)
    {
        if (_testGameWindow is { IsVisible: true })
        {
            _testGameWindow.Activate();
            return;
        }

        _testGameWindow = new TestGameWindow(_target, TestGameActionRecorded, StopAll) { Owner = this };
        _testGameWindow.Closed += (_, _) => _testGameWindow = null;
        _testGameWindow.Show();
        SetStatus("Det ejede testspil er åbnet i et separat vindue.");
    }

    private void Collect_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.CollectCurrency, "Valuta steg med 25.");

    private void Spend_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.SpendCurrency, "Valuta faldt med 10.");

    private void Damage_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.TakeDamage, "Liv faldt med 15.");

    private void Heal_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.Heal, "Liv steg med 10.");

    private void Fire_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.Fire, "Ammunition faldt med 1.");

    private void Reload_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.Reload, "Ammunition steg med 5.");

    private void DrainStamina_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.DrainStamina, "Stamina faldt med 20.");

    private void TickTimer_Click(object sender, RoutedEventArgs e) => ApplyTargetAction(_target.TickTimer, "Timeren faldt med 1.");

    private void FirstScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = _scanner.FirstScan(_target, CreateRequest());
            ShowHits();
            ScanStatusText.Text = DescribeSummary(summary);
            WriteAudit("first_scan", true, DescribeSummary(summary));
            SetStatus("Første søgning færdig. Ændr nu værdien i testspillet og vælg en passende næste søgning.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            ShowError(exception.Message);
        }
    }

    private void NextScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = _scanner.NextScan(_target, CreateRequest());
            ShowHits();
            ScanStatusText.Text = DescribeSummary(summary);
            WriteAudit("next_scan", true, DescribeSummary(summary));
            SetStatus(summary.CandidateCount == 1
                ? "Du har én kandidat. Vælg den, og opret derefter en trainerfunktion."
                : "Næste søgning færdig. Fortsæt med at ændre værdien og filtrere resultaterne.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            ShowError(exception.Message);
        }
    }

    private void ClearScan_Click(object sender, RoutedEventArgs e)
    {
        _scanner.Clear();
        Hits.Clear();
        ScanStatusText.Text = "Ingen aktiv søgning.";
        WriteAudit("scan_cleared", true, "Søgesessionen blev ryddet.");
        SetStatus("Søgesessionen er ryddet.");
    }

    private void CreateFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner.Candidates.Count != 1)
        {
            ShowError("Begyndertilstand kræver præcis én kandidat, før der oprettes en trainerfunktion.");
            return;
        }

        var row = ResultsGrid.SelectedItem as HitRow ?? Hits.Single();
        if (row.Hit.CurrentValue.Type != ScanDataType.Int32)
        {
            ShowError("Det indbyggede testspil kan kun skrive Int32-felter i v0.1. Andre typer kan undersøges, men ikke bruges som trainerfunktioner endnu.");
            return;
        }

        try
        {
            var control = SelectedControl();
            var feature = TrainerBuilder.Create(
                row.Hit,
                _target,
                FeatureNameBox.Text,
                SelectedOperation(),
                control,
                FeatureValueBox.Text,
                category: CategoryBox.Text,
                description: $"Oprettet fra søgning ved {row.Hit.AddressText}.",
                hotkey: HotkeyBox.Text,
                minimum: control == TrainerControlKind.Slider ? SliderMinimumBox.Text : null,
                maximum: control == TrainerControlKind.Slider ? SliderMaximumBox.Text : null,
                step: control == TrainerControlKind.Slider ? SliderStepBox.Text : null);
            Features.Add(feature);
            FeaturesList.SelectedItem = feature;
            SyncProfile();
            WriteAudit("feature_created", true, $"Funktionen '{feature.Name}' blev oprettet fra {row.Hit.AddressText}.", feature);
            TrainerStatusText.Text = $"{feature.Name} er føjet til din lokale trainer. Test den og gem derefter profilen.";
            SetStatus("Trainerfunktion oprettet. Den er deklarative data, ikke et script.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            ShowError(exception.Message);
        }
    }

    private void ActivateFeature_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeature() is not { } feature)
        {
            ShowError("Vælg en trainerfunktion først.");
            return;
        }

        ActivateFeature(feature);
    }

    private void DeactivateFeature_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeature() is not { } feature)
        {
            ShowError("Vælg en trainerfunktion først.");
            return;
        }

        DeactivateFeature(feature);
    }

    private void FeaturesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ConfigureSelectedFeature();

    private void RuntimeToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!_updatingRuntimeControl && SelectedFeature() is { Control: TrainerControlKind.Toggle } feature)
        {
            ActivateFeature(feature);
        }
    }

    private void RuntimeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_updatingRuntimeControl && SelectedFeature() is { Control: TrainerControlKind.Toggle } feature)
        {
            DeactivateFeature(feature);
        }
    }

    private void RuntimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingRuntimeControl || SelectedFeature() is not { Control: TrainerControlKind.Slider } feature)
        {
            return;
        }

        var value = e.NewValue.ToString("G17", CultureInfo.InvariantCulture);
        RuntimeValueBox.Text = value;
        RuntimeSliderValueText.Text = $"Valgt værdi: {value}";
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfile();
            _store.Save(_profile);
            RefreshSavedProfiles(_profile.Id);
            WriteAudit("profile_saved", true, $"Profilen '{_profile.Name}' blev gemt.");
            TrainerStatusText.Text = $"Profilen er gemt lokalt: {_store.GetPath(_profile.Id)}";
            SetStatus("Trainerprofilen er gemt. Ved næste start skal den valideres mod mål og version.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            ShowError(exception.Message);
        }
    }

    private void LoadSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfilesBox.SelectedItem is not StoredProfileItem item)
        {
            ShowError("Vælg en gemt profil først.");
            return;
        }

        try
        {
            _runtime.StopAll();
            _profile = _store.Load(item.Path);
            Features.Clear();
            foreach (var feature in _profile.Features)
            {
                Features.Add(feature);
            }

            ProfileNameBox.Text = _profile.Name;
            ProfileNotesBox.Text = _profile.Notes;
            ConfigureSelectedFeature();
            var validation = ProfileValidator.Validate(_profile, _target);
            WriteAudit("profile_loaded", validation.IsReady, string.Join(" ", validation.Messages));
            TrainerStatusText.Text = string.Join(Environment.NewLine, validation.Messages);
            SetStatus($"Profil indlæst. Status: {DescribeValidationState(validation.State)}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            ShowError(exception.Message);
        }
    }

    private void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        RefreshSavedProfiles();
        SetStatus("Listen over lokale profiler er opdateret.");
    }

    private void DataTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDataType() == ScanDataType.Boolean && SearchValueBox.IsEnabled)
        {
            SearchValueBox.Text = "true";
        }
    }

    private void ConditionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSearchValueAvailability();

    private void ControlBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBuilderControlRules();

    private void OperationBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBuilderControlRules();

    private void FreezeTimer_Tick(object? sender, EventArgs e)
    {
        var results = _runtime.Tick(_target);
        foreach (var result in results.Where(result => !result.Succeeded ||
                     (result.After is not null && result.Before is not null && !result.Before.Value.NumericallyEquals(result.After.Value))))
        {
            WriteAudit("freeze_tick", result.Succeeded, result.Message, null, result.Before, result.After);
        }

        if (results.Any(result => result.After is not null && result.Before is not null && !result.Before.Value.NumericallyEquals(result.After.Value)))
        {
            UpdateTargetState();
            ConfigureSelectedFeature();
        }
    }

    private void ActivateFeature(TrainerFeature feature)
    {
        var overrideValue = feature.Control is TrainerControlKind.Slider or TrainerControlKind.NumberInput
            ? RuntimeValueBox.Text
            : null;
        var actionDescription = feature.Operation == TrainerOperation.RestoreOriginal
            ? "gendanne den oprindelige sessionsværdi"
            : $"udføre '{feature.Name}'";
        if (!Confirm($"Vil du {actionDescription} i det ejede testspil?"))
        {
            ConfigureSelectedFeature();
            return;
        }

        var result = _runtime.Activate(_target, feature, userConfirmed: true, overrideValue);
        HandleRuntimeResult(result);
    }

    private void DeactivateFeature(TrainerFeature feature)
    {
        if (!Confirm($"Vil du deaktivere '{feature.Name}' og gendanne den oprindelige sessionsværdi, hvis den findes?"))
        {
            ConfigureSelectedFeature();
            return;
        }

        var result = _runtime.Deactivate(_target, feature, userConfirmed: true);
        HandleRuntimeResult(result);
    }

    private void HandleRuntimeResult(TrainerActionResult result)
    {
        UpdateTargetState();
        WriteAudit("trainer_action", result.Succeeded, result.Message, SelectedFeature(), result.Before, result.After);
        TrainerStatusText.Text = result.Message;
        ConfigureSelectedFeature();
        SetStatus(result.Succeeded ? "Trainerhandlingen blev udført og vist." : result.Message);
    }

    private ScanRequest CreateRequest()
    {
        var type = SelectedDataType();
        var condition = SelectedCondition();
        return new ScanRequest(
            type,
            condition,
            condition == ScanCondition.ExactValue ? ScanValue.Parse(type, SearchValueBox.Text) : null);
    }

    private void ApplyTargetAction(Action action, string message)
    {
        action();
        UpdateTargetState();
        WriteAudit("test_action", true, message);
        SetStatus(message + " Vælg nu den scanregel, der beskriver ændringen.");
    }

    private void TestGameActionRecorded(bool targetWasReset, string message)
    {
        if (targetWasReset)
        {
            _runtime.StopAll();
            _scanner.Clear();
            Hits.Clear();
            ScanStatusText.Text = "Søgningen blev ryddet, fordi testspillet blev nulstillet.";
            ConfigureSelectedFeature();
        }

        UpdateTargetState();
        WriteAudit("test_action", true, message);
        SetStatus(message + " Vælg nu den scanregel, der beskriver ændringen.");
    }

    private void StopAll()
    {
        var stopped = _runtime.StopAll();
        ConfigureSelectedFeature();
        var message = stopped > 0
            ? $"{stopped} aktiv(e) fastholdelse(r) er stoppet. Ingen værdier blev skrevet under stop."
            : "Ingen aktiv fastholdelse var i gang.";
        WriteAudit("stop_all", true, message);
        TrainerStatusText.Text = message;
        SetStatus(message);
    }

    private void UpdateTargetState()
    {
        TargetText.Text = $"{_target.Identity.DisplayName} · version {_target.Identity.Version} · {_target.Identity.Architecture} · kun lokal testbuffer";
        GameStateText.Text = $"Valuta:  {_target.Currency}\nLiv:     {_target.Health}\nAmmo:    {_target.Ammo}\nStamina: {_target.Stamina}\nTimer:   {_target.Timer}";
        _testGameWindow?.RefreshState();
    }

    private void UpdateSearchValueAvailability()
    {
        var needsValue = SelectedCondition() == ScanCondition.ExactValue;
        SearchValueBox.IsEnabled = needsValue;
        if (!needsValue)
        {
            SearchValueBox.Text = string.Empty;
        }
    }

    private void UpdateBuilderControlRules()
    {
        if (!IsLoaded && ControlBox is null)
        {
            return;
        }

        var control = SelectedControl();
        SliderOptionsPanel.Visibility = control == TrainerControlKind.Slider ? Visibility.Visible : Visibility.Collapsed;
        switch (control)
        {
            case TrainerControlKind.Toggle:
                OperationBox.SelectedIndex = 2;
                OperationBox.IsEnabled = false;
                BuilderHintText.Text = "Kontakt = fasthold den valgte værdi, indtil den deaktiveres og gendannes.";
                break;
            case TrainerControlKind.Slider:
                OperationBox.SelectedIndex = 0;
                OperationBox.IsEnabled = false;
                BuilderHintText.Text = "Slider = indstil en værdi inden for minimum, maksimum og trin.";
                break;
            case TrainerControlKind.NumberInput:
                if (SelectedOperation() is not (TrainerOperation.SetValue or TrainerOperation.AddValue))
                {
                    OperationBox.SelectedIndex = 0;
                }

                OperationBox.IsEnabled = true;
                BuilderHintText.Text = "Talindtastning = indstil eller tilføj en værdi, som du skriver ved brug.";
                break;
            default:
                if (SelectedOperation() == TrainerOperation.FreezeValue)
                {
                    OperationBox.SelectedIndex = 0;
                }

                OperationBox.IsEnabled = true;
                BuilderHintText.Text = "Knap = udfør en synlig engangshandling, fx indstil, tilføj eller gendan.";
                break;
        }

        var restores = SelectedOperation() == TrainerOperation.RestoreOriginal;
        FeatureValueBox.IsEnabled = !restores;
        if (restores)
        {
            FeatureValueBox.Text = string.Empty;
        }
    }

    private void ConfigureSelectedFeature()
    {
        _updatingRuntimeControl = true;
        try
        {
            if (SelectedFeature() is not { } feature)
            {
                RuntimeFeatureText.Text = "Vælg en funktion for at teste den.";
                RuntimeValueBox.Visibility = Visibility.Collapsed;
                RuntimeSlider.Visibility = Visibility.Collapsed;
                RuntimeSliderValueText.Visibility = Visibility.Collapsed;
                RuntimeToggle.Visibility = Visibility.Collapsed;
                RuntimeApplyButton.IsEnabled = false;
                RuntimeDeactivateButton.Visibility = Visibility.Collapsed;
                return;
            }

            var isToggle = feature.Control == TrainerControlKind.Toggle;
            var isSlider = feature.Control == TrainerControlKind.Slider;
            var acceptsRuntimeValue = feature.Control is TrainerControlKind.NumberInput or TrainerControlKind.Slider;
            var active = _runtime.IsFreezeActive(feature);
            RuntimeFeatureText.Text = $"{feature.Name} · {DescribeFeature(feature)} · {(active ? "AKTIV" : "klar")}";
            RuntimeValueBox.Text = feature.Value;
            RuntimeValueBox.IsReadOnly = !acceptsRuntimeValue;
            RuntimeValueBox.Visibility = isToggle || feature.Operation == TrainerOperation.RestoreOriginal ? Visibility.Collapsed : Visibility.Visible;
            RuntimeApplyButton.IsEnabled = !isToggle;
            RuntimeApplyButton.Visibility = isToggle ? Visibility.Collapsed : Visibility.Visible;
            RuntimeApplyButton.Content = feature.Operation == TrainerOperation.RestoreOriginal ? "Gendan oprindelig" : "Udfør valgt";
            RuntimeDeactivateButton.Visibility = isToggle ? Visibility.Visible : Visibility.Collapsed;
            RuntimeToggle.Visibility = isToggle ? Visibility.Visible : Visibility.Collapsed;
            RuntimeToggle.IsChecked = active;
            RuntimeSlider.Visibility = isSlider ? Visibility.Visible : Visibility.Collapsed;
            RuntimeSliderValueText.Visibility = isSlider ? Visibility.Visible : Visibility.Collapsed;

            if (isSlider)
            {
                ConfigureSlider(feature);
            }
        }
        finally
        {
            _updatingRuntimeControl = false;
        }
    }

    private void ConfigureSlider(TrainerFeature feature)
    {
        try
        {
            RuntimeSlider.Minimum = ScanValue.Parse(feature.Reference.DataType, feature.Minimum!).AsDouble();
            RuntimeSlider.Maximum = ScanValue.Parse(feature.Reference.DataType, feature.Maximum!).AsDouble();
            RuntimeSlider.TickFrequency = ScanValue.Parse(feature.Reference.DataType, feature.Step!).AsDouble();
            RuntimeSlider.Value = ScanValue.Parse(feature.Reference.DataType, feature.Value).AsDouble();
            RuntimeValueBox.Text = RuntimeSlider.Value.ToString("G17", CultureInfo.InvariantCulture);
            RuntimeSliderValueText.Text = $"Valgt værdi: {RuntimeValueBox.Text}";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            RuntimeSlider.Visibility = Visibility.Collapsed;
            RuntimeSliderValueText.Text = "Slideren kan ikke vises for denne værdi.";
            TrainerStatusText.Text = exception.Message;
        }
    }

    private void RefreshSavedProfiles(Guid? selectId = null)
    {
        SavedProfiles.Clear();
        foreach (var path in _store.ListPaths())
        {
            try
            {
                var profile = _store.Load(path);
                var item = new StoredProfileItem(path, profile.Id, $"{profile.Name} · {profile.UpdatedUtc.LocalDateTime:g}");
                SavedProfiles.Add(item);
                if (selectId == profile.Id)
                {
                    SavedProfilesBox.SelectedItem = item;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
            {
                SavedProfiles.Add(new StoredProfileItem(path, Guid.Empty, $"[kan ikke indlæse] {Path.GetFileName(path)}"));
            }
        }
    }

    private void ShowHits()
    {
        Hits.Clear();
        foreach (var hit in _scanner.Candidates)
        {
            Hits.Add(new HitRow(hit));
        }

        if (Hits.Count == 1)
        {
            ResultsGrid.SelectedItem = Hits[0];
        }
    }

    private void SyncProfile()
    {
        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Uden navn" : ProfileNameBox.Text.Trim();
        _profile = _profile with
        {
            Name = name,
            Notes = ProfileNotesBox.Text.Trim(),
            Features = Features.ToArray(),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    private static string DescribeSummary(ScanSummary summary) =>
        $"{summary.CandidateCount:N0} kandidater efter {summary.ScannedValueCount:N0} kontroller ({summary.Elapsed.TotalMilliseconds:N0} ms)." +
        (summary.WasCapped ? " Kandidatgrænsen blev nået." : string.Empty);

    private static string DescribeValidationState(ProfileValidationState state) => state switch
    {
        ProfileValidationState.Ready => "klar",
        ProfileValidationState.NeedsReview => "kræver gennemgang",
        ProfileValidationState.NeedsRepair => "kræver reparation",
        ProfileValidationState.NotAttached => "intet mål valgt",
        _ => state.ToString()
    };

    private static string DescribeFeature(TrainerFeature feature) =>
        $"{feature.Control} · {feature.Operation}" +
        (string.IsNullOrWhiteSpace(feature.Hotkey) ? string.Empty : $" · gemt genvej: {feature.Hotkey}");

    private ScanDataType SelectedDataType() => DataTypeBox.SelectedIndex switch
    {
        0 => ScanDataType.Int8,
        1 => ScanDataType.UInt8,
        2 => ScanDataType.Int16,
        3 => ScanDataType.UInt16,
        4 => ScanDataType.Int32,
        5 => ScanDataType.UInt32,
        6 => ScanDataType.Int64,
        7 => ScanDataType.UInt64,
        8 => ScanDataType.Float,
        9 => ScanDataType.Double,
        10 => ScanDataType.Boolean,
        _ => throw new InvalidOperationException("Vælg en datatype.")
    };

    private ScanCondition SelectedCondition() => ConditionBox.SelectedIndex switch
    {
        0 => ScanCondition.ExactValue,
        1 => ScanCondition.UnknownInitialValue,
        2 => ScanCondition.Increased,
        3 => ScanCondition.Decreased,
        4 => ScanCondition.Changed,
        5 => ScanCondition.Unchanged,
        _ => throw new InvalidOperationException("Vælg en sammenligning.")
    };

    private TrainerControlKind SelectedControl() => ControlBox.SelectedIndex switch
    {
        0 => TrainerControlKind.Button,
        1 => TrainerControlKind.Toggle,
        2 => TrainerControlKind.Slider,
        3 => TrainerControlKind.NumberInput,
        _ => throw new InvalidOperationException("Vælg en kontroltype.")
    };

    private TrainerOperation SelectedOperation() => OperationBox.SelectedIndex switch
    {
        0 => TrainerOperation.SetValue,
        1 => TrainerOperation.AddValue,
        2 => TrainerOperation.FreezeValue,
        3 => TrainerOperation.RestoreOriginal,
        _ => throw new InvalidOperationException("Vælg en trainerhandling.")
    };

    private TrainerFeature? SelectedFeature() => FeaturesList.SelectedItem as TrainerFeature;

    private bool Confirm(string question) => MessageBox.Show(question, "Trainer Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void ShowError(string message)
    {
        WriteAudit("error", false, message);
        SetStatus(message);
        MessageBox.Show(message, "Trainer Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void WriteAudit(
        string eventType,
        bool succeeded,
        string message,
        TrainerFeature? feature = null,
        ScanValue? before = null,
        ScanValue? after = null)
    {
        try
        {
            _auditLog.Append(new TrainerAuditEvent(
                DateTimeOffset.UtcNow,
                eventType,
                succeeded,
                _target.Identity.TargetId,
                feature?.Id.ToString("D"),
                message,
                before?.Display,
                after?.Display));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The application must remain stoppable even if a local log drive
            // is temporarily unavailable. The visible status remains intact.
        }
    }

    public sealed record StoredProfileItem(string Path, Guid Id, string Display);

    public sealed record HitRow(ScanHit Hit)
    {
        public string AddressText => Hit.AddressText;

        public string Current => Hit.CurrentValue.Display;

        public string Change => Hit.ChangeText;

        public string Region => Hit.RegionName;
    }
}
