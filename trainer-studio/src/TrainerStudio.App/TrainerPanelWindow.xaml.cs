using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrainerStudio.Core;

namespace TrainerStudio.App;

/// <summary>
/// A compact view of the declarative features in a saved trainer profile.
/// It deliberately delegates all writes, confirmations and logging to the
/// owner window so it cannot acquire an independent target or bypass consent.
/// </summary>
public partial class TrainerPanelWindow : Window
{
    private IReadOnlyList<TrainerFeature> _features;
    private readonly Func<TrainerFeature, bool> _isActive;
    private readonly Action<TrainerFeature, string?> _activate;
    private readonly Action<TrainerFeature> _deactivate;
    private readonly Action _stopAll;
    private IReadOnlyList<TrainerFeature>? _renderedFeatures;
    private readonly Dictionary<Guid, TextBlock> _statusLabels = new();
    private readonly Dictionary<Guid, CheckBox> _toggles = new();
    private bool _updatingStates;

    public TrainerPanelWindow(
        IReadOnlyList<TrainerFeature> features,
        Func<TrainerFeature, bool> isActive,
        Action<TrainerFeature, string?> activate,
        Action<TrainerFeature> deactivate,
        Action stopAll)
    {
        _features = features?.ToArray() ?? throw new ArgumentNullException(nameof(features));
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        _deactivate = deactivate ?? throw new ArgumentNullException(nameof(deactivate));
        _stopAll = stopAll ?? throw new ArgumentNullException(nameof(stopAll));

        InitializeComponent();
        Refresh(_features);
    }

    public void Refresh(IReadOnlyList<TrainerFeature> features)
    {
        _features = features?.ToArray() ?? throw new ArgumentNullException(nameof(features));
        if (_renderedFeatures is not null && _renderedFeatures.SequenceEqual(_features))
        {
            RefreshStates();
            return;
        }
        _renderedFeatures = _features;
        _statusLabels.Clear();
        _toggles.Clear();
        CategoriesPanel.Children.Clear();

        if (_features.Count == 0)
        {
            CategoriesPanel.Children.Add(new TextBlock
            {
                Text = "Din trainer er tom. Opret først en funktion i Trainer Builder.",
                Foreground = Brush("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4)
            });
            return;
        }

        // GroupBy retains the first category's place in the profile. This makes
        // the compact menu follow the user's move-up/move-down ordering.
        foreach (var category in _features.GroupBy(feature =>
                     string.IsNullOrWhiteSpace(feature.Category) ? "Generelt" : feature.Category))
        {
            var categoryPanel = new StackPanel();
            categoryPanel.Children.Add(new TextBlock
            {
                Text = category.Key.ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("Accent"),
                Margin = new Thickness(2, 8, 2, 4)
            });

            foreach (var feature in category)
            {
                categoryPanel.Children.Add(CreateFeatureCard(feature));
            }

            CategoriesPanel.Children.Add(categoryPanel);
        }
    }

    private Border CreateFeatureCard(TrainerFeature feature)
    {
        var card = new Border
        {
            Background = Brush("PanelBackground"),
            BorderBrush = new SolidColorBrush(Color.FromRgb(54, 82, 105)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 7)
        };
        var panel = new StackPanel();
        card.Child = panel;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = feature.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var status = new TextBlock
        {
            Text = _isActive(feature) ? "AKTIV" : "KLAR",
            Foreground = _isActive(feature)
                ? new SolidColorBrush(Color.FromRgb(191, 230, 197))
                : Brush("Muted"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusLabels[feature.Id] = status;
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        panel.Children.Add(header);

        var description = string.IsNullOrWhiteSpace(feature.Description)
            ? DescribeControl(feature)
            : feature.Description;
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 7)
        });

        switch (feature.Control)
        {
            case TrainerControlKind.Toggle:
                AddToggleControl(panel, feature);
                break;
            case TrainerControlKind.Slider:
                AddSliderControl(panel, feature);
                break;
            case TrainerControlKind.NumberInput:
                AddNumberInputControl(panel, feature);
                break;
            default:
                AddButtonControl(panel, feature);
                break;
        }

        return card;
    }

    private void AddButtonControl(Panel panel, TrainerFeature feature)
    {
        var button = new Button
        {
            Content = ButtonCaption(feature),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => Activate(feature, null);
        panel.Children.Add(button);
    }

    private void AddToggleControl(Panel panel, TrainerFeature feature)
    {
        var toggle = new CheckBox
        {
            Content = "Fasthold aktiv",
            IsChecked = _isActive(feature),
            Margin = new Thickness(4, 2, 4, 2)
        };
        toggle.Checked += (_, _) => { if (!_updatingStates) Activate(feature, null); };
        toggle.Unchecked += (_, _) => { if (!_updatingStates) Deactivate(feature); };
        _toggles[feature.Id] = toggle;
        panel.Children.Add(toggle);
    }

    private void AddSliderControl(Panel panel, TrainerFeature feature)
    {
        if (!TryGetSliderRange(feature, out var minimum, out var maximum, out var step, out var initial))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Sliderens gemte interval kan ikke vises.",
                Foreground = new SolidColorBrush(Color.FromRgb(207, 82, 92)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var valueText = new TextBlock
        {
            Text = $"Valgt værdi: {FormatSliderValue(initial)}",
            Foreground = Brush("Muted"),
            Margin = new Thickness(4, 0, 4, 2)
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = initial,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(4, 0, 4, 4)
        };
        slider.ValueChanged += (_, _) => valueText.Text = $"Valgt værdi: {FormatSliderValue(slider.Value)}";
        var apply = new Button
        {
            Content = "Anvend værdi",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        apply.Click += (_, _) => Activate(feature, FormatSliderValue(slider.Value));
        panel.Children.Add(valueText);
        panel.Children.Add(slider);
        panel.Children.Add(apply);
    }

    private void AddNumberInputControl(Panel panel, TrainerFeature feature)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var value = new TextBox { Text = feature.Value, MinWidth = 100 };
        var apply = new Button { Content = ButtonCaption(feature) };
        apply.Click += (_, _) => Activate(feature, value.Text);
        row.Children.Add(value);
        Grid.SetColumn(apply, 1);
        row.Children.Add(apply);
        panel.Children.Add(row);
    }

    private void Activate(TrainerFeature feature, string? value)
    {
        _activate(feature, value);
        Refresh(_features);
    }

    private void RefreshStates()
    {
        _updatingStates = true;
        try
        {
            foreach (var feature in _features)
            {
                var active = _isActive(feature);
                if (_statusLabels.TryGetValue(feature.Id, out var label))
                {
                    label.Text = active ? "AKTIV" : "KLAR";
                    label.Foreground = active ? new SolidColorBrush(Color.FromRgb(191, 230, 197)) : Brush("Muted");
                }
                if (_toggles.TryGetValue(feature.Id, out var toggle)) toggle.IsChecked = active;
            }
        }
        finally { _updatingStates = false; }
    }

    private void Deactivate(TrainerFeature feature)
    {
        _deactivate(feature);
        Refresh(_features);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _stopAll();
        StatusText.Text = "Alle aktive fastholdelser er stoppet.";
        Refresh(_features);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _stopAll();
            StatusText.Text = "Alle aktive fastholdelser er stoppet.";
            Refresh(_features);
            e.Handled = true;
        }
    }

    private static bool TryGetSliderRange(
        TrainerFeature feature,
        out double minimum,
        out double maximum,
        out double step,
        out double initial)
    {
        minimum = maximum = step = initial = 0;
        try
        {
            minimum = ScanValue.Parse(feature.Reference.DataType, feature.Minimum ?? string.Empty).AsDouble();
            maximum = ScanValue.Parse(feature.Reference.DataType, feature.Maximum ?? string.Empty).AsDouble();
            step = ScanValue.Parse(feature.Reference.DataType, feature.Step ?? string.Empty).AsDouble();
            initial = ScanValue.Parse(feature.Reference.DataType, feature.Value).AsDouble();
            return minimum <= maximum && step > 0 && initial >= minimum && initial <= maximum;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string ButtonCaption(TrainerFeature feature) => feature.Operation switch
    {
        TrainerOperation.AddValue => "Tilføj værdi",
        TrainerOperation.RestoreOriginal => "Gendan oprindelig",
        _ => "Udfør"
    };

    private static string DescribeControl(TrainerFeature feature) => feature.Control switch
    {
        TrainerControlKind.Toggle => "Fasthold den gemte værdi, indtil kontakten deaktiveres.",
        TrainerControlKind.Slider => "Vælg en værdi i det gemte interval og anvend den.",
        TrainerControlKind.NumberInput => "Skriv den ønskede værdi og anvend den.",
        _ => feature.Operation == TrainerOperation.RestoreOriginal
            ? "Gendan den oprindelige værdi fra denne session."
            : "Udfør den gemte handling efter godkendelse."
    };

    private static string FormatSliderValue(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private Brush Brush(string key) => (Brush)FindResource(key);
}
