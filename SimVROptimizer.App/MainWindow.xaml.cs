using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly bool _continueSession;
    private readonly SessionCoordinator _coordinator;
    private readonly SystemScanner _scanner;
    private AppConfig _config = new();
    private IReadOnlyList<RunningAppCandidate> _applications = [];
    private IReadOnlyList<ServiceCandidate> _services = [];
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private bool _allowClose;
    private bool _applyingConfig;

    public MainWindow(bool continueSession = false)
    {
        InitializeComponent();
        _continueSession = continueSession;
        _paths.EnsureCreated();
        var logger = new FileLogger(_paths.LogFile);
        var commands = new CommandRunner();
        var optimizer = new TransactionalOptimizer(commands, _paths, logger);
        _coordinator = new SessionCoordinator(optimizer, new SimulatorLauncher(logger), new VrRuntimeLauncher(logger));
        _scanner = new SystemScanner(commands);
        _coordinator.StatusChanged += AppendStatus;
        _coordinator.ProgressChanged += UpdatePipeline;
        AdminLabel.Text = AdminService.IsAdministrator() ? "ADMINISTRATOR" : "STANDARD USER";
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config = await JsonStore.LoadOrDefaultAsync(_paths.ConfigFile, () => new AppConfig());
        ApplyOptionsToControls();
        ShowCpuProfile();
        UpdateRecoveryState();
        await ScanSystemAsync();

        if (_coordinator.HasRecoveryJournal)
        {
            AppendStatus("An unfinished session was detected. Restore it before starting another session.");
            var answer = MessageBox.Show(
                "An unfinished optimizer session was found. Restore the recorded system state now?",
                "Recovery required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) await RestoreRecoveryAsync();
        }

        if (_continueSession && !_coordinator.HasRecoveryJournal)
            await ContinuePendingLaunchAsync();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartSelectedSessionAsync();

    private async Task StartSelectedSessionAsync(bool automaticConfirmed = false)
    {
        if (SimulatorCombo.SelectedItem is not DetectedSimulator detectedSimulator)
        {
            MessageBox.Show("Select a simulator first.", "VR Auto-Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 30 or > 900)
        {
            MessageBox.Show("Launch timeout must be between 30 and 900 seconds.", "VR Auto-Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var simulator = detectedSimulator.Definition;
        _config = ReadConfigFromControls(simulator.Id, timeout);
        if (_config.SessionMode == SessionMode.Automatic) SelectAllStoppableItems();
        await JsonStore.SaveAtomicAsync(_paths.ConfigFile, _config);

        if (_config.SessionMode == SessionMode.Automatic && !automaticConfirmed)
        {
            var selectedApps = _applications.Count(item => item.Selected && item.CanStop);
            var selectedServices = _services.Count(item => item.Selected && item.CanStop);
            var creatorApps = _config.Options.ContentCreatorMode
                ? _applications.Count(item => SessionSelectionPolicy.IsContentCreatorApplication(item))
                : 0;
            var creatorServices = _config.Options.ContentCreatorMode
                ? _services.Count(item => SessionSelectionPolicy.IsContentCreatorService(item))
                : 0;
            var creatorSummary = _config.Options.ContentCreatorMode
                ? $" Content Creator Mode will keep {creatorApps} creator application(s) and {creatorServices} helper service(s) running."
                : "";
            var answer = MessageBox.Show(
                $"Automatic {_config.Options.Profile} mode will close {selectedApps} selected application(s), stop {selectedServices} selected service(s), apply CPU settings, launch {_config.Options.VrRuntime}, and start {simulator.Name}.{creatorSummary} Save your work first. Continue?",
                "Confirm automatic session",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        if (!AdminService.IsAdministrator())
        {
            var answer = MessageBox.Show(
                "Real optimization requires administrator access. Relaunch as administrator? Your selections have been saved.",
                "Administrator access",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    var pending = new PendingLaunch
                    {
                        SimulatorId = simulator.Id,
                        SessionMode = _config.SessionMode,
                        Options = _config.Options,
                        ProcessNames = _applications.Where(item => item.Selected && item.CanStop).Select(item => item.ProcessName).ToArray(),
                        ServiceNames = _services.Where(item => item.Selected && item.CanStop).Select(item => item.ServiceName).ToArray(),
                        CustomApplications = _config.CustomApplications
                    };
                    await JsonStore.SaveAtomicAsync(_paths.PendingLaunchFile, pending);
                    AdminService.RelaunchElevated("--continue-session");
                    _allowClose = true;
                    Application.Current.Shutdown();
                }
                catch (Win32Exception)
                {
                    if (File.Exists(_paths.PendingLaunchFile)) File.Delete(_paths.PendingLaunchFile);
                    AppendStatus("Administrator request was cancelled.");
                }
            }
            return;
        }

        SetRunningState(true);
        if (_config.Options.ContentCreatorMode)
            AppendStatus("Content Creator Mode active: streaming, capture, audio-routing, and creator helper tools are protected.");
        _sessionCancellation = new CancellationTokenSource();
        _sessionTask = RunSessionAsync(simulator, _config.Options, _sessionCancellation.Token);
        await _sessionTask;
    }

    private async Task RunSessionAsync(SimulatorDefinition simulator, OptimizerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            SetStateDisplay("SESSION ACTIVE", "CyanBrush");
            await _coordinator.RunAsync(simulator, options, _applications, _services, cancellationToken);
            AppendStatus("Simulator exited; restoration completed.");
        }
        catch (OperationCanceledException)
        {
            AppendStatus("Session cancelled; restoration completed.");
        }
        catch (Exception exception)
        {
            AppendStatus("ERROR: " + exception.Message);
            MessageBox.Show(exception.Message, "Session error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            SetRunningState(false);
            UpdateRecoveryState();
            if (!_coordinator.HasRecoveryJournal) CompletePipeline();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetStateDisplay("ABORTING / RESTORING", "AccentBrush");
        _sessionCancellation?.Cancel();
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e) => await RestoreRecoveryAsync();

    private async Task RestoreRecoveryAsync()
    {
        if (!AdminService.IsAdministrator())
        {
            var answer = MessageBox.Show(
                "Recovery requires administrator access. Relaunch as administrator now?",
                "Recovery",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    AdminService.RelaunchElevated();
                    _allowClose = true;
                    Application.Current.Shutdown();
                }
                catch (Win32Exception)
                {
                    AppendStatus("Administrator request was cancelled.");
                }
            }
            return;
        }

        try
        {
            SetRunningState(true);
            SetStateDisplay("RESTORING", "AccentBrush");
            await _coordinator.RestoreRecoveryAsync();
            AppendStatus("Recovery completed.");
        }
        catch (Exception exception)
        {
            AppendStatus("RECOVERY ERROR: " + exception.Message);
            MessageBox.Show(exception.Message, "Recovery incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunningState(false);
            UpdateRecoveryState();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _sessionTask is null || _sessionTask.IsCompleted) return;
        e.Cancel = true;
        _sessionCancellation?.Cancel();
        AppendStatus("Window close requested; restoring the session before exit.");
        try { await _sessionTask; } catch { /* RunSessionAsync reports failures. */ }
        if (!_coordinator.HasRecoveryJournal)
        {
            _allowClose = true;
            Close();
        }
    }

    private AppConfig ReadConfigFromControls(string simulatorId, int timeout) => new()
    {
        SelectedSimulatorId = simulatorId,
        SessionMode = ModeCombo.SelectedItem is SessionMode mode ? mode : SessionMode.Manual,
        Options = new OptimizerOptions
        {
            DryRun = false,
            Profile = ProfileCombo.SelectedItem is OptimizationProfile profile ? profile : OptimizationProfile.Standard,
            UseUltimatePowerPlan = PowerPlanCheck.IsChecked == true,
            ProcessPriority = PriorityCombo.SelectedItem is ProcessPriorityPreference priority ? priority : ProcessPriorityPreference.AboveNormal,
            UseVendorAwareCpuSets = CpuSetsCheck.IsChecked == true,
            EnableNvidiaPersistence = NvidiaCheck.IsChecked == true,
            UseMsfs2024FastLaunch = FastLaunchCheck.IsChecked == true,
            FlushDnsCache = FlushDnsCheck.IsChecked == true,
            DisableGameDvr = GameDvrCheck.IsChecked == true,
            ClearStandbyMemory = StandbyMemoryCheck.IsChecked == true,
            UseHighResolutionTimer = TimerResolutionCheck.IsChecked == true,
            DisableFullscreenOptimizations = FullscreenOptimizationsCheck.IsChecked == true,
            DisablePowerThrottling = PowerThrottlingCheck.IsChecked == true,
            ApplyNetworkMemoryOptimizations = NetworkMemoryCheck.IsChecked == true,
            ContentCreatorMode = ContentCreatorCheck.IsChecked == true,
            VrRuntime = VrRuntimeCombo.SelectedItem is VrRuntimePreference runtime ? runtime : VrRuntimePreference.None,
            LaunchTimeoutSeconds = timeout
        },
        CustomApplications = ReadCustomApplications()
    };

    private void ApplyOptionsToControls()
    {
        _applyingConfig = true;
        ProfileCombo.ItemsSource = Enum.GetValues<OptimizationProfile>();
        ProfileCombo.SelectedItem = _config.Options.Profile;
        PowerPlanCheck.IsChecked = _config.Options.UseUltimatePowerPlan;
        PriorityCombo.ItemsSource = Enum.GetValues<ProcessPriorityPreference>();
        PriorityCombo.SelectedItem = _config.Options.ProcessPriority;
        CpuSetsCheck.IsChecked = _config.Options.UseVendorAwareCpuSets;
        NvidiaCheck.IsChecked = _config.Options.EnableNvidiaPersistence;
        FastLaunchCheck.IsChecked = _config.Options.UseMsfs2024FastLaunch;
        FlushDnsCheck.IsChecked = _config.Options.FlushDnsCache;
        GameDvrCheck.IsChecked = _config.Options.DisableGameDvr;
        StandbyMemoryCheck.IsChecked = _config.Options.ClearStandbyMemory;
        TimerResolutionCheck.IsChecked = _config.Options.UseHighResolutionTimer;
        FullscreenOptimizationsCheck.IsChecked = _config.Options.DisableFullscreenOptimizations;
        PowerThrottlingCheck.IsChecked = _config.Options.DisablePowerThrottling;
        NetworkMemoryCheck.IsChecked = _config.Options.ApplyNetworkMemoryOptimizations;
        ContentCreatorCheck.IsChecked = _config.Options.ContentCreatorMode;
        VrRuntimeCombo.ItemsSource = Enum.GetValues<VrRuntimePreference>();
        VrRuntimeCombo.SelectedItem = _config.Options.VrRuntime;
        ModeCombo.ItemsSource = Enum.GetValues<SessionMode>();
        ModeCombo.SelectedItem = _config.SessionMode;
        TimeoutBox.Text = _config.Options.LaunchTimeoutSeconds.ToString();
        CustomKillBox.Text = string.Join(Environment.NewLine, _config.CustomApplications.Select(rule => rule.ProcessName));
        CustomRestartBox.Text = string.Join(Environment.NewLine, _config.CustomApplications
            .Where(rule => !string.IsNullOrWhiteSpace(rule.RestartExecutablePath))
            .Select(rule => $"{rule.ProcessName}={rule.RestartExecutablePath}"));
        _applyingConfig = false;
        UpdateModeDescription();
    }

    private void ShowCpuProfile()
    {
        try
        {
            var profile = new CpuOptimizer().GetProfile();
            var type = profile.IsAmd && profile.IsX3D ? "AMD X3D"
                : profile.IsIntel && profile.IsHybrid ? "Intel hybrid"
                : profile.IsAmd ? "AMD"
                : profile.IsIntel ? "Intel"
                : "Unknown vendor";
            CpuInfoText.Text = $"Detected CPU: {profile.Model} · {type} · {profile.PhysicalCoreCount} cores / {profile.LogicalProcessorCount} logical processors";
        }
        catch (Exception exception)
        {
            CpuInfoText.Text = "CPU topology unavailable: " + exception.Message;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanSystemAsync();

    private void SimulatorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateFastLaunchAvailability();

    private void UpdateFastLaunchAvailability()
    {
        if (FastLaunchCheck is null) return;
        var isMsfs2024 = SimulatorCombo.SelectedItem is DetectedSimulator detected
            && detected.Definition.Id is "msfs2024-steam" or "msfs2024-store";
        FastLaunchCheck.IsEnabled = !_coordinator.IsRunning && isMsfs2024;
    }

    private async void SaveCustomButton_Click(object sender, RoutedEventArgs e)
    {
        _config.CustomApplications = ReadCustomApplications();
        await JsonStore.SaveAtomicAsync(_paths.ConfigFile, _config);
        AppendStatus($"Saved {_config.CustomApplications.Count} persistent custom application rule(s).");
        await ScanSystemAsync();
    }

    private async Task ScanSystemAsync()
    {
        ScanButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        SetStateDisplay("SCANNING THIS PC", "AccentBrush");
        AppendStatus("Scanning installed simulators, visible applications, and relevant running services…");
        try
        {
            var customApplications = ReadCustomApplications();
            _config.CustomApplications = customApplications;
            var result = await _scanner.ScanAsync(customApplications);
            _applications = result.Applications;
            _services = result.Services;
            SimulatorCombo.ItemsSource = result.Simulators;
            SimulatorGrid.ItemsSource = result.Simulators;
            AppsGrid.ItemsSource = _applications;
            ServicesGrid.ItemsSource = _services;
            SimulatorCombo.SelectedItem = result.Simulators.FirstOrDefault(item => item.Definition.Id == _config.SelectedSimulatorId)
                ?? result.Simulators.FirstOrDefault(item => item.Definition.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase))
                ?? result.Simulators.FirstOrDefault();
            ApplyModeSelection();
            AppendStatus($"Scan complete: {result.Simulators.Count} simulator(s), {result.Applications.Count} app candidate(s), {result.Services.Count} relevant service(s).");
            if (result.Simulators.Count == 0)
                AppendStatus("No supported simulator installation was detected. Rescan after installing or repairing its launcher manifest.");
        }
        catch (Exception exception)
        {
            AppendStatus("SCAN ERROR: " + exception.Message);
        }
        finally
        {
            ScanButton.IsEnabled = !_coordinator.IsRunning;
            SetStateDisplay("READY", "GreenBrush");
            UpdateRecoveryState();
            StartButton.IsEnabled = StartButton.IsEnabled && SimulatorCombo.Items.Count > 0;
        }
    }

    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateModeDescription();
        if (_applications.Count == 0 && _services.Count == 0) return;
        if (ModeCombo.SelectedItem is SessionMode.Automatic)
            SelectAllStoppableItems();
        else
            ClearSelections();
    }

    private void ContentCreatorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applications.Count == 0 && _services.Count == 0) return;
        if (ModeCombo.SelectedItem is SessionMode.Automatic) SelectAllStoppableItems();
        UpdateModeDescription();
    }

    private void ProfileCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingConfig || ProfileCombo.SelectedItem is not OptimizationProfile profile) return;
        var defaults = new OptimizerOptions();
        OptimizationProfiles.Apply(defaults, profile);
        PowerPlanCheck.IsChecked = defaults.UseUltimatePowerPlan;
        PriorityCombo.SelectedItem = defaults.ProcessPriority;
        CpuSetsCheck.IsChecked = defaults.UseVendorAwareCpuSets;
        NvidiaCheck.IsChecked = defaults.EnableNvidiaPersistence;
        FastLaunchCheck.IsChecked = defaults.UseMsfs2024FastLaunch;
        FlushDnsCheck.IsChecked = defaults.FlushDnsCache;
        GameDvrCheck.IsChecked = defaults.DisableGameDvr;
        StandbyMemoryCheck.IsChecked = defaults.ClearStandbyMemory;
        TimerResolutionCheck.IsChecked = defaults.UseHighResolutionTimer;
        FullscreenOptimizationsCheck.IsChecked = defaults.DisableFullscreenOptimizations;
        PowerThrottlingCheck.IsChecked = defaults.DisablePowerThrottling;
        NetworkMemoryCheck.IsChecked = defaults.ApplyNetworkMemoryOptimizations;
        if (profile == OptimizationProfile.Standard)
        {
            foreach (var service in _services) service.Selected = false;
            ServicesGrid.Items.Refresh();
        }
        ServicesGrid.IsEnabled = !_coordinator.IsRunning && profile == OptimizationProfile.Aggressive;
        if (ModeCombo.SelectedItem is SessionMode.Automatic) SelectAllStoppableItems();
        UpdateModeDescription();
    }

    private void UpdateModeDescription()
    {
        if (ModeDescription is null) return;
        var profile = ProfileCombo.SelectedItem is OptimizationProfile selected ? selected : OptimizationProfile.Standard;
        ModeDescription.Text = ModeCombo.SelectedItem is SessionMode.Automatic
            ? ContentCreatorCheck.IsChecked == true
                ? $"Automatic {profile} optimization is active; streaming, capture, audio-routing, and creator helper tools will remain running."
                : profile == OptimizationProfile.Aggressive
                    ? "Aggressive mode selects every safely restartable candidate and applies the stronger CPU/GPU defaults."
                    : "Standard mode selects high and medium impact candidates while leaving lower-impact background items running."
            : profile == OptimizationProfile.Aggressive
                ? "Choose applications and services manually before starting the session. All changed service states are restored on exit."
                : "Choose applications manually before starting the session. Service control is available only in Aggressive profile.";
    }

    private void ApplyModeSelection()
    {
        if (ModeCombo.SelectedItem is SessionMode.Automatic) SelectAllStoppableItems();
    }

    private void SelectAllStoppableItems()
    {
        var profile = ProfileCombo.SelectedItem is OptimizationProfile selectedProfile
            ? selectedProfile
            : OptimizationProfile.Standard;
        SessionSelectionPolicy.SelectAutomatic(_applications, _services, ContentCreatorCheck.IsChecked == true, profile);
        AppsGrid.Items.Refresh();
        ServicesGrid.Items.Refresh();
    }

    private void ClearSelections()
    {
        SessionSelectionPolicy.Clear(_applications, _services);
        AppsGrid.Items.Refresh();
        ServicesGrid.Items.Refresh();
    }

    private List<CustomApplicationRule> ReadCustomApplications()
    {
        if (CustomKillBox is null || CustomRestartBox is null) return _config.CustomApplications;
        var restartPaths = SplitLines(CustomRestartBox.Text)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(parts => NormalizeProcessName(parts[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

        return SplitLines(CustomKillBox.Text)
            .Select(NormalizeProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new CustomApplicationRule
            {
                ProcessName = name,
                RestartExecutablePath = restartPaths.GetValueOrDefault(name, "")
            })
            .ToList();
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeProcessName(string value)
    {
        var name = value.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private async Task ContinuePendingLaunchAsync()
    {
        if (!File.Exists(_paths.PendingLaunchFile))
        {
            AppendStatus("No pending elevated session was found.");
            return;
        }

        try
        {
            var pending = await JsonStore.LoadRequiredAsync<PendingLaunch>(_paths.PendingLaunchFile);
            _config = new AppConfig
            {
                SelectedSimulatorId = pending.SimulatorId,
                SessionMode = pending.SessionMode,
                Options = pending.Options,
                CustomApplications = pending.CustomApplications.ToList()
            };
            ApplyOptionsToControls();
            SimulatorCombo.SelectedItem = SimulatorCombo.Items.Cast<DetectedSimulator>()
                .FirstOrDefault(item => item.Definition.Id == pending.SimulatorId);

            if (pending.SessionMode == SessionMode.Automatic)
            {
                SelectAllStoppableItems();
            }
            else
            {
                var processNames = pending.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var serviceNames = pending.ServiceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var application in _applications) application.Selected = application.CanStop && processNames.Contains(application.ProcessName);
                foreach (var service in _services) service.Selected = service.CanStop && serviceNames.Contains(service.ServiceName);
                AppsGrid.Items.Refresh();
                ServicesGrid.Items.Refresh();
            }

            File.Delete(_paths.PendingLaunchFile);
            AppendStatus("Administrator access granted; continuing the pending session automatically.");
            await StartSelectedSessionAsync(automaticConfirmed: true);
        }
        catch (Exception exception)
        {
            AppendStatus("PENDING SESSION ERROR: " + exception.Message);
            if (File.Exists(_paths.PendingLaunchFile)) File.Delete(_paths.PendingLaunchFile);
        }
    }

    private void SetRunningState(bool running)
    {
        StartButton.IsEnabled = !running && !_coordinator.HasRecoveryJournal;
        CancelButton.IsEnabled = running && _sessionCancellation is not null;
        RestoreButton.IsEnabled = !running && _coordinator.HasRecoveryJournal;
        SimulatorCombo.IsEnabled = !running;
        AppsGrid.IsEnabled = !running;
        ServicesGrid.IsEnabled = !running && ProfileCombo.SelectedItem is OptimizationProfile.Aggressive;
        ScanButton.IsEnabled = !running;
        ModeCombo.IsEnabled = !running;
        ProfileCombo.IsEnabled = !running;
        VrRuntimeCombo.IsEnabled = !running;
        PowerPlanCheck.IsEnabled = !running;
        PriorityCombo.IsEnabled = !running;
        CpuSetsCheck.IsEnabled = !running;
        NvidiaCheck.IsEnabled = !running;
        FastLaunchCheck.IsEnabled = !running
            && SimulatorCombo.SelectedItem is DetectedSimulator detected
            && detected.Definition.Id is "msfs2024-steam" or "msfs2024-store";
        FlushDnsCheck.IsEnabled = !running;
        GameDvrCheck.IsEnabled = !running;
        StandbyMemoryCheck.IsEnabled = !running;
        TimerResolutionCheck.IsEnabled = !running;
        FullscreenOptimizationsCheck.IsEnabled = !running;
        PowerThrottlingCheck.IsEnabled = !running;
        NetworkMemoryCheck.IsEnabled = !running;
        TimeoutBox.IsEnabled = !running;
        CustomKillBox.IsEnabled = !running;
        CustomRestartBox.IsEnabled = !running;
        SaveCustomButton.IsEnabled = !running;
        ContentCreatorCheck.IsEnabled = !running;
        if (!running) SetStateDisplay("READY", "GreenBrush");
    }

    private void SetStateDisplay(string text, string brushResource)
    {
        StateLabel.Text = text;
        StateLamp.Fill = (Brush)FindResource(brushResource);
    }

    private void UpdateRecoveryState()
    {
        RestoreButton.IsEnabled = _coordinator.HasRecoveryJournal && !_coordinator.IsRunning;
        StartButton.IsEnabled = !_coordinator.HasRecoveryJournal && !_coordinator.IsRunning;
        if (SimulatorCombo.Items.Count == 0) StartButton.IsEnabled = false;
    }

    private void AppendStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void UpdatePipeline(SessionProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            var lamps = new[] { Stage1Lamp, Stage2Lamp, Stage3Lamp, Stage4Lamp, Stage5Lamp };
            var labels = new[] { Stage1Label, Stage2Label, Stage3Label, Stage4Label, Stage5Label };
            var current = (int)progress.Stage - 1;
            for (var index = 0; index < lamps.Length; index++)
            {
                lamps[index].Fill = (Brush)FindResource(index < current ? "GreenBrush" : index == current ? "CyanBrush" : "BorderBrush");
                labels[index].Foreground = (Brush)FindResource(index <= current ? "TextBrush" : "MutedTextBrush");
            }
            PipelineDetail.Text = $"STAGE {(int)progress.Stage}/5  /  {progress.Title}  /  {progress.Detail}";
        });
    }

    private void CompletePipeline()
    {
        var lamps = new[] { Stage1Lamp, Stage2Lamp, Stage3Lamp, Stage4Lamp, Stage5Lamp };
        foreach (var lamp in lamps) lamp.Fill = (Brush)FindResource("GreenBrush");
        PipelineDetail.Text = "PIPELINE COMPLETE  /  ORIGINAL SYSTEM STATE RESTORED";
    }
}
