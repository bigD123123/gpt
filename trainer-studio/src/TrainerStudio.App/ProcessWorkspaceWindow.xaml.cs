using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrainerStudio.Core;
using TrainerStudio.Windows;

namespace TrainerStudio.App;

public partial class ProcessWorkspaceWindow : Window
{
    private readonly MemoryScanner _scanner = new();
    private readonly TrainerRuntime _runtime = new();
    private readonly ProfileStore _store;
    private readonly AuditLogWriter _audit;
    private readonly DispatcherTimer _heartbeat;
    private readonly DispatcherTimer _processTimer;
    private readonly List<TrainerFeature> _features = [];
    private WindowsProcessTarget? _target;
    private TrainerProfile? _profile;
    private TrainerPanelWindow? _trainerWindow;
    private MemoryReference? _builderReference;
    private Guid? _editingFeatureId;
    private CancellationTokenSource? _scanCancellation;
    private bool _initializing = true;
    private bool _updatingRules;
    private bool _updatingWrites;
    private bool _busy;
    private bool _refreshingProcesses;
    private bool _closing;

    public ObservableCollection<WindowsProcessDescriptor> Processes { get; } = [];
    public ObservableCollection<WorkspaceHitRow> Hits { get; } = [];
    public ObservableCollection<WorkspacePointerRow> PointerRows { get; } = [];
    public ObservableCollection<WorkspaceFeatureRow> FeatureRows { get; } = [];
    public ObservableCollection<WorkspaceProfileRow> Profiles { get; } = [];

    public ProcessWorkspaceWindow()
    {
        InitializeComponent();
        DataContext = this;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrainerStudio");
        _store = new ProfileStore(Path.Combine(root, "Profiles"));
        _audit = new AuditLogWriter(Path.Combine(root, "events.jsonl"));
        _initializing = false;
        ApplyBuilderRules();
        UpdateScanHint();
        RefreshProfiles();
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _heartbeat.Tick += Heartbeat_Tick;
        _heartbeat.Start();
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _processTimer.Tick += async (_, _) => await RefreshProcessesAsync();
        _processTimer.Start();
        Loaded += async (_, _) => await RefreshProcessesAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _heartbeat.Stop();
        _processTimer.Stop();
        _scanCancellation?.Cancel();
        _runtime.StopAll();
        _target?.DisableWriting();
        _target?.Dispose();
        base.OnClosed(e);
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        if (_closing || _busy || _refreshingProcesses) return;
        _refreshingProcesses = true;
        var selected = ProcessesBox.SelectedItem as WindowsProcessDescriptor;
        try
        {
            var processes = await Task.Run(WindowsProcessCatalog.ListProcesses);
            if (_closing) return;
            Processes.Clear();
            foreach (var process in processes) Processes.Add(process);
            ProcessesBox.SelectedItem = processes.FirstOrDefault(item => item.ProcessId == selected?.ProcessId && item.StartTimeUtc == selected.StartTimeUtc);
            UpdateRecognition();
        }
        catch (Exception exception) { ReportError("Proceslisten kunne ikke opdateres", exception); }
        finally { _refreshingProcesses = false; }
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ProcessesBox.SelectedItem is not WindowsProcessDescriptor selected)
        {
            SetStatus("Vælg først en proces i listen.");
            return;
        }
        if (!Confirm($"Vil du forbinde til {selected.Name} (PID {selected.ProcessId})?\n\nBekræft, at du ejer eller har tilladelse til at undersøge programmet. Forbindelsen starter kun med læseadgang.")) return;
        if (_features.Count > 0 && !Confirm("Den nuværende trainer bliver fjernet fra editoren ved processkift. Fortsæt kun, hvis du har gemt den ønskede profil. Vil du fortsætte?")) return;
        SetBusy(true);
        try
        {
            var target = await Task.Run(() => WindowsProcessTarget.Attach(selected));
            if (_closing) { target.Dispose(); return; }
            DisconnectCore();
            _target = target;
            ApplyProfile(TrainerProfile.Create($"{selected.Name} – min trainer", target.Identity));
            TargetInfoText.Text = $"{selected.Name} · PID {selected.ProcessId} · {target.Identity.Architecture} · version {target.Identity.Version} · {selected.EngineHint}\n{selected.ExecutablePath}";
            ClearScan();
            ProfilesBox.SelectedItem = Profiles.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Profile.Target.TargetId, target.Identity.TargetId));
            UpdateRecognition();
            Log("process_attached", true, $"Tilknyttet PID {selected.ProcessId} i læsetilstand. Build: {target.Identity.BuildFingerprint}.");
            SetStatus("Proces tilknyttet. Søg efter en værdi; slå kun værdiændringer til, når du vil afprøve en funktion.");
        }
        catch (Exception exception) { ReportError("Tilknytning mislykkedes", exception); }
        finally { SetBusy(false); }
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DisconnectCore();
        ClearScan();
        SetBusy(false);
        TargetInfoText.Text = "Ingen proces tilknyttet. Din trainer kan stadig gemmes og eksporteres.";
        RefreshFeatures();
        Log("process_detached", true, "Procesforbindelsen blev lukket; alle fastholdelser er stoppet.");
        SetStatus("Forbindelsen er lukket. Ingen værdier blev skrevet under afbrydelsen.");
    }

    private void DisconnectCore()
    {
        _runtime.StopAll();
        _target?.DisableWriting();
        _target?.Dispose();
        _target = null;
        _updatingWrites = true;
        WritesCheckBox.IsChecked = false;
        _updatingWrites = false;
        _builderReference = null;
        PointerRows.Clear();
    }

    private void Writes_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingWrites) return;
        var target = _target;
        if (_busy || target is null || !Confirm($"Vil du tillade direkte værdiændringer i {target.Process.Name} (PID {target.Process.ProcessId})?\n\nHver trainerhandling skal stadig godkendes. Esc stopper fastholdelser og slår skriveadgang fra."))
        {
            WritesCheckBox.IsChecked = false;
            return;
        }
        // MessageBox pumps timers. The process may exit while approval is open.
        if (!ReferenceEquals(_target, target) || !target.IsAlive)
        {
            WritesCheckBox.IsChecked = false;
            SetStatus("Processen blev lukket eller skiftet under godkendelsen. Forbind på ny.");
            return;
        }
        if (!target.TryEnableWriting(out var error))
        {
            WritesCheckBox.IsChecked = false;
            Log("writes_enabled", false, error);
            SetStatus(error);
            return;
        }
        Log("writes_enabled", true, "Skriveadgang blev slået til af brugeren.");
        SetStatus("Værdiændringer er tilladt i den valgte proces. Trainerhandlinger kræver fortsat godkendelse.");
    }

    private void Writes_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingWrites) return;
        var stopped = _runtime.StopAll();
        _target?.DisableWriting();
        RefreshFeatures();
        Log("writes_disabled", true, $"Skriveadgang slået fra; {stopped} fastholdelser stoppet uden en ny skrivning.");
    }

    private void Topmost_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Topmost_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private async void FirstScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(first: true);
    private async void NextScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(first: false);

    private async Task ScanAsync(bool first)
    {
        if (_busy) return;
        try
        {
            var target = RequireTarget();
            target.ScanOptions = CreateSnapshotOptions();
            var type = (ScanDataType)DataTypeBox.SelectedIndex;
            var condition = (ScanCondition)ConditionBox.SelectedIndex;
            var request = new ScanRequest(type, condition,
                condition == ScanCondition.ExactValue ? ScanValue.Parse(type, SearchValueBox.Text) : null,
                MaxCandidates: 100_000,
                Alignment: EveryByteCheckBox.IsChecked == true ? ScanAlignment.EveryByte : ScanAlignment.Native);
            _scanCancellation = new CancellationTokenSource();
            SetBusy(true);
            var progress = new Progress<ScanProgress>(value =>
            {
                if (!_closing && _busy) ScanStatusText.Text = $"Undersøger hukommelse: {value.ScannedValueCount:N0} værdier · {value.CandidateCount:N0} kandidater · {value.Elapsed.TotalSeconds:F1} s. Du kan annullere.";
            });
            var token = _scanCancellation.Token;
            var result = await Task.Run(() => first
                ? _scanner.FirstScan(target, request, token, progress)
                : _scanner.NextScan(target, request, token, progress), token);
            if (_closing) return;
            SetBusy(false);
            ShowHits();
            var limitation = result.WasCapped || result.WasSnapshotTruncated
                ? " AFGRÆNSET SØGNING: kandidat- eller læsebudgettet er nået. Ét tilbageværende fund beviser kun entydighed inden for den undersøgte del."
                : string.Empty;
            ScanStatusText.Text = $"{result.CandidateCount:N0} kandidater · {result.ScannedValueCount:N0} kontroller · {result.Elapsed.TotalSeconds:F2} s. Viser højst 2.000 resultater. {result.SkippedReadCount:N0} blokke kunne ikke læses.{limitation}";
            Log(first ? "first_scan" : "next_scan", true, ScanStatusText.Text);
            SetStatus(first ? "Første søgning færdig. Ændr værdien i spillet, og brug derefter Næste søgning." : "Næste søgning færdig. Vælg og kontrollér en kandidat før du opretter en funktion.");
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Søgningen blev annulleret. Den tidligere færdige søgesession er bevaret.";
            Log("scan_cancelled", true, ScanStatusText.Text);
            SetStatus(ScanStatusText.Text);
        }
        catch (Exception exception) { ReportError("Søgningen mislykkedes", exception); }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            if (!_closing) SetBusy(false);
        }
    }

    private MemorySnapshotOptions CreateSnapshotOptions()
    {
        if (!int.TryParse(MaxMemoryBox.Text, out var mib) || mib is < 1 or > 2048)
            throw new ArgumentException("Læsning skal være mellem 1 og 2.048 MiB. Start med 256 MiB, og øg kun ved behov.");
        var start = ParseHexAddress(StartAddressBox.Text, 0);
        var end = ParseHexAddress(EndAddressBox.Text, long.MaxValue);
        if (end <= start) throw new ArgumentException("Slutadressen skal være større end startadressen.");
        return new MemorySnapshotOptions(MaxBytes: (long)mib * 1024 * 1024, WritableOnly: WritableOnlyCheckBox.IsChecked == true,
            IncludeMapped: MappedCheckBox.IsChecked == true, StartAddress: start, EndAddressExclusive: end);
    }

    private static long ParseHexAddress(string text, long fallback)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0) return fallback;
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized[2..];
        if (!long.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result) || result < 0)
            throw new ArgumentException("Adressefeltet skal være et positivt hexadecimalt tal, fx 0x7FF00000.");
        return result;
    }

    private void ShowHits()
    {
        Hits.Clear();
        foreach (var hit in _scanner.Candidates.Take(2000)) Hits.Add(new WorkspaceHitRow(hit));
        if (Hits.Count == 1) ResultsGrid.SelectedIndex = 0;
    }

    private void ClearScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearScan();
        SetBusy(false);
    }
    private void ClearScan()
    {
        _scanner.Clear();
        Hits.Clear();
        _builderReference = null;
        PointerRows.Clear();
        ScanStatusText.Text = "Ingen aktiv søgning.";
        ReferenceText.Text = "Vælg et søgeresultat for at oprette en funktion.";
        PointerStatusText.Text = "Ingen pointeranalyse endnu.";
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private void Condition_Changed(object sender, SelectionChangedEventArgs e) { if (!_initializing) UpdateScanHint(); }
    private void UpdateScanHint()
    {
        SearchValueBox.IsEnabled = ConditionBox.SelectedIndex == 0;
        ScanHintText.Text = ConditionBox.SelectedIndex switch
        {
            0 => "Præcis værdi: skriv tallet, som du ser i spillet. Start med Int32 for heltal eller Float for decimaler.",
            1 => "Ukendt startværdi: gem første observation uden at kende tallet. Ændr noget i spillet, og vælg derefter Steget/Faldet/Ændret.",
            2 => "Steget: behold kun værdier, der er større end ved sidste færdige søgning.",
            3 => "Faldet: behold kun værdier, der er mindre end ved sidste færdige søgning.",
            4 => "Ændret: behold kun værdier, der har ændret sig siden sidste søgning.",
            _ => "Uændret: behold kun værdier, som stadig er de samme som ved sidste søgning."
        };
    }

    private void Result_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _busy || ResultsGrid.SelectedItem is not WorkspaceHitRow row || _target is null) return;
        try
        {
            // Create the reference through the same builder as a real feature;
            // heap addresses must never be disguised as main-module offsets.
            _target.RefreshModules();
            var preview = TrainerBuilder.Create(row.Hit, _target, "Reference", TrainerOperation.SetValue, TrainerControlKind.Button, row.Hit.CurrentValue.Display);
            SetBuilderReference(preview.Reference);
            FeatureValueBox.Text = row.Current;
        }
        catch (Exception exception) { ReportError("Kandidaten kan ikke bruges", exception); }
    }

    private void ReadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultsGrid.SelectedItem is not WorkspaceHitRow row) return;
        try
        {
            if (!RequireTarget().TryRead(row.Hit.Address, row.Hit.CurrentValue.Type, out var value))
                throw new InvalidOperationException("Værdien kan ikke længere læses. Processen eller objektet kan være ændret.");
            SetStatus($"{row.AddressText}: nu {value.Display} ({value.Type}). Dette kontrollerer læsbarhed; afprøv virkningen i spillet.");
        }
        catch (Exception exception) { ReportError("Læsning mislykkedes", exception); }
    }

    private async void PointerScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultsGrid.SelectedItem is not WorkspaceHitRow selected)
        {
            SetStatus("Vælg først en værdi i søgeresultaterne.");
            return;
        }
        try
        {
            var target = RequireTarget();
            target.ScanOptions = CreateSnapshotOptions() with { PrioritizeModules = true };
            if (!int.TryParse(PointerDepthBox.Text, out var depth) || depth is < 1 or > 5)
                throw new ArgumentException("Pointerdybden skal være mellem 1 og 5.");
            if (!long.TryParse(PointerOffsetBox.Text, out var offset) || offset is < 0 or > 1_048_576)
                throw new ArgumentException("Maksimalt pointer-offset skal være mellem 0 og 1.048.576 bytes.");
            var options = new PointerScanOptions(MaxDepth: depth, MaxOffset: offset);
            _scanCancellation = new CancellationTokenSource();
            var token = _scanCancellation.Token;
            SetBusy(true);
            PointerStatusText.Text = "Søger modulforankrede stier. Dette kan tage tid; STOP ALLE annullerer også denne søgning.";
            var result = await Task.Run(() =>
            {
                target.RefreshModules();
                return new PointerScanner().Scan(target, selected.Hit.Address, selected.Hit.CurrentValue.Type, options, token);
            }, token);
            if (_closing) return;
            PointerRows.Clear();
            foreach (var reference in result.References) PointerRows.Add(new WorkspacePointerRow(reference, "Fundet; test efter genstart"));
            if (PointerRows.Count > 0) PointersGrid.SelectedIndex = 0;
            PointerStatusText.Text = $"{result.References.Count:N0} stier · {result.ScannedPointerCount:N0} pointerværdier · {result.Elapsed.TotalSeconds:F2} s." +
                (result.WasCapped ? " AFGRÆNSET af læse- eller arbejdsbudgettet; flere stier kan eksistere." : "") +
                " Kontrollér flere spilsessioner, før du stoler på en sti.";
            Log("pointer_scan", true, PointerStatusText.Text);
            SetStatus("Pointeranalyse færdig. Vælg en sti og brug den i Builder, hvis du vil afprøve den.");
        }
        catch (OperationCanceledException) { SetStatus("Pointeranalysen blev annulleret; tidligere stier er bevaret."); }
        catch (Exception exception) { ReportError("Pointeranalysen mislykkedes", exception); }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            if (!_closing) SetBusy(false);
        }
    }

    private async void PointerRescan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultsGrid.SelectedItem is not WorkspaceHitRow selected || PointerRows.Count == 0)
        {
            SetStatus("Vælg et aktuelt søgeresultat og find først pointerstier.");
            return;
        }
        try
        {
            var target = RequireTarget();
            var previous = PointerRows.Select(row => row.Reference).ToArray();
            _scanCancellation = new CancellationTokenSource();
            var token = _scanCancellation.Token;
            SetBusy(true);
            var remaining = await Task.Run(() => new PointerScanner().Rescan(target, previous, selected.Hit.Address, token), token);
            if (_closing) return;
            PointerRows.Clear();
            foreach (var reference in remaining) PointerRows.Add(new WorkspacePointerRow(reference, "Løser til den valgte adresse nu"));
            PointerStatusText.Text = $"{remaining.Count} af {previous.Length} stier peger stadig på den valgte, læsbare adresse. Det er ikke en garanti for samme betydning i spillet.";
            Log("pointer_rescan", true, PointerStatusText.Text);
            SetStatus(PointerStatusText.Text);
        }
        catch (OperationCanceledException) { SetStatus("Genvalidering blev annulleret."); }
        catch (Exception exception) { ReportError("Genvalidering mislykkedes", exception); }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            if (!_closing) SetBusy(false);
        }
    }

    private void LoadProfilePointers_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PointerRows.Clear();
        foreach (var reference in _features.Where(feature => feature.Reference.Kind == MemoryReferenceKind.PointerPath)
                     .Select(feature => feature.Reference).Take(1000))
            PointerRows.Add(new WorkspacePointerRow(reference, "Gemt sti; afventer genvalidering"));
        SetBusy(false);
        PointerStatusText.Text = $"{PointerRows.Count} gemte stier indlæst. Find værdien i den nye proces, vælg søgeresultatet og tryk Genvalidér fundne stier.";
    }

    private void UsePointer_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PointersGrid.SelectedItem is not WorkspacePointerRow row) return;
        try
        {
            var address = row.Reference.Resolve(RequireTarget());
            if (!_target!.TryRead(address, row.Reference.DataType, out var value)) throw new InvalidOperationException("Pointerstiens slutværdi kan ikke læses.");
            SetBuilderReference(row.Reference);
            FeatureValueBox.Text = value.Display;
            SetStatus("Pointerstien er valgt i Builder. Åbn fanen Søg og opret funktion for at give funktionen et navn.");
        }
        catch (Exception exception) { ReportError("Pointerstien kunne ikke bruges", exception); }
    }

    private void SetBuilderReference(MemoryReference reference)
    {
        _builderReference = reference;
        ReferenceText.Text = DescribeReference(reference) + (reference.Kind == MemoryReferenceKind.SessionAddress
            ? "\nKun denne session: adressen bliver ugyldig efter processens genstart. Find en modulforankret pointersti for mulig genbrug."
            : "\nReferencen bliver genopløst og kontrolleret mod programbuild før brug.");
    }

    private void BuilderRules_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && !_updatingRules) ApplyBuilderRules();
    }

    private void ApplyBuilderRules()
    {
        _updatingRules = true;
        try
        {
            var control = (TrainerControlKind)ControlBox.SelectedIndex;
            SliderOptionsPanel.Visibility = control == TrainerControlKind.Slider ? Visibility.Visible : Visibility.Collapsed;
            OperationBox.IsEnabled = control is TrainerControlKind.Button or TrainerControlKind.NumberInput;
            if (control == TrainerControlKind.Toggle) OperationBox.SelectedIndex = (int)TrainerOperation.FreezeValue;
            else if (control == TrainerControlKind.Slider) OperationBox.SelectedIndex = (int)TrainerOperation.SetValue;
            else if ((TrainerOperation)OperationBox.SelectedIndex == TrainerOperation.FreezeValue ||
                     (control == TrainerControlKind.NumberInput && (TrainerOperation)OperationBox.SelectedIndex == TrainerOperation.RestoreOriginal))
                OperationBox.SelectedIndex = (int)TrainerOperation.SetValue;
            FeatureValueBox.IsEnabled = (TrainerOperation)OperationBox.SelectedIndex != TrainerOperation.RestoreOriginal;
        }
        finally { _updatingRules = false; }
    }

    private TrainerFeature BuildFeature(Guid? replacingFeatureId = null)
    {
        if (_builderReference is null) throw new InvalidOperationException("Vælg først et søgeresultat eller en valideret pointersti.");
        var target = RequireTarget();
        if (_profile is null || !StringComparer.Ordinal.Equals(_profile.Target.TargetId, target.Identity.TargetId))
            throw new InvalidOperationException("Den indlæste profil hører til et andet program. Vælg Ny profil til proces eller indlæs den rigtige profil først.");
        // Editing a name must not silently certify an old offset after an
        // update. A repair must begin with a current scan/reference selection.
        _ = _builderReference.Resolve(target);
        if (!string.IsNullOrWhiteSpace(HotkeyBox.Text))
        {
            KeyGesture gesture;
            try { gesture = new KeyGestureConverter().ConvertFromInvariantString(HotkeyBox.Text.Trim()) as KeyGesture ?? throw new ArgumentException("Ugyldig genvej."); }
            catch (Exception exception) { throw new ArgumentException("Skriv en genvej som F1 eller Ctrl+F2, eller lad feltet være tomt.", exception); }
            if (gesture.Key == Key.Escape) throw new ArgumentException("Esc er reserveret til STOP ALLE.");
            foreach (var existing in _features.Where(item => item.Id != replacingFeatureId && !string.IsNullOrWhiteSpace(item.Hotkey)))
            {
                try
                {
                    if (new KeyGestureConverter().ConvertFromInvariantString(existing.Hotkey!) is KeyGesture other &&
                        other.Key == gesture.Key && other.Modifiers == gesture.Modifiers)
                        throw new InvalidOperationException($"Genvejen bruges allerede af '{existing.Name}'. Vælg en anden genvej.");
                }
                catch (Exception exception) when (exception is NotSupportedException or ArgumentException or FormatException) { }
            }
        }
        var control = (TrainerControlKind)ControlBox.SelectedIndex;
        return TrainerBuilder.CreateFromReference(_builderReference, target, FeatureNameBox.Text,
            (TrainerOperation)OperationBox.SelectedIndex, control, FeatureValueBox.Text,
            category: CategoryBox.Text, description: DescriptionBox.Text, hotkey: HotkeyBox.Text,
            minimum: control == TrainerControlKind.Slider ? SliderMinimumBox.Text : null,
            maximum: control == TrainerControlKind.Slider ? SliderMaximumBox.Text : null,
            step: control == TrainerControlKind.Slider ? SliderStepBox.Text : null);
    }

    private void CreateFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var feature = BuildFeature();
            _features.Add(feature);
            _editingFeatureId = null;
            RefreshFeatures(feature.Id);
            Log("feature_created", true, $"'{feature.Name}': {DescribeReference(feature.Reference)}", feature);
            SetStatus("Funktionen er oprettet. Test den via Min trainer, og gem derefter profilen.");
        }
        catch (Exception exception) { ReportError("Funktionen kunne ikke oprettes", exception); }
    }

    private void UpdateFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (_editingFeatureId is not { } id || _features.FirstOrDefault(item => item.Id == id) is not { } old)
                throw new InvalidOperationException("Vælg en eksisterende funktion under Min trainer og tryk Redigér valgt først.");
            if (_runtime.IsFreezeActive(old)) throw new InvalidOperationException("Deaktivér funktionen, før den redigeres.");
            var updated = BuildFeature(old.Id) with { Id = old.Id };
            _features[_features.IndexOf(old)] = updated;
            RefreshFeatures(updated.Id);
            Log("feature_updated", true, $"'{updated.Name}' blev opdateret. Profilen skal gemmes.", updated);
            SetStatus("Funktionen er opdateret. Test ændringen, og gem profilen.");
        }
        catch (Exception exception) { ReportError("Funktionen kunne ikke opdateres", exception); }
    }

    private void EditFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedFeature() is not { } feature) { SetStatus("Vælg først en trainerfunktion."); return; }
        _editingFeatureId = feature.Id;
        SetBuilderReference(feature.Reference);
        FeatureNameBox.Text = feature.Name;
        ControlBox.SelectedIndex = (int)feature.Control;
        OperationBox.SelectedIndex = (int)feature.Operation;
        FeatureValueBox.Text = feature.Value;
        CategoryBox.Text = feature.Category;
        DescriptionBox.Text = feature.Description;
        HotkeyBox.Text = feature.Hotkey ?? string.Empty;
        SliderMinimumBox.Text = feature.Minimum ?? "0";
        SliderMaximumBox.Text = feature.Maximum ?? "1000";
        SliderStepBox.Text = feature.Step ?? "1";
        SetStatus("Funktionen er indlæst i Builder på første fane. Ret felterne og tryk Opdatér valgt trainerfunktion. En ugyldig reference kan repareres ved at vælge et nyt fund først.");
    }

    private void OpenTrainer_Click(object sender, RoutedEventArgs e)
    {
        if (_trainerWindow is { IsVisible: true }) { _trainerWindow.Activate(); return; }
        _trainerWindow = new TrainerPanelWindow(_features.ToArray(), _runtime.IsFreezeActive, ActivateFeature, DeactivateFeature, StopAll) { Owner = this };
        _trainerWindow.PreviewKeyDown += Window_PreviewKeyDown;
        _trainerWindow.Closed += (_, _) => _trainerWindow = null;
        _trainerWindow.Show();
    }

    private void UseFeature_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeature() is { } feature)
        {
            if (feature.Control is TrainerControlKind.Slider or TrainerControlKind.NumberInput)
            {
                OpenTrainer_Click(sender, e);
                SetStatus("Brug slideren eller talfeltet i Min trainer for at vælge den ønskede værdi.");
            }
            else ActivateFeature(feature, null);
        }
        else SetStatus("Vælg først en trainerfunktion.");
    }

    private void ActivateFeature(TrainerFeature feature, string? runtimeValue)
    {
        if (_busy) { SetStatus("Afslut eller annullér søgningen, før en trainerhandling udføres."); return; }
        try
        {
            var target = RequireTarget();
            if (!target.WritesEnabled) throw new InvalidOperationException("Slå 'Tillad værdiændringer' til for den valgte proces først.");
            var address = feature.Reference.Resolve(target);
            if (!Confirm($"Vil du udføre '{feature.Name}' i {target.Process.Name} (PID {target.Process.ProcessId})?\n\n{feature.Operation} · {feature.Reference.DataType} · adresse 0x{address:X}\nVærdi: {runtimeValue ?? feature.Value}\n\nFastholdelse fortsætter indtil deaktivering eller STOP ALLE.")) return;
            if (!ReferenceEquals(_target, target) || !_features.Contains(feature))
                throw new InvalidOperationException("Proces eller trainerfunktion blev ændret under godkendelsen. Vælg funktionen igen.");
            HandleAction(_runtime.Activate(target, feature, userConfirmed: true, runtimeValue, expectedResolvedAddress: address), feature);
        }
        catch (Exception exception) { ReportError("Trainerhandlingen blev afvist", exception); RefreshFeatures(feature.Id); }
    }

    private void DeactivateFeature_Click(object sender, RoutedEventArgs e) { if (SelectedFeature() is { } feature) DeactivateFeature(feature); }
    private void DeactivateFeature(TrainerFeature feature)
    {
        if (_busy) return;
        try
        {
            var target = RequireTarget();
            if (!Confirm($"Vil du deaktivere '{feature.Name}'? En oprindelig sessionsværdi gendannes kun, hvis den stadig hører til den samme adresse og proces.")) return;
            if (!ReferenceEquals(_target, target) || !_features.Contains(feature))
                throw new InvalidOperationException("Proces eller funktion blev ændret under godkendelsen. Fastholdelserne skal kontrolleres på ny.");
            HandleAction(_runtime.Deactivate(target, feature, userConfirmed: true), feature);
        }
        catch (Exception exception) { ReportError("Deaktivering mislykkedes", exception); }
    }

    private void HandleAction(TrainerActionResult result, TrainerFeature feature)
    {
        Log("trainer_action", result.Succeeded, result.Message, feature, result.Before, result.After);
        RefreshFeatures(feature.Id);
        TrainerStatusText.Text = result.Message;
        SetStatus(result.Message);
    }

    private void CopyFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedFeature() is not { } feature) return;
        var copy = feature with { Id = Guid.NewGuid(), Name = $"{feature.Name} (kopi)", Hotkey = null };
        _features.Insert(_features.IndexOf(feature) + 1, copy);
        RefreshFeatures(copy.Id);
        SetStatus("Kopien er oprettet. Brug Redigér valgt for at ændre den.");
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveFeature(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveFeature(1);
    private void MoveFeature(int direction)
    {
        if (_busy || SelectedFeature() is not { } feature) return;
        var oldIndex = _features.IndexOf(feature);
        var newIndex = oldIndex + direction;
        if (newIndex < 0 || newIndex >= _features.Count) return;
        _features.RemoveAt(oldIndex);
        _features.Insert(newIndex, feature);
        RefreshFeatures(feature.Id);
    }
    private void DeleteFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedFeature() is not { } feature || !Confirm($"Vil du fjerne '{feature.Name}' fra profilen?")) return;
        if (_runtime.IsFreezeActive(feature))
        {
            if (_target is null) StopAll();
            else
            {
                var result = _runtime.Deactivate(_target, feature, userConfirmed: true);
                HandleAction(result, feature);
                if (!result.Succeeded) return;
            }
        }
        _features.Remove(feature);
        RefreshFeatures();
        Log("feature_deleted", true, "Funktionen blev fjernet fra editoren.", feature);
    }

    private TrainerFeature? SelectedFeature() => (FeaturesGrid.SelectedItem as WorkspaceFeatureRow)?.Feature;

    private void RefreshFeatures(Guid? selectedId = null)
    {
        selectedId ??= SelectedFeature()?.Id;
        FeatureRows.Clear();
        foreach (var feature in _features)
        {
            var status = _runtime.IsFreezeActive(feature) ? "AKTIV · " : "";
            if (_target is null) status += "Ingen proces · " + DescribeReference(feature.Reference);
            else
            {
                try
                {
                    var address = feature.Reference.Resolve(_target);
                    status += _target.TryRead(address, feature.Reference.DataType, out var current)
                        ? $"{current.Display} · {DescribeReference(feature.Reference)}"
                        : "Kræver reparation: slutværdien kan ikke læses";
                }
                catch (Exception exception) { status += "Kræver reparation: " + exception.Message; }
            }
            var row = new WorkspaceFeatureRow(feature, status);
            FeatureRows.Add(row);
            if (feature.Id == selectedId) FeaturesGrid.SelectedItem = row;
        }
        _trainerWindow?.Refresh(_features.ToArray());
    }

    private void ApplyProfile(TrainerProfile profile)
    {
        _runtime.StopAll();
        _profile = profile;
        _features.Clear();
        _features.AddRange(profile.Features);
        ProfileNameBox.Text = profile.Name;
        NotesBox.Text = profile.Notes;
        _editingFeatureId = null;
        _builderReference = null;
        RefreshFeatures();
    }

    private TrainerProfile SyncProfile()
    {
        if (_profile is null) throw new InvalidOperationException("Forbind først til en proces eller importér en eksisterende profil.");
        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0) throw new ArgumentException("Profilen skal have et navn.");
        _profile = _profile with { Name = name, Notes = NotesBox.Text.Trim(), Features = _features.ToArray(), UpdatedUtc = DateTimeOffset.UtcNow };
        return _profile;
    }

    private void RefreshProfiles(Guid? selectedId = null)
    {
        selectedId ??= (ProfilesBox.SelectedItem as WorkspaceProfileRow)?.Profile.Id;
        Profiles.Clear();
        try
        {
            foreach (var path in _store.ListPaths())
            {
                try
                {
                    var profile = _store.Load(path);
                    var item = new WorkspaceProfileRow(profile, path);
                    Profiles.Add(item);
                    if (profile.Id == selectedId) ProfilesBox.SelectedItem = item;
                }
                catch (Exception exception)
                {
                    Log("profile_load_error", false, $"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
            UpdateRecognition();
        }
        catch (Exception exception) { ReportError("Profilbiblioteket kunne ikke læses", exception); }
    }

    private void UpdateRecognition()
    {
        var matching = (from process in Processes
                        from profile in Profiles
                        where StringComparer.Ordinal.Equals(process.TargetId, profile.Profile.Target.TargetId)
                        select $"{process.Name} (PID {process.ProcessId}) → {profile.Profile.Name}").Take(5).ToArray();
        RecognitionText.Text = matching.Length == 0 ? "Ingen kørende proces matcher dine gemte profiler endnu."
            : "Kendte profiler fundet: " + string.Join(" · ", matching) + ". Vælg proces og indlæs profil for at fortsætte.";
        if (_target is not null && ProfilesBox.SelectedItem is null)
            ProfilesBox.SelectedItem = Profiles.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Profile.Target.TargetId, _target.Identity.TargetId));
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var target = RequireTarget();
            if (_features.Count > 0 && !Confirm("Vil du starte en ny tom profil? Gem først den nuværende profil, hvis du vil beholde ændringerne.")) return;
            ApplyProfile(TrainerProfile.Create($"{target.Process.Name} – ny profil", target.Identity));
            SetStatus("Ny profil til den tilknyttede proces er oprettet.");
        }
        catch (Exception exception) { ReportError("Ny profil kunne ikke oprettes", exception); }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var profile = SyncProfile();
            if (_target is { IsAlive: true } current && StringComparer.Ordinal.Equals(profile.Target.TargetId, current.Identity.TargetId))
            {
                var allCurrent = profile.Features.All(feature =>
                {
                    try { return current.TryRead(feature.Reference.Resolve(current), feature.Reference.DataType, out _); }
                    catch (Exception) { return false; }
                });
                if (allCurrent) profile = _profile = profile with { Target = current.Identity };
            }
            _store.Save(profile);
            RefreshProfiles(profile.Id);
            Log("profile_saved", true, $"'{profile.Name}' blev gemt med {profile.Features.Count} funktioner.");
            SetStatus("Profilen er gemt. Sessionadresser kan ikke genbruges efter processens genstart; modul- og pointerreferencer genvalideres.");
        }
        catch (Exception exception) { ReportError("Profilen kunne ikke gemmes", exception); }
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ProfilesBox.SelectedItem is not WorkspaceProfileRow selected) { SetStatus("Vælg en gemt profil først."); return; }
        try
        {
            if (_features.Count > 0 && !Confirm("Vil du erstatte den aktuelle trainer i editoren med den gemte profil? Eventuelle aktive fastholdelser stoppes.")) return;
            ApplyProfile(_store.Load(selected.Path));
            DisplayValidation();
            Log("profile_loaded", true, $"Profilen '{_profile!.Name}' blev indlæst; ingen funktioner blev aktiveret.");
        }
        catch (Exception exception) { ReportError("Profilen kunne ikke indlæses", exception); }
    }

    private void ValidateProfile_Click(object sender, RoutedEventArgs e)
    {
        try { SyncProfile(); DisplayValidation(); }
        catch (Exception exception) { ReportError("Profilen kunne ikke valideres", exception); }
    }

    private void DisplayValidation()
    {
        if (_profile is null) return;
        var result = ProfileValidator.Validate(_profile, _target);
        TrainerStatusText.Text = string.Join(Environment.NewLine, result.Messages);
        RefreshFeatures();
        SetStatus($"Profilstatus: {result.State}. {string.Join(" ", result.Messages)}");
        Log("profile_validation", result.IsReady, string.Join(" ", result.Messages));
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var copy = _store.Duplicate(SyncProfile());
            ApplyProfile(copy);
            RefreshProfiles(copy.Id);
            SetStatus("En selvstændig profilkopi er gemt. Originalen er bevaret.");
        }
        catch (Exception exception) { ReportError("Profilen kunne ikke kopieres", exception); }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new SaveFileDialog { Title = "Eksportér profil", Filter = "Trainer Studio-profil|*.trainerprofile.json", DefaultExt = ".trainerprofile.json", FileName = "min-trainer.trainerprofile.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _store.Export(SyncProfile(), dialog.FileName);
            Log("profile_exported", true, "Profilen blev eksporteret som datafil.");
            SetStatus("Profilen er eksporteret. Den kan importeres som en ny kopi på en anden pc.");
        }
        catch (Exception exception) { ReportError("Eksport mislykkedes", exception); }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Importér profil", Filter = "Trainer Studio-profil|*.trainerprofile.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = _store.Import(dialog.FileName);
            if (_features.Count == 0 || Confirm("Profilen er importeret som en ny bibliotekskopi. Vil du indlæse den i editoren og stoppe aktive fastholdelser?"))
                ApplyProfile(imported);
            RefreshProfiles(imported.Id);
            Log("profile_imported", true, "En profil blev importeret med nye id'er. Ingen funktioner er aktiveret.");
            SetStatus("Profilen er importeret. Validér den mod den rigtige proces før brug.");
        }
        catch (Exception exception) { ReportError("Import mislykkedes", exception); }
    }

    private void Heartbeat_Tick(object? sender, EventArgs e)
    {
        if (_closing || _target is null) return;
        if (!_target.IsAlive)
        {
            _scanCancellation?.Cancel();
            DisconnectCore();
            RefreshFeatures();
            SetBusy(_busy);
            TargetInfoText.Text = "Den tilknyttede proces er lukket. Forbind på ny efter genstart og validér din profil.";
            Log("process_exited", true, "Processen er lukket. Skriveadgang og alle fastholdelser er stoppet.");
            SetStatus("Spilprocessen er lukket; traineren er stoppet. Den gemte profil kan genindlæses efter ny tilknytning.");
            return;
        }
        if (_runtime.ActiveFreezeFeatureIds.Count == 0) return;
        try
        {
            var results = _runtime.Tick(_target);
            var changed = false;
            foreach (var result in results)
            {
                if (!result.Succeeded || (result.Before is { } before && result.After is { } after && !before.NumericallyEquals(after)))
                {
                    changed = true;
                    Log("freeze_tick", result.Succeeded, result.Message, before: result.Before, after: result.After);
                }
            }
            if (changed) RefreshFeatures();
        }
        catch (Exception exception) { _runtime.StopAll(); ReportError("Fastholdelser er stoppet efter en fejl", exception); RefreshFeatures(); }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => StopAll();
    private void StopAll()
    {
        _scanCancellation?.Cancel();
        var stopped = _runtime.StopAll();
        _target?.DisableWriting();
        _updatingWrites = true;
        WritesCheckBox.IsChecked = false;
        _updatingWrites = false;
        RefreshFeatures();
        Log("stop_all", true, $"{stopped} fastholdelser stoppet. Skriveadgang slået fra og søgning annulleret. Ingen ny værdi blev skrevet.");
        SetStatus("STOP: fastholdelser stoppet, skriveadgang slået fra og eventuel søgning annulleret.");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { StopAll(); e.Handled = true; return; }
        if (_busy || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        foreach (var feature in _features.Where(item => !string.IsNullOrWhiteSpace(item.Hotkey)))
        {
            try
            {
                if (new KeyGestureConverter().ConvertFromInvariantString(feature.Hotkey!) is not KeyGesture gesture || !gesture.Matches(this, e)) continue;
                if (_runtime.IsFreezeActive(feature)) DeactivateFeature(feature);
                else ActivateFeature(feature, null);
                e.Handled = true;
                break;
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
            { /* Imported shortcuts remain inert if WPF cannot interpret them. */ }
        }
    }

    private WindowsProcessTarget RequireTarget()
    {
        if (_target is null || !_target.IsAlive) throw new InvalidOperationException("Forbind først til en kørende proces.");
        return _target;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        var attached = _target is { IsAlive: true };
        ProcessesBox.IsEnabled = !busy;
        RefreshProcessesButton.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;
        DetachButton.IsEnabled = attached && !busy;
        WritesCheckBox.IsEnabled = attached && !busy;
        ScanOptionsPanel.IsEnabled = !busy;
        ResultsGrid.IsEnabled = !busy;
        BuilderPanel.IsEnabled = !busy;
        PointerOptionsPanel.IsEnabled = !busy;
        FirstScanButton.IsEnabled = attached && !busy;
        NextScanButton.IsEnabled = attached && !busy && _scanner.HasSession;
        ReadSelectedButton.IsEnabled = attached && !busy;
        ClearScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        PointerScanButton.IsEnabled = attached && !busy;
        PointerRescanButton.IsEnabled = attached && !busy && PointerRows.Count > 0;
        UsePointerButton.IsEnabled = attached && !busy && PointerRows.Count > 0;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Log(string eventType, bool succeeded, string message, TrainerFeature? feature = null, ScanValue? before = null, ScanValue? after = null)
    {
        if (_closing) return;
        var line = $"{DateTime.Now:T} {(succeeded ? "OK" : "FEJL")} · {message}";
        if (EventsBox.Text.Length > 150_000) EventsBox.Text = EventsBox.Text[^75_000..];
        EventsBox.AppendText(line + Environment.NewLine);
        EventsBox.ScrollToEnd();
        try
        {
            _audit.Append(new TrainerAuditEvent(DateTimeOffset.UtcNow, eventType, succeeded,
                _target?.Identity.TargetId ?? _profile?.Target.TargetId ?? "not-attached", feature?.Id.ToString(), message,
                before?.Display, after?.Display));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Handlingen er vist, men logfilen kunne ikke skrives: " + exception.Message;
        }
    }

    private void ReportError(string context, Exception exception)
    {
        if (_closing) return;
        var message = $"{context}: {exception.Message}";
        Log("error", false, message);
        SetStatus(message);
    }
    private void SetStatus(string text) { if (!_closing) StatusText.Text = text; }
    private bool Confirm(string message) => MessageBox.Show(_trainerWindow is { IsActive: true } ? _trainerWindow : this,
        message, "Bekræft ændringen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string DescribeReference(MemoryReference reference) => reference.Kind switch
    {
        MemoryReferenceKind.SessionAddress => $"Session · 0x{reference.SessionAddress:X} · {reference.DataType}",
        MemoryReferenceKind.PointerPath => $"{reference.ModuleName}+0x{reference.Offset:X} → {string.Join(" → ", reference.PointerOffsets ?? [])} · {reference.DataType}",
        _ => $"{reference.ModuleName}+0x{reference.Offset:X} · {reference.DataType}"
    };
}

public sealed record WorkspaceHitRow(ScanHit Hit)
{
    public string AddressText => Hit.AddressText;
    public string Current => Hit.CurrentValue.Display;
    public string Change => Hit.ChangeText;
    public string Region => Hit.RegionName;
}
public sealed record WorkspacePointerRow(MemoryReference Reference, string Status)
{
    public string Module => Reference.ModuleName;
    public string Offset => $"0x{Reference.Offset:X}";
    public string Path => string.Join(" → ", Reference.PointerOffsets ?? []);
}
public sealed record WorkspaceFeatureRow(TrainerFeature Feature, string Status)
{
    public string Name => Feature.Name;
    public string Category => Feature.Category;
    public string Control => Feature.Control.ToString();
}
public sealed record WorkspaceProfileRow(TrainerProfile Profile, string Path)
{
    public string Display => $"{Profile.Name} · {Profile.Target.ModuleName} · {Profile.Features.Count} funktioner";
}
