using System.Diagnostics;
using Microsoft.Win32;

namespace SimVROptimizer.Core;

public sealed class TransactionalOptimizer
{
    private const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private readonly ICommandRunner _commands;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private SessionJournal? _journal;
    private bool _timerResolutionActive;

    public TransactionalOptimizer(ICommandRunner commands, AppPaths paths, FileLogger logger)
    {
        _commands = commands;
        _paths = paths;
        _logger = logger;
    }

    public event Action<string>? StatusChanged;
    public bool HasRecoveryJournal => File.Exists(_paths.JournalFile);

    public async Task BeginAsync(string simulatorName, OptimizerOptions options, CancellationToken cancellationToken)
    {
        await BeginAsync(simulatorName, options, [], [], cancellationToken).ConfigureAwait(false);
    }

    public async Task BeginAsync(
        string simulatorName,
        OptimizerOptions options,
        IReadOnlyList<RunningAppCandidate> applications,
        IReadOnlyList<ServiceCandidate> services,
        CancellationToken cancellationToken)
    {
        if (HasRecoveryJournal) throw new InvalidOperationException("An unfinished session must be restored first.");
        _journal = new SessionJournal { SimulatorName = simulatorName, DryRun = options.DryRun };
        if (!options.DryRun) await PersistAsync(cancellationToken).ConfigureAwait(false);

        await ReportAsync(options.DryRun ? "Dry-run: no system settings will be changed." : "Recovery journal created.", cancellationToken);
        if (options.UseUltimatePowerPlan) await ApplyUltimatePowerPlanAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.EnableNvidiaPersistence) await EnableNvidiaPersistenceAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.FlushDnsCache) await FlushDnsCacheAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.DisableGameDvr)
        {
            await SetRegistryDwordAsync(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, options.DryRun, cancellationToken).ConfigureAwait(false);
            await SetRegistryDwordAsync(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, options.DryRun, cancellationToken).ConfigureAwait(false);
        }
        if (options.DisableFullscreenOptimizations)
            await SetRegistryDwordAsync(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.ApplyNetworkMemoryOptimizations)
        {
            const string multimediaPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
            await SetRegistryDwordAsync(RegistryHive.LocalMachine, multimediaPath, "NetworkThrottlingIndex", uint.MaxValue, options.DryRun, cancellationToken).ConfigureAwait(false);
            await SetRegistryDwordAsync(RegistryHive.LocalMachine, multimediaPath, "SystemResponsiveness", 0, options.DryRun, cancellationToken).ConfigureAwait(false);
        }
        if (options.UseHighResolutionTimer) await EnableHighResolutionTimerAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.ClearStandbyMemory) await ClearStandbyMemoryAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (options.Profile == OptimizationProfile.Aggressive)
        {
            foreach (var service in services.Where(item => item.Selected && item.CanStop))
                await StopServiceIfRunningAsync(service.ServiceName, options.DryRun, cancellationToken).ConfigureAwait(false);
        }
        foreach (var application in applications.Where(item => item.Selected && item.CanStop))
            await StopApplicationAsync(application, options.DryRun, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_timerResolutionActive)
        {
            AdvancedSystemTuning.ReleaseHalfMillisecondTimer();
            _timerResolutionActive = false;
            await ReportAsync("Released the high-resolution timer request.", cancellationToken).ConfigureAwait(false);
        }

        var journal = _journal;
        if (journal is null && File.Exists(_paths.JournalFile))
        {
            journal = await JsonStore.LoadRequiredAsync<SessionJournal>(_paths.JournalFile, cancellationToken).ConfigureAwait(false);
        }

        if (journal is null || journal.DryRun)
        {
            _journal = null;
            if (File.Exists(_paths.JournalFile)) File.Delete(_paths.JournalFile);
            return;
        }

        var failures = new List<string>();
        foreach (var mutation in journal.Mutations.AsEnumerable().Reverse())
        {
            try
            {
                await RestoreMutationAsync(mutation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add($"{mutation.Kind}/{mutation.Target}: {exception.Message}");
                await ReportAsync($"Restore failed for {mutation.Target}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Restoration was incomplete. The recovery journal was retained. " + string.Join("; ", failures));
        }

        File.Delete(_paths.JournalFile);
        _journal = null;
        await ReportAsync("Original system state restored.", cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyUltimatePowerPlanAsync(bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync("Power plan: would temporarily enable Ultimate Performance.", cancellationToken);
        if (dryRun) return;

        var currentResult = await RequiredCommandAsync("powercfg.exe", ["/getactivescheme"], cancellationToken).ConfigureAwait(false);
        var originalGuid = OutputParsers.ParsePowerPlanGuid(currentResult.StandardOutput)
            ?? throw new InvalidOperationException("Could not determine the active power plan.");

        var temporaryGuid = Guid.NewGuid().ToString("D");
        await RecordAsync(new StateMutation(MutationKind.CreatedPowerPlan, temporaryGuid, "absent", "present", DateTimeOffset.UtcNow), cancellationToken);
        await RequiredCommandAsync("powercfg.exe", ["/duplicatescheme", UltimateTemplateGuid, temporaryGuid], cancellationToken).ConfigureAwait(false);
        await RecordAsync(new StateMutation(MutationKind.PowerPlan, "active", originalGuid, temporaryGuid, DateTimeOffset.UtcNow), cancellationToken);
        await RequiredCommandAsync("powercfg.exe", ["/setactive", temporaryGuid], cancellationToken).ConfigureAwait(false);
        await ReportAsync("Temporary Ultimate Performance plan enabled.", cancellationToken);
    }

    private async Task StopServiceIfRunningAsync(string serviceName, bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync($"Service {serviceName}: would stop only if currently running.", cancellationToken);
        if (dryRun) return;

        var query = await _commands.RunAsync("sc.exe", ["query", serviceName], cancellationToken).ConfigureAwait(false);
        if (!query.Succeeded)
        {
            await ReportAsync($"Skipped service {serviceName}: Windows would not allow its state to be queried.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!OutputParsers.IsServiceRunning(query.StandardOutput)) return;

        var mutation = new StateMutation(MutationKind.Service, serviceName, "running", "stopped", DateTimeOffset.UtcNow);
        await RecordAsync(mutation, cancellationToken);
        var stop = await _commands.RunAsync("sc.exe", ["stop", serviceName], cancellationToken).ConfigureAwait(false);
        if (!stop.Succeeded)
        {
            var stateAfterFailure = await _commands.RunAsync("sc.exe", ["query", serviceName], cancellationToken).ConfigureAwait(false);
            if (stateAfterFailure.Succeeded && !OutputParsers.IsServiceRunning(stateAfterFailure.StandardOutput))
            {
                await ReportAsync($"Stopped service {serviceName}; Windows returned exit code {stop.ExitCode} while confirming the request.", cancellationToken).ConfigureAwait(false);
                return;
            }

            await RemoveMutationAsync(mutation, cancellationToken).ConfigureAwait(false);
            var detail = FirstNonBlank(stop.StandardError, stop.StandardOutput, $"exit code {stop.ExitCode}");
            await ReportAsync($"Skipped service {serviceName}: Windows refused the stop request ({detail.Trim()}).", cancellationToken).ConfigureAwait(false);
            return;
        }
        await ReportAsync($"Stopped service {serviceName}.", cancellationToken);
    }

    private async Task EnableNvidiaPersistenceAsync(bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync("NVIDIA persistence: would enable only GPUs where it is currently disabled.", cancellationToken);
        if (dryRun) return;

        var query = await _commands.RunAsync("nvidia-smi.exe", ["--query-gpu=index,persistence_mode", "--format=csv,noheader"], cancellationToken).ConfigureAwait(false);
        if (!query.Succeeded)
        {
            await ReportAsync("NVIDIA persistence was skipped because nvidia-smi is unavailable or no supported NVIDIA GPU was detected.", cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var gpu in OutputParsers.ParseNvidiaPersistence(query.StandardOutput).Where(item => !item.Value))
        {
            await RecordAsync(new StateMutation(MutationKind.NvidiaPersistence, gpu.Key, "disabled", "enabled", DateTimeOffset.UtcNow), cancellationToken);
            await RequiredCommandAsync("nvidia-smi.exe", ["-i", gpu.Key, "-pm", "1"], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopApplicationAsync(RunningAppCandidate application, bool dryRun, CancellationToken cancellationToken)
    {
        var recoveryDescription = application.RestartSupport == "Automatic"
            ? "restart afterward"
            : "be restarted by Windows or the user when needed";
        await ReportAsync($"Application {application.DisplayName}: would stop for this session and {recoveryDescription}.", cancellationToken);
        if (dryRun) return;

        var processes = Process.GetProcessesByName(application.ProcessName);
        if (processes.Length == 0) return;
        await RecordAsync(new StateMutation(MutationKind.Process, application.ProcessName, application.RestartCommand, "stopped", DateTimeOffset.UtcNow), cancellationToken);

        foreach (var process in processes)
        {
            using (process)
            {
                try { process.CloseMainWindow(); }
                catch (InvalidOperationException) { }
            }
        }
        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        foreach (var process in Process.GetProcessesByName(application.ProcessName))
        {
            using (process)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        await ReportAsync($"Stopped {application.DisplayName} for the session.", cancellationToken);
    }

    private async Task RestoreMutationAsync(StateMutation mutation, CancellationToken cancellationToken)
    {
        switch (mutation.Kind)
        {
            case MutationKind.PowerPlan:
                await RequiredCommandAsync("powercfg.exe", ["/setactive", mutation.OriginalValue], cancellationToken).ConfigureAwait(false);
                break;
            case MutationKind.CreatedPowerPlan:
                var plans = await RequiredCommandAsync("powercfg.exe", ["/list"], cancellationToken).ConfigureAwait(false);
                if (plans.StandardOutput.Contains(mutation.Target, StringComparison.OrdinalIgnoreCase))
                    await RequiredCommandAsync("powercfg.exe", ["/delete", mutation.Target], cancellationToken).ConfigureAwait(false);
                break;
            case MutationKind.Service when mutation.OriginalValue == "running":
                var service = await _commands.RunAsync("sc.exe", ["query", mutation.Target], cancellationToken).ConfigureAwait(false);
                if (!service.Succeeded) throw new InvalidOperationException($"Could not query service {mutation.Target} during restoration.");
                if (!OutputParsers.IsServiceRunning(service.StandardOutput))
                    await RequiredCommandAsync("sc.exe", ["start", mutation.Target], cancellationToken).ConfigureAwait(false);
                break;
            case MutationKind.NvidiaPersistence:
                await RequiredCommandAsync("nvidia-smi.exe", ["-i", mutation.Target, "-pm", mutation.OriginalValue == "enabled" ? "1" : "0"], cancellationToken).ConfigureAwait(false);
                break;
            case MutationKind.Process:
                var runningProcesses = Process.GetProcessesByName(mutation.Target);
                var isRunning = runningProcesses.Length > 0;
                foreach (var runningProcess in runningProcesses) runningProcess.Dispose();
                if (!isRunning) RestartApplication(mutation.OriginalValue);
                break;
            case MutationKind.RegistryValue:
                RestoreRegistryValue(mutation);
                break;
        }
    }

    private async Task FlushDnsCacheAsync(bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync("DNS cache: would flush cached resolver entries (one-time operation).", cancellationToken).ConfigureAwait(false);
        if (!dryRun) await RequiredCommandAsync("ipconfig.exe", ["/flushdns"], cancellationToken).ConfigureAwait(false);
    }

    private async Task EnableHighResolutionTimerAsync(bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync("Timer: would request a 0.5 ms system timer resolution for this session.", cancellationToken).ConfigureAwait(false);
        if (dryRun) return;
        if (AdvancedSystemTuning.TryRequestHalfMillisecondTimer(out var actual, out var error))
        {
            _timerResolutionActive = true;
            await ReportAsync($"High-resolution timer active ({actual / 10_000d:0.###} ms actual).", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReportAsync($"High-resolution timer request was skipped: {error}.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ClearStandbyMemoryAsync(bool dryRun, CancellationToken cancellationToken)
    {
        await ReportAsync("Memory: would clear the standby list (one-time operation; contents repopulate normally).", cancellationToken).ConfigureAwait(false);
        if (dryRun) return;
        var message = AdvancedSystemTuning.TryPurgeStandbyMemory(out var error)
            ? "Standby memory list cleared."
            : $"Standby memory clear was skipped: {error}.";
        await ReportAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetRegistryDwordAsync(
        RegistryHive hive,
        string path,
        string name,
        uint value,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var target = $"{hive}|{path}|{name}";
        await ReportAsync($"Registry tuning: would set {hive}\\{path}\\{name}.", cancellationToken).ConfigureAwait(false);
        if (dryRun) return;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var existingKey = baseKey.OpenSubKey(path, writable: false);
        var original = existingKey is null || !existingKey.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
            ? "absent"
            : EncodeRegistryValue(existingKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), existingKey.GetValueKind(name));
        await RecordAsync(new StateMutation(MutationKind.RegistryValue, target, original, value.ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        using var writableKey = baseKey.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Could not open registry path {path} for writing.");
        writableKey.SetValue(name, unchecked((int)value), RegistryValueKind.DWord);
    }

    private static string EncodeRegistryValue(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => $"DWord:{Convert.ToUInt32(value)}",
        RegistryValueKind.QWord => $"QWord:{Convert.ToUInt64(value)}",
        RegistryValueKind.Binary => $"Binary:{Convert.ToBase64String((byte[])(value ?? Array.Empty<byte>()))}",
        RegistryValueKind.ExpandString => $"ExpandString:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value?.ToString() ?? ""))}",
        _ => $"String:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value?.ToString() ?? ""))}"
    };

    private static void RestoreRegistryValue(StateMutation mutation)
    {
        var parts = mutation.Target.Split('|', 3);
        if (parts.Length != 3 || !Enum.TryParse<RegistryHive>(parts[0], out var hive))
            throw new InvalidOperationException("The registry recovery entry is invalid.");
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(parts[1], writable: true)
            ?? throw new InvalidOperationException($"Could not open registry path {parts[1]} for restoration.");
        if (mutation.OriginalValue == "absent")
        {
            key.DeleteValue(parts[2], throwOnMissingValue: false);
            return;
        }

        var separator = mutation.OriginalValue.IndexOf(':');
        if (separator < 0) throw new InvalidOperationException("The registry recovery value is invalid.");
        var kind = mutation.OriginalValue[..separator];
        var data = mutation.OriginalValue[(separator + 1)..];
        switch (kind)
        {
            case "DWord": key.SetValue(parts[2], unchecked((int)uint.Parse(data)), RegistryValueKind.DWord); break;
            case "QWord": key.SetValue(parts[2], unchecked((long)ulong.Parse(data)), RegistryValueKind.QWord); break;
            case "Binary": key.SetValue(parts[2], Convert.FromBase64String(data), RegistryValueKind.Binary); break;
            case "ExpandString": key.SetValue(parts[2], System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)), RegistryValueKind.ExpandString); break;
            case "String": key.SetValue(parts[2], System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)), RegistryValueKind.String); break;
            default: throw new InvalidOperationException($"Unsupported registry recovery type {kind}.");
        }
    }

    private static void RestartApplication(string restartCommand)
    {
        if (restartCommand.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            var executable = restartCommand[4..];
            if (ApplicationRestartPolicy.CanLaunchDirectly(executable) && File.Exists(executable))
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            return;
        }

        if (restartCommand.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var target = restartCommand[6..];
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(target);
            Process.Start(startInfo);
        }
    }

    private async Task<CommandResult> RequiredCommandAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}: {error.Trim()}");
        }
        return result;
    }

    private async Task RecordAsync(StateMutation mutation, CancellationToken cancellationToken)
    {
        if (_journal is null) throw new InvalidOperationException("No active session journal.");
        _journal.Mutations.Add(mutation);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveMutationAsync(StateMutation mutation, CancellationToken cancellationToken)
    {
        if (_journal is null) throw new InvalidOperationException("No active session journal.");
        _journal.Mutations.Remove(mutation);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FirstNonBlank(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value));

    private Task PersistAsync(CancellationToken cancellationToken) =>
        JsonStore.SaveAtomicAsync(_paths.JournalFile, _journal, cancellationToken);

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
