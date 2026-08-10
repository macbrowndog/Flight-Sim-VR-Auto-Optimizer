using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

public sealed class SimulatorLauncher
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";
    private readonly FileLogger _logger;
    private readonly CpuOptimizer _cpuOptimizer = new();
    private readonly Dictionary<int, CpuOptimizationScope> _cpuScopes = [];

    public SimulatorLauncher(FileLogger logger) => _logger = logger;
    public event Action<string>? StatusChanged;

    public async Task<Process?> LaunchAndWaitAsync(
        SimulatorDefinition simulator,
        OptimizerOptions options,
        CancellationToken cancellationToken)
    {
        var existingProcesses = simulator.ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
        var existingPids = existingProcesses.Select(process => process.Id).ToHashSet();
        foreach (var existingProcess in existingProcesses) existingProcess.Dispose();
        var launchedAfterUtc = DateTime.UtcNow.AddSeconds(-2);

        var plan = CreateLaunchPlan(simulator, options);
        var fastLaunchNote = plan.Arguments.Contains("-FastLaunch", StringComparison.OrdinalIgnoreCase)
            || plan.Target.Contains("-FastLaunch", StringComparison.OrdinalIgnoreCase)
            ? " with -FastLaunch"
            : "";
        await ReportAsync(options.DryRun
            ? $"Dry-run: would launch {simulator.Name}{fastLaunchNote}."
            : $"Launching {simulator.Name}{fastLaunchNote}.", cancellationToken);
        if (options.DryRun) return null;

        Start(plan);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.LaunchTimeoutSeconds, 30, 900)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                foreach (var process in simulator.ProcessNames.SelectMany(Process.GetProcessesByName))
                {
                    try
                    {
                        if (!existingPids.Contains(process.Id) && process.StartTime.ToUniversalTime() >= launchedAfterUtc)
                        {
                            await ReportAsync($"Detected new simulator process PID {process.Id}.", cancellationToken);
                            try
                            {
                                var scope = _cpuOptimizer.Apply(process, options);
                                _cpuScopes[process.Id] = scope;
                                await ReportAsync(scope.Summary, cancellationToken);
                            }
                            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or EntryPointNotFoundException)
                            {
                                await ReportAsync($"CPU optimization was skipped: {exception.Message}", cancellationToken);
                            }
                            return process;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        process.Dispose();
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{simulator.Name} did not create a new known process within {options.LaunchTimeoutSeconds} seconds.");
        }
    }

    public async Task RestoreProcessTuningAsync(Process process, CancellationToken cancellationToken = default)
    {
        if (!_cpuScopes.Remove(process.Id, out var scope)) return;
        try
        {
            _cpuOptimizer.Restore(process, scope);
            if (!process.HasExited) await ReportAsync("Original simulator priority and CPU-set assignment restored.", cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await ReportAsync($"Could not restore simulator CPU settings: {exception.Message}", cancellationToken);
        }
    }

    public static SimulatorLaunchPlan CreateLaunchPlan(SimulatorDefinition simulator, OptimizerOptions options)
    {
        var useFastLaunch = options.UseMsfs2024FastLaunch
            && simulator.Id is "msfs2024-steam" or "msfs2024-store";
        if (useFastLaunch && simulator.Id == "msfs2024-steam")
            return new SimulatorLaunchPlan(simulator.LaunchKind, simulator.LaunchTarget + "//-FastLaunch/", "");
        if (useFastLaunch && simulator.Id == "msfs2024-store")
            return new SimulatorLaunchPlan(simulator.LaunchKind, simulator.LaunchTarget, "-FastLaunch");
        return new SimulatorLaunchPlan(simulator.LaunchKind, simulator.LaunchTarget, simulator.Arguments);
    }

    private static void Start(SimulatorLaunchPlan plan)
    {
        if (plan.Kind == LaunchKind.Uri
            && plan.Target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(plan.Arguments))
        {
            ActivatePackagedApplication(plan.Target[AppsFolderPrefix.Length..], plan.Arguments);
            return;
        }

        ProcessStartInfo startInfo;
        if (plan.Kind == LaunchKind.Uri && plan.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(plan.Target);
        }
        else
        {
            startInfo = new ProcessStartInfo(plan.Target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(plan.Arguments)) startInfo.Arguments = plan.Arguments;
        }
        Process.Start(startInfo);
    }

    private static void ActivatePackagedApplication(string appUserModelId, string arguments)
    {
        var managerType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)
            ?? throw new InvalidOperationException("Windows packaged-app activation is unavailable.");
        var manager = (IApplicationActivationManager)Activator.CreateInstance(managerType)!;
        try
        {
            var result = manager.ActivateApplication(appUserModelId, arguments, ActivateOptions.None, out _);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record SimulatorLaunchPlan(LaunchKind Kind, string Target, string Arguments);

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        ActivateOptions options,
        out uint processId);

    [PreserveSig]
    int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);

    [PreserveSig]
    int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
}

[Flags]
internal enum ActivateOptions
{
    None = 0
}
