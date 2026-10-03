using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public sealed class ManualSimulatorWindow : Window
{
    private readonly ComboBox _typeBox = new() { SelectedValuePath = "Key", MinHeight = 32 };
    private readonly TextBox _nameBox = new();
    private readonly TextBox _pathBox = new();
    private readonly TextBox _argumentsBox = new();
    private readonly TextBlock _processText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _customId;

    public ManualSimulator? Simulator { get; private set; }

    public ManualSimulatorWindow(ManualSimulator? initial, IReadOnlyList<ManualSimulator> saved)
    {
        Title = initial is null ? "Add simulator" : "Edit simulator";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 18, 20));
        Foreground = Brushes.White;
        _customId = initial?.Id ?? "custom-" + Guid.NewGuid().ToString("N");

        var root = new StackPanel { Margin = new Thickness(18) };
        var typeText = new FrameworkElementFactory(typeof(TextBlock));
        typeText.SetBinding(TextBlock.TextProperty, new Binding("Value"));
        _typeBox.ItemTemplate = new DataTemplate { VisualTree = typeText };
        _typeBox.ItemsSource = new[] { new KeyValuePair<string, string>("", "Custom") }
            .Concat(SimulatorCatalog.Identities).ToArray();
        _typeBox.SelectedValue = SimulatorCatalog.Identities.Any(item => item.Key == initial?.Id) ? initial!.Id : "";
        _typeBox.IsEnabled = initial is null;
        AddField("Simulator type", _typeBox);
        AddField("Name", _nameBox);

        var pathPanel = new DockPanel();
        var browse = new Button { Content = "BROWSE...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        browse.Background = (Brush)FindResource("ControlHoverBrush");
        browse.Foreground = (Brush)FindResource("AccentBrush");
        DockPanel.SetDock(browse, Dock.Right);
        pathPanel.Children.Add(browse);
        pathPanel.Children.Add(_pathBox);
        AddField("Executable path", pathPanel);
        AutomationProperties.SetName(_pathBox, "Executable path");
        AddField("Arguments (optional)", _argumentsBox);
        _processText.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_processText);
        root.Children.Add(new TextBlock
        {
            Text = "Choose the actual game EXE, not an updater or launcher. Tracking a replacement game process is not supported. Known simulator types override automatic detection for all profiles on this PC.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(125, 211, 252))
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button
        {
            Content = "CANCEL", MinWidth = 90, Padding = new Thickness(10, 5, 10, 5), IsCancel = true,
            Background = (Brush)FindResource("ControlHoverBrush"), Foreground = (Brush)FindResource("TextBrush")
        });
        var save = new Button { Content = "SAVE", MinWidth = 90, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        save.Background = (Brush)FindResource("AccentBrush");
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        Content = root;

        _pathBox.TextChanged += (_, _) => _processText.Text = "Monitored process: " + Path.GetFileNameWithoutExtension(_pathBox.Text.Trim().Trim('"'));
        if (initial is not null) LoadEntry(initial);
        else _processText.Text = "Monitored process: choose an EXE";
        _typeBox.SelectionChanged += (_, _) =>
        {
            var id = (string?)_typeBox.SelectedValue ?? "";
            var existing = saved.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                LoadEntry(existing);
                Title = "Edit simulator";
                _typeBox.IsEnabled = false;
            }
            else if (_typeBox.SelectedItem is KeyValuePair<string, string> choice && id.Length > 0)
                _nameBox.Text = choice.Value;
        };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            _pathBox.Text = picker.FileName;
            if (string.IsNullOrWhiteSpace(_nameBox.Text)) _nameBox.Text = Path.GetFileNameWithoutExtension(picker.FileName);
        };
        save.Click += (_, _) =>
        {
            var path = _pathBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(_nameBox.Text) || !Path.IsPathFullyQualified(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                MessageBox.Show(this, "Enter a name and choose an existing EXE using its full path.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var id = (string?)_typeBox.SelectedValue;
            Simulator = new ManualSimulator(string.IsNullOrEmpty(id) ? _customId : id,
                _nameBox.Text.Trim(), Path.GetFullPath(path), _argumentsBox.Text);
            DialogResult = true;
        };
        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };

        void AddField(string label, FrameworkElement control)
        {
            root.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 4) });
            control.Margin = new Thickness(0, 0, 0, 12);
            if (control is Control input) input.Padding = new Thickness(8, 5, 8, 5);
            AutomationProperties.SetName(control, label);
            root.Children.Add(control);
        }
    }

    private void LoadEntry(ManualSimulator entry)
    {
        _nameBox.Text = entry.Name;
        _pathBox.Text = entry.ExecutablePath;
        _argumentsBox.Text = entry.Arguments;
    }
}
