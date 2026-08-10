using SimVROptimizer.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Power plan parser", TestPowerPlanParserAsync),
    ("Service state parser", TestServiceParserAsync),
    ("NVIDIA state parser", TestNvidiaParserAsync),
    ("Running service parser", TestRunningServiceParserAsync),
    ("Impact service scan", TestImpactServiceScanAsync),
    ("Critical runtime protection rules", TestCriticalRuntimeProtectionAsync),
    ("Packaged application restart safety", TestPackagedApplicationRestartSafetyAsync),
    ("Denied service stop is non-fatal", TestDeniedServiceStopAsync),
    ("CPU topology scan", TestCpuTopologyAsync),
    ("Optimization profiles", TestOptimizationProfilesAsync),
    ("Automatic selection policy", TestAutomaticSelectionPolicyAsync),
    ("Persistent custom application rule", TestCustomApplicationRuleAsync),
    ("Nine simulator configurations", TestSimulatorCatalogAsync),
    ("MSFS 2024 FastLaunch plans", TestMsfs2024FastLaunchAsync),
    ("Log rotation", TestLogRotationAsync),
    ("Pending launch roundtrip", TestPendingLaunchRoundtripAsync),
    ("Dry-run makes no journal", TestDryRunAsync),
    ("Transactional restore", TestTransactionalRestoreAsync),
    ("Corrupt recovery journal is retained", TestCorruptJournalAsync)
};

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
    return Task.CompletedTask;
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
    True(!result.Services.Single(service => service.ServiceName == "BITS").CanStop);
    Equal(ImpactLevel.Medium, result.Services.Single(service => service.ServiceName.StartsWith("GoogleUpdater", StringComparison.Ordinal)).Impact);
    True(result.Services.Single(service => service.ServiceName == "Apple Mobile Device Service").CanStop);
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
    foreach (var processName in new[] { "GamingServices", "GameInput", "TextInputHost", "nvcontainer", "vrss_gaze_provider", "TobiiPlatformRuntime", "Navigraph", "Navigraph Simlink", "MOZA Cockpit", "SimRacingStudio", "pia-service" })
        True((bool)(method.Invoke(null, [processName]) ?? false));
    True(!(bool)(method.Invoke(null, ["CCleaner64"]) ?? true));
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
    var restartable = new RunningAppCandidate { ProcessName = "Restartable", DisplayName = "Restartable", Impact = ImpactLevel.Medium, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\restartable.exe", CanStop = true };
    var lowImpact = new RunningAppCandidate { ProcessName = "LowImpact", DisplayName = "LowImpact", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\low.exe", CanStop = true };
    var manualRestart = new RunningAppCandidate { ProcessName = "Manual", DisplayName = "Manual", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "none:", CanStop = true };
    var protectedApp = new RunningAppCandidate { ProcessName = "Protected", DisplayName = "Protected", Impact = ImpactLevel.Low, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\protected.exe", CanStop = false };
    var stoppableService = new ServiceCandidate { ServiceName = "Stoppable", DisplayName = "Stoppable", Impact = ImpactLevel.Medium, Reason = "test", CanStop = true };
    var protectedService = new ServiceCandidate { ServiceName = "Protected", DisplayName = "Protected", Impact = ImpactLevel.Low, Reason = "test", CanStop = false };

    SessionSelectionPolicy.SelectAutomatic([restartable, lowImpact, manualRestart, protectedApp], [stoppableService, protectedService]);
    True(restartable.Selected);
    True(!lowImpact.Selected);
    True(!manualRestart.Selected);
    True(!protectedApp.Selected);
    True(!stoppableService.Selected);
    True(!protectedService.Selected);

    SessionSelectionPolicy.SelectAutomatic([lowImpact], [], profile: OptimizationProfile.Aggressive);
    True(lowImpact.Selected);

    var obs = new RunningAppCandidate { ProcessName = "obs64", DisplayName = "OBS Studio", Impact = ImpactLevel.Medium, Reason = "test", InstanceCount = 1, MemoryMb = 1, RestartCommand = "exe:C:\\obs64.exe", CanStop = true };
    var streamDeckService = new ServiceCandidate { ServiceName = "ElgatoRemoteControlServer", DisplayName = "Elgato Remote Control", Impact = ImpactLevel.Low, Reason = "test", CanStop = true };
    SessionSelectionPolicy.SelectAutomatic([restartable, obs], [stoppableService, streamDeckService], contentCreatorMode: true, profile: OptimizationProfile.Aggressive);
    True(restartable.Selected);
    True(!obs.Selected);
    True(stoppableService.Selected);
    True(!streamDeckService.Selected);
    return Task.CompletedTask;
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
    True(options.ApplyNetworkMemoryOptimizations);

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
    True(!options.ApplyNetworkMemoryOptimizations);
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
}

static Task TestSimulatorCatalogAsync()
{
    Equal(9, SimulatorCatalog.SupportedConfigurationCount);
    True(SimulatorCatalog.Find("il2-sturmovik-steam") is not null);
    Equal("il2-sturmovik-steam", SimulatorCatalog.SteamByAppId["307960"].Id);
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
        CustomApplications = [new CustomApplicationRule { ProcessName = "CustomTool", RestartExecutablePath = @"C:\\Tools\\CustomTool.exe" }]
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
            return Ok("Power Scheme GUID: " + created);
        }
        if (file == "sc.exe" && args.FirstOrDefault() == "query")
        {
            var serviceName = args[1];
            var wasStopped = fixture.Commands.Calls.Any(call =>
                call.File == "sc.exe" && string.Join(" ", call.Args) == $"stop {serviceName}");
            return Ok(wasStopped ? "STATE : 1 STOPPED" : "STATE : 4 RUNNING");
        }
        if (file == "nvidia-smi.exe" && args.FirstOrDefault()?.StartsWith("--query", StringComparison.Ordinal) == true)
            return Ok("0, Disabled\n1, Enabled");
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
    await fixture.Optimizer.RestoreAsync();
    True(!File.Exists(fixture.Paths.JournalFile));
    True(fixture.Commands.Calls.Any(call => call.File == "sc.exe" && string.Join(" ", call.Args) == "start SysMain"));
    True(fixture.Commands.Calls.Any(call => call.File == "nvidia-smi.exe" && string.Join(" ", call.Args) == "-i 0 -pm 0"));
    True(fixture.Commands.Calls.Any(call => call.File == "powercfg.exe" && call.Args.FirstOrDefault() == "/delete"));
}

static async Task TestCorruptJournalAsync()
{
    var fixture = CreateFixture();
    Directory.CreateDirectory(fixture.Paths.BaseDirectory);
    await File.WriteAllTextAsync(fixture.Paths.JournalFile, "{not-json");
    await ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Optimizer.RestoreAsync());
    True(File.Exists(fixture.Paths.JournalFile));
}

static (TransactionalOptimizer Optimizer, FakeCommandRunner Commands, AppPaths Paths) CreateFixture()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(directory);
    paths.EnsureCreated();
    var commands = new FakeCommandRunner();
    return (new TransactionalOptimizer(commands, paths, new FileLogger(paths.LogFile)), commands, paths);
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
