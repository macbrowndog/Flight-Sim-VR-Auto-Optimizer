using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SimVROptimizer.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Power plan parser", TestPowerPlanParserAsync),
    ("AMD X3D Balanced power policy", TestAmdX3dBalancedPowerPlanAsync),
    ("AMD X3D Xbox Game Bar protection", TestAmdX3dGameBarProtectionAsync),
    ("Service state parser", TestServiceParserAsync),
    ("Transient updater service restoration", TestTransientUpdaterServiceRestorationAsync),
    ("NVIDIA state parser", TestNvidiaParserAsync),
    ("Running service parser", TestRunningServiceParserAsync),
    ("Impact service scan", TestImpactServiceScanAsync),
    ("Critical runtime protection rules", TestCriticalRuntimeProtectionAsync),
    ("Packaged application restart safety", TestPackagedApplicationRestartSafetyAsync),
    ("Denied service stop is non-fatal", TestDeniedServiceStopAsync),
    ("CPU topology scan", TestCpuTopologyAsync),
    ("CPU topology strategy", TestCpuTopologyStrategyAsync),
    ("Processor load summaries", TestProcessorLoadSummariesAsync),
    ("Optimization profiles", TestOptimizationProfilesAsync),
    ("Automatic selection policy", TestAutomaticSelectionPolicyAsync),
    ("Application classification", TestApplicationClassificationAsync),
    ("Service classification", TestServiceClassificationAsync),
    ("Online application guidance", TestOnlineApplicationGuidanceAsync),
    ("Application update checker", TestApplicationUpdateCheckerAsync),
    ("Selection state notification", TestSelectionStateNotificationAsync),
    ("Application after-flight choices", TestApplicationAfterFlightChoicesAsync),
    ("Saved selection preferences", TestSavedSelectionPreferencesAsync),
    ("Named user profiles", TestNamedUserProfilesAsync),
    ("Profiles survive administrator continuation", TestProfilesSurviveContinuationAsync),
    ("Persistent custom application rule", TestCustomApplicationRuleAsync),
    ("Ten simulator configurations", TestSimulatorCatalogAsync),
    ("IL-2 Korea standalone launcher resolution", TestIl2KoreaLauncherResolutionAsync),
    ("MSFS 2024 FastLaunch plans", TestMsfs2024FastLaunchAsync),
    ("Bundled OpenXR Turbo layer", TestOpenXrTurboLayerAsync),
    ("VR runtime shutdown policy", TestVrRuntimeShutdownPolicyAsync),
    ("Xbox post-flight cleanup", TestXboxSessionCleanupAsync),
    ("Performance telemetry calculations", TestPerformanceTelemetryAsync),
    ("MSFS display settings parser", TestMsfsDisplaySettingsParserAsync),
    ("NVIDIA DLSS model preset mapping", TestNvidiaDlssPresetMappingAsync),
    ("Performance monitor sampling", TestPerformanceMonitorSamplingAsync),
    ("VR toolbar telemetry bridge", TestToolbarTelemetryBridgeAsync),
    ("VR toolbar package installer", TestToolbarPackageInstallerAsync),
    ("Log rotation", TestLogRotationAsync),
    ("Pending launch roundtrip", TestPendingLaunchRoundtripAsync),
    ("Recovery journal owner detection", TestRecoveryJournalOwnerDetectionAsync),
    ("Session safety pre-check", TestSessionPreflightAsync),
    ("Dry-run makes no journal", TestDryRunAsync),
    ("Transactional restore", TestTransactionalRestoreAsync),
    ("Failed restore verification retains journal", TestRestoreVerificationFailureAsync),
    ("Applications remain closed after restore", TestApplicationLeftClosedAsync),
    ("Selected applications restart after restore", TestApplicationRestartedAsync),
    ("OneDrive is restored after flight", TestOneDriveRestoredAsync),
    ("Corrupt recovery journal is retained", TestCorruptJournalAsync)
};

static Task TestSessionPreflightAsync()
{
    var simulator = new SimulatorDefinition("test", "Test Simulator", ["test"], LaunchKind.Uri, "test://launch");
    var safeApp = new RunningAppCandidate
    {
        ProcessName = "safeapp", DisplayName = "Safe App", Impact = ImpactLevel.Medium,
        Reason = "Test", InstanceCount = 1, MemoryMb = 10, RestartCommand = "safeapp.exe", CanStop = true,
        Classification = WorkloadClassification.Recommended,
        Selected = true
    };
    var protectedService = new ServiceCandidate
    {
        ServiceName = "Protected", DisplayName = "Protected", Impact = ImpactLevel.Low,
        Reason = "Test", CanStop = false
    };

    var ready = SessionPreflight.Evaluate(new SessionPreflightContext(
        true, false, simulator, new(VrRuntimePreference.None, true, false, "None selected."),
        [safeApp], [protectedService], OptimizationProfile.Standard));
    True(ready.CanProceed);
    Equal(0, ready.BlockedCount);

    var blocked = SessionPreflight.Evaluate(new SessionPreflightContext(
        false, true, null, new(VrRuntimePreference.PimaxPlay, false, false, "Launcher missing."),
        [safeApp], [protectedService], OptimizationProfile.Standard));
    True(!blocked.CanProceed);
    Equal(3, blocked.BlockedCount);
    True(blocked.WarningCount == 1);
    return Task.CompletedTask;
}

static Task TestRecoveryJournalOwnerDetectionAsync()
{
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    var active = new SessionJournal
    {
        OwnerProcessId = current.Id,
        OwnerProcessStartedAtUtc = current.StartTime.ToUniversalTime()
    };
    var stale = new SessionJournal
    {
        OwnerProcessId = int.MaxValue,
        OwnerProcessStartedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
    };
    True(RecoveryJournalInspector.IsOwnerProcessActive(active));
    True(!RecoveryJournalInspector.IsOwnerProcessActive(stale));
    return Task.CompletedTask;
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static Task TestPowerPlanParserAsync()
{
    Equal("381b4222-f694-41f0-9685-ff5bb260df2e", OutputParsers.ParsePowerPlanGuid("Power Scheme GUID: 381B4222-F694-41F0-9685-FF5BB260DF2E  (Balanced)"));
    Equal(null, OutputParsers.ParsePowerPlanGuid("not a power plan"));
    return Task.CompletedTask;
}

static Task TestServiceParserAsync()
{
    True(OutputParsers.IsServiceRunning("STATE              : 4  RUNNING"));
    True(!OutputParsers.IsServiceRunning("STATE              : 1  STOPPED"));
    True(OutputParsers.IsServiceStoppedCleanly("STATE : 1 STOPPED\nWIN32_EXIT_CODE : 0 (0x0)\nSERVICE_EXIT_CODE : 0 (0x0)"));
    True(!OutputParsers.IsServiceStoppedCleanly("STATE : 1 STOPPED\nWIN32_EXIT_CODE : 1067\nSERVICE_EXIT_CODE : 0"));
    return Task.CompletedTask;
}

static async Task TestTransientUpdaterServiceRestorationAsync()
{
    var fixture = CreateFixture();
    var journal = new SessionJournal
    {
        SimulatorName = "Test Sim",
        Mutations =
        [
            new StateMutation(MutationKind.Service, "edgeupdate", "running", "stopped", DateTimeOffset.UtcNow)
        ]
    };
    await JsonStore.SaveAtomicAsync(fixture.Paths.JournalFile, journal);
    fixture.Commands.Handler = (file, args) => file == "sc.exe" && args.FirstOrDefault() == "query"
        ? Ok("STATE : 1 STOPPED\nWIN32_EXIT_CODE : 0 (0x0)\nSERVICE_EXIT_CODE : 0 (0x0)")
        : Ok();

    var report = await fixture.Optimizer.RestoreAsync();

    True(report.Succeeded);
    Equal(1, report.RestoredCount);
    True(!File.Exists(fixture.Paths.JournalFile));
    True(fixture.Commands.Calls.Any(call => call.File == "sc.exe" && string.Join(" ", call.Args) == "start edgeupdate"));
}

static Task TestNvidiaParserAsync()
{
    var values = OutputParsers.ParseNvidiaPersistence("0, Disabled\r\n1, Enabled\r\n");
    Equal(false, values["0"]);
    Equal(true, values["1"]);
    return Task.CompletedTask;
}

static Task TestRunningServiceParserAsync()
{
    var names = OutputParsers.ParseRunningServices("SERVICE_NAME: NahimicService\r\n DISPLAY_NAME: Nahimic\r\n\r\nSERVICE_NAME: BITS\r\nSERVICE_NAME: Apple Mobile Device Service\r\n");
    Equal(3, names.Count);
    Equal("NahimicService", names[0]);
    Equal("BITS", names[1]);
    Equal("Apple Mobile Device Service", names[2]);
    return Task.CompletedTask;
}

static async Task TestImpactServiceScanAsync()
{
    var commands = new FakeCommandRunner
    {
        Handler = (file, args) => file == "sc.exe"
            ? Ok("SERVICE_NAME: NahimicService\r\nSERVICE_NAME: BITS\r\nSERVICE_NAME: GoogleUpdaterService145.0.1\r\nSERVICE_NAME: Apple Mobile Device Service\r\nSERVICE_NAME: DiagTrack\r\nSERVICE_NAME: MDCoreSvc\r\nSERVICE_NAME: GameInputRedistService\r\nSERVICE_NAME: NvContainerLocalSystem\r\nSERVICE_NAME: PiServiceLauncher\r\nSERVICE_NAME: PrivateInternetAccessService\r\nSERVICE_NAME: UnrelatedService\r\n")
            : Ok()
    };
    var result = await new SystemScanner(commands).ScanAsync();
    True(commands.Calls.Any(call => call.File == "sc.exe" && string.Join(" ", call.Args) == "query type= service"));
    True(result.Applications.All(application => !string.IsNullOrWhiteSpace(application.DisplayName)));
    Equal(10, result.Services.Count);
    True(result.Services.Single(service => service.ServiceName == "NahimicService").CanStop);
    Equal(WorkloadClassification.Recommended, result.Services.Single(service => service.ServiceName == "NahimicService").Classification);
    True(!result.Services.Single(service => service.ServiceName == "BITS").CanStop);
    Equal(WorkloadClassification.Protected, result.Services.Single(service => service.ServiceName == "BITS").Classification);
    Equal(ImpactLevel.Medium, result.Services.Single(service => service.ServiceName.StartsWith("GoogleUpdater", StringComparison.Ordinal)).Impact);
    True(result.Services.Single(service => service.ServiceName == "Apple Mobile Device Service").CanStop);
    Equal(WorkloadClassification.Recommended, result.Services.Single(service => service.ServiceName == "Apple Mobile Device Service").Classification);
    Equal(ImpactLevel.Low, result.Services.Single(service => service.ServiceName == "DiagTrack").Impact);
    True(!result.Services.Single(service => service.ServiceName == "MDCoreSvc").CanStop);
    True(!result.Services.Single(service => service.ServiceName == "GameInputRedistService").CanStop);
    True(!result.Services.Single(service => service.ServiceName == "NvContainerLocalSystem").CanStop);
    True(!result.Services.Single(service => service.ServiceName == "PiServiceLauncher").CanStop);
    True(!result.Services.Single(service => service.ServiceName == "PrivateInternetAccessService").CanStop);
}

static Task TestCriticalRuntimeProtectionAsync()
{
    var method = typeof(SystemScanner).GetMethod("IsProtectedApplication", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Protection rule method was not found.");
    foreach (var processName in new[] { "GamingServices", "GameInput", "TextInputHost", "nvcontainer", "vrss_gaze_provider", "TobiiPlatformRuntime", "Navigraph", "Navigraph Simlink", "MOZA Cockpit", "SimRacingStudio", "pi_server", "pi_vst", "pi_overlay", "PiPlayService", "PiPlatformService_64", "MSFS_AutoFPS", "MSFS2024_AutoFPS_by_kayJay" })
        True((bool)(method.Invoke(null, [processName]) ?? false));
    True(!(bool)(method.Invoke(null, ["CCleaner64"]) ?? true));
    True(!(bool)(method.Invoke(null, ["pia-service"]) ?? true));
    return Task.CompletedTask;
}

static Task TestPackagedApplicationRestartSafetyAsync()
{
    True(!ApplicationRestartPolicy.CanLaunchDirectly(@"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_1.0_x64__cw5n1h2txyewy\Widgets.exe"));
    True(!ApplicationRestartPolicy.CanLaunchDirectly(@"C:\Windows\SystemApps\Microsoft.Windows.Search_cw5n1h2txyewy\SearchHost.exe"));
    True(ApplicationRestartPolicy.CanLaunchDirectly(@"C:\Program Files\CCleaner\CCleaner64.exe"));

    var resolver = typeof(SystemScanner).GetMethod("ResolveRestartCommand", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Restart resolver was not found.");
    Equal("none:", (string?)resolver.Invoke(null, ["Widgets", @"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_1.0_x64__cw5n1h2txyewy\Widgets.exe"]));
    Equal(@"shell:shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App", (string?)resolver.Invoke(null, ["PhoneExperienceHost", @"C:\Program Files\WindowsApps\Microsoft.YourPhone_1.0_x64__8wekyb3d8bbwe\PhoneExperienceHost.exe"]));

    var launchResolver = typeof(ApplicationRestarter).GetMethod("ResolveLaunchTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Application launch target resolver was not found.");
    Equal(@"shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App", (string?)launchResolver.Invoke(null, ["PhoneExperienceHost", @"shell:shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App"]));
    return Task.CompletedTask;
}

static async Task TestDeniedServiceStopAsync()
{
    var fixture = CreateFixture();
    fixture.Commands.Handler = (file, args) =>
    {
        if (file != "sc.exe") return Ok();
        if (args.FirstOrDefault() == "query") return Ok("STATE : 4 RUNNING");
        if (args.FirstOrDefault() == "stop") return new CommandResult(5, "[SC] OpenService FAILED 5: Access is denied.", "");
        return Ok();
    };
    var service = new ServiceCandidate { ServiceName = "ProtectedTest", DisplayName = "Protected test", Impact = ImpactLevel.Unknown, Reason = "test", CanStop = true, Selected = true };
    await fixture.Optimizer.BeginAsync("Test Sim", new OptimizerOptions
    {
        Profile = OptimizationProfile.Aggressive,
        UseUltimatePowerPlan = false,
        EnableNvidiaPersistence = false,
        FlushDnsCache = false
    }, [], [service], CancellationToken.None);
    var journal = await JsonStore.LoadRequiredAsync<SessionJournal>(fixture.Paths.JournalFile);
    Equal(0, journal.Mutations.Count);
    await fixture.Optimizer.RestoreAsync();
}

static Task TestCpuTopologyAsync()
{
    var profile = new CpuOptimizer().GetProfile();
    True(!string.IsNullOrWhiteSpace(profile.Vendor));
    True(!string.IsNullOrWhiteSpace(profile.Model));
    True(profile.LogicalProcessorCount > 0);
    True(profile.PhysicalCoreCount > 0);
    return Task.CompletedTask;
}

static Task TestAutomaticSelectionPolicyAsync()
{
    var restartable = new RunningAppCandidate { ProcessName = "Restartable", DisplayName = "Restartable", Impact = ImpactLevel.Medium, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\restartable.exe", CanStop = true, Classification = WorkloadClassification.Recommended };
    var lowImpact = new RunningAppCandidate { ProcessName = "LowImpact", DisplayName = "LowImpact", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\low.exe", CanStop = true, Classification = WorkloadClassification.Optional };
    var manualRestart = new RunningAppCandidate { ProcessName = "Manual", DisplayName = "Manual", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "none:", CanStop = true, Classification = WorkloadClassification.Recommended };
    var protectedApp = new RunningAppCandidate { ProcessName = "Protected", DisplayName = "Protected", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\protected.exe", CanStop = false, Classification = WorkloadClassification.Protected };
    var requiredLasso = new RunningAppCandidate { ProcessName = "ProcessGovernor", DisplayName = "Process Lasso Core Engine", Impact = ImpactLevel.High, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\ProcessGovernor.exe", CanStop = true, SelectionRequired = true, Classification = WorkloadClassification.Recommended };
    var stoppableService = new ServiceCandidate { ServiceName = "Stoppable", DisplayName = "Stoppable", Impact = ImpactLevel.Medium, Reason = "test", CanStop = true };
    var protectedService = new ServiceCandidate { ServiceName = "Protected", DisplayName = "Protected", Impact = ImpactLevel.Low, Reason = "test", CanStop = false };

    SessionSelectionPolicy.SelectAutomatic([restartable, lowImpact, manualRestart, protectedApp, requiredLasso], [stoppableService, protectedService]);
    True(restartable.Selected);
    True(!lowImpact.Selected);
    True(!manualRestart.Selected);
    True(!protectedApp.Selected);
    True(requiredLasso.Selected);
    True(!stoppableService.Selected);
    True(!protectedService.Selected);

    SessionSelectionPolicy.Clear([requiredLasso], []);
    True(requiredLasso.Selected);
    SessionSelectionPolicy.ApplySaved([requiredLasso], [], new Dictionary<string, bool> { ["ProcessGovernor"] = false }, new Dictionary<string, bool>(), OptimizationProfile.Standard, false);
    True(requiredLasso.Selected);

    SessionSelectionPolicy.SelectAutomatic([lowImpact], [], profile: OptimizationProfile.Aggressive);
    True(!lowImpact.Selected);

    var obs = new RunningAppCandidate { ProcessName = "obs64", DisplayName = "OBS Studio", Impact = ImpactLevel.Medium, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\obs64.exe", CanStop = true };
    var streamDeckService = new ServiceCandidate { ServiceName = "ElgatoRemoteControlServer", DisplayName = "Elgato Remote Control", Impact = ImpactLevel.Low, Reason = "test", CanStop = true };
    SessionSelectionPolicy.SelectAutomatic([restartable, obs], [stoppableService, streamDeckService], contentCreatorMode: true, profile: OptimizationProfile.Aggressive);
    True(restartable.Selected);
    True(!obs.Selected);
    True(stoppableService.Selected);
    True(!streamDeckService.Selected);
    return Task.CompletedTask;
}

static async Task TestAmdX3dBalancedPowerPlanAsync()
{
    const string originalPlan = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    const string balancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    var profile = new CpuProfile("AuthenticAMD", "AMD Ryzen 9 9950X3D", false, true, true, false, 16, 32, []);
    var fixture = CreateFixture(cpuProfileProvider: new FakeCpuProfileProvider(profile));
    var activePlan = originalPlan;
    fixture.Commands.Handler = (file, args) =>
    {
        if (file == "powercfg.exe" && args.FirstOrDefault() == "/getactivescheme")
            return Ok("Power Scheme GUID: " + activePlan);
        if (file == "powercfg.exe" && args.FirstOrDefault() == "/setactive")
            activePlan = args[1];
        return Ok();
    };

    await fixture.Optimizer.BeginAsync("Test Sim", new OptimizerOptions
    {
        UseUltimatePowerPlan = true,
        DisableGameDvr = true,
        EnableNvidiaPersistence = false,
        FlushDnsCache = false
    }, [], [], CancellationToken.None);

    var journal = await JsonStore.LoadRequiredAsync<SessionJournal>(fixture.Paths.JournalFile);
    Equal(1, journal.Mutations.Count);
    Equal(MutationKind.PowerPlan, journal.Mutations.Single().Kind);
    Equal(originalPlan, journal.Mutations.Single().OriginalValue);
    Equal(balancedPlan, journal.Mutations.Single().AppliedValue);
    True(!journal.Mutations.Any(mutation => mutation.Kind == MutationKind.RegistryValue));
    True(fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && string.Join(" ", call.Args) == "/setactive " + balancedPlan));
    True(!fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && call.Args.FirstOrDefault() == "/duplicatescheme"));

    await fixture.Optimizer.RestoreAsync();
    True(fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && string.Join(" ", call.Args) == "/setactive " + originalPlan));
    Equal(originalPlan, activePlan);
}

static Task TestAmdX3dGameBarProtectionAsync()
{
    var x3d = new CpuProfile("AuthenticAMD", "AMD Ryzen 9 9950X3D", false, true, true, false, 16, 32, []);
    var amd = new CpuProfile("AuthenticAMD", "AMD Ryzen 9 9950X", false, true, false, false, 16, 32, []);
    True(CpuProtectionPolicy.IsXboxGameBarProtected(x3d, "XboxGameBar"));
    True(CpuProtectionPolicy.IsXboxGameBarProtected(x3d, "GameBar"));
    True(!CpuProtectionPolicy.IsXboxGameBarProtected(amd, "XboxGameBar"));
    True(!CpuProtectionPolicy.IsXboxGameBarProtected(x3d, "XboxPcApp"));
    return Task.CompletedTask;
}

static Task TestCpuTopologyStrategyAsync()
{
    var intelSets = new[]
    {
        CpuSet(0, 0, 0, 0, 10),
        CpuSet(1, 0, 1, 0, 10),
        CpuSet(2, 0, 2, 1, 10, parked: true),
        CpuSet(3, 1, 0, 0, 5),
        CpuSet(4, 1, 1, 1, 5),
        CpuSet(5, 1, 2, 2, 10, allocated: true),
        CpuSet(6, 1, 3, 3, 10, realTime: true)
    };
    var intel = new CpuProfile("GenuineIntel", "Test Hybrid", true, false, false, true, 6, 7, intelSets);
    var intelPlan = CpuTopologyPlanner.Create(intel, true);
    Equal(CpuSchedulingStrategy.IntelPerformanceCpuSets, intelPlan.Strategy);
    Equal(2, intelPlan.ProcessorGroupCount);
    Equal("0,1", string.Join(',', intelPlan.CpuSetIds));
    True(intelPlan.Description.Contains("2 processor groups", StringComparison.Ordinal));

    var disabledPlan = CpuTopologyPlanner.Create(intel, false);
    Equal(CpuSchedulingStrategy.SchedulerManaged, disabledPlan.Strategy);

    var amdX3d = new CpuProfile("AuthenticAMD", "AMD Ryzen 9 9950X3D", false, true, true, true, 16, 32, intelSets);
    var amdPlan = CpuTopologyPlanner.Create(amdX3d, true);
    Equal(CpuSchedulingStrategy.SchedulerManaged, amdPlan.Strategy);
    True(amdPlan.Description.Contains("cache/CCD safety", StringComparison.Ordinal));

    var unavailableSets = intelSets
        .Select(item => item with { Parked = item.EfficiencyClass == 10, Allocated = false, RealTime = false })
        .ToArray();
    var unavailable = intel with { CpuSets = unavailableSets };
    var unavailablePlan = CpuTopologyPlanner.Create(unavailable, true);
    Equal(CpuSchedulingStrategy.SchedulerManaged, unavailablePlan.Strategy);
    True(unavailablePlan.Description.Contains("parked or reserved", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static CpuSetDescriptor CpuSet(
    uint id,
    ushort group,
    byte logicalIndex,
    byte coreIndex,
    byte efficiencyClass,
    bool parked = false,
    bool allocated = false,
    bool realTime = false) =>
    new(id, group, logicalIndex, coreIndex, efficiencyClass, parked, allocated, false, realTime);

static Task TestProcessorLoadSummariesAsync()
{
    var cpuSets = Enumerable.Range(0, 32)
        .Select(index => CpuSet((uint)index, 0, (byte)index, (byte)(index / 2), 10))
        .ToArray();
    var amd = new CpuProfile(
        "AuthenticAMD", "AMD Ryzen 9 9950X3D", false, true, true, false, 16, 32, cpuSets);
    var amdReadings = Enumerable.Range(0, 32)
        .Select(index => index < 16 ? 20d : 70d)
        .ToArray();
    var amdGroups = ProcessorLoadSummarizer.Summarize(amd, amdReadings);
    Equal(2, amdGroups.Count);
    Equal("CCD0", amdGroups[0].Label);
    Equal(20d, amdGroups[0].AveragePercent);
    Equal("CCD1", amdGroups[1].Label);
    Equal(70d, amdGroups[1].AveragePercent);

    var generic = ProcessorLoadSummarizer.Summarize(null, [10d, 40d, 20d]);
    Equal(1, generic.Count);
    Equal("ALL LOGICAL", generic[0].Label);
    Equal(40d, generic[0].PeakPercent);
    Equal(1, generic[0].PeakLogicalProcessor);
    True(ProcessorLoadSummarizer.Format(amdGroups).Contains("CCD0", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static Task TestApplicationClassificationAsync()
{
    var recommended = ApplicationClassifier.Classify("OneDrive", true, true, false, @"exe:C:\Program Files\OneDrive\OneDrive.exe");
    var optional = ApplicationClassifier.Classify("Discord", true, true, false, @"exe:C:\Users\Test\Discord.exe");
    var unknown = ApplicationClassifier.Classify("UncataloguedTool", false, true, false, @"exe:C:\Tools\Unknown.exe");
    var protectedApp = ApplicationClassifier.Classify("MSFS_AutoFPS", true, false, false, @"exe:C:\Tools\MSFS_AutoFPS.exe");
    var custom = ApplicationClassifier.Classify("MyCustomTool", false, true, true, @"exe:C:\Tools\Custom.exe");
    var processLasso = ApplicationClassifier.Classify("ProcessGovernor", true, true, false, @"exe:C:\Program Files\Process Lasso\ProcessGovernor.exe");
    var pia = ApplicationClassifier.Classify("pia-service", true, true, false, @"exe:C:\Program Files\Private Internet Access\pia-service.exe");

    Equal(WorkloadClassification.Recommended, recommended.Classification);
    Equal(WorkloadClassification.Optional, optional.Classification);
    Equal(WorkloadClassification.Unknown, unknown.Classification);
    Equal(WorkloadClassification.Protected, protectedApp.Classification);
    Equal(WorkloadClassification.Optional, custom.Classification);
    Equal(WorkloadClassification.Recommended, processLasso.Classification);
    Equal(WorkloadClassification.Optional, pia.Classification);
    Equal("Unrecognized background application. Review its name and purpose before choosing whether to close it.", unknown.Reason);
    var unknownCandidate = new RunningAppCandidate
    {
        ProcessName = "UncataloguedTool",
        DisplayName = "Uncatalogued Tool",
        Impact = ImpactLevel.Unknown,
        Reason = "No known direct MSFS impact.",
        InstanceCount = 1,
        MemoryMb = 10,
        RestartCommand = "none:",
        CanStop = true,
        Classification = WorkloadClassification.Unknown
    };
    Equal("KEEP RUNNING", unknownCandidate.ClassificationLabel);
    Equal("NO KNOWN", unknownCandidate.ImpactLabel);
    return Task.CompletedTask;
}

static Task TestServiceClassificationAsync()
{
    var recommended = ServiceClassifier.Classify("GoogleUpdaterService", true, true);
    var optional = ServiceClassifier.Classify("SysMain", true, true);
    var protectedService = ServiceClassifier.Classify("GameInputRedistService", true, false);
    var unknown = ServiceClassifier.Classify("ThirdPartyMysteryService", false, true);

    Equal(WorkloadClassification.Recommended, recommended.Classification);
    Equal(WorkloadClassification.Optional, optional.Classification);
    Equal(WorkloadClassification.Protected, protectedService.Classification);
    Equal(WorkloadClassification.Unknown, unknown.Classification);
    Equal("Unrecognized third-party service. Leave it running unless you know what it supports and can safely pause it.", unknown.Reason);
    var unknownCandidate = new ServiceCandidate
    {
        ServiceName = "ThirdPartyMysteryService",
        DisplayName = "Third-party Mystery Service",
        Impact = ImpactLevel.Unknown,
        Reason = "No known direct MSFS impact.",
        CanStop = true,
        Classification = WorkloadClassification.Unknown
    };
    Equal("KEEP RUNNING", unknownCandidate.ClassificationLabel);
    Equal("NO KNOWN", unknownCandidate.ImpactLabel);
    return Task.CompletedTask;
}

static Task TestOnlineApplicationGuidanceAsync()
{
    var review = new RunningAppCandidate
    {
        ProcessName = "Widgets", DisplayName = "Windows Widgets", Impact = ImpactLevel.Unknown,
        Reason = "Local fallback", InstanceCount = 1, MemoryMb = 20, RestartCommand = "none:", CanStop = true,
        Identity = new SoftwareIdentity(
            SoftwareIdentityConfidence.Identified, "Test Publisher", "Windows Widgets", "1.0", "ABC123", true, "Local test")
    };
    var locallyProtected = new RunningAppCandidate
    {
        ProcessName = "MSFS_AutoFPS", DisplayName = "MSFS AutoFPS", Impact = ImpactLevel.Low,
        Reason = "Protected locally", InstanceCount = 1, MemoryMb = 20, RestartCommand = "none:", CanStop = false,
        Classification = WorkloadClassification.Protected
    };
    var catalogue = new OnlineApplicationCatalogue
    {
        SchemaVersion = 1,
        Applications =
        [
            new OnlineApplicationGuidanceEntry
            {
                Names = ["Widgets.exe"], Guidance = "Recommend", Impact = "Low",
                Publishers = ["Test Publisher"], Products = ["Windows Widgets"], Sha256 = ["ABC123"],
                Why = "Optional during a flight.", MsfsImpact = "May use background resources."
            },
            new OnlineApplicationGuidanceEntry
            {
                Names = ["MSFS_AutoFPS"], Guidance = "Recommend", Impact = "High",
                Why = "Remote override.", MsfsImpact = "Remote override."
            }
        ],
        Services =
        [
            new OnlineServiceGuidanceEntry
            {
                Names = ["UpdaterService"], Guidance = "Recommend", Impact = "Medium",
                Why = "Optional updater.", OperationalNote = "May use background resources."
            }
        ]
    };

    Equal(1, OnlineApplicationGuidancePolicy.Apply([review, locallyProtected], catalogue));
    Equal(WorkloadClassification.Recommended, review.Classification);
    Equal("RECOMMEND", review.ClassificationLabel);
    Equal(ImpactLevel.Low, review.Impact);
    Equal(SoftwareIdentityConfidence.Verified, review.Identity!.Confidence);
    True(review.Identity.Source.Contains("SHA-256", StringComparison.Ordinal));
    Equal(WorkloadClassification.Protected, locallyProtected.Classification);
    True(!locallyProtected.CanStop);
    var service = new ServiceCandidate
    {
        ServiceName = "UpdaterService", DisplayName = "Updater Service", Impact = ImpactLevel.Unknown,
        Reason = "Local fallback", CanStop = true
    };
    Equal(1, OnlineApplicationGuidancePolicy.ApplyServices([service], catalogue));
    Equal(WorkloadClassification.Recommended, service.Classification);
    Equal(ImpactLevel.Medium, service.Impact);
    Equal(SoftwareIdentityConfidence.Likely, service.Identity!.Confidence);

    Equal(
        @"C:\Program Files\Test App\service.exe",
        ServiceExecutablePath.Resolve("\"C:\\Program Files\\Test App\\service.exe\" --service"));
    var systemIdentity = SoftwareIdentityInspector.InspectFile(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
    True(systemIdentity.Sha256.Length == 64);
    True(systemIdentity.TrustedSignature);
    return Task.CompletedTask;
}

static async Task TestApplicationUpdateCheckerAsync()
{
    var requests = 0;
    using var client = new HttpClient(new StubHttpMessageHandler(request =>
    {
        requests++;
        True(request.Headers.UserAgent.Any(item => item.Product?.Name == "VR-Auto-Optimizer"));
        True(request.Headers.Accept.Any(item => item.MediaType == "application/vnd.github+json"));
        True(request.Headers.Contains("X-GitHub-Api-Version"));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"tag_name":"v2.3.0","name":"VR Auto-Optimizer 2.3.0","html_url":"https://github.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/tag/v2.3.0"}""",
                Encoding.UTF8,
                "application/json")
        };
    }));
    var checker = new ApplicationUpdateChecker(client);

    var available = await checker.CheckAsync(new Version(2, 2, 0, 0));
    True(available.IsUpdateAvailable);
    Equal(new Version(2, 2, 0), available.CurrentVersion);
    Equal(new Version(2, 3, 0), available.LatestVersion);
    Equal("VR Auto-Optimizer 2.3.0", available.ReleaseName);
    Equal("github.com", available.ReleaseUri.Host);

    var current = await checker.CheckAsync(new Version(2, 3, 0, 0));
    True(!current.IsUpdateAvailable);
    Equal(2, requests);
    Equal(new Version(2, 3, 1), ApplicationUpdateChecker.ParseReleaseVersion("v2.3.1-beta.1"));
}

static Task TestOptimizationProfilesAsync()
{
    var options = new OptimizerOptions();
    OptimizationProfiles.Apply(options, OptimizationProfile.Aggressive);
    Equal(OptimizationProfile.Aggressive, options.Profile);
    Equal(ProcessPriorityPreference.High, options.ProcessPriority);
    True(options.UseVendorAwareCpuSets);
    True(options.EnableNvidiaPersistence);
    True(options.UseMsfs2024FastLaunch);
    True(options.FlushDnsCache);
    True(options.DisableGameDvr);
    True(options.ClearStandbyMemory);
    True(options.UseHighResolutionTimer);
    True(options.DisableFullscreenOptimizations);
    True(options.DisablePowerThrottling);

    OptimizationProfiles.Apply(options, OptimizationProfile.Standard);
    Equal(ProcessPriorityPreference.High, options.ProcessPriority);
    True(options.UseVendorAwareCpuSets);
    True(options.EnableNvidiaPersistence);
    True(options.UseMsfs2024FastLaunch);
    True(options.FlushDnsCache);
    True(!options.DisableGameDvr);
    True(!options.ClearStandbyMemory);
    True(!options.UseHighResolutionTimer);
    True(!options.DisableFullscreenOptimizations);
    True(!options.DisablePowerThrottling);
    return Task.CompletedTask;
}

static async Task TestCustomApplicationRuleAsync()
{
    using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
    var processName = currentProcess.ProcessName;
    var commands = new FakeCommandRunner
    {
        Handler = (file, _) => file == "sc.exe" ? Ok("") : Ok()
    };
    var result = await new SystemScanner(commands).ScanAsync([
        new CustomApplicationRule { ProcessName = processName, RestartExecutablePath = Environment.ProcessPath ?? "" }
    ]);
    var candidate = result.Applications.Single(application => application.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
    True(candidate.IsCustom);
    Equal("Automatic", candidate.RestartSupport);
    Equal(WorkloadClassification.Optional, candidate.Classification);
}

static Task TestOpenXrTurboLayerAsync()
{
    Equal(@"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit", OpenXrTurboLayer.RegistryPath);
    Equal("VR_Optimizer_Turbo_Layer.json", OpenXrTurboLayer.ManifestFileName);
    Equal("VR_Optimizer_Turbo_Layer.dll", OpenXrTurboLayer.LibraryFileName);
    True(OpenXrTurboLayer.IsPackageAvailable);

    var module = NativeLibrary.Load(OpenXrTurboLayer.LibraryPath);
    try
    {
        var export = NativeLibrary.GetExport(module, "xrNegotiateLoaderApiLayerInterface");
        var negotiate = Marshal.GetDelegateForFunctionPointer<OpenXrNegotiateDelegate>(export);
        var loader = new OpenXrLoaderInfo
        {
            StructType = 1,
            StructVersion = 1,
            StructSize = (nuint)Marshal.SizeOf<OpenXrLoaderInfo>(),
            MinInterfaceVersion = 1,
            MaxInterfaceVersion = 1,
            MinApiVersion = 0x0001000000000000,
            MaxApiVersion = 0x0001000100000000
        };
        var request = new OpenXrLayerRequest
        {
            StructType = 2,
            StructVersion = 1,
            StructSize = (nuint)Marshal.SizeOf<OpenXrLayerRequest>()
        };
        Equal(0, negotiate(ref loader, IntPtr.Zero, ref request));
        Equal((uint)1, request.LayerInterfaceVersion);
        True(request.GetInstanceProcAddr != IntPtr.Zero);
        True(request.CreateApiLayerInstance != IntPtr.Zero);
    }
    finally
    {
        NativeLibrary.Free(module);
    }
    return Task.CompletedTask;
}

static Task TestSimulatorCatalogAsync()
{
    Equal(10, SimulatorCatalog.SupportedConfigurationCount);
    True(SimulatorCatalog.Find("il2-sturmovik-steam") is not null);
    Equal("il2-sturmovik-steam", SimulatorCatalog.SteamByAppId["307960"].Id);
    return Task.CompletedTask;
}

static Task TestSelectionStateNotificationAsync()
{
    var application = new RunningAppCandidate
    {
        ProcessName = "SelectionTest",
        DisplayName = "Selection test",
        Impact = ImpactLevel.Low,
        Reason = "test",
        InstanceCount = 1,
        MemoryMb = 1,
        RestartCommand = "exe:C:\\selection-test.exe",
        CanStop = true
    };
    var changes = 0;
    application.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(RunningAppCandidate.Selected)) changes++;
    };

    application.Selected = true;
    application.Selected = true;
    True(application.Selected);
    Equal(1, changes);
    return Task.CompletedTask;
}

static async Task TestSavedSelectionPreferencesAsync()
{
    var application = new RunningAppCandidate
    {
        ProcessName = "ExampleApp",
        DisplayName = "Example app",
        Impact = ImpactLevel.Low,
        Reason = "test",
        InstanceCount = 1,
        MemoryMb = 1,
        RestartCommand = "exe:C:\\example.exe",
        CanStop = true
    };
    var protectedApplication = new RunningAppCandidate
    {
        ProcessName = "MSFS_AutoFPS",
        DisplayName = "MSFS AutoFPS",
        Impact = ImpactLevel.Low,
        Reason = "test",
        InstanceCount = 1,
        MemoryMb = 1,
        RestartCommand = "none:",
        CanStop = false
    };
    var service = new ServiceCandidate
    {
        ServiceName = "ExampleService",
        DisplayName = "Example service",
        Impact = ImpactLevel.Low,
        Reason = "test",
        CanStop = true
    };
    var applicationSelections = new Dictionary<string, bool> { ["exampleapp"] = true, ["MSFS_AutoFPS"] = true };
    var serviceSelections = new Dictionary<string, bool> { ["exampleservice"] = true };

    SessionSelectionPolicy.ApplySaved(
        [application, protectedApplication],
        [service],
        applicationSelections,
        serviceSelections,
        OptimizationProfile.Standard,
        contentCreatorMode: false);
    True(application.Selected);
    True(!protectedApplication.Selected);
    True(!service.Selected);

    SessionSelectionPolicy.ApplySaved(
        [application],
        [service],
        applicationSelections,
        serviceSelections,
        OptimizationProfile.Aggressive,
        contentCreatorMode: false);
    True(service.Selected);

    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var configPath = Path.Combine(directory, "config.json");
    await JsonStore.SaveAtomicAsync(configPath, new AppConfig
    {
        ApplicationSelections = new Dictionary<string, bool>(applicationSelections, StringComparer.OrdinalIgnoreCase),
        ServiceSelections = new Dictionary<string, bool>(serviceSelections, StringComparer.OrdinalIgnoreCase)
    });
    var loaded = await JsonStore.LoadRequiredAsync<AppConfig>(configPath);
    Equal(true, loaded.ApplicationSelections["exampleapp"]);
    Equal(true, loaded.ServiceSelections["exampleservice"]);
    Directory.Delete(directory, true);
}

static async Task TestNamedUserProfilesAsync()
{
    var config = new AppConfig
    {
        SelectedSimulatorId = "msfs2024-store",
        SessionMode = SessionMode.Automatic,
        Options = new OptimizerOptions
        {
            Profile = OptimizationProfile.Aggressive,
            VrRuntime = VrRuntimePreference.PimaxPlay,
            UseVendorAwareCpuSets = true,
            UseOpenXrTurboMode = true,
            LaunchTimeoutSeconds = 240
        },
        CustomApplications = [new CustomApplicationRule { ProcessName = "ExampleTool", RestartExecutablePath = @"C:\Tools\ExampleTool.exe" }],
        ApplicationSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["OneDrive"] = true },
        ServiceSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["SysMain"] = true },
        ApplicationAfterFlightActions = new Dictionary<string, ApplicationAfterFlightAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["ExampleTool"] = ApplicationAfterFlightAction.Restart
        }
    };

    var saved = UserProfileStore.SaveOrReplace(config, "MSFS VR");
    Equal("MSFS VR", config.ActiveSavedProfileName);
    Equal(1, config.SavedProfiles.Count);
    config.Options.Profile = OptimizationProfile.Standard;
    config.ApplicationSelections["OneDrive"] = false;
    config.CustomApplications[0].ProcessName = "Changed";
    Equal(OptimizationProfile.Aggressive, saved.Options.Profile);
    Equal(true, saved.ApplicationSelections["onedrive"]);
    Equal(ApplicationAfterFlightAction.Restart, saved.ApplicationAfterFlightActions["exampletool"]);
    Equal("ExampleTool", saved.CustomApplications[0].ProcessName);

    True(UserProfileStore.TryApply(config, "msfs vr"));
    Equal("msfs2024-store", config.SelectedSimulatorId);
    Equal(SessionMode.Automatic, config.SessionMode);
    Equal(OptimizationProfile.Aggressive, config.Options.Profile);
    Equal(VrRuntimePreference.PimaxPlay, config.Options.VrRuntime);
    True(config.Options.UseOpenXrTurboMode);
    Equal(true, config.ServiceSelections["sysmain"]);
    Equal(ApplicationAfterFlightAction.Restart, config.ApplicationAfterFlightActions["exampletool"]);

    config.Options.LaunchTimeoutSeconds = 300;
    UserProfileStore.SaveOrReplace(config, "MSFS VR");
    Equal(1, config.SavedProfiles.Count);
    Equal(300, config.SavedProfiles.Single().Options.LaunchTimeoutSeconds);

    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "profiles.json");
    await JsonStore.SaveAtomicAsync(path, config);
    var loaded = await JsonStore.LoadRequiredAsync<AppConfig>(path);
    Equal("MSFS VR", loaded.ActiveSavedProfileName);
    Equal(1, loaded.SavedProfiles.Count);
    Equal(true, loaded.SavedProfiles[0].ApplicationSelections["onedrive"]);
    Equal(ApplicationAfterFlightAction.Restart, loaded.SavedProfiles[0].ApplicationAfterFlightActions["exampletool"]);
    True(UserProfileStore.Delete(loaded, "MSFS VR"));
    Equal(0, loaded.SavedProfiles.Count);
    Equal<string?>(null, loaded.ActiveSavedProfileName);
    Directory.Delete(directory, true);
}

static Task TestProfilesSurviveContinuationAsync()
{
    var current = new AppConfig
    {
        ActiveSavedProfileName = "MSFS VR",
        ApplicationSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["OneDrive"] = true },
        ServiceSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["SysMain"] = true },
        ApplicationAfterFlightActions = new Dictionary<string, ApplicationAfterFlightAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["ExampleTool"] = ApplicationAfterFlightAction.LeaveClosed
        },
        SavedProfiles =
        [
            new SavedUserProfile
            {
                Name = "MSFS VR",
                SelectedSimulatorId = "msfs2024-store",
                Options = new OptimizerOptions { Profile = OptimizationProfile.Aggressive },
                ApplicationSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["OneDrive"] = true }
            }
        ]
    };
    var pending = new PendingLaunch
    {
        SimulatorId = "msfs2024-store",
        SessionMode = SessionMode.Manual,
        Options = new OptimizerOptions { Profile = OptimizationProfile.Standard },
        CustomApplications = [],
        ApplicationAfterFlightActions = new Dictionary<string, ApplicationAfterFlightAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["ExampleTool"] = ApplicationAfterFlightAction.Restart
        }
    };

    var continued = UserProfileStore.CreateContinuedConfig(current, pending);
    Equal("MSFS VR", continued.ActiveSavedProfileName);
    Equal(1, continued.SavedProfiles.Count);
    Equal(true, continued.ApplicationSelections["onedrive"]);
    Equal(true, continued.ServiceSelections["sysmain"]);
    Equal(ApplicationAfterFlightAction.Restart, continued.ApplicationAfterFlightActions["exampletool"]);
    Equal(OptimizationProfile.Standard, continued.Options.Profile);

    current.SavedProfiles[0].Name = "Changed";
    Equal("MSFS VR", continued.SavedProfiles[0].Name);
    return Task.CompletedTask;
}

static Task TestApplicationAfterFlightChoicesAsync()
{
    var restartable = new RunningAppCandidate
    {
        ProcessName = "Example", DisplayName = "Example", Impact = ImpactLevel.Low,
        Reason = "Test", InstanceCount = 1, MemoryMb = 1, RestartCommand = @"exe:C:\Tools\Example.exe", CanStop = true
    };
    restartable.AfterFlightAction = ApplicationAfterFlightAction.Restart;
    Equal(ApplicationAfterFlightAction.Restart, restartable.AfterFlightAction);
    Equal("Restart", restartable.PostFlightState);
    True(restartable.CanChangeAfterFlight);
    Equal("RESTART", restartable.AfterFlightChoices.Single(choice => choice.Action == ApplicationAfterFlightAction.Restart).ToString());
    Equal(ApplicationAfterFlightAction.Restart, restartable.SelectedAfterFlightChoice.Action);

    var manual = new RunningAppCandidate
    {
        ProcessName = "Manual", DisplayName = "Manual", Impact = ImpactLevel.Low,
        Reason = "Test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "none:", CanStop = true
    };
    manual.AfterFlightAction = ApplicationAfterFlightAction.Restart;
    Equal(ApplicationAfterFlightAction.LeaveClosed, manual.AfterFlightAction);
    True(!manual.CanChangeAfterFlight);

    var oneDrive = new RunningAppCandidate
    {
        ProcessName = "OneDrive", DisplayName = "Microsoft OneDrive", Impact = ImpactLevel.Medium,
        Reason = "Test", InstanceCount = 1, MemoryMb = 1, RestartCommand = @"exe:C:\OneDrive.exe", CanStop = true
    };
    Equal(ApplicationAfterFlightAction.Restart, oneDrive.AfterFlightAction);
    True(!oneDrive.CanChangeAfterFlight);

    restartable.AfterFlightAction = ApplicationAfterFlightAction.LeaveClosed;
    var config = new AppConfig();
    var changed = ApplicationAfterFlightPolicy.ApplySelection(config, restartable, ApplicationAfterFlightAction.Restart);
    True(changed);
    Equal(ApplicationAfterFlightAction.Restart, restartable.AfterFlightAction);
    Equal(ApplicationAfterFlightAction.Restart, config.ApplicationAfterFlightActions["Example"]);
    return Task.CompletedTask;
}

static Task TestIl2KoreaLauncherResolutionAsync()
{
    var root = @"D:\Games\Il2Series";
    var expected = Path.GetFullPath(Path.Combine(root, @"bin\game\launcher.exe"));
    var actual = SystemScanner.ResolveIl2KoreaLauncher(
        [root],
        path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));
    Equal(expected, actual);

    var missing = SystemScanner.ResolveIl2KoreaLauncher([root], _ => false);
    True(missing is null);
    return Task.CompletedTask;
}

static Task TestMsfs2024FastLaunchAsync()
{
    var options = new OptimizerOptions { UseMsfs2024FastLaunch = true };
    var steam = SimulatorLauncher.CreateLaunchPlan(SimulatorCatalog.Find("msfs2024-steam")!, options);
    Equal("steam://run/2537590//-FastLaunch/", steam.Target);
    Equal("", steam.Arguments);

    var store = SimulatorLauncher.CreateLaunchPlan(SimulatorCatalog.Find("msfs2024-store")!, options);
    Equal("shell:AppsFolder\\Microsoft.Limitless_8wekyb3d8bbwe!App", store.Target);
    Equal("-FastLaunch", store.Arguments);

    options.UseMsfs2024FastLaunch = false;
    var disabled = SimulatorLauncher.CreateLaunchPlan(SimulatorCatalog.Find("msfs2024-steam")!, options);
    Equal("steam://run/2537590", disabled.Target);

    options.UseMsfs2024FastLaunch = true;
    var msfs2020 = SimulatorLauncher.CreateLaunchPlan(SimulatorCatalog.Find("msfs2020-steam")!, options);
    Equal("steam://run/1250410", msfs2020.Target);
    return Task.CompletedTask;
}

static Task TestVrRuntimeShutdownPolicyAsync()
{
    var steamVr = VrRuntimeLauncher.GetShutdownPolicy(VrRuntimePreference.SteamVR);
    Equal(TimeSpan.FromSeconds(30), steamVr.GracefulTimeout);
    True(!steamVr.AllowForcedTermination);
    True(steamVr.Method.Contains("graceful", StringComparison.OrdinalIgnoreCase));

    var pimax = VrRuntimeLauncher.GetShutdownPolicy(VrRuntimePreference.PimaxPlay);
    True(pimax.AllowForcedTermination);
    True(pimax.GracefulTimeout < steamVr.GracefulTimeout);
    return Task.CompletedTask;
}

static Task TestPerformanceTelemetryAsync()
{
    var unavailableStatus = PerformanceDashboardMonitor.BuildFpsUnavailableStatus("test source unavailable");
    True(unavailableStatus.Contains("MONITORING ACTIVE", StringComparison.Ordinal));
    True(unavailableStatus.Contains("CPU/MainThread/memory data active", StringComparison.Ordinal));
    True(unavailableStatus.Contains("FPS unavailable", StringComparison.Ordinal));

    var values = PerformanceDashboardMonitor.ParseCsv("game.exe,42,\"Hardware: Independent Flip\",16.667");
    Equal(4, values.Length);
    Equal("Hardware: Independent Flip", values[2]);
    Equal("16.667", values[3]);

    var steady = Enumerable.Repeat(10d, 99).Append(50d).ToArray();
    var onePercentLow = PerformanceDashboardMonitor.CalculateOnePercentLow(steady);
    True(onePercentLow.HasValue);
    Equal(20d, onePercentLow!.Value);
    Equal<double?>(null, PerformanceDashboardMonitor.CalculateOnePercentLow([16, 17, 16]));
    Equal<double?>(60, PerformanceDashboardMonitor.HoldLastReading(null, 60, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)));
    Equal<double?>(72, PerformanceDashboardMonitor.HoldLastReading(72, 60, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)));
    Equal<double?>(null, PerformanceDashboardMonitor.HoldLastReading(null, 60, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3)));
    Equal(50d, PerformanceDashboardMonitor.CalculateThreadCpuPercent(
        TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    Equal(100d, PerformanceDashboardMonitor.CalculateThreadCpuPercent(
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    True(Math.Abs(PerformanceDashboardMonitor.CalculateMainThreadFrameTimeMs(94.38, 171.6)!.Value - 5.5) < 0.01);
    Equal<double?>(null, PerformanceDashboardMonitor.CalculateMainThreadFrameTimeMs(75, null));
    var presentMonByPid = PerformanceDashboardMonitor.BuildPresentMonArguments(1234);
    True(presentMonByPid.Contains("--process_id 1234", StringComparison.Ordinal));
    True(presentMonByPid.Contains("--stop_existing_session", StringComparison.Ordinal));
    True(!presentMonByPid.Contains("--exclude_dropped", StringComparison.Ordinal));
    var presentMonByName = PerformanceDashboardMonitor.BuildPresentMonArguments(1234, "FlightSimulator2024.exe");
    True(presentMonByName.Contains("--process_name \"FlightSimulator2024.exe\"", StringComparison.Ordinal));
    var simConnectDirectory = Path.Combine(Path.GetTempPath(), "simconnect-source-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(simConnectDirectory);
    try
    {
        var simulatorPath = Path.Combine(simConnectDirectory, "FlightSimulator2024.exe");
        var simConnectPath = Path.Combine(simConnectDirectory, "SimConnect_internal.dll");
        File.WriteAllBytes(simulatorPath, []);
        File.WriteAllBytes(simConnectPath, []);
        Equal(simConnectPath, SimConnectFpsSource.FindLibraryNearExecutable(simulatorPath));
    }
    finally
    {
        Directory.Delete(simConnectDirectory, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestMsfsDisplaySettingsParserAsync()
{
    const string config = """
        Version 66
        {Video
            AntiAliasing DLSS
            DLSSMode QUALITY
            AntiAliasingVR TAA
            DLSSModeVR AUTO
        }
        {Graphics
            Version 2.1.0
            Preset Ultra
        }
        {GraphicsVR
            Version 2.1.0
            Preset VRMedium
        }
        """;
    var settings = MsfsDisplaySettingsReader.Parse(config, @"C:\MSFS\UserCfg.opt");
    Equal("66", settings.UserConfigVersion);
    Equal("DLSS", settings.Desktop.AntiAliasing);
    Equal("QUALITY", settings.Desktop.DlssMode);
    Equal("2.1.0", settings.Desktop.GraphicsVersion);
    Equal("Ultra", settings.Desktop.Preset);
    Equal("TAA", settings.Vr.AntiAliasing);
    Equal("AUTO", settings.Vr.DlssMode);
    Equal("VR Medium", settings.Vr.Preset);
    return Task.CompletedTask;
}

static Task TestNvidiaDlssPresetMappingAsync()
{
    Equal("Use 3D app setting", NvidiaDlssSettingsReader.FormatPreset(0, 11));
    Equal("Recommended", NvidiaDlssSettingsReader.FormatPreset(1, 0));
    Equal("Recommended (Default)", NvidiaDlssSettingsReader.FormatPreset(1, 0x00FFFFFE));
    Equal("Recommended (Latest)", NvidiaDlssSettingsReader.FormatPreset(1, 0x00FFFFFF));
    Equal("Preset A", NvidiaDlssSettingsReader.FormatPreset(1, 1));
    Equal("Preset K", NvidiaDlssSettingsReader.FormatPreset(1, 11));
    Equal("Preset Z", NvidiaDlssSettingsReader.FormatPreset(1, 26));
    return Task.CompletedTask;
}

static async Task TestPerformanceMonitorSamplingAsync()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(directory);
    paths.EnsureCreated();
    await using var monitor = new PerformanceDashboardMonitor(paths, new FileLogger(paths.LogFile));
    var completion = new TaskCompletionSource<PerformanceTelemetrySample>(TaskCreationOptions.RunContinuationsAsynchronously);
    monitor.SampleReady += sample => completion.TrySetResult(sample);
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    await monitor.StartAsync(current.Id, false);
    var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    True(finished == completion.Task);
    var sample = await completion.Task;
    if (sample.LogicalProcessorUsage.Count == 0) throw new InvalidOperationException("CPU sampler returned no logical processors.");
    if (sample.MainThreadFrameTimeMs is < 0) throw new InvalidOperationException("Simulator main-thread frame-time reading was invalid.");
    if (sample.SimulatorMemoryMb <= 0) throw new InvalidOperationException("Simulator process memory reading was empty.");
    await monitor.StopAsync();
    Directory.Delete(directory, true);
}

static async Task TestLogRotationAsync()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "optimizer.log");
    var logger = new FileLogger(path, maxBytes: 1024, retainedFiles: 2);
    await logger.WriteAsync(new string('A', 700));
    await logger.WriteAsync(new string('B', 700));
    await logger.WriteAsync(new string('C', 700));
    True(File.Exists(path));
    True(File.Exists(path + ".1"));
    True(File.Exists(path + ".2"));
}

static async Task TestXboxSessionCleanupAsync()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var messages = new List<string>();
    var cleanup = new XboxSessionCleanup(
        new FileLogger(Path.Combine(directory, "xbox-cleanup.log")),
        applicationNames: []);
    cleanup.StatusChanged += messages.Add;

    await cleanup.CleanupAsync();

    True(!XboxSessionCleanup.DefaultApplicationNames.Contains("GamingServices", StringComparer.OrdinalIgnoreCase));
    True(!XboxSessionCleanup.DefaultApplicationNames.Contains("GamingServicesNet", StringComparer.OrdinalIgnoreCase));
    True(messages.Any(message => message.Contains("services were left untouched", StringComparison.Ordinal)));
}

static async Task TestPendingLaunchRoundtripAsync()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var file = Path.Combine(directory, "pending-launch.json");
    Directory.CreateDirectory(directory);
    var pending = new PendingLaunch
    {
        SimulatorId = "msfs2024-store",
        SessionMode = SessionMode.Automatic,
        Options = new OptimizerOptions { DryRun = false, Profile = OptimizationProfile.Aggressive, ProcessPriority = ProcessPriorityPreference.High, ContentCreatorMode = true, VrRuntime = VrRuntimePreference.SteamVR },
        ProcessNames = ["OneDrive"],
        ServiceNames = ["GoogleUpdaterService"],
        CustomApplications = [new CustomApplicationRule { ProcessName = "CustomTool", RestartExecutablePath = @"C:\\Tools\\CustomTool.exe" }],
        ApplicationAfterFlightActions = new Dictionary<string, ApplicationAfterFlightAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["CustomTool"] = ApplicationAfterFlightAction.Restart
        }
    };
    await JsonStore.SaveAtomicAsync(file, pending);
    var loaded = await JsonStore.LoadRequiredAsync<PendingLaunch>(file);
    Equal(SessionMode.Automatic, loaded.SessionMode);
    Equal(ProcessPriorityPreference.High, loaded.Options.ProcessPriority);
    Equal(OptimizationProfile.Aggressive, loaded.Options.Profile);
    Equal(VrRuntimePreference.SteamVR, loaded.Options.VrRuntime);
    True(loaded.Options.ContentCreatorMode);
    Equal("OneDrive", loaded.ProcessNames.Single());
    Equal("CustomTool", loaded.CustomApplications.Single().ProcessName);
    Equal(ApplicationAfterFlightAction.Restart, loaded.ApplicationAfterFlightActions["customtool"]);
}

static async Task TestDryRunAsync()
{
    var fixture = CreateFixture();
    var services = new[]
    {
        new ServiceCandidate { ServiceName = "SysMain", DisplayName = "SysMain", Impact = ImpactLevel.Low, Reason = "test", CanStop = true, Selected = true },
        new ServiceCandidate { ServiceName = "Spooler", DisplayName = "Spooler", Impact = ImpactLevel.Low, Reason = "test", CanStop = true, Selected = true }
    };
    await fixture.Optimizer.BeginAsync("Test Sim", new OptimizerOptions
    {
        DryRun = true,
        UseUltimatePowerPlan = true,
        EnableNvidiaPersistence = true
    }, [], services, CancellationToken.None);
    True(!File.Exists(fixture.Paths.JournalFile));
    Equal(0, fixture.Commands.Calls.Count);
    await fixture.Optimizer.RestoreAsync();
}

static async Task TestTransactionalRestoreAsync()
{
    var fixture = CreateFixture();
    fixture.Commands.Handler = (file, args) =>
    {
        var joined = string.Join(" ", args);
        if (file == "powercfg.exe" && joined == "/getactivescheme")
            return Ok("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e");
        if (file == "powercfg.exe" && joined == "/list")
        {
            var created = fixture.Commands.Calls.First(call => call.File == "powercfg.exe" && call.Args.FirstOrDefault() == "/duplicatescheme").Args[2];
            var deleted = fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && call.Args.FirstOrDefault() == "/delete");
            return Ok(deleted ? "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e" : "Power Scheme GUID: " + created);
        }
        if (file == "sc.exe" && args.FirstOrDefault() == "query")
        {
            var serviceName = args[1];
            var lastStateCommand = fixture.Commands.Calls
                .Where(call => call.File == "sc.exe" && call.Args.Length > 1 && call.Args[1] == serviceName && call.Args[0] is "stop" or "start")
                .LastOrDefault();
            return Ok(lastStateCommand.Args?.FirstOrDefault() == "stop" ? "STATE : 1 STOPPED" : "STATE : 4 RUNNING");
        }
        if (file == "nvidia-smi.exe" && args.FirstOrDefault()?.StartsWith("--query", StringComparison.Ordinal) == true)
        {
            var restored = fixture.Commands.Calls.Any(call => call.File == "nvidia-smi.exe" && string.Join(" ", call.Args) == "-i 0 -pm 0");
            return Ok(restored ? "0, Disabled\n1, Enabled" : "0, Disabled\n1, Enabled");
        }
        return Ok();
    };

    var services = new[]
    {
        new ServiceCandidate { ServiceName = "SysMain", DisplayName = "SysMain", Impact = ImpactLevel.Low, Reason = "test", CanStop = true, Selected = true },
        new ServiceCandidate { ServiceName = "Spooler", DisplayName = "Spooler", Impact = ImpactLevel.Low, Reason = "test", CanStop = true, Selected = true }
    };
    await fixture.Optimizer.BeginAsync("Test Sim", new OptimizerOptions
    {
        DryRun = false,
        Profile = OptimizationProfile.Aggressive,
        UseUltimatePowerPlan = true,
        EnableNvidiaPersistence = true,
        FlushDnsCache = false
    }, [], services, CancellationToken.None);
    True(File.Exists(fixture.Paths.JournalFile));

    var journal = await JsonStore.LoadRequiredAsync<SessionJournal>(fixture.Paths.JournalFile);
    Equal(5, journal.Mutations.Count);
    var report = await fixture.Optimizer.RestoreAsync();
    True(!File.Exists(fixture.Paths.JournalFile));
    True(File.Exists(fixture.Paths.RestorationReportFile));
    True(report.Succeeded);
    Equal(5, report.RestoredCount);
    Equal(0, report.FailedCount);
    True(fixture.Commands.Calls.Any(call => call.File == "sc.exe" && string.Join(" ", call.Args) == "start SysMain"));
    True(fixture.Commands.Calls.Any(call => call.File == "nvidia-smi.exe" && string.Join(" ", call.Args) == "-i 0 -pm 0"));
    True(fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && call.Args.FirstOrDefault() == "/delete"));
}

static async Task TestRestoreVerificationFailureAsync()
{
    var fixture = CreateFixture();
    var journal = new SessionJournal
    {
        SimulatorName = "Test Sim",
        Mutations =
        [
            new StateMutation(MutationKind.PowerPlan, "active", "381b4222-f694-41f0-9685-ff5bb260df2e", "temporary", DateTimeOffset.UtcNow)
        ]
    };
    await JsonStore.SaveAtomicAsync(fixture.Paths.JournalFile, journal);
    fixture.Commands.Handler = (file, args) => file == "powercfg.exe" && args.FirstOrDefault() == "/getactivescheme"
        ? Ok("Power Scheme GUID: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        : Ok();

    await ThrowsAsync<InvalidOperationException>(() => fixture.Optimizer.RestoreAsync());
    True(File.Exists(fixture.Paths.JournalFile));
    var report = await JsonStore.LoadRequiredAsync<RestorationReport>(fixture.Paths.RestorationReportFile);
    Equal(1, report.FailedCount);
    True(!report.Succeeded);
}

static async Task TestApplicationLeftClosedAsync()
{
    var fixture = CreateFixture();
    var processName = "SimVROptimizerTestProcessThatDoesNotExist";
    var journal = new SessionJournal
    {
        SimulatorName = "Test Sim",
        Mutations =
        [
            new StateMutation(MutationKind.Process, processName, @"exe:C:\Tools\TestApp.exe", "stopped", DateTimeOffset.UtcNow)
        ]
    };
    await JsonStore.SaveAtomicAsync(fixture.Paths.JournalFile, journal);
    var report = await fixture.Optimizer.RestoreAsync();
    True(report.Succeeded);
    Equal(1, report.LeftClosedCount);
    Equal(RestorationOutcome.LeftClosed, report.Items.Single().Outcome);
    True(!File.Exists(fixture.Paths.JournalFile));
}

static async Task TestApplicationRestartedAsync()
{
    var restarter = new FakeApplicationRestarter { Result = true };
    var fixture = CreateFixture(restarter);
    var processName = "RestartAfterFlight";
    var journal = new SessionJournal
    {
        SimulatorName = "Test Sim",
        Mutations =
        [
            new StateMutation(MutationKind.Process, processName, @"exe:C:\Tools\RestartAfterFlight.exe", "stopped:restart", DateTimeOffset.UtcNow)
        ]
    };
    await JsonStore.SaveAtomicAsync(fixture.Paths.JournalFile, journal);

    var report = await fixture.Optimizer.RestoreAsync();

    True(report.Succeeded);
    Equal(1, report.RestoredCount);
    Equal(0, report.LeftClosedCount);
    Equal(processName, restarter.ProcessName);
    Equal(@"exe:C:\Tools\RestartAfterFlight.exe", restarter.RestartCommand);
}

static async Task TestOneDriveRestoredAsync()
{
    var restarter = new FakeApplicationRestarter { Result = true };
    var fixture = CreateFixture(restarter);
    var journal = new SessionJournal
    {
        SimulatorName = "Test Sim",
        Mutations =
        [
            new StateMutation(MutationKind.Process, "OneDrive", @"exe:C:\Users\Test\OneDrive.exe", "stopped", DateTimeOffset.UtcNow)
        ]
    };
    await JsonStore.SaveAtomicAsync(fixture.Paths.JournalFile, journal);

    var report = await fixture.Optimizer.RestoreAsync();

    True(report.Succeeded);
    Equal(1, report.RestoredCount);
    Equal(0, report.LeftClosedCount);
    Equal(RestorationOutcome.Restored, report.Items.Single().Outcome);
    Equal("OneDrive", restarter.ProcessName);
    Equal(@"exe:C:\Users\Test\OneDrive.exe", restarter.RestartCommand);
    True(!File.Exists(fixture.Paths.JournalFile));
}

static async Task TestCorruptJournalAsync()
{
    var fixture = CreateFixture();
    Directory.CreateDirectory(fixture.Paths.BaseDirectory);
    await File.WriteAllTextAsync(fixture.Paths.JournalFile, "{not-json");
    await ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Optimizer.RestoreAsync());
    True(File.Exists(fixture.Paths.JournalFile));
}

static async Task TestToolbarTelemetryBridgeAsync()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var portProbe = new TcpListener(System.Net.IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();

    await using var server = new DashboardTelemetryServer(new FileLogger(Path.Combine(directory, "toolbar.log")), port);
    await server.StartAsync();
    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(new Uri(server.Endpoint), CancellationToken.None);
    var initial = await ReceiveToolbarFrameAsync(socket);
    True(!initial.SessionActive);
    True(!initial.OpenXrTurboMode);

    server.BeginSession("Microsoft Flight Simulator 2024", openXrTurboMode: true);
    var started = await ReceiveToolbarFrameAsync(socket);
    True(started.SessionActive);
    True(started.OpenXrTurboMode);
    Equal("Microsoft Flight Simulator 2024", started.Simulator);

    var sample = new PerformanceTelemetrySample(
        DateTimeOffset.UtcNow, 72.5, 70.1, 55.2, 13.8, 32.4, 44.1, 67.8, 8123,
        [10.0, 20.0, 30.0, 40.0], true, true, "MSFS visual FPS via SimConnect");
    server.Publish(sample);
    var published = await ReceiveToolbarFrameAsync(socket);
    Equal(72.5, published.Sample!.Fps);
    Equal(1, published.StutterCount);
    Equal(1, published.CpuSpikeCount);
    Equal(4, published.Sample.LogicalProcessorUsage.Count);
    Equal(67.8, published.Sample.MainThreadFrameTimeMs);
    Equal(5, published.SchemaVersion);
    True(published.OpenXrTurboMode);
    Equal("", published.CpuName);
    Equal("ALL LOGICAL", published.ProcessorGroups.Single().Label);

    server.ResetStutterCounter();
    var stutterReset = await ReceiveToolbarFrameAsync(socket);
    Equal(0, stutterReset.StutterCount);
    Equal(1, stutterReset.CpuSpikeCount);

    server.ResetCpuSpikeCounter();
    var spikeReset = await ReceiveToolbarFrameAsync(socket);
    Equal(0, spikeReset.StutterCount);
    Equal(0, spikeReset.CpuSpikeCount);
}

static async Task<DashboardTelemetryFrame> ReceiveToolbarFrameAsync(ClientWebSocket socket)
{
    var buffer = new byte[16384];
    var builder = new StringBuilder();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
    } while (!result.EndOfMessage);
    return JsonSerializer.Deserialize<DashboardTelemetryFrame>(builder.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}

static async Task TestToolbarPackageInstallerAsync()
{
    var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var packages = Path.Combine(root, "Packages");
    var community = Path.Combine(packages, "Community");
    var source = Path.Combine(root, "source", MsfsToolbarPanelInstaller.PackageName);
    Directory.CreateDirectory(Path.Combine(source, "html_ui"));
    Directory.CreateDirectory(Path.Combine(source, "ingamepanels"));
    await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), "{\"title\":\"FlightDeckTools VR Dashboard\"}");
    await File.WriteAllTextAsync(Path.Combine(source, "layout.json"), "{\"content\":[]}");
    await File.WriteAllBytesAsync(Path.Combine(source, "ingamepanels", "panel.spb"), [1, 2, 3]);
    var userConfig = Path.Combine(root, "UserCfg.opt");
    await File.WriteAllTextAsync(userConfig, $"InstalledPackagesPath \"{packages}\"");

    var installer = new MsfsToolbarPanelInstaller(source, [userConfig]);
    var before = installer.GetStatus();
    True(before.PackageAvailable);
    True(!before.IsInstalled);
    Equal(community, before.CommunityFolder);

    var installed = await installer.InstallAsync();
    True(installed.IsInstalled);
    True(File.Exists(Path.Combine(installed.TargetDirectory!, "ingamepanels", "panel.spb")));

    await File.WriteAllTextAsync(Path.Combine(source, "html_ui", "updated.txt"), "updated");
    var updated = await installer.InstallAsync();
    True(updated.IsInstalled);
    True(File.Exists(Path.Combine(updated.TargetDirectory!, "html_ui", "updated.txt")));

    var removed = await installer.RemoveAsync();
    True(!removed.IsInstalled);
    True(!Directory.Exists(installed.TargetDirectory));
}

static (TransactionalOptimizer Optimizer, FakeCommandRunner Commands, AppPaths Paths) CreateFixture(
    IApplicationRestarter? applicationRestarter = null,
    ICpuProfileProvider? cpuProfileProvider = null)
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(directory);
    paths.EnsureCreated();
    var commands = new FakeCommandRunner();
    cpuProfileProvider ??= new FakeCpuProfileProvider(new CpuProfile("GenuineIntel", "Test CPU", true, false, false, false, 8, 16, []));
    return (new TransactionalOptimizer(commands, paths, new FileLogger(paths.LogFile), applicationRestarter, cpuProfileProvider), commands, paths);
}

static CommandResult Ok(string output = "") => new(0, output, "");
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<(string File, string[] Args)> Calls { get; } = [];
    public Func<string, string[], CommandResult> Handler { get; set; } = (_, _) => new CommandResult(0, "", "");

    public Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToArray();
        Calls.Add((fileName, args));
        return Task.FromResult(Handler(fileName, args));
    }
}

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(handler(request));
}

internal sealed class FakeApplicationRestarter : IApplicationRestarter
{
    public bool Result { get; set; }
    public bool Running { get; set; }
    public string? ProcessName { get; private set; }
    public string? RestartCommand { get; private set; }

    public bool IsRunning(string processName) => Running;

    public Task<bool> RestartAndVerifyAsync(string processName, string restartCommand, CancellationToken cancellationToken)
    {
        ProcessName = processName;
        RestartCommand = restartCommand;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCpuProfileProvider(CpuProfile profile) : ICpuProfileProvider
{
    public CpuProfile GetProfile() => profile;
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int OpenXrNegotiateDelegate(ref OpenXrLoaderInfo loaderInfo, IntPtr layerName, ref OpenXrLayerRequest request);

[StructLayout(LayoutKind.Sequential)]
internal struct OpenXrLoaderInfo
{
    public int StructType;
    public uint StructVersion;
    public nuint StructSize;
    public uint MinInterfaceVersion;
    public uint MaxInterfaceVersion;
    public ulong MinApiVersion;
    public ulong MaxApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OpenXrLayerRequest
{
    public int StructType;
    public uint StructVersion;
    public nuint StructSize;
    public uint LayerInterfaceVersion;
    public ulong LayerApiVersion;
    public IntPtr GetInstanceProcAddr;
    public IntPtr CreateApiLayerInstance;
}
