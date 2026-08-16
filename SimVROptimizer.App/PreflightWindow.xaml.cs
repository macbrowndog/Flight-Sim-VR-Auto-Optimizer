using System.Windows;
using System.Windows.Media;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public partial class PreflightWindow : Window
{
    public PreflightWindow(PreflightReport report)
    {
        InitializeComponent();
        CheckItems.ItemsSource = report.Items.Select(PreflightDisplayItem.From).ToArray();
        ContinueButton.IsEnabled = report.CanProceed;

        if (report.CanProceed)
        {
            SummaryPanel.Background = Brush("#102219");
            SummaryPanel.BorderBrush = FindBrush("GreenBrush");
            SummaryText.Foreground = FindBrush("GreenBrush");
            SummaryText.Text = report.WarningCount == 0
                ? "READY TO PROCEED / All safety checks passed."
                : $"READY WITH {report.WarningCount} WARNING(S) / Review the amber entries before continuing.";
        }
        else
        {
            SummaryPanel.Background = Brush("#281313");
            SummaryPanel.BorderBrush = FindBrush("RedBrush");
            SummaryText.Foreground = FindBrush("RedBrush");
            SummaryText.Text = $"START BLOCKED / Resolve {report.BlockedCount} red item(s), then run the safety check again.";
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private Brush FindBrush(string key) => (Brush)FindResource(key);
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private sealed record PreflightDisplayItem(string Name, string Detail, string StatusText, Brush StatusBrush, Brush StatusBackground)
    {
        public static PreflightDisplayItem From(PreflightItem item)
        {
            var (text, foreground, background) = item.Status switch
            {
                PreflightStatus.Ready => ("READY", "#65D487", "#102219"),
                PreflightStatus.Warning => ("WARNING", "#F2B84B", "#2D2514"),
                _ => ("BLOCKED", "#ED6A62", "#281313")
            };
            return new(item.Name, item.Detail, text, Brush(foreground), Brush(background));
        }
    }
}
