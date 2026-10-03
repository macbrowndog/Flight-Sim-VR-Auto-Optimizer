using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly bool _continueSession;
    private readonly bool _restoreLastSession;
    private readonly SessionCoordinator _coordinator;
    private readonly SystemScanner _scanner;
    private readonly OnlineApplicationGuidanceClient _onlineApplicationGuidance = new();
    private readonly ApplicationUpdateChecker _updateChecker = new();
    private readonly VerifiedUpdateDownloader _updateDownloader = new();
    private readonly VrRuntimeLauncher _vrRuntimeLauncher;
    private readonly MsfsOnlineServicesHealthChecker _onlineServicesHealth;
    private readonly PerformanceHistoryStore _performanceHistoryStore;
    private readonly RecoveryShortcutService _recoveryShortcuts;
    private readonly ApplicationRestartTester _applicationRestartTester = new();
    private readonly SimulatorVersionReader _simulatorVersionReader;
    private readonly ObservableCollection<CompanionApplicationRule> _companionApplications = [];
    private AppConfig _config = new();
    private IReadOnlyList<RunningAppCandidate> _applications = [];
    private IReadOnlyList<ServiceCandidate> _services = [];
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private bool _allowClose;
    private bool _applyingConfig;
    private bool _applyingScanResults;
    private bool _applyingDlssIndicatorState;
    private bool _uiReady;
    private readonly SemaphoreSlim _configSaveLock = new(1, 1);
    private readonly PerformanceDashboardMonitor _dashboardMonitor;
    private readonly DashboardTelemetryServer _toolbarTelemetry;
    private readonly MsfsToolbarPanelInstaller _toolbarPanelInstaller;
    private CpuProfile? _cpuProfile;
    private readonly Queue<PerformanceTelemetrySample> _dashboardHistory = new();
    private readonly object _performanceSessionGate = new();
    private readonly List<PerformanceTelemetrySample> _performanceSessionSamples = [];
    private DateTimeOffset? _performanceSessionStartedAt;
    private string _performanceSessionSimulator = "";
    private string _performanceSessionProfile = "";
    private string _performanceSessionSimulatorVersion = "Unknown";
    private string _performanceSessionGpuDriverVersion = "Unknown";
    private string _performanceSessionOptimizerVersion = "Unknown";
    private int? _performanceSessionProcessId;
    private string _performanceSessionSimulatorId = "";
    private IReadOnlyList<PerformanceTrendEntry> _performanceTrend = [];
    private MsfsDisplaySettings? _currentDisplaySettings;
    private MsfsOnlineHealthReport? _lastOnlineHealth;
    private int _dashboardStutterCount;
    private int _dashboardCpuSpikeCount;
    private bool _anomalyTrackingEnabled = true;
    private bool _restartRequiredAfterSession;
    private bool _profileDirty;
    private bool _loadingProfileAssociations;

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
        _simulatorVersionReader = new SimulatorVersionReader(commands);
        _onlineServicesHealth = new MsfsOnlineServicesHealthChecker(commands);
        _performanceHistoryStore = new PerformanceHistoryStore(_paths.PerformanceHistoryFile);
        var optimizer = new TransactionalOptimizer(commands, _paths, logger);
        _vrRuntimeLauncher = new VrRuntimeLauncher(logger);
        var companionApplicationLauncher = new CompanionApplicationLauncher(logger);
        _coordinator = new SessionCoordinator(
            optimizer,
            new SimulatorLauncher(logger),
            _vrRuntimeLauncher,
            new XboxSessionCleanup(logger),
            companionApplicationLauncher);
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
        RefreshVrDiagnostics();
        await RefreshPerformanceHistoryAsync();

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

        if (simulator.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsStatusText.Text = "Checking MSFS online-services readiness before launch…";
            try
            {
                _lastOnlineHealth = await _onlineServicesHealth.CheckAsync();
                UpdateOnlineHealthDisplay(_lastOnlineHealth);
            }
            catch (Exception exception)
            {
                _lastOnlineHealth = null;
                AppendStatus("MSFS online-services preflight check could not complete: " + exception.Message);
            }
        }

        var basePreflight = SessionPreflight.Evaluate(new SessionPreflightContext(
            AdminService.IsAdministrator(),
            _coordinator.HasRecoveryJournal,
            simulator,
            _vrRuntimeLauncher.CheckAvailability(_config.Options.VrRuntime),
            _applications,
            _services,
            _config.Options.Profile));
        var preflight = simulator.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase) && _lastOnlineHealth is not null
            ? new PreflightReport(basePreflight.Items.Append(BuildOnlineServicesPreflightItem(_lastOnlineHealth)).ToArray())
            : basePreflight;

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
                        CompanionApplications = _config.CompanionApplications,
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

    private void RefreshDisplaySettingsButton_Click(object sender, RoutedEventArgs e) => RefreshDisplaySettingsPanel();

    private void RefreshDisplaySettingsPanel()
    {
        RefreshDlssIndicatorState();
        RefreshGpuDriverDetails();
        if (SimulatorCombo.SelectedItem is not DetectedSimulator detectedSimulator
            || !detectedSimulator.Definition.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase))
        {
            ClearDisplaySettingsPanel("Select Microsoft Flight Simulator 2020 or 2024 to read its display and NVIDIA DLSS settings.");
            return;
        }

        var settings = MsfsDisplaySettingsReader.ReadForSimulator(detectedSimulator.Definition.Id);
        if (settings is null)
        {
            ClearDisplaySettingsPanel("UserCfg.opt could not be found or read. Start MSFS once and close it normally, then select Refresh Settings.");
            return;
        }

        _currentDisplaySettings = settings;
        BackupUserCfgButton.IsEnabled = File.Exists(settings.ConfigPath);

        var nvidia = NvidiaDlssSettingsReader.Read(detectedSimulator.Definition.Id);
        SetDisplayMode(settings.Desktop, nvidia.DlssLibraryVersion, DisplayDesktopRenderingText, DisplayDesktopDlssText);
        SetDisplayMode(settings.Vr, nvidia.DlssLibraryVersion, DisplayVrRenderingText, DisplayVrDlssText);
        SetDisplayPreset(nvidia.FrameGeneration, DisplayFgNameText, DisplayFgValueText, DisplayFgSourceText);
        SetDisplayPreset(nvidia.SuperResolution, DisplaySrNameText, DisplaySrValueText, DisplaySrSourceText);
        SetDisplayPreset(nvidia.RayReconstruction, DisplayRrNameText, DisplayRrValueText, DisplayRrSourceText);
        DisplayNvidiaProfileText.Text = "PROFILE  /  " + nvidia.Profile;
        DisplayDlssVersionText.Text = "LOADED DLSS LIBRARY  /  " + nvidia.DlssLibraryVersion;
        DisplayConfigVersionText.Text = "USERCFG VERSION  /  " + settings.UserConfigVersion;
        DisplayConfigPathText.Text = "USERCFG.OPT  /  " + settings.ConfigPath;
        DisplayConfigPathText.ToolTip = settings.ConfigPath;
        DisplayNvidiaStatusText.Text = nvidia.Status;
        DisplaySettingsStatusText.Text = $"SETTINGS READ  /  {detectedSimulator.Name}  /  {DateTime.Now:t}";
        _ = RefreshDisplayRecommendationsAsync(settings, detectedSimulator.Name);
    }

    private async Task RefreshDisplayRecommendationsAsync(MsfsDisplaySettings settings, string simulatorName)
    {
        try
        {
            var sessions = await _performanceHistoryStore.LoadAsync();
            var latest = sessions.FirstOrDefault(session => session.Simulator.Equals(simulatorName, StringComparison.OrdinalIgnoreCase));
            var runtime = OpenXrDiagnostics.Read();
            var recommendation = DisplaySettingRecommendationEngine.Analyze(settings.Vr, latest, runtime.Display.RefreshRate);
            DisplayBalanceText.Text = "BALANCE  /  " + recommendation.BalanceLabel;
            DisplayBalanceText.Foreground = HealthBrush(recommendation.Balance == PerformanceBalance.InsufficientData
                ? DiagnosticHealth.Review : DiagnosticHealth.Ready);
            DisplayDlssRecommendationText.Text = "DLSS  /  " + recommendation.DlssRecommendation;
            DisplayScaleRecommendationText.Text = "RENDER SCALE  /  " + recommendation.RenderScaleRecommendation;
            DisplayTargetRecommendationText.Text = "FRAME-RATE TARGET  /  " + recommendation.FrameRateTarget;
            DisplayRecommendationEvidenceText.Text = "EVIDENCE  /  " + recommendation.Evidence;
            DisplayRecommendationSafetyText.Text = recommendation.SafetyNote;
        }
        catch (Exception exception)
        {
            DisplayBalanceText.Text = "BALANCE  /  Recommendation unavailable: " + exception.Message;
            DisplayBalanceText.Foreground = (Brush)FindResource("AccentBrush");
        }
    }

    private void BackupUserCfgButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDisplaySettings is null) return;
        try
        {
            var backup = MsfsUserCfgBackup.Create(_currentDisplaySettings.ConfigPath);
            AppendStatus("UserCfg.opt backup created: " + backup);
            MessageBox.Show("Backup created successfully:\n\n" + backup + "\n\nNo graphics settings were changed.",
                "UserCfg.opt backup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show("UserCfg.opt could not be backed up: " + exception.Message,
                "UserCfg.opt backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshGpuDriverDetails()
    {
        var drivers = GpuDriverInfoReader.Read();
        if (drivers.Count == 0)
        {
            DisplayGpuText.Text = "GPU  /  NVIDIA or AMD display adapter not detected";
            DisplayGpuDriverText.Text = "DRIVER  /  —";
            return;
        }

        DisplayGpuText.Text = "GPU  /  " + string.Join("  |  ", drivers.Select(driver => driver.Name));
        DisplayGpuDriverText.Text = "DRIVER  /  " + string.Join("  |  ", drivers.Select(driver =>
            driver.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)
                ? $"NVIDIA {driver.DisplayVersion} (Windows {driver.InstalledVersion}) / {driver.DriverDate}"
                : $"AMD {driver.DisplayVersion} / {driver.DriverDate}"));
        DisplayGpuText.ToolTip = DisplayGpuText.Text;
        DisplayGpuDriverText.ToolTip = DisplayGpuDriverText.Text;
    }

    private void RefreshDlssIndicatorState() => SetDlssIndicatorState(NvidiaDlssIndicator.Read());

    private void SetDlssIndicatorState(NvidiaDlssIndicatorState state, string? overrideStatus = null)
    {
        _applyingDlssIndicatorState = true;
        try
        {
            DisplayDlssIndicatorCheck.IsChecked = state.Enabled;
            DisplayDlssIndicatorCheck.Content = state.Enabled ? "OVERLAY / ON" : "OVERLAY / OFF";
            DisplayDlssIndicatorCheck.IsEnabled = state.Available && AdminService.IsAdministrator();
            DisplayDlssIndicatorStatusText.Text = overrideStatus ?? state.Status
                + (state.Available && !AdminService.IsAdministrator()
                    ? " Run VR Auto-Optimizer as administrator to change it."
                    : string.Empty);
        }
        finally
        {
            _applyingDlssIndicatorState = false;
        }
    }

    private void DisplayDlssIndicatorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingDlssIndicatorState || !_uiReady) return;
        var enable = DisplayDlssIndicatorCheck.IsChecked == true;
        try
        {
            var state = NvidiaDlssIndicator.SetEnabled(enable);
            SetDlssIndicatorState(state);
            AppendStatus($"NVIDIA DLSS information overlay set to {(enable ? "ON" : "OFF")} globally.");
        }
        catch (Exception exception)
        {
            var current = NvidiaDlssIndicator.Read();
            SetDlssIndicatorState(current, "DLSS information overlay could not be changed: " + exception.Message);
            AppendStatus("DLSS information overlay could not be changed: " + exception.Message);
        }
    }

    private void ClearDisplaySettingsPanel(string status)
    {
        _currentDisplaySettings = null;
        BackupUserCfgButton.IsEnabled = false;
        DisplaySettingsStatusText.Text = status;
        DisplayDesktopRenderingText.Text = "—";
        DisplayDesktopDlssText.Text = "DLSS MODE  /  —";
        DisplayVrRenderingText.Text = "—";
        DisplayVrDlssText.Text = "DLSS MODE  /  —";
        DisplayNvidiaProfileText.Text = "PROFILE  /  —";
        DisplayDlssVersionText.Text = "LOADED DLSS LIBRARY  /  —";
        DisplayConfigVersionText.Text = "USERCFG VERSION  /  —";
        DisplayConfigPathText.Text = "USERCFG.OPT  /  —";
        DisplayNvidiaStatusText.Text = string.Empty;
        DisplayBalanceText.Text = "BALANCE  /  Complete a monitored VR flight to generate recommendations.";
        DisplayDlssRecommendationText.Text = "DLSS  /  —";
        DisplayScaleRecommendationText.Text = "RENDER SCALE  /  —";
        DisplayTargetRecommendationText.Text = "FRAME-RATE TARGET  /  —";
        DisplayRecommendationEvidenceText.Text = "EVIDENCE  /  —";
        foreach (var text in new[] { DisplayFgValueText, DisplayFgSourceText, DisplaySrValueText, DisplaySrSourceText, DisplayRrValueText, DisplayRrSourceText })
            text.Text = "—";
    }

    private static void SetDisplayMode(MsfsGraphicsDisplaySetting setting, string dlssLibraryVersion,
        System.Windows.Controls.TextBlock renderingText, System.Windows.Controls.TextBlock dlssText)
    {
        var dlss = setting.AntiAliasing.Equals("DLSS", StringComparison.OrdinalIgnoreCase)
            ? setting.DlssMode
            : $"NOT ACTIVE / SAVED {setting.DlssMode}";
        renderingText.Text = NvidiaDlssSettingsReader.FormatRenderingLabel(setting.AntiAliasing, dlssLibraryVersion);
        dlssText.Text = "DLSS MODE  /  " + dlss;
    }

    private static void SetDisplayPreset(NvidiaDlssPresetSetting setting,
        System.Windows.Controls.TextBlock nameText, System.Windows.Controls.TextBlock valueText,
        System.Windows.Controls.TextBlock sourceText)
    {
        nameText.Text = setting.Name.ToUpperInvariant();
        valueText.Text = setting.Value;
        sourceText.Text = setting.Source;
    }

    private async void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e) => await RefreshDiagnosticsAsync();

    private async void ExportSupportPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var versionText = version is null ? "Unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export privacy-scrubbed support package",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"VR-Auto-Optimizer-Support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        DiagnosticsStatusText.Text = "Creating privacy-scrubbed support package…";
        try
        {
            var exporter = new SupportPackageExporter(_paths);
            var context = new SupportPackageContext(
                _config,
                _cpuProfile,
                _applications,
                _services,
                versionText);
            var result = await exporter.ExportAsync(dialog.FileName, context);
            DiagnosticsStatusText.Text = $"SUPPORT PACKAGE READY  /  {result.FileCount} files  /  {result.SizeBytes / 1024d / 1024d:0.0} MB";
            AppendStatus($"Exported privacy-scrubbed support package to {result.Path}.");
            MessageBox.Show(
                "The support package is ready. Usernames, the computer name and personal Windows folder paths were replaced automatically. You may now attach the ZIP to Discord.",
                "Support package exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = "SUPPORT PACKAGE FAILED  /  " + exception.Message;
            MessageBox.Show("The support package could not be created: " + exception.Message,
                "Support package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        DiagnosticsStatusText.Text = "Refreshing OpenXR, online-services and performance-history diagnostics…";
        RefreshVrDiagnostics();
        await RefreshPerformanceHistoryAsync();
        try
        {
            _lastOnlineHealth = await _onlineServicesHealth.CheckAsync();
            UpdateOnlineHealthDisplay(_lastOnlineHealth);
            DiagnosticsStatusText.Text = $"DIAGNOSTICS REFRESHED  /  {DateTime.Now:t}";
        }
        catch (Exception exception)
        {
            OnlineHealthSummaryText.Text = "CHECK FAILED  /  " + exception.Message;
            OnlineHealthSummaryText.Foreground = (Brush)FindResource("RedBrush");
            DiagnosticsStatusText.Text = "Diagnostics completed with an online-services check error.";
        }
    }

    private void RefreshVrDiagnostics()
    {
        var report = OpenXrDiagnostics.Read();
        var selected = _config.Options.VrRuntime;
        var availability = _vrRuntimeLauncher.CheckAvailability(selected);
        var alignment = OpenXrDiagnostics.EvaluateRuntimeAlignment(selected, report.RuntimeName);
        VrHealthSummaryText.Text = report.Health.ToString().ToUpperInvariant() + "  /  " + report.Summary;
        VrHealthSummaryText.Foreground = HealthBrush(report.Health);
        VrSelectedRuntimeText.Text = "AUTO-LAUNCH RUNTIME  /  " + selected;
        VrRuntimeAvailabilityText.Text = "LAUNCHER STATUS  /  " + availability.Detail;
        VrRuntimeAvailabilityText.Foreground = HealthBrush(availability.Available ? DiagnosticHealth.Ready : DiagnosticHealth.Problem);
        VrRuntimeAlignmentText.Text = "RUNTIME ALIGNMENT  /  " + alignment.Detail;
        VrRuntimeAlignmentText.Foreground = HealthBrush(alignment.Health);
        VrRuntimeNameText.Text = "ACTIVE RUNTIME  /  " + report.RuntimeName;
        VrHeadsetText.Text = "HEADSET  /  " + report.Display.Headset;
        var launchers = Enum.GetValues<VrRuntimePreference>()
            .Where(runtime => runtime != VrRuntimePreference.None)
            .Select(runtime => _vrRuntimeLauncher.CheckAvailability(runtime))
            .Where(item => item.Available)
            .Select(item => item.Runtime + (item.AlreadyRunning ? " (running)" : string.Empty))
            .ToArray();
        VrInstalledRuntimesText.Text = "AVAILABLE LAUNCHERS  /  " + (launchers.Length == 0 ? "None detected" : string.Join(", ", launchers));
    }

    private void UpdateOnlineHealthDisplay(MsfsOnlineHealthReport report)
    {
        OnlineHealthSummaryText.Text = report.Health.ToString().ToUpperInvariant() + "  /  " + report.Summary;
        OnlineHealthSummaryText.Foreground = HealthBrush(report.Health);
        OnlineHealthGrid.ItemsSource = report.Items;
        RepairOnlineServicesButton.IsEnabled = !_coordinator.IsRunning && AdminService.IsAdministrator();
    }

    private async void RepairOnlineServicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            MessageBox.Show("Close MSFS before repairing online-service readiness.", "MSFS online services", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AdminService.IsAdministrator())
        {
            MessageBox.Show("Run VR Auto-Optimizer as administrator to use Safe Repair.", "Administrator access required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                "Safe Repair will flush the Windows DNS cache and request startup only for existing, non-disabled Xbox support services. It will not reset or reinstall Gaming Services. Continue?",
                "Safe MSFS online-services repair", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RepairOnlineServicesButton.IsEnabled = false;
        OnlineHealthSummaryText.Text = "REPAIRING  /  Flushing DNS and checking Xbox support services…";
        try
        {
            _lastOnlineHealth = await _onlineServicesHealth.RepairSafeAsync();
            UpdateOnlineHealthDisplay(_lastOnlineHealth);
            AppendStatus("Safe MSFS online-services repair completed and readiness was rechecked.");
        }
        catch (Exception exception)
        {
            OnlineHealthSummaryText.Text = "REPAIR FAILED  /  " + exception.Message;
            OnlineHealthSummaryText.Foreground = (Brush)FindResource("RedBrush");
            AppendStatus("MSFS online-services repair failed: " + exception.Message);
        }
        finally
        {
            RepairOnlineServicesButton.IsEnabled = !_coordinator.IsRunning && AdminService.IsAdministrator();
        }
    }

    private void OpenGamingServicesRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Open Microsoft's official Gaming Services Repair Tool page in your default browser? Nothing will be downloaded or run automatically.",
                "Microsoft Gaming Services Repair Tool", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("https://aka.ms/GamingRepairTool") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show("The Microsoft repair page could not be opened: " + exception.Message,
                "Microsoft Gaming Services Repair Tool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenMsfsStatusButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MsfsOfficialServiceStatusClient.StatusPageUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show("The official MSFS status page could not be opened: " + exception.Message,
                "MSFS online-services status", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static PreflightItem BuildOnlineServicesPreflightItem(MsfsOnlineHealthReport report) =>
        new("MSFS online services",
            report.Health == DiagnosticHealth.Ready ? PreflightStatus.Ready : PreflightStatus.Warning,
            report.Summary + (report.Health == DiagnosticHealth.Ready
                ? string.Empty
                : " Offline flying remains available; open Diagnostics to review or repair these items."));

    private async Task RefreshPerformanceHistoryAsync()
    {
        var sessions = await _performanceHistoryStore.LoadAsync();
        _performanceTrend = PerformanceSessionAnalyzer.BuildTrend(sessions);
        PerformanceHistoryGrid.ItemsSource = _performanceTrend.Reverse().ToArray();
        RedrawPerformanceTrend();
        var changes = _performanceTrend.Where(entry => entry.Changes != "—").ToArray();
        PerformanceTrendChangesText.Text = changes.Length == 0
            ? "VERSION / PROFILE CHANGES  /  None recorded"
            : "VERSION / PROFILE CHANGES  /  " + string.Join("   •   ", changes.Select(entry => $"{entry.StartedLabel}: {entry.Changes}"));
        var comparison = PerformanceSessionAnalyzer.CompareLatestMatching(sessions);
        if (sessions.Count == 0)
        {
            PerformanceComparisonText.Text = "No completed monitored sessions have been recorded yet.";
            return;
        }
        if (comparison is null)
        {
            var latest = sessions[0];
            PerformanceComparisonText.Text = $"LATEST  /  {latest.Simulator} / {latest.Profile} / {latest.StartedLabel}\nA second completed monitored session with the same simulator and profile is required for comparison.";
            return;
        }

        PerformanceComparisonText.Text =
            $"LATEST {comparison.Latest.StartedLabel}  vs  BASELINE {comparison.Baseline.StartedLabel}\n" +
            $"AVG FPS {Delta(comparison.AverageFpsDelta, "0.0")}  /  " +
            $"MAIN THREAD {Delta(comparison.MainThreadMsDelta, "0.0", " ms")}  /  " +
            $"STUTTERS/MIN {Delta(comparison.StuttersPerMinuteDelta, "0.0")}  /  SPIKES/MIN {Delta(comparison.CpuSpikesPerMinuteDelta, "0.0")}";
    }

    private PerformanceTrendEntry[] SelectedPerformanceHistoryEntries() =>
        PerformanceHistoryGrid.SelectedItems.OfType<PerformanceTrendEntry>().ToArray();

    private async void SavePerformanceHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPerformanceHistoryEntries();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more history records to save.", "Save Performance Comparison",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Performance Comparison",
            Filter = "VR Auto-Optimizer comparison (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"VR-Auto-Optimizer-comparison-{DateTime.Now:yyyy-MM-dd-HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var document = new PerformanceComparisonExportDocument
            {
                ExportedAt = DateTimeOffset.Now,
                Sessions = selected.Select(entry => entry.Session).OrderBy(session => session.StartedAt).ToList()
            };
            await JsonStore.SaveAtomicAsync(dialog.FileName, document);
            AppendStatus($"Saved {selected.Length} performance comparison record(s): {dialog.FileName}");
        }
        catch (Exception exception)
        {
            MessageBox.Show("The selected comparison records could not be saved: " + exception.Message,
                "Save Performance Comparison", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeletePerformanceHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPerformanceHistoryEntries();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more history records to delete.", "Delete Performance History",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var noun = selected.Length == 1 ? "record" : "records";
        if (MessageBox.Show(
                $"Permanently delete the selected {selected.Length} performance history {noun}?\n\nThis changes future comparisons and cannot be undone.",
                "Delete Performance History", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = await _performanceHistoryStore.DeleteAsync(selected.Select(entry => entry.Session.Id));
            await RefreshPerformanceHistoryAsync();
            AppendStatus($"Deleted {removed} performance history record(s).");
        }
        catch (Exception exception)
        {
            MessageBox.Show("The selected history records could not be deleted: " + exception.Message,
                "Delete Performance History", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task BeginPerformanceSessionAsync(int processId)
    {
        var simulator = SimulatorCombo.SelectedItem as DetectedSimulator;
        var simulatorId = simulator?.Definition.Id ?? "";
        var simulatorVersion = await _simulatorVersionReader.ReadAsync(processId, simulatorId);
        lock (_performanceSessionGate)
        {
            _performanceSessionSamples.Clear();
            _performanceSessionStartedAt = DateTimeOffset.Now;
            _performanceSessionSimulator = simulator?.Name ?? "Flight simulator";
            _performanceSessionProfile = _config.Options.Profile.ToString();
            _performanceSessionSimulatorVersion = simulatorVersion;
            _performanceSessionProcessId = processId;
            _performanceSessionSimulatorId = simulatorId;
            var drivers = GpuDriverInfoReader.Read();
            _performanceSessionGpuDriverVersion = drivers.Count == 0
                ? "Unknown"
                : string.Join(" | ", drivers.Select(driver => $"{driver.Vendor} {driver.DisplayVersion}"));
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            _performanceSessionOptimizerVersion = version is null ? "Unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void CancelPerformanceSession()
    {
        lock (_performanceSessionGate)
        {
            _performanceSessionSamples.Clear();
            _performanceSessionStartedAt = null;
            _performanceSessionProcessId = null;
            _performanceSessionSimulatorId = "";
        }
    }

    private async Task CompletePerformanceSessionAsync()
    {
        int? processId;
        string simulatorId;
        string simulatorVersion;
        lock (_performanceSessionGate)
        {
            processId = _performanceSessionProcessId;
            simulatorId = _performanceSessionSimulatorId;
            simulatorVersion = _performanceSessionSimulatorVersion;
        }
        if (processId.HasValue && simulatorVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var retry = await _simulatorVersionReader.ReadAsync(processId.Value, simulatorId);
            if (!retry.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                lock (_performanceSessionGate) _performanceSessionSimulatorVersion = retry;
            }
        }

        PerformanceSessionSummary? summary;
        lock (_performanceSessionGate)
        {
            summary = _performanceSessionStartedAt.HasValue
                ? PerformanceSessionAnalyzer.Summarize(_performanceSessionStartedAt.Value, DateTimeOffset.Now,
                    _performanceSessionSimulator, _performanceSessionProfile, _performanceSessionSamples.ToArray(),
                    _performanceSessionSimulatorVersion, _performanceSessionGpuDriverVersion, _performanceSessionOptimizerVersion)
                : null;
            _performanceSessionStartedAt = null;
            _performanceSessionProcessId = null;
            _performanceSessionSimulatorId = "";
            _performanceSessionSamples.Clear();
        }
        if (summary is null) return;
        await _performanceHistoryStore.AppendAsync(summary);
        AppendStatus($"Performance history saved: {summary.Simulator}, {summary.DurationMinutes:0.0} minutes, {summary.FpsSampleCount} FPS sample(s).");
        await RefreshPerformanceHistoryAsync();
    }

    private Brush HealthBrush(DiagnosticHealth health) => (Brush)FindResource(health switch
    {
        DiagnosticHealth.Ready => "GreenBrush",
        DiagnosticHealth.Review => "AccentBrush",
        _ => "RedBrush"
    });

    private static string Delta(double? value, string format, string suffix = "") =>
        value.HasValue ? $"{value.Value.ToString("+" + format + ";-" + format + ";0")}{suffix}" : "—";

    private IReadOnlyList<PreflightItem> BuildPlannedActionItems(SimulatorDefinition simulator)
    {
        var selectedApplications = _applications.Where(item => item.Selected && item.CanStop).ToArray();
        var restartCount = selectedApplications.Count(item =>
            item.IsOneDrive || item.AfterFlightAction == ApplicationAfterFlightAction.Restart);
        var leaveClosedCount = selectedApplications.Length - restartCount;
        var selectedServices = _services.Where(item => item.Selected && item.CanStop).ToArray();
        var manualRestartApplications = selectedApplications.Where(item => !item.CanRestartAfterFlight).ToArray();
        var linkedServices = selectedServices.Where(item => item.HasDependencyLinks).ToArray();
        var enabledCompanions = _config.CompanionApplications.Where(item => item.Enabled).ToArray();
        var missingCompanions = enabledCompanions.Where(item =>
            !File.Exists(Environment.ExpandEnvironmentVariables(item.ExecutablePath.Trim().Trim('"')))).ToArray();
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

        var items = new List<PreflightItem>
        {
            new("Planned launch", PreflightStatus.Action,
                $"{_config.SessionMode} {_config.Options.Profile} session will launch {simulator.Name} with {runtime}."),
            new("Applications", PreflightStatus.Action,
                $"{selectedApplications.Length} selected application(s) will close: {restartCount} will restart after the flight and {leaveClosedCount} will remain closed."),
            new("Services", PreflightStatus.Action,
                selectedServices.Length == 0
                    ? "No services will be stopped."
                    : $"{selectedServices.Length} selected service(s) will stop temporarily and return to their recorded state after the flight."),
            new("Companion apps", PreflightStatus.Action,
                enabledCompanions.Length == 0
                    ? "No companion applications are configured to preload."
                    : $"{enabledCompanions.Length} companion application(s) will launch automatically; " +
                      $"{enabledCompanions.Count(item => item.LaunchTiming == CompanionLaunchTiming.BeforeSimulator)} before the simulator, " +
                      $"{enabledCompanions.Count(item => item.LaunchTiming == CompanionLaunchTiming.AfterSimulatorStarts)} after it starts, and " +
                      $"{enabledCompanions.Count(item => item.LaunchTiming == CompanionLaunchTiming.ReadyToFly)} when ready to fly. " +
                      $"{enabledCompanions.Count(item => item.RunAsAdministrator)} request administrator access, " +
                      $"{enabledCompanions.Count(item => item.MinimizeAfterLaunch)} will minimize after launch, and " +
                      $"{enabledCompanions.Count(item => item.CleanupAction == CompanionCleanupAction.CloseOnSessionEnd)} will close after the flight."),
            new("Performance actions", PreflightStatus.Action,
                tuning.Count == 0 ? "No optional performance actions are selected." : string.Join(", ", tuning) + ".")
        };
        if (manualRestartApplications.Length > 0)
            items.Add(new("Restart warning", PreflightStatus.Warning,
                "No reliable restart command was detected for: " + string.Join(", ", manualRestartApplications.Select(item => item.DisplayName)) + ". These applications will remain closed and must be restarted manually."));
        if (linkedServices.Length > 0)
            items.Add(new("Service dependency review", PreflightStatus.Warning,
                string.Join("  |  ", linkedServices.Select(item => $"{item.DisplayName}: {item.DependencyDetails.Replace(Environment.NewLine, "; ")}"))));
        if (missingCompanions.Length > 0)
            items.Add(new("Companion app warning", PreflightStatus.Warning,
                "Executable not found for: " + string.Join(", ", missingCompanions.Select(item =>
                    string.IsNullOrWhiteSpace(item.Name) ? item.ExecutablePath : item.Name)) + ". These entries will be skipped."));
        return items;
    }

    private async Task RunSessionAsync(SimulatorDefinition simulator, OptimizerOptions options, CancellationToken cancellationToken)
    {
        var closeApplicationAfterCleanup = false;
        try
        {
            SetStateDisplay("SESSION ACTIVE", "CyanBrush");
            await _coordinator.RunAsync(simulator, options, _applications, _services, _config.CompanionApplications, cancellationToken);
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
            LogPerformanceCsv = DashboardCsvCheck.IsChecked == true,
            EnableOnlineApplicationGuidance = OnlineGuidanceCheck.IsChecked == true
        },
        CustomApplications = ReadCustomApplications(),
        CompanionApplications = ReadCompanionApplications(),
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
        OnlineGuidanceCheck.IsChecked = _config.Options.EnableOnlineApplicationGuidance;
        CustomKillBox.Text = string.Join(Environment.NewLine, _config.CustomApplications.Select(rule => rule.ProcessName));
        CustomRestartBox.Text = string.Join(Environment.NewLine, _config.CustomApplications
            .Where(rule => !string.IsNullOrWhiteSpace(rule.RestartExecutablePath))
            .Select(rule => $"{rule.ProcessName}={rule.RestartExecutablePath}"));
        _companionApplications.Clear();
        foreach (var rule in _config.CompanionApplications) _companionApplications.Add(CopyCompanionRule(rule));
        CompanionAppsGrid.ItemsSource = _companionApplications;
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
        LoadProfileAssociations(_config.ActiveSavedProfileName);
    }

    private string SelectedProfileName() =>
        (SavedProfileCombo.SelectedItem as string ?? SavedProfileCombo.Text).Trim();

    private void ProfileSetting_Changed(object sender, RoutedEventArgs e) => MarkProfileDirty();

    private void SavedProfileCombo_Changed(object sender, RoutedEventArgs e)
    {
        if (SavedProfileCombo.SelectedItem is string selected) LoadProfileAssociations(selected);
        UpdateProfileStatus();
    }

    private void ProfileAssociation_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingProfileAssociations) return;
        MarkProfileDirty();
    }

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
        var enteredProfile = _config.SavedProfiles.FirstOrDefault(profile =>
            profile.Name.Equals(entered, StringComparison.OrdinalIgnoreCase));
        var enteredIsActive = !string.IsNullOrWhiteSpace(active)
            && entered.Equals(active, StringComparison.OrdinalIgnoreCase);
        var running = _coordinator.IsRunning;
        if (enteredProfile is not null)
        {
            CaptureCurrentControls();
            var differences = UserProfileStore.Diff(_config, enteredProfile, ReadProfileAssociations());
            ProfileDifferencesGrid.ItemsSource = differences;
            if (enteredIsActive) _profileDirty = differences.Count > 0;
        }
        else
        {
            ProfileDifferencesGrid.ItemsSource = null;
        }

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
            ? "CURRENT SETTINGS / Enter a profile name, then Save Changes to store companion apps and the complete setup."
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

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            MessageBox.Show("Finish or restore the current flight session before installing an update.",
                "Update unavailable during session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "CHECKING…";
        try
        {
            var installed = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                ?? new Version(0, 0, 0);
            var update = await _updateChecker.CheckAsync(installed);
            if (update.IsUpdateAvailable)
            {
                AppendStatus($"Update available: VR Auto-Optimizer {update.LatestVersion} (installed {update.CurrentVersion}).");
                if (update.VerifiedInstaller is null)
                {
                    var openRelease = MessageBox.Show(
                        $"VR Auto-Optimizer {update.LatestVersion} is available, but this release does not provide both an installer and its SHA-256 checksum.\n\n" +
                        "For safety, the optimizer will not download it automatically. Open the official GitHub release page?",
                        "Manual update required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (openRelease == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(update.ReleaseUri.AbsoluteUri) { UseShellExecute = true });
                }
                else
                {
                    var answer = MessageBox.Show(
                        $"VR Auto-Optimizer {update.LatestVersion} is available.\n\n" +
                        $"Installed version: {update.CurrentVersion}\n" +
                        $"Latest release: {update.ReleaseName}\n" +
                        $"Installer: {update.VerifiedInstaller.Name}\n\n" +
                        "Yes: download and validate the installer in the app.\n" +
                        "No: open the GitHub release page instead.",
                        "Verified update available", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
                    if (answer == MessageBoxResult.No)
                        Process.Start(new ProcessStartInfo(update.ReleaseUri.AbsoluteUri) { UseShellExecute = true });
                    else if (answer == MessageBoxResult.Yes)
                    {
                        var progress = new Progress<double>(value =>
                            CheckUpdateButton.Content = $"DOWNLOADING {value * 100:0}%");
                        var download = await _updateDownloader.DownloadAsync(
                            update.VerifiedInstaller, _paths.UpdateDirectory, progress);
                        AppendStatus($"Verified update downloaded: {download.Path}; SHA-256 {download.Sha256}.");
                        var install = MessageBox.Show(
                            $"The installer downloaded successfully and its SHA-256 checksum matches the official release.\n\n" +
                            $"SHA-256: {download.Sha256}\n\n" +
                            "Run the installer now? VR Auto-Optimizer will close after it starts.",
                            "Update verified", MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (install == MessageBoxResult.Yes)
                        {
                            Process.Start(new ProcessStartInfo(download.Path) { UseShellExecute = true });
                            _allowClose = true;
                            Application.Current.Shutdown();
                            return;
                        }
                    }
                }
            }
            else
            {
                AppendStatus($"Update check complete: version {update.CurrentVersion} is current.");
                MessageBox.Show(
                    $"VR Auto-Optimizer is up to date.\n\nInstalled version: {update.CurrentVersion}\nLatest release: {update.LatestVersion}",
                    "No update available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            AppendStatus("Update check unavailable: " + exception.Message);
            MessageBox.Show(
                "The latest version could not be checked. Confirm that this PC can access GitHub and try again.\n\n" + exception.Message,
                "Update check unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CheckUpdateButton.Content = "CHECK UPDATE";
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnlineGuidanceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingConfig || !_uiReady) return;
        MarkProfileDirty();
        if (!_coordinator.IsRunning)
            await ScanSystemAsync();
    }

    private void SimulatorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileSimulatorAssociationText is not null)
            ProfileSimulatorAssociationText.Text = "SIMULATOR  /  " + ((SimulatorCombo.SelectedItem as DetectedSimulator)?.Definition.Id ?? "—");
        UpdateSimulatorOptionAvailability();
        MarkProfileDirty();
        if (MainTabs is not null && ReferenceEquals(MainTabs.SelectedItem, DisplaySettingsTab))
            RefreshDisplaySettingsPanel();
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
        _config.CompanionApplications = ReadCompanionApplications();
        await SaveConfigAsync();
        AppendStatus($"Saved {_config.CustomApplications.Count} close rule(s) and {_config.CompanionApplications.Count} companion preload rule(s).");
        await ScanSystemAsync();
    }

    private void AddCompanionAppButton_Click(object sender, RoutedEventArgs e)
    {
        var lastDirectory = _companionApplications
            .Select(item => Path.GetDirectoryName(item.ExecutablePath))
            .LastOrDefault(Directory.Exists);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a companion application",
            InitialDirectory = lastDirectory,
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        AddCompanionApplication(dialog.FileName);
    }

    private void AddCompanionApplication(string executablePath)
    {
        var existing = _companionApplications.FirstOrDefault(item =>
            item.ExecutablePath.Equals(executablePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            CompanionAppsGrid.SelectedItem = existing;
            CompanionAppsGrid.ScrollIntoView(existing);
            return;
        }

        var rule = new CompanionApplicationRule
        {
            Name = Path.GetFileNameWithoutExtension(executablePath),
            ExecutablePath = executablePath,
            RunAsAdministrator = false,
            MinimizeAfterLaunch = false,
            LaunchTiming = CompanionLaunchTiming.BeforeSimulator,
            CleanupAction = CompanionCleanupAction.LeaveRunning
        };
        _companionApplications.Add(rule);
        CompanionAppsGrid.SelectedItem = rule;
        CompanionAppsGrid.ScrollIntoView(rule);
        MarkProfileDirty();
    }

    private void RemoveCompanionAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionAppsGrid.SelectedItem is not CompanionApplicationRule rule) return;
        _companionApplications.Remove(rule);
        MarkProfileDirty();
    }

    private void CompanionAppsGrid_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (_applyingConfig) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            CompanionAppsGrid.Items.Refresh();
            MarkProfileDirty();
        }));
    }

    private void CompanionOption_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingConfig || !_uiReady) return;
        MarkProfileDirty();
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        try
        {
            CaptureCurrentControls();
            var saved = UserProfileStore.SaveOrReplace(_config, SavedProfileCombo.Text, ReadProfileAssociations());
            await SaveConfigAsync();
            RefreshSavedProfiles();
            _profileDirty = false;
            UpdateProfileStatus();
            AppendStatus($"Saved user profile '{saved.Name}' with the current simulator, options, companion apps, applications, and services.");
            MessageBox.Show(
                $"User profile '{saved.Name}' was saved successfully.\n\nThe simulator, workflow, VR runtime, optimization settings, companion app preload list, application and service choices, after-flight actions, and custom close list have been stored.",
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
                await BeginPerformanceSessionAsync(processId.Value);
                _dashboardHistory.Clear();
                DashFps.Text = "—";
                DashAverageFps.Text = "—";
                DashGpuLoad.Text = "—";
                DashGpuMemory.Text = "—";
                DashGpuLoad.ToolTip = "Waiting for GPU-Z sensors";
                DashGpuMemory.ToolTip = "Waiting for GPU-Z sensors";
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
                    CancelPerformanceSession();
                    _toolbarTelemetry.EndSession("Performance monitor could not start: " + exception.Message);
                    DashboardStatusText.Text = "MONITOR ERROR — " + exception.Message;
                    AppendStatus("Performance dashboard could not start: " + exception.Message);
                }
            }
            else
            {
                await _dashboardMonitor.StopAsync();
                if (!processId.HasValue) await CompletePerformanceSessionAsync();
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
        lock (_performanceSessionGate)
        {
            if (_performanceSessionStartedAt.HasValue)
            {
                _performanceSessionSamples.Add(sample);
                if (_performanceSessionSamples.Count > 120_000) _performanceSessionSamples.RemoveAt(0);
            }
        }
        var gpu = _toolbarTelemetry.Publish(sample);
        Dispatcher.BeginInvoke(() => UpdateDashboard(sample, gpu));
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

    private void DashboardAnomalyTrackingButton_Click(object sender, RoutedEventArgs e)
    {
        _anomalyTrackingEnabled = !_anomalyTrackingEnabled;
        _dashboardMonitor.AnomalyTrackingEnabled = _anomalyTrackingEnabled;
        UpdateAnomalyTrackingDisplay();
        UpdateDashboardCounterDisplay();
        AppendStatus(_anomalyTrackingEnabled
            ? "Frame-time stutter and CPU-spike recording resumed."
            : "Frame-time stutter and CPU-spike recording paused; other performance monitoring remains active.");
    }

    private void UpdateAnomalyTrackingDisplay()
    {
        DashboardAnomalyTrackingButton.Content = _anomalyTrackingEnabled
            ? "STUTTER/SPIKE COUNTING / ON"
            : "STUTTER/SPIKE COUNTING / OFF";
        DashboardAnomalyTrackingButton.Foreground = (Brush)FindResource(_anomalyTrackingEnabled ? "GreenBrush" : "AccentBrush");
        DashboardAnomalyTrackingButton.BorderBrush = (Brush)FindResource(_anomalyTrackingEnabled ? "GreenBrush" : "AccentBrush");
        DashboardAnomalyTrackingButton.Background = new SolidColorBrush(
            _anomalyTrackingEnabled ? Color.FromRgb(23, 49, 34) : Color.FromRgb(49, 42, 23));
        if (_dashboardMonitor.IsRunning)
        {
            DashboardStatusText.Text = _anomalyTrackingEnabled
                ? "LIVE — frame-time stutter and CPU-spike recording enabled."
                : "LIVE — stutter/spike recording paused; other metrics remain active.";
        }
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
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (ReferenceEquals(MainTabs.SelectedItem, DashboardTab))
            Dispatcher.BeginInvoke(() => DashboardScroll.ScrollToTop());
        else if (ReferenceEquals(MainTabs.SelectedItem, DisplaySettingsTab))
            Dispatcher.BeginInvoke(() =>
            {
                DisplaySettingsScroll.ScrollToTop();
                RefreshDisplaySettingsPanel();
            });
        else if (ReferenceEquals(MainTabs.SelectedItem, DiagnosticsTab))
            Dispatcher.BeginInvoke(async () =>
            {
                DiagnosticsScroll.ScrollToTop();
                await RefreshDiagnosticsAsync();
            });
    }

    private void UpdateDashboard(PerformanceTelemetrySample sample, GpuTelemetrySnapshot gpu)
    {
        _dashboardHistory.Enqueue(sample);
        while (_dashboardHistory.Count > 120) _dashboardHistory.Dequeue();
        if (sample.Stutter) _dashboardStutterCount++;
        if (sample.CpuSpike) _dashboardCpuSpikeCount++;

        if (sample.Fps.HasValue) DashFps.Text = FormatMetric(sample.Fps, "0.0");
        if (sample.AverageFps.HasValue) DashAverageFps.Text = FormatMetric(sample.AverageFps, "0.0");
        DashGpuLoad.Text = gpu.LoadPercent.HasValue ? $"{gpu.LoadPercent.Value:0.0}%" : "—";
        DashGpuMemory.Text = gpu.MemoryUsedPercent.HasValue ? $"{gpu.MemoryUsedPercent.Value:0.0}%" : "—";
        DashGpuLoad.ToolTip = gpu.Status;
        DashGpuMemory.ToolTip = gpu.Status;
        DashProcessCpu.Text = $"{sample.SimulatorCpuPercent:0.0}%";
        DashMainThread.Text = sample.MainThreadFrameTimeMs.HasValue
            ? $"{sample.MainThreadFrameTimeMs.Value:0.0} ms"
            : "—";
        DashMemory.Text = $"{sample.SimulatorMemoryMb:N0} MB";
        DashSystemCpu.Text = $"SYSTEM CPU {sample.SystemCpuPercent:0.0}%";
        DashCpuName.Text = _cpuProfile?.Model ?? "CPU MODEL UNAVAILABLE";
        DashboardStatusText.Text = _anomalyTrackingEnabled
            ? "LIVE — " + sample.FrameSourceStatus
            : "LIVE — stutter/spike recording paused; " + sample.FrameSourceStatus;
        DashCoreText.Text = ProcessorLoadSummarizer.Format(
            ProcessorLoadSummarizer.Summarize(_cpuProfile, sample.LogicalProcessorUsage));

        UpdateDashboardCounterDisplay();
        RedrawDashboardGraphs();
    }

    private void UpdateDashboardCounterDisplay()
    {
        var paused = _anomalyTrackingEnabled ? "" : "  /  PAUSED";
        DashboardStutterText.Text = $"FRAME-TIME STUTTERS: {_dashboardStutterCount}{paused}";
        DashboardStutterText.Foreground = (Brush)FindResource(!_anomalyTrackingEnabled
            ? "AccentBrush"
            : _dashboardStutterCount > 0 ? "RedBrush" : "GreenBrush");
        DashboardCpuSpikeText.Text = $"CPU SPIKE SAMPLES: {_dashboardCpuSpikeCount}{paused}";
        DashboardCpuSpikeText.Foreground = (Brush)FindResource(!_anomalyTrackingEnabled
            ? "AccentBrush"
            : _dashboardCpuSpikeCount > 0 ? "RedBrush" : "GreenBrush");
    }

    private void DashboardGraph_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawDashboardGraphs();

    private void PerformanceTrendGraph_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPerformanceTrend();

    private void RedrawPerformanceTrend()
    {
        if (PerformanceTrendCanvas is null) return;
        PerformanceTrendCanvas.Children.Clear();
        var width = PerformanceTrendCanvas.ActualWidth;
        var height = PerformanceTrendCanvas.ActualHeight;
        if (_performanceTrend.Count == 0 || width <= 0 || height <= 0) return;
        var maximum = Math.Max(60, Math.Ceiling(_performanceTrend
            .Select(entry => entry.Session.AverageFps)
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(60).Max() / 30) * 30);
        PerformanceTrendScaleText.Text = $"0–{maximum:0} FPS";

        AddLine(entry => entry.Session.AverageFps, (Brush)FindResource("CyanBrush"), 2.2);
        for (var index = 1; index < _performanceTrend.Count; index++)
        {
            if (_performanceTrend[index].Changes == "—") continue;
            var x = _performanceTrend.Count == 1 ? 0 : index * width / (_performanceTrend.Count - 1);
            var marker = new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = (Brush)FindResource("AccentBrush"),
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection([4, 3]),
                ToolTip = _performanceTrend[index].Changes
            };
            PerformanceTrendCanvas.Children.Add(marker);
        }

        void AddLine(Func<PerformanceTrendEntry, double?> selector, Brush brush, double thickness)
        {
            var points = new PointCollection();
            for (var index = 0; index < _performanceTrend.Count; index++)
            {
                var value = selector(_performanceTrend[index]);
                if (!value.HasValue) continue;
                var x = _performanceTrend.Count == 1 ? width / 2 : index * width / (_performanceTrend.Count - 1);
                var y = height - Math.Clamp(value.Value / maximum, 0, 1) * height;
                points.Add(new Point(x, y));
            }
            if (points.Count == 0) return;
            PerformanceTrendCanvas.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = points,
                Stroke = brush,
                StrokeThickness = thickness
            });
        }
    }

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

    private async void DuplicateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var source = SelectedProfileName();
        if (!_config.SavedProfiles.Any(profile => profile.Name.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Choose a saved profile first.", "Duplicate profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = new ProfileNameWindow("Duplicate profile", $"Enter a name for the copy of '{source}'.", source + " Copy") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var duplicate = UserProfileStore.Duplicate(_config, source, prompt.ProfileName);
            await SaveConfigAsync();
            RefreshSavedProfiles();
            SavedProfileCombo.SelectedItem = duplicate.Name;
            AppendStatus($"Duplicated user profile '{source}' as '{duplicate.Name}'.");
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Duplicate profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var source = SelectedProfileName();
        if (!_config.SavedProfiles.Any(profile => profile.Name.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Choose a saved profile first.", "Rename profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = new ProfileNameWindow("Rename profile", $"Enter a new name for '{source}'.", source) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var renamed = UserProfileStore.Rename(_config, source, prompt.ProfileName);
            await SaveConfigAsync();
            RefreshSavedProfiles();
            SavedProfileCombo.SelectedItem = renamed.Name;
            UpdateProfileStatus();
            AppendStatus($"Renamed user profile '{source}' to '{renamed.Name}'.");
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Rename profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedProfileName();
        var profile = _config.SavedProfiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            MessageBox.Show("Choose a saved profile first.", "Export profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var safeName = string.Concat(profile.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export VR Auto-Optimizer profile",
            Filter = "VR Auto-Optimizer profile (*.vrprofile.json)|*.vrprofile.json|JSON file (*.json)|*.json",
            FileName = safeName + ".vrprofile.json",
            AddExtension = true,
            DefaultExt = ".vrprofile.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await UserProfileStore.ExportAsync(profile, dialog.FileName);
            AppendStatus($"Exported user profile '{profile.Name}' to {dialog.FileName}.");
        }
        catch (Exception exception)
        {
            MessageBox.Show("The profile could not be exported: " + exception.Message, "Export profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import VR Auto-Optimizer profile",
            Filter = "VR Auto-Optimizer profile (*.vrprofile.json;*.json)|*.vrprofile.json;*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = await UserProfileStore.ReadImportAsync(dialog.FileName);
            var exists = _config.SavedProfiles.Any(profile => profile.Name.Equals(imported.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && MessageBox.Show($"A profile named '{imported.Name}' already exists. Replace it with the imported profile?",
                    "Import profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            imported = UserProfileStore.Import(_config, imported, replace: exists);
            await SaveConfigAsync();
            RefreshSavedProfiles();
            SavedProfileCombo.SelectedItem = imported.Name;
            AppendStatus($"Imported user profile '{imported.Name}' from {dialog.FileName}. Choose Load to apply it.");
        }
        catch (Exception exception)
        {
            MessageBox.Show("The profile could not be imported: " + exception.Message, "Import profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ProfileAssociations ReadProfileAssociations() => new()
    {
        SimulatorId = (SimulatorCombo.SelectedItem as DetectedSimulator)?.Definition.Id ?? _config.SelectedSimulatorId ?? "",
        Aircraft = ProfileAircraftBox.Text.Trim(),
        VrHeadset = ProfileHeadsetBox.Text.Trim(),
        MonitorConfiguration = ProfileMonitorBox.Text.Trim()
    };

    private void LoadProfileAssociations(string? profileName)
    {
        if (ProfileAircraftBox is null) return;
        var profile = string.IsNullOrWhiteSpace(profileName) ? null : _config.SavedProfiles.FirstOrDefault(item =>
            item.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        _loadingProfileAssociations = true;
        try
        {
            var associations = profile?.Associations ?? new ProfileAssociations();
            ProfileAircraftBox.Text = associations.Aircraft;
            ProfileHeadsetBox.Text = associations.VrHeadset;
            ProfileMonitorBox.Text = associations.MonitorConfiguration;
            var simulatorId = associations.SimulatorId;
            if (string.IsNullOrWhiteSpace(simulatorId)) simulatorId = profile?.SelectedSimulatorId ?? _config.SelectedSimulatorId ?? "—";
            ProfileSimulatorAssociationText.Text = "SIMULATOR  /  " + simulatorId;
        }
        finally
        {
            _loadingProfileAssociations = false;
        }
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
            if (OnlineGuidanceCheck.IsChecked == true)
            {
                AppendStatus("Identity scan: reading local executable metadata, signatures, and hashes. No local details are uploaded.");
                try
                {
                    var locallyIdentified = await Task.Run(() =>
                        SoftwareIdentityInspector.Inspect(result.Applications, result.Services));
                    var identityTotal = result.Applications.Count + result.Services.Count;
                    AppendStatus($"Local identity scan: identified {locallyIdentified} of {identityTotal} application and service entries.");
                    AppendStatus("Online guidance: downloading the privacy-safe application and service catalogue; no local names, hashes, or PC details are uploaded.");
                    var catalogue = await _onlineApplicationGuidance.DownloadAsync();
                    var matchedApplications = OnlineApplicationGuidancePolicy.Apply(result.Applications, catalogue);
                    var matchedServices = OnlineApplicationGuidancePolicy.ApplyServices(result.Services, catalogue);
                    AppendStatus($"Online guidance: matched {matchedApplications} application(s) and {matchedServices} service(s); unmatched items remain set to Keep Running.");
                    var identities = result.Applications.Select(item => item.Identity)
                        .Concat(result.Services.Select(item => item.Identity))
                        .Where(identity => identity is not null)
                        .Cast<SoftwareIdentity>()
                        .ToList();
                    AppendStatus(
                        $"Identity confidence: {identities.Count(item => item.Confidence == SoftwareIdentityConfidence.Verified)} verified, " +
                        $"{identities.Count(item => item.Confidence == SoftwareIdentityConfidence.Identified)} identified, " +
                        $"{identities.Count(item => item.Confidence == SoftwareIdentityConfidence.Likely)} likely, " +
                        $"{identityTotal - identities.Count(item => item.Confidence != SoftwareIdentityConfidence.Unidentified)} unidentified.");
                }
                catch (Exception exception)
                {
                    AppendStatus("Online guidance unavailable; unmatched applications and services remain set to Keep Running. " + exception.Message);
                }
            }
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
            ApplySelectedFirstOrdering();
            _applyingScanResults = false;
            AppendStatus($"Scan complete: {result.Simulators.Count} simulator(s), {result.Applications.Count} app candidate(s), {result.Services.Count} relevant service(s).");
            var classifications = result.Applications
                .GroupBy(item => item.Classification)
                .ToDictionary(group => group.Key, group => group.Count());
            AppendStatus($"Application guidance: {Count(WorkloadClassification.Recommended)} recommend, {Count(WorkloadClassification.Optional) + Count(WorkloadClassification.Unknown)} keep running, {Count(WorkloadClassification.Protected)} protected.");
            var serviceClassifications = result.Services
                .GroupBy(item => item.Classification)
                .ToDictionary(group => group.Key, group => group.Count());
            AppendStatus($"Service guidance: {ServiceCount(WorkloadClassification.Recommended)} recommend, {ServiceCount(WorkloadClassification.Optional) + ServiceCount(WorkloadClassification.Unknown)} keep running, {ServiceCount(WorkloadClassification.Protected)} protected.");
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
                    ? "Aggressive mode selects Recommend applications, includes approved services, and applies the stronger CPU/GPU defaults. Keep Running applications remain unchecked unless you saved a choice."
                    : "Standard mode selects only Recommend applications. Keep Running applications remain unchecked unless you saved a choice."
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
                var allowServiceSelection = selected
                    && service.CanStop
                    && ProfileCombo.SelectedItem is OptimizationProfile.Aggressive;
                if (allowServiceSelection && service.HasDependencyLinks)
                {
                    var decision = MessageBox.Show(
                        $"Review the Windows service relationships before stopping {service.DisplayName}:\n\n{service.DependencyDetails}\n\nThe optimizer will restore the service after the flight, but related features may be unavailable during the session. Select this service?",
                        "Service dependency warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    allowServiceSelection = decision == MessageBoxResult.Yes;
                    if (!allowServiceSelection) checkBox.IsChecked = false;
                }
                service.Selected = allowServiceSelection;
                _config.ServiceSelections[service.ServiceName] = service.Selected;
                break;
            default:
                return;
        }

        ApplySelectedFirstOrdering();
        await SaveSelectionPreferencesAsync();
        MarkProfileDirty();
    }

    private void ApplySelectedFirstOrdering()
    {
        ApplySelectedFirstOrdering(AppsGrid);
        ApplySelectedFirstOrdering(ServicesGrid);
    }

    private static void ApplySelectedFirstOrdering(System.Windows.Controls.DataGrid grid)
    {
        if (grid.ItemsSource is null) return;
        grid.Items.SortDescriptions.Clear();
        grid.Items.SortDescriptions.Add(new SortDescription(nameof(RunningAppCandidate.Selected), ListSortDirection.Descending));
        grid.Items.Refresh();
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

    private async void TestRestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning) return;
        if (sender is not System.Windows.Controls.Button { DataContext: RunningAppCandidate application } button) return;
        if (!application.CanTestRestart)
        {
            MessageBox.Show("Set After Flight to Restart before testing this application.",
                "Test application restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                $"This test will close every running instance of {application.DisplayName} and immediately try to relaunch it.\n\nSave any work in that application before continuing. Start the test now?",
                "Test application restart", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "…";
        try
        {
            var result = await _applicationRestartTester.TestAsync(application);
            AppendStatus("Restart test: " + result.Detail);
            MessageBox.Show(result.Detail, "Test application restart", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            AppendStatus("Restart test failed: " + exception.Message);
            MessageBox.Show("The restart test failed: " + exception.Message,
                "Test application restart", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = application.CanTestRestart;
        }
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

    private List<CompanionApplicationRule> ReadCompanionApplications()
    {
        if (CompanionAppsGrid is not null)
        {
            CompanionAppsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            CompanionAppsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        }
        return _companionApplications
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ExecutablePath))
            .Select(rule =>
            {
                var copy = CopyCompanionRule(rule);
                copy.Name = copy.Name.Trim();
                copy.ExecutablePath = copy.ExecutablePath.Trim().Trim('"');
                copy.LaunchDelaySeconds = Math.Clamp(copy.LaunchDelaySeconds, 0, 300);
                return copy;
            })
            .ToList();
    }

    private static CompanionApplicationRule CopyCompanionRule(CompanionApplicationRule rule) => new()
    {
        Enabled = rule.Enabled,
        RunAsAdministrator = rule.RunAsAdministrator,
        MinimizeAfterLaunch = rule.MinimizeAfterLaunch,
        Name = rule.Name ?? "",
        ExecutablePath = rule.ExecutablePath ?? "",
        LaunchTiming = rule.LaunchTiming,
        LaunchDelaySeconds = rule.LaunchDelaySeconds,
        CleanupAction = rule.CleanupAction
    };

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
        CompanionAppsGrid.IsEnabled = !running;
        AddCompanionAppButton.IsEnabled = !running;
        RemoveCompanionAppButton.IsEnabled = !running;
        SaveCustomButton.IsEnabled = !running;
        ContentCreatorCheck.IsEnabled = !running;
        DashboardEnabledCheck.IsEnabled = !running;
        DashboardCsvCheck.IsEnabled = !running;
        RepairOnlineServicesButton.IsEnabled = !running && AdminService.IsAdministrator();
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
