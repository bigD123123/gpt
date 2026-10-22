using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrainerStudio.Theme.Tests;

internal static class Program
{
    private static int _failures;
    private static string _outputDirectory = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("WPF theme tests must run on Windows.");
            return 1;
        }

        _outputDirectory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/theme-tests");
        Directory.CreateDirectory(_outputDirectory);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new TrainerStudio.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();

        Window? window = null;
        try
        {
            var enabledCombo = CreateCombo();
            var disabledCombo = CreateCombo();
            disabledCombo.IsEnabled = false;

            var grid = new DataGrid
            {
                Height = 180,
                Margin = new Thickness(4),
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                ItemsSource = new ObservableCollection<SampleRow>
                {
                    new("0x1000", "50"),
                    new("0x1004", "75")
                }
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "Adresse", Binding = new Binding(nameof(SampleRow.Address)), Width = 320 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Aktuel", Binding = new Binding(nameof(SampleRow.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var disabledButton = new Button
            {
                Content = "Udfør valgt",
                Width = 360,
                Height = 36,
                IsEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var content = new StackPanel
            {
                Margin = new Thickness(18),
                Background = (Brush)app.FindResource("PageBackground")
            };
            content.Children.Add(new TextBlock { Text = "Trainer Studio — Windows theme smoke test", FontSize = 20, Margin = new Thickness(4, 0, 4, 12) });
            content.Children.Add(new TextBlock { Text = "Closed dropdown", Margin = new Thickness(4) });
            content.Children.Add(enabledCombo);
            content.Children.Add(new TextBlock { Text = "Disabled dropdown", Margin = new Thickness(4, 12, 4, 4) });
            content.Children.Add(disabledCombo);
            content.Children.Add(new TextBlock { Text = "Results table", Margin = new Thickness(4, 12, 4, 4) });
            content.Children.Add(grid);
            content.Children.Add(disabledButton);

            window = new Window
            {
                Title = "Trainer Studio theme verification",
                Width = 760,
                Height = 590,
                ShowActivated = false,
                Background = (Brush)app.FindResource("PageBackground"),
                Foreground = new SolidColorBrush(Color.FromRgb(231, 240, 247)),
                Content = content
            };
            window.Show();
            Pump(window);
            SaveImage(content, "theme-overview.png");

            Check("Closed ComboBox has a dark surface and legible selection", () =>
                AssertControl(enabledCombo, "Int32", 4.5, "combo-closed.png"));
            Check("Disabled ComboBox keeps a dark surface and legible selection", () =>
                AssertControl(disabledCombo, "Int32", 3, "combo-disabled.png"));
            Check("Disabled Button keeps a dark surface and legible label", () =>
                AssertControl(disabledButton, "Udfør valgt", 3, "button-disabled.png"));
            Check("DataGrid header has a dark surface and legible label", () =>
            {
                var header = Descendants<DataGridColumnHeader>(grid).First(element => Equals(element.Content, "Adresse"));
                AssertControl(header, "Adresse", 4.5, "table-header.png");
            });
            Check("DataGrid rows have dark surfaces and legible values", () =>
            {
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                var cell = Descendants<DataGridCell>(row).First();
                AssertControl(cell, "0x1000", 4.5, "table-cell.png");
            });

            enabledCombo.IsDropDownOpen = true;
            Pump(window);
            Check("Open ComboBox items have dark surfaces and legible labels", () =>
            {
                var popup = enabledCombo.Template.FindName("PART_Popup", enabledCombo) as Popup
                    ?? throw new InvalidOperationException("ComboBox must expose its standard PART_Popup.");
                if (!popup.IsOpen || popup.Child is not FrameworkElement popupRoot)
                {
                    throw new InvalidOperationException("The dropdown did not open.");
                }

                SaveImage(popupRoot, "combo-open.png");
                var unselectedItem = (ComboBoxItem)enabledCombo.ItemContainerGenerator.ContainerFromIndex(1);
                AssertControl(unselectedItem, "UInt32", 4.5, "combo-open-item.png");
                var selectedItem = (ComboBoxItem)enabledCombo.ItemContainerGenerator.ContainerFromIndex(0);
                AssertContrast(selectedItem, "Int32", 4.5, "combo-selected-item.png");
            });
            enabledCombo.IsDropDownOpen = false;

            grid.SelectedIndex = 0;
            Pump(window);
            Check("Selected DataGrid row keeps readable values", () =>
            {
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                var cell = Descendants<DataGridCell>(row).First();
                AssertContrast(cell, "0x1000", 4.5, "table-selected-cell.png");
            });

            Check("Actual MainWindow renders with the application theme", () =>
            {
                var mainWindow = new TrainerStudio.App.MainWindow { ShowActivated = false };
                try
                {
                    mainWindow.Show();
                    Pump(mainWindow);
                    SaveImage((FrameworkElement)mainWindow.Content, "main-window.png");
                }
                finally
                {
                    mainWindow.Close();
                }
            });

            Console.WriteLine(_failures == 0
                ? "PASS: 8 Windows WPF theme checks"
                : $"FAIL: {_failures} Windows WPF theme check(s)");
            Console.WriteLine($"Rendered evidence: {_outputDirectory}");
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            window?.Close();
            app.Shutdown();
        }
    }

    private static ComboBox CreateCombo() => new()
    {
        Width = 360,
        Height = 34,
        HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[] { "Int32", "UInt32" },
        SelectedIndex = 0
    };

    private static void Check(string name, Action assertion)
    {
        try
        {
            assertion();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL: {name}: {exception.Message}");
        }
    }

    private static void AssertControl(FrameworkElement control, string label, double requiredContrast, string filename)
    {
        var bitmap = SaveImage(control, filename);
        var background = SampleBlankSurface(bitmap);
        if (Luminance(background) > 0.15)
        {
            throw new InvalidOperationException($"Rendered surface is too light ({background}); native light styling is still visible.");
        }

        AssertLabelContrast(control, label, background, requiredContrast);
    }

    private static void AssertContrast(FrameworkElement control, string label, double requiredContrast, string filename)
    {
        var bitmap = SaveImage(control, filename);
        AssertLabelContrast(control, label, SampleBlankSurface(bitmap), requiredContrast);
    }

    private static void AssertLabelContrast(FrameworkElement root, string label, Color background, double requiredContrast)
    {
        var text = Descendants<TextBlock>(root).FirstOrDefault(element => element.Text == label && element.IsVisible)
            ?? throw new InvalidOperationException($"Visible label '{label}' was not rendered.");
        if (text.Foreground is not SolidColorBrush foreground)
        {
            throw new InvalidOperationException($"Label '{label}' does not use a measurable solid foreground.");
        }

        var textLuminance = Luminance(foreground.Color);
        var backgroundLuminance = Luminance(background);
        var contrast = (Math.Max(textLuminance, backgroundLuminance) + 0.05) /
                       (Math.Min(textLuminance, backgroundLuminance) + 0.05);
        if (contrast < requiredContrast)
        {
            throw new InvalidOperationException($"Label '{label}' has {contrast:F2}:1 contrast; expected at least {requiredContrast}:1.");
        }
    }

    private static BitmapSource SaveImage(FrameworkElement element, string filename)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var renderRoot = PresentationSource.FromVisual(element)?.RootVisual as FrameworkElement ?? element;
        if (renderRoot is Window { Content: FrameworkElement windowContent } && element.IsDescendantOf(windowContent))
        {
            renderRoot = windowContent;
        }

        var region = ReferenceEquals(element, renderRoot)
            ? new Rect(0, 0, element.ActualWidth, element.ActualHeight)
            : element.TransformToAncestor(renderRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            // Capture from the visual root, so a transparent cell or content
            // presenter includes its actual row/parent background.
            context.DrawRectangle(new VisualBrush(renderRoot)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = region
            }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_outputDirectory, filename));
        encoder.Save(file);
        return bitmap;
    }

    private static Color SampleBlankSurface(BitmapSource bitmap)
    {
        // Labels are left-aligned. The middle-right interior stays blank even
        // when a native template adds a dropdown arrow or header sort glyph.
        var x = Math.Clamp((int)(bitmap.PixelWidth * 0.7), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp(bitmap.PixelHeight / 2, 0, bitmap.PixelHeight - 1);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        if (pixel[3] < 250)
        {
            throw new InvalidOperationException("The control surface rendered transparent instead of supplying an explicit dark background.");
        }

        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private sealed record SampleRow(string Address, string Value);
}
