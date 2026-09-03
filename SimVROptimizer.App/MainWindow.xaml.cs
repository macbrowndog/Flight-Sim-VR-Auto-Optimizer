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
    private readonly bool _restoreLastSession;
    private readonly SessionCoordinator _coordinator;
    private readonly SystemScanner _scanner;
    private readonly VrRuntimeLauncher _vrRuntimeLauncher;
    private readonly RecoveryShortcutService _recoveryShortcuts;
    private AppConfig _config = new();
    private IReadOnlyList<RunningAppCandidate> _applications = [];
    private IReadOnlyList<ServiceCandidate> _services = [];
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private bool _allowClose;
    private bool _applyingConfig;
    private bool _applyingScanResults;
    private bool _uiReady;
    private readonly SemaphoreSlim _configSaveLock = new(1, 1);
    private readonly PerformanceDashboardMonitor _dashboardMonitor;
    private readonly DashboardTelemetryServer _toolbarTelemetry;
    private readonly MsfsToolbarPanelInstaller _toolbarPanelInstaller;
    private CpuProfile? _cpuProfile;
    private readonly Queue<PerformanceTelemetrySample> _dashboardHistory = new();
    private int _dashboardStutterCount;
    private int _dashboardCpuSpikeCount;
    private bool _restartRequiredAfterSession;
    private bool _profileDirty;

    public MainWindow(bool continueSession = false, bool restoreLastSession = false)
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var versionText = version is null ? "UNKNOWN" : $"{version.Major}.{version.Minor}.{version.Build}";
        HeaderVersionLabel.Text = $"ANDREW BROWN © 2026 • VERSION {versionText}";
        _continueSession = continueSession;
        _restoreLastSession = restoreLastSession;
        _paths.EnsureCreated();
        var logger = new FileLogger(_paths.LogFile);
        var commands = new CommandRunner();
        var optimizer = new TransactionalOptimizer(commands, _paths, logger);
        _vrRuntimeLauncher = new VrRuntimeLauncher(logger);
        _coordinator = new SessionCoordinator(
            optimizer,
            new SimulatorLauncher(logger),
            _vrRuntimeLauncher,
            new XboxSessionCleanup(commands, logger));
        _scanner = new SystemScanner(commands);
        _recoveryShortcuts = new RecoveryShortcutService();
        _coordinator.StatusChanged += AppendStatus;
        _coordinator.ProgressChanged += UpdatePipeline;
        _coordinator.SimulatorProcessChanged += SimulatorProcessChanged;
        _dashboardMonitor = new PerformanceDashboardMonitor(_paths, logger);
        _dashboardMonitor.SampleReady += DashboardSampleReady;
        try { _cpuProfile = new CpuOptimizer().GetProfile(); }
        catch { _cpuProfile = null; }
        _toolbarTelemetry = new DashboardTelemetryServer(logger, cpuProfile: _cpuProfile);
        _toolbarPanelInstaller = new MsfsToolbarPanelInstaller(
            Path.Combine(AppContext.BaseDirectory, "MSFS", MsfsToolbarPanelInstaller.PackageName));
        AdminLabel.Text = AdminService.IsAdministrator() ? "ADMINISTRATOR" : "STANDARD USER";
        _uiReady = true;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _toolbarTelemetry.StartAsync();
        }
        catch (Exception exception)
        {
            AppendStatus("VR toolbar telemetry bridge could not start: " + exception.Message);
        }
        RefreshToolbarPanelStatus();
        _config = await JsonStore.LoadOrDefaultAsync(_paths.ConfigFile, () => new AppConfig());
        if (!string.IsNullOrWhiteSpace(_config.ActiveSavedProfileName))
            UserProfileStore.TryApply(_config, _config.ActiveSavedProfileName);
        ApplyOptionsToControls();
        ShowCpuProfile();
        UpdateRecoveryState();
        TrySynchronizeRecoveryShortcuts();
        ReportButton.IsEnabled = File.Exists(_paths.RestorationReportFile);

        if (_coordinator.HasRecoveryJournal)
        {
            if (await IsRecordedSessionStillActiveAsync())
            {
                AppendStatus("Recovery journal belongs to another optimizer process that is still running; automatic recovery was not started.");
                if (_restoreLastSession)
                    MessageBox.Show("The recorded optimizer session is still running. Recovery has not been started.", "Session still active", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AppendStatus("Interrupted session detected; starting automatic recovery before scanning.");
                await RestoreRecoveryAsync(automatic: true);
            }
        }
        else if (_restoreLastSession)
        {
            AppendStatus("Restore shortcut opened, but no unfinished session was found.");
            MessageBox.Show("No unfinished VR Auto-Optimizer session was found.", "Recovery", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await ScanSystemAsync();

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
        await SaveConfigAsync();

        var preflight = SessionPreflight.Evaluate(new SessionPreflightContext(
            AdminService.IsAdministrator(),
            _coordinator.HasRecoveryJournal,
            simulator,
            _vrRuntimeLauncher.CheckAvailability(_config.Options.VrRuntime),
            _applications,
            _services,
            _config.Options.Profile));

        if (!automaticConfirmed || !preflight.CanProceed)
        {
            var safetyWindow = new PreflightWindow(preflight, BuildPlannedActionItems(simulator)) { Owner = this };
            if (safetyWindow.ShowDialog() != true) return;
        }

        AppendStatus($"Session safety check passed with {preflight.WarningCount} warning(s).");

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
                        CustomApplications = _config.CustomApplications,
                        ApplicationAfterFlightActions = _applications.ToDictionary(
                            item => item.ProcessName,
                            item => item.AfterFlightAction,
                            StringComparer.OrdinalIgnoreCase)
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
        TryPrepareRecoveryShortcuts();
        if (_config.Options.ContentCreatorMode)
            AppendStatus("Content Creator Mode active: streaming, capture, audio-routing, and creator helper tools are protected.");
        _sessionCancellation = new CancellationTokenSource();
        _sessionTask = RunSessionAsync(simulator, _config.Options, _sessionCancellation.Token);
        await _sessionTask;
    }

    private IReadOnlyList<PreflightItem> BuildPlannedActionItems(SimulatorDefinition simulator)
    {
        var selectedApplications = _applications.Where(item => item.Selected && item.CanStop).ToArray();
        var restartCount = selectedApplications.Count(item =>
            item.IsOneDrive || item.AfterFlightAction == ApplicationAfterFlightAction.Restart);
        var leaveClosedCount = selectedApplications.Length - restartCount;
        var selectedServices = _services.Count(item => item.Selected && item.CanStop);
        var runtime = _config.Options.VrRuntime == VrRuntimePreference.None
            ? "no separately launched VR runtime"
            : _config.Options.VrRuntime.ToString();

        var tuning = new List<string>();
        if (_config.Options.UseUltimatePowerPlan) tuning.Add("CPU-aware power plan");
        tuning.Add($"{_config.Options.ProcessPriority} simulator priority");
        if (_config.Options.UseVendorAwareCpuSets) tuning.Add("vendor-aware CPU topology");
        if (_config.Options.EnableNvidiaPersistence) tuning.Add("NVIDIA persistence");
        if (_config.Options.UseOpenXrTurboMode) tuning.Add("OpenXR Turbo frame pacing");
        if (_config.Options.UseMsfs2024FastLaunch && (simulator.Id is "msfs2024-steam" or "msfs2024-store")) tuning.Add("MSFS FastLaunch");
        if (_config.Options.Profile == OptimizationProfile.Aggressive) tuning.Add("selected Aggressive adjustments");

        return
        [
            new("Planned launch", PreflightStatus.Action,
                $"{_config.SessionMode} {_config.Options.Profile} session will launch {simulator.Name} with {runtime}."),
            new("Applications", PreflightStatus.Action,
                $"{selectedApplications.Length} selected application(s) will close: {restartCount} will restart after the flight and {leaveClosedCount} will remain closed."),
            new("Services", PreflightStatus.Action,
                selectedServices == 0
                    ? "No services will be stopped."
                    : $"{selectedServices} selected service(s) will stop temporarily and return to their recorded state after the flight."),
            new("Performance actions", PreflightStatus.Action,
                tuning.Count == 0 ? "No optional performance actions are selected." : string.Join(", ", tuning) + ".")
        ];
    }

    private async Task RunSessionAsync(SimulatorDefinition simulator, OptimizerOptions options, CancellationToken cancellationToken)
    {
        var closeApplicationAfterCleanup = false;
        try
        {
            SetStateDisplay("SESSION ACTIVE", "CyanBrush");
            await _coordinator.RunAsync(simulator, options, _applications, _services, cancellationToken);
            AppendStatus("Simulator exited; restoration completed.");
            closeApplicationAfterCleanup = ShowRestorationReport(closeApplicationOnCloseReport: true);
        }
        catch (OperationCanceledException)
        {
            AppendStatus("Session cancelled; restoration completed.");
        }
        catch (Exception exception)
        {
            AppendStatus("ERROR: " + exception.Message);
            MessageBox.Show(exception.Message, "Session error", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowRestorationReport();
        }
        finally
        {
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            if (!_coordinator.HasRecoveryJournal)
            {
                _restartRequiredAfterSession = true;
                AppendStatus("Flight session complete. Restart VR Auto-Optimizer before starting another flight.");
            }
            SetRunningState(false);
            UpdateRecoveryState();
            if (!_coordinator.HasRecoveryJournal) TryMarkRecoveryComplete();
            if (!_coordinator.HasRecoveryJournal) CompletePipeline();
            if (closeApplicationAfterCleanup && !_coordinator.HasRecoveryJournal)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetStateDisplay("ABORTING / RESTORING", "AccentBrush");
        _sessionCancellation?.Cancel();
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e) => await RestoreRecoveryAsync();

    private async void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await JsonStore.LoadRequiredAsync<RestorationReport>(_paths.RestorationReportFile);
            new RestorationReportWindow(report, _paths.RestorationReportFile) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show("The last restoration report could not be opened: " + exception.Message, "Restoration report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RestoreRecoveryAsync(bool automatic = false)
    {
        if (!AdminService.IsAdministrator())
        {
            var answer = automatic ? MessageBoxResult.Yes : MessageBox.Show(
                "Recovery requires administrator access. Relaunch as administrator now?",
                "Recovery",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    AdminService.RelaunchElevated("--restore-last-session");
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
            TryMarkRecoveryComplete();
            ShowRestorationReport();
        }
        catch (Exception exception)
        {
            AppendStatus("RECOVERY ERROR: " + exception.Message);
            MessageBox.Show(exception.Message, "Recovery incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowRestorationReport();
        }
        finally
        {
            SetRunningState(false);
            UpdateRecoveryState();
        }
    }

    private bool ShowRestorationReport(bool closeApplicationOnCloseReport = false)
    {
        if (_coordinator.LastRestorationReport is not { } report) return false;
        ReportButton.IsEnabled = true;
        var reportWindow = new RestorationReportWindow(
            report,
            _paths.RestorationReportFile,
            closeApplicationOnCloseReport) { Owner = this };
        reportWindow.ShowDialog();
        return reportWindow.CloseApplicationRequested;
    }

    private async Task<bool> IsRecordedSessionStillActiveAsync()
    {
        try
        {
            var journal = await JsonStore.LoadRequiredAsync<SessionJournal>(_paths.JournalFile);
            return RecoveryJournalInspector.IsOwnerProcessActive(journal);
        }
        catch
        {
            return false;
        }
    }

    private void TrySynchronizeRecoveryShortcuts()
    {
        try { _recoveryShortcuts.Synchronize(_coordinator.HasRecoveryJournal); }
        catch (Exception exception) { AppendStatus("Recovery shortcut warning: " + exception.Message); }
    }

    private void TryPrepareRecoveryShortcuts()
    {
        try { _recoveryShortcuts.PrepareForSession(); }
        catch (Exception exception) { AppendStatus("Recovery shortcut warning: " + exception.Message); }
    }

    private void TryMarkRecoveryComplete()
    {
        try { _recoveryShortcuts.MarkRecoveryComplete(); }
        catch (Exception exception) { AppendStatus("Recovery shortcut cleanup warning: " + exception.Message); }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _sessionTask is null || _sessionTask.IsCompleted)
        {
            await _dashboardMonitor.StopAsync();
            return;
        }
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
            UseOpenXrTurboMode = OpenXrTurboCheck.IsChecked == true,
            FlushDnsCache = FlushDnsCheck.IsChecked == true,
            DisableGameDvr = GameDvrCheck.IsChecked == true,
            ClearStandbyMemory = StandbyMemoryCheck.IsChecked == true,
            UseHighResolutionTimer = TimerResolutionCheck.IsChecked == true,
            DisableFullscreenOptimizations = FullscreenOptimizationsCheck.IsChecked == true,
            DisablePowerThrottling = PowerThrottlingCheck.IsChecked == true,
            ContentCreatorMode = ContentCreatorCheck.IsChecked == true,
            VrRuntime = VrRuntimeCombo.SelectedItem is VrRuntimePreference runtime ? runtime : VrRuntimePreference.None,
            LaunchTimeoutSeconds = timeout,
            EnablePerformanceDashboard = DashboardEnabledCheck.IsChecked == true,
            LogPerformanceCsv = DashboardCsvCheck.IsChecked == true
        },
        CustomApplications = ReadCustomApplications(),
        ApplicationSelections = new Dictionary<string, bool>(_config.ApplicationSelections, StringComparer.OrdinalIgnoreCase),
        ServiceSelections = new Dictionary<string, bool>(_config.ServiceSelections, StringComparer.OrdinalIgnoreCase),
        ApplicationAfterFlightActions = new Dictionary<string, ApplicationAfterFlightAction>(_config.ApplicationAfterFlightActions, StringComparer.OrdinalIgnoreCase),
        ActiveSavedProfileName = _config.ActiveSavedProfileName,
        SavedProfiles = _config.SavedProfiles
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
        OpenXrTurboCheck.IsChecked = _config.Options.UseOpenXrTurboMode;
        FlushDnsCheck.IsChecked = _config.Options.FlushDnsCache;
        GameDvrCheck.IsChecked = _config.Options.DisableGameDvr;
        StandbyMemoryCheck.IsChecked = _config.Options.ClearStandbyMemory;
        TimerResolutionCheck.IsChecked = _config.Options.UseHighResolutionTimer;
        FullscreenOptimizationsCheck.IsChecked = _config.Options.DisableFullscreenOptimizations;
        PowerThrottlingCheck.IsChecked = _config.Options.DisablePowerThrottling;
        ContentCreatorCheck.IsChecked = _config.Options.ContentCreatorMode;
        VrRuntimeCombo.ItemsSource = Enum.GetValues<VrRuntimePreference>();
        VrRuntimeCombo.SelectedItem = _config.Options.VrRuntime;
        ModeCombo.ItemsSource = Enum.GetValues<SessionMode>();
        ModeCombo.SelectedItem = _config.SessionMode;
        TimeoutBox.Text = _config.Options.LaunchTimeoutSeconds.ToString();
        DashboardEnabledCheck.IsChecked = _config.Options.EnablePerformanceDashboard;
        DashboardCsvCheck.IsChecked = _config.Options.LogPerformanceCsv;
        CustomKillBox.Text = string.Join(Environment.NewLine, _config.CustomApplications.Select(rule => rule.ProcessName));
        CustomRestartBox.Text = string.Join(Environment.NewLine, _config.CustomApplications
            .Where(rule => !string.IsNullOrWhiteSpace(rule.RestartExecutablePath))
            .Select(rule => $"{rule.ProcessName}={rule.RestartExecutablePath}"));
        RefreshSavedProfiles();
        ApplyCpuAwareControlRules();
        UpdateModeDescription();
        _profileDirty = false;
        _applyingConfig = false;
        UpdateProfileStatus();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        await _toolbarTelemetry.DisposeAsync();
    }

    private void RefreshSavedProfiles()
    {
        if (SavedProfileCombo is null) return;
        SavedProfileCombo.ItemsSource = _config.SavedProfiles
            .Select(profile => profile.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SavedProfileCombo.Text = _config.ActiveSavedProfileName ?? "";
    }

    private string SelectedProfileName() =>
        (SavedProfileCombo.SelectedItem as string ?? SavedProfileCombo.Text).Trim();

    private void ProfileSetting_Changed(object sender, RoutedEventArgs e) => MarkProfileDirty();

    private void SavedProfileCombo_Changed(object sender, RoutedEventArgs e) => UpdateProfileStatus();

    private void MarkProfileDirty()
    {
        if (!_uiReady || _applyingConfig || _applyingScanResults || ProfileStatusText is null) return;
        _profileDirty = true;
        UpdateProfileStatus();
    }

    private void UpdateProfileStatus()
    {
        if (!_uiReady || ProfileStatusText is null || SaveProfileButton is null || RevertProfileButton is null) return;

        var active = _config.ActiveSavedProfileName;
        var entered = SelectedProfileName();
        var enteredProfileExists = _config.SavedProfiles.Any(profile =>
            profile.Name.Equals(entered, StringComparison.OrdinalIgnoreCase));
        var enteredIsActive = !string.IsNullOrWhiteSpace(active)
            && entered.Equals(active, StringComparison.OrdinalIgnoreCase);
        var running = _coordinator.IsRunning;

        if (enteredProfileExists && !enteredIsActive)
        {
            ProfileStatusText.Text = $"PROFILE SELECTED / Choose LOAD to use '{entered}'.";
            ProfileStatusText.Foreground = (Brush)FindResource("CyanBrush");
            SaveProfileButton.IsEnabled = false;
            RevertProfileButton.IsEnabled = false;
            return;
        }

        if (enteredIsActive && _profileDirty)
        {
            ProfileStatusText.Text = $"PROFILE MODIFIED / '{active}' has unsaved changes.";
            ProfileStatusText.Foreground = (Brush)FindResource("AccentBrush");
            SaveProfileButton.IsEnabled = !running;
            RevertProfileButton.IsEnabled = !running;
            return;
        }

        if (enteredIsActive)
        {
            ProfileStatusText.Text = $"PROFILE SAVED / '{active}' matches the stored profile.";
            ProfileStatusText.Foreground = (Brush)FindResource("GreenBrush");
            SaveProfileButton.IsEnabled = false;
            RevertProfileButton.IsEnabled = false;
            return;
        }

        ProfileStatusText.Text = string.IsNullOrWhiteSpace(entered)
            ? "CURRENT SETTINGS / Grid choices are auto-saved; enter a name to create a profile."
            : $"NEW PROFILE / Save current settings as '{entered}'.";
        ProfileStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
        SaveProfileButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(entered);
        RevertProfileButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(active) && _profileDirty;
    }

    private void ShowCpuProfile()
    {
        try
        {
            var profile = _cpuProfile ?? new CpuOptimizer().GetProfile();
            _cpuProfile = profile;
            _toolbarTelemetry.SetCpuProfile(profile);
            var type = profile.IsAmd && profile.IsX3D ? "AMD X3D"
                : profile.IsIntel && profile.IsHybrid ? "Intel hybrid"
                : profile.IsAmd ? "AMD"
                : profile.IsIntel ? "Intel"
                : "Unknown vendor";
            if (profile.IsAmd && profile.IsX3D)
            {
                PowerPlanCheck.Content = "AMD X3D / Windows Balanced (recommended)";
                PowerPlanCheck.ToolTip = "Keeps or temporarily selects Windows Balanced so AMD's chipset drivers, Game Mode and Windows scheduler can manage the cache CCD correctly. The original plan is restored after the flight.";
            }
            else
            {
                PowerPlanCheck.Content = "ULTIMATE PERFORMANCE / temporary";
                PowerPlanCheck.ToolTip = "Temporarily enables Ultimate Performance for the flight and restores the original Windows power plan afterward.";
            }
            var wasApplyingConfig = _applyingConfig;
            _applyingConfig = true;
            ApplyCpuAwareControlRules();
            _applyingConfig = wasApplyingConfig;
            CpuInfoText.Text = $"Detected CPU: {profile.Model} · {type} · {profile.PhysicalCoreCount} cores / {profile.LogicalProcessorCount} logical processors";
            DashCpuName.Text = profile.Model;
            var groups = Math.Max(1, profile.CpuSets.Select(item => item.Group).Distinct().Count());
            var groupText = groups == 1 ? "1 processor group" : $"{groups} processor groups";
            var plan = CpuTopologyPlanner.Create(profile, _config.Options.UseVendorAwareCpuSets);
            CpuInfoText.Text += $" · {groupText}\nCPU strategy: {plan.Description}";
        }
        catch (Exception exception)
        {
            CpuInfoText.Text = "CPU topology unavailable: " + exception.Message;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanSystemAsync();

    private void SimulatorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSimulatorOptionAvailability();
        MarkProfileDirty();
    }

    private void UpdateSimulatorOptionAvailability()
    {
        if (FastLaunchCheck is null || OpenXrTurboCheck is null) return;
        var simulatorId = (SimulatorCombo.SelectedItem as DetectedSimulator)?.Definition.Id;
        var isMsfs2024 = simulatorId is "msfs2024-steam" or "msfs2024-store";
        FastLaunchCheck.IsEnabled = !_coordinator.IsRunning && isMsfs2024;
        OpenXrTurboCheck.IsEnabled = !_coordinator.IsRunning && OpenXrTurboLayer.IsPackageAvailable;
    }

    private async void SaveCustomButton_Click(object sender, RoutedEventArgs e)
    {
        _config.CustomApplications = ReadCustomApplications();
        await SaveConfigAsync();
        AppendStatus($"Saved {_config.CustomApplications.Count} persistent custom application rule(s).");
        await ScanSystemAsync();
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        try
        {
            CaptureCurrentControls();
            var saved = UserProfileStore.SaveOrReplace(_config, SavedProfileCombo.Text);
            await SaveConfigAsync();
            RefreshSavedProfiles();
            _profileDirty = false;
            UpdateProfileStatus();
            AppendStatus($"Saved user profile '{saved.Name}' with the current simulator, options, applications, and services.");
            MessageBox.Show(
                $"User profile '{saved.Name}' was saved successfully.\n\nThe simulator, workflow, VR runtime, optimization settings, application and service choices, after-flight actions, and custom app list have been stored.",
                "Profile saved successfully",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Save user profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show("The user profile could not be saved: " + exception.Message, "Save user profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SimulatorProcessChanged(int? processId)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (processId.HasValue && DashboardEnabledCheck.IsChecked == true)
            {
                var simulatorName = (SimulatorCombo.SelectedItem as DetectedSimulator)?.Name ?? "Flight simulator";
                _toolbarTelemetry.BeginSession(simulatorName, _config.Options.UseOpenXrTurboMode);
                _dashboardHistory.Clear();
                DashFps.Text = "—";
                DashAverageFps.Text = "—";
                DashOneLow.Text = "—";
                DashFrameTime.Text = "—";
                FpsGraphLine.Points.Clear();
                _dashboardStutterCount = 0;
                _dashboardCpuSpikeCount = 0;
                UpdateDashboardCounterDisplay();
                DashboardStatusText.Text = $"LIVE — monitoring simulator PID {processId.Value}.";
                try
                {
                    await _dashboardMonitor.StartAsync(processId.Value, DashboardCsvCheck.IsChecked == true);
                }
                catch (Exception exception)
                {
                    _toolbarTelemetry.EndSession("Performance monitor could not start: " + exception.Message);
                    DashboardStatusText.Text = "MONITOR ERROR — " + exception.Message;
                    AppendStatus("Performance dashboard could not start: " + exception.Message);
                }
            }
            else
            {
                await _dashboardMonitor.StopAsync();
                _toolbarTelemetry.EndSession(processId.HasValue
                    ? "Performance monitoring is disabled for this session"
                    : "Flight session complete");
                DashboardStatusText.Text = processId.HasValue
                    ? "MONITORING DISABLED FOR THIS SESSION"
                    : "SESSION COMPLETE — final readings retained.";
            }
        });
    }

    private void DashboardSampleReady(PerformanceTelemetrySample sample)
    {
        _toolbarTelemetry.Publish(sample);
        Dispatcher.BeginInvoke(() => UpdateDashboard(sample));
    }

    private void ResetDashboardStuttersButton_Click(object sender, RoutedEventArgs e)
    {
        _dashboardStutterCount = 0;
        _toolbarTelemetry.ResetStutterCounter();
        UpdateDashboardCounterDisplay();
        AppendStatus("Performance dashboard frame-time stutter counter reset.");
    }

    private void ResetDashboardCpuSpikesButton_Click(object sender, RoutedEventArgs e)
    {
        _dashboardCpuSpikeCount = 0;
        _toolbarTelemetry.ResetCpuSpikeCounter();
        UpdateDashboardCounterDisplay();
        AppendStatus("Performance dashboard CPU spike counter reset.");
    }

    private void RefreshToolbarPanelStatus()
    {
        if (ToolbarPanelStatusText is null) return;
        var status = _toolbarPanelInstaller.GetStatus();
        var bridge = _toolbarTelemetry.IsRunning
            ? $"Telemetry bridge ready at {_toolbarTelemetry.Endpoint}."
            : "Telemetry bridge is not running.";
        ToolbarPanelStatusText.Text = status.Detail + "\n" + bridge;
        ToolbarPanelPathText.Text = status.CommunityFolder is null
            ? "COMMUNITY FOLDER / NOT DETECTED"
            : "COMMUNITY FOLDER / " + status.CommunityFolder;
        InstallToolbarPanelButton.Content = status.IsInstalled ? "UPDATE PANEL" : "INSTALL PANEL";
        InstallToolbarPanelButton.IsEnabled = status.PackageAvailable && !_coordinator.IsRunning;
        RemoveToolbarPanelButton.IsEnabled = status.IsInstalled && !_coordinator.IsRunning;
    }

    private async void InstallToolbarPanelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _toolbarPanelInstaller.InstallAsync();
            AppendStatus("Installed the VR Optimizer toolbar package. Restart MSFS 2024 to load it.");
            RefreshToolbarPanelStatus();
            MessageBox.Show(
                $"The VR Optimizer toolbar panel was installed to:\n\n{status.TargetDirectory}\n\nRestart MSFS 2024, begin a flight, then open VR OPTIMIZER from the in-simulator toolbar.",
                "VR Optimizer panel installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show("The VR Dashboard toolbar panel could not be installed: " + exception.Message,
                "VR Dashboard installation", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshToolbarPanelStatus();
        }
    }

    private async void RemoveToolbarPanelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _toolbarPanelInstaller.RemoveAsync();
            AppendStatus("Removed the VR Optimizer toolbar package. Restart MSFS 2024 to unload it.");
            RefreshToolbarPanelStatus();
        }
        catch (Exception exception)
        {
            MessageBox.Show("The VR Dashboard toolbar panel could not be removed: " + exception.Message,
                "VR Dashboard removal", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshToolbarPanelStatus();
        }
    }

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !ReferenceEquals(MainTabs.SelectedItem, DashboardTab)) return;
        Dispatcher.BeginInvoke(() => DashboardScroll.ScrollToTop());
    }

    private void UpdateDashboard(PerformanceTelemetrySample sample)
    {
        _dashboardHistory.Enqueue(sample);
        while (_dashboardHistory.Count > 120) _dashboardHistory.Dequeue();
        if (sample.Stutter) _dashboardStutterCount++;
        if (sample.CpuSpike) _dashboardCpuSpikeCount++;

        if (sample.Fps.HasValue) DashFps.Text = FormatMetric(sample.Fps, "0.0");
        if (sample.AverageFps.HasValue) DashAverageFps.Text = FormatMetric(sample.AverageFps, "0.0");
        if (sample.OnePercentLowFps.HasValue) DashOneLow.Text = FormatMetric(sample.OnePercentLowFps, "0.0");
        if (sample.FrameTimeMs.HasValue) DashFrameTime.Text = FormatMetric(sample.FrameTimeMs, "0.0");
        DashProcessCpu.Text = $"{sample.SimulatorCpuPercent:0.0}%";
        DashMainThread.Text = sample.MainThreadFrameTimeMs.HasValue
            ? $"{sample.MainThreadFrameTimeMs.Value:0.0} ms"
            : "—";
        DashMemory.Text = $"{sample.SimulatorMemoryMb:N0} MB";
        DashSystemCpu.Text = $"SYSTEM CPU {sample.SystemCpuPercent:0.0}%";
        DashCpuName.Text = _cpuProfile?.Model ?? "CPU MODEL UNAVAILABLE";
        DashboardStatusText.Text = "LIVE — " + sample.FrameSourceStatus;
        DashCoreText.Text = ProcessorLoadSummarizer.Format(
            ProcessorLoadSummarizer.Summarize(_cpuProfile, sample.LogicalProcessorUsage));

        UpdateDashboardCounterDisplay();
        RedrawDashboardGraphs();
    }

    private void UpdateDashboardCounterDisplay()
    {
        DashboardStutterText.Text = $"FRAME-TIME STUTTERS: {_dashboardStutterCount}";
        DashboardStutterText.Foreground = (Brush)FindResource(_dashboardStutterCount > 0 ? "RedBrush" : "GreenBrush");
        DashboardCpuSpikeText.Text = $"CPU SPIKE SAMPLES: {_dashboardCpuSpikeCount}";
        DashboardCpuSpikeText.Foreground = (Brush)FindResource(_dashboardCpuSpikeCount > 0 ? "RedBrush" : "GreenBrush");
    }

    private void DashboardGraph_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawDashboardGraphs();

    private void RedrawDashboardGraphs()
    {
        var history = _dashboardHistory.ToArray();
        if (history.Length == 0) return;
        var validFps = history.Where(sample => sample.Fps.HasValue).Select(sample => sample.Fps!.Value).ToArray();
        var fpsMax = Math.Max(60, Math.Ceiling(validFps.DefaultIfEmpty(60).Max() / 30) * 30);
        DashFpsScale.Text = $"0–{fpsMax:0}";
        FpsGraphLine.Points = BuildGraphPoints(validFps, FpsGraph.ActualWidth, FpsGraph.ActualHeight, fpsMax);
        SystemCpuGraphLine.Points = BuildGraphPoints(history.Select(sample => sample.SystemCpuPercent).ToArray(), CpuGraph.ActualWidth, CpuGraph.ActualHeight, 100);
        ProcessCpuGraphLine.Points = BuildGraphPoints(history.Select(sample => sample.SimulatorCpuPercent).ToArray(), CpuGraph.ActualWidth, CpuGraph.ActualHeight, 100);
    }

    internal static PointCollection BuildGraphPoints(IReadOnlyList<double> values, double width, double height, double maximum)
    {
        var points = new PointCollection();
        if (values.Count == 0 || width <= 0 || height <= 0 || maximum <= 0) return points;
        for (var index = 0; index < values.Count; index++)
        {
            var x = values.Count == 1 ? width : index * width / (values.Count - 1);
            var y = height - Math.Clamp(values[index] / maximum, 0, 1) * height;
            points.Add(new Point(x, y));
        }
        return points;
    }

    private static string FormatMetric(double? value, string format) => value?.ToString(format) ?? "—";

    private async void LoadProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var name = SelectedProfileName();
        if (!UserProfileStore.TryApply(_config, name))
        {
            MessageBox.Show("Choose a saved profile first.", "Load user profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await SaveConfigAsync();
        ApplyOptionsToControls();
        ShowCpuProfile();
        AppendStatus($"Loaded user profile '{_config.ActiveSavedProfileName}'. Rescanning to apply its application and service choices.");
        await ScanSystemAsync();
        var selectedApplications = _applications.Count(item => item.Selected);
        var selectedServices = _services.Count(item => item.Selected);
        MessageBox.Show(
            $"User profile '{_config.ActiveSavedProfileName}' loaded successfully.\n\n" +
            $"Simulator: {(SimulatorCombo.SelectedItem as DetectedSimulator)?.Name ?? "Not detected"}\n" +
            $"Workflow: {_config.SessionMode}\n" +
            $"Optimization: {_config.Options.Profile}\n" +
            $"Selected applications: {selectedApplications}\n" +
            $"Selected services: {selectedServices}",
            "Profile loaded successfully",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void RevertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var activeName = _config.ActiveSavedProfileName;
        if (string.IsNullOrWhiteSpace(activeName) || !UserProfileStore.TryApply(_config, activeName))
        {
            MessageBox.Show("Load a saved profile before reverting changes.", "Revert profile changes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await SaveConfigAsync();
        ApplyOptionsToControls();
        ShowCpuProfile();
        await ScanSystemAsync();
        _profileDirty = false;
        UpdateProfileStatus();
        AppendStatus($"Reverted unsaved changes to user profile '{activeName}'.");
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var name = SelectedProfileName();
        if (!_config.SavedProfiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Choose a saved profile first.", "Delete user profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete the saved profile '{name}'?", "Delete user profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        UserProfileStore.Delete(_config, name);
        await SaveConfigAsync();
        RefreshSavedProfiles();
        _profileDirty = false;
        UpdateProfileStatus();
        AppendStatus($"Deleted user profile '{name}'. Current on-screen settings were left unchanged.");
    }

    private void CaptureCurrentControls()
    {
        foreach (var application in _applications)
        {
            _config.ApplicationSelections[application.ProcessName] = application.Selected;
            _config.ApplicationAfterFlightActions[application.ProcessName] = application.AfterFlightAction;
        }
        foreach (var service in _services)
            _config.ServiceSelections[service.ServiceName] = service.Selected;

        var simulatorId = (SimulatorCombo.SelectedItem as DetectedSimulator)?.Definition.Id
            ?? _config.SelectedSimulatorId
            ?? "";
        var timeout = int.TryParse(TimeoutBox.Text, out var value)
            ? Math.Clamp(value, 30, 900)
            : Math.Clamp(_config.Options.LaunchTimeoutSeconds, 30, 900);
        _config = ReadConfigFromControls(simulatorId, timeout);
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
            _applyingScanResults = true;
            _applications = result.Applications;
            _services = result.Services;
            ApplyAfterFlightActions();
            SimulatorCombo.ItemsSource = result.Simulators;
            SimulatorGrid.ItemsSource = result.Simulators;
            AppsGrid.ItemsSource = _applications;
            ServicesGrid.ItemsSource = _services;
            SimulatorCombo.SelectedItem = result.Simulators.FirstOrDefault(item => item.Definition.Id == _config.SelectedSimulatorId)
                ?? result.Simulators.FirstOrDefault(item => item.Definition.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase))
                ?? result.Simulators.FirstOrDefault();
            ApplyModeSelection();
            _applyingScanResults = false;
            AppendStatus($"Scan complete: {result.Simulators.Count} simulator(s), {result.Applications.Count} app candidate(s), {result.Services.Count} relevant service(s).");
            var classifications = result.Applications
                .GroupBy(item => item.Classification)
                .ToDictionary(group => group.Key, group => group.Count());
            AppendStatus($"Application guidance: {Count(WorkloadClassification.Recommended)} recommended, {Count(WorkloadClassification.Optional)} optional, {Count(WorkloadClassification.Protected)} protected, {Count(WorkloadClassification.Unknown)} unknown.");
            var serviceClassifications = result.Services
                .GroupBy(item => item.Classification)
                .ToDictionary(group => group.Key, group => group.Count());
            AppendStatus($"Service guidance: {ServiceCount(WorkloadClassification.Recommended)} recommended, {ServiceCount(WorkloadClassification.Optional)} optional, {ServiceCount(WorkloadClassification.Protected)} protected, {ServiceCount(WorkloadClassification.Unknown)} unknown.");
            if (result.Simulators.Count == 0)
                AppendStatus("No supported simulator installation was detected. Rescan after installing or repairing its launcher manifest.");

            int Count(WorkloadClassification classification) =>
                classifications.TryGetValue(classification, out var count) ? count : 0;
            int ServiceCount(WorkloadClassification classification) =>
                serviceClassifications.TryGetValue(classification, out var count) ? count : 0;
        }
        catch (Exception exception)
        {
            AppendStatus("SCAN ERROR: " + exception.Message);
        }
        finally
        {
            _applyingScanResults = false;
            ScanButton.IsEnabled = !_coordinator.IsRunning;
            SetStateDisplay("READY", "GreenBrush");
            UpdateRecoveryState();
            StartButton.IsEnabled = StartButton.IsEnabled && SimulatorCombo.Items.Count > 0;
        }
    }

    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateModeDescription();
        if (_applyingConfig) return;
        if (_applications.Count == 0 && _services.Count == 0) return;
        if (ModeCombo.SelectedItem is SessionMode.Automatic)
            SelectAllStoppableItems();
        else
            ClearSelections();
        MarkProfileDirty();
    }

    private void ContentCreatorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingConfig) return;
        if (_applications.Count == 0 && _services.Count == 0) return;
        if (ModeCombo.SelectedItem is SessionMode.Automatic)
            SelectAllStoppableItems();
        else
            ApplySavedSelections();
        UpdateModeDescription();
        MarkProfileDirty();
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
        ApplyCpuAwareControlRules();
        if (profile == OptimizationProfile.Standard)
        {
            foreach (var service in _services) service.Selected = false;
            ServicesGrid.Items.Refresh();
        }
        ServicesGrid.IsEnabled = !_coordinator.IsRunning && profile == OptimizationProfile.Aggressive;
        if (ModeCombo.SelectedItem is SessionMode.Automatic)
            SelectAllStoppableItems();
        else
            ApplySavedSelections();
        UpdateModeDescription();
        MarkProfileDirty();
    }

    private void UpdateModeDescription()
    {
        if (ModeDescription is null) return;
        var profile = ProfileCombo.SelectedItem is OptimizationProfile selected ? selected : OptimizationProfile.Standard;
        ModeDescription.Text = ModeCombo.SelectedItem is SessionMode.Automatic
            ? ContentCreatorCheck.IsChecked == true
                ? $"Automatic {profile} optimization is active; streaming, capture, audio-routing, and creator helper tools will remain running."
                : profile == OptimizationProfile.Aggressive
                    ? "Aggressive mode selects only Recommended applications, includes approved services, and applies the stronger CPU/GPU defaults. Optional and Unknown applications remain unchecked unless you saved a choice."
                    : "Standard mode selects only Recommended applications. Optional and Unknown applications remain unchecked unless you saved a choice."
            : profile == OptimizationProfile.Aggressive
                ? "Choose applications and services manually before starting the session. All changed service states are restored on exit."
                : "Choose applications manually before starting the session. Service control is available only in Aggressive profile.";
    }

    private void ApplyModeSelection()
    {
        if (ModeCombo.SelectedItem is SessionMode.Automatic)
            SelectAllStoppableItems();
        else
            ApplySavedSelections();
    }

    private void SelectAllStoppableItems()
    {
        var profile = ProfileCombo.SelectedItem is OptimizationProfile selectedProfile
            ? selectedProfile
            : OptimizationProfile.Standard;
        SessionSelectionPolicy.SelectAutomatic(_applications, _services, ContentCreatorCheck.IsChecked == true, profile);
        ApplySavedSelections();
        AppsGrid.Items.Refresh();
        ServicesGrid.Items.Refresh();
    }

    private void ClearSelections()
    {
        SessionSelectionPolicy.Clear(_applications, _services);
        ApplySavedSelections();
        AppsGrid.Items.Refresh();
        ServicesGrid.Items.Refresh();
    }

    private void ApplySavedSelections()
    {
        var profile = ProfileCombo.SelectedItem is OptimizationProfile selectedProfile
            ? selectedProfile
            : OptimizationProfile.Standard;
        SessionSelectionPolicy.ApplySaved(
            _applications,
            _services,
            _config.ApplicationSelections,
            _config.ServiceSelections,
            profile,
            ContentCreatorCheck.IsChecked == true);
    }

    private void ApplyAfterFlightActions()
    {
        foreach (var application in _applications)
        {
            if (application.IsOneDrive)
            {
                application.AfterFlightAction = ApplicationAfterFlightAction.Restart;
                continue;
            }

            if (_config.ApplicationAfterFlightActions.TryGetValue(application.ProcessName, out var action))
                application.AfterFlightAction = action;
        }
    }

    private async void CandidateSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox) return;
        var selected = checkBox.IsChecked == true;
        switch (checkBox.DataContext)
        {
            case RunningAppCandidate application:
                application.Selected = selected && application.CanStop;
                _config.ApplicationSelections[application.ProcessName] = application.Selected;
                break;
            case ServiceCandidate service:
                service.Selected = selected
                    && service.CanStop
                    && ProfileCombo.SelectedItem is OptimizationProfile.Aggressive;
                _config.ServiceSelections[service.ServiceName] = service.Selected;
                break;
            default:
                return;
        }

        await SaveSelectionPreferencesAsync();
        MarkProfileDirty();
    }

    private async void AfterFlightSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingConfig || _applyingScanResults) return;
        if (sender is not System.Windows.Controls.ComboBox { DataContext: RunningAppCandidate application } comboBox) return;

        // SelectionChanged can run before the TwoWay binding writes SelectedValue
        // back to the row. Capture the newly selected choice directly.
        if (comboBox.SelectedItem is not ApplicationAfterFlightChoice choice) return;
        var requestedAction = choice.Action;
        if (!ApplicationAfterFlightPolicy.ApplySelection(_config, application, requestedAction)) return;

        await SaveSelectionPreferencesAsync();
        MarkProfileDirty();
    }

    private async Task SaveSelectionPreferencesAsync()
    {
        try
        {
            await SaveConfigAsync();
        }
        catch (Exception exception)
        {
            AppendStatus("Unable to save application/service selections: " + exception.Message);
        }
    }

    private async Task SaveConfigAsync()
    {
        await _configSaveLock.WaitAsync();
        try
        {
            await JsonStore.SaveAtomicAsync(_paths.ConfigFile, _config);
        }
        finally
        {
            _configSaveLock.Release();
        }
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
            _config = UserProfileStore.CreateContinuedConfig(_config, pending);
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
        StartButton.IsEnabled = !running && !_coordinator.HasRecoveryJournal && !_restartRequiredAfterSession;
        CancelButton.IsEnabled = running && _sessionCancellation is not null;
        RestoreButton.IsEnabled = !running && _coordinator.HasRecoveryJournal;
        ReportButton.IsEnabled = !running && File.Exists(_paths.RestorationReportFile);
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
        OpenXrTurboCheck.IsEnabled = !running && OpenXrTurboLayer.IsPackageAvailable;
        FlushDnsCheck.IsEnabled = !running;
        GameDvrCheck.IsEnabled = !running && _cpuProfile is not { IsAmd: true, IsX3D: true };
        if (_cpuProfile is { IsAmd: true, IsX3D: true }) GameDvrCheck.IsChecked = false;
        StandbyMemoryCheck.IsEnabled = !running;
        TimerResolutionCheck.IsEnabled = !running;
        FullscreenOptimizationsCheck.IsEnabled = !running;
        PowerThrottlingCheck.IsEnabled = !running;
        TimeoutBox.IsEnabled = !running;
        CustomKillBox.IsEnabled = !running;
        CustomRestartBox.IsEnabled = !running;
        SaveCustomButton.IsEnabled = !running;
        ContentCreatorCheck.IsEnabled = !running;
        DashboardEnabledCheck.IsEnabled = !running;
        DashboardCsvCheck.IsEnabled = !running;
        UpdateProfileStatus();
        RefreshToolbarPanelStatus();
        if (!running)
            SetStateDisplay(_restartRequiredAfterSession ? "RESTART REQUIRED" : "READY", _restartRequiredAfterSession ? "AccentBrush" : "GreenBrush");
    }

    private void ApplyCpuAwareControlRules()
    {
        if (GameDvrCheck is null) return;
        if (_cpuProfile is { IsAmd: true, IsX3D: true })
        {
            GameDvrCheck.IsChecked = false;
            GameDvrCheck.IsEnabled = false;
            GameDvrCheck.Content = "GAME BAR / kept on for AMD X3D scheduling";
            GameDvrCheck.ToolTip = "Xbox Game Bar must remain available so Windows and AMD's chipset drivers can identify games and direct them to the cache CCD.";
            return;
        }

        GameDvrCheck.Content = "GAME BAR / GAME DVR off temporarily";
        GameDvrCheck.ToolTip = "Temporarily disables Game Bar capture and Game DVR for the flight session.";
        GameDvrCheck.IsEnabled = !_coordinator.IsRunning;
    }

    private void SetStateDisplay(string text, string brushResource)
    {
        StateLabel.Text = text;
        StateLamp.Fill = (Brush)FindResource(brushResource);
    }

    private void UpdateRecoveryState()
    {
        RestoreButton.IsEnabled = _coordinator.HasRecoveryJournal && !_coordinator.IsRunning;
        StartButton.IsEnabled = !_coordinator.HasRecoveryJournal && !_coordinator.IsRunning && !_restartRequiredAfterSession;
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
