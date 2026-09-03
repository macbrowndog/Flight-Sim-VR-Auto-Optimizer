using System.Windows;
using System.Windows.Media;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public partial class RestorationReportWindow : Window
{
    private readonly bool _closeApplicationOnCloseReport;

    public RestorationReportWindow(
        RestorationReport report,
        string reportPath,
        bool closeApplicationOnCloseReport = false)
    {
        InitializeComponent();
        _closeApplicationOnCloseReport = closeApplicationOnCloseReport;
        SessionText.Text = $"SESSION {report.SessionId}  /  {report.SimulatorName}  /  {report.CompletedAtUtc.ToLocalTime():g}";
        ReportGrid.ItemsSource = report.Items;
        ReportPathText.Text = "Saved report: " + reportPath;
        ReportPathText.ToolTip = reportPath;

        if (report.Succeeded)
        {
            SummaryPanel.Background = Brush("#102219");
            SummaryPanel.BorderBrush = FindBrush("GreenBrush");
            SummaryText.Foreground = FindBrush("GreenBrush");
            SummaryText.Text = $"RESTORATION VERIFIED / {report.RestoredCount} restored · {report.LeftClosedCount} app(s) left closed · {report.ManualActionCount} manual action(s) · 0 failed";
        }
        else
        {
            SummaryPanel.Background = Brush("#281313");
            SummaryPanel.BorderBrush = FindBrush("RedBrush");
            SummaryText.Foreground = FindBrush("RedBrush");
            SummaryText.Text = $"RESTORATION INCOMPLETE / {report.RestoredCount} restored · {report.LeftClosedCount} app(s) left closed · {report.ManualActionCount} manual action(s) · {report.FailedCount} failed · journal retained";
        }
    }

    public bool CloseApplicationRequested { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseApplicationRequested = _closeApplicationOnCloseReport;
        Close();
    }
    private Brush FindBrush(string key) => (Brush)FindResource(key);
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
