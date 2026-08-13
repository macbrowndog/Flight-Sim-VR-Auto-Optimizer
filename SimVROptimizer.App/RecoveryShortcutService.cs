using System.IO;
using System.Runtime.InteropServices;

namespace SimVROptimizer.App;

internal sealed class RecoveryShortcutService
{
    private const string ShortcutName = "VR Auto-Optimizer - Restore Last Session.lnk";
    private const string StartupShortcutName = "VR Auto-Optimizer Recovery.lnk";
    private readonly string _executablePath;

    public RecoveryShortcutService()
    {
        _executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the application executable path.");
    }

    public string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupShortcutName);

    public void Synchronize(bool recoveryRequired)
    {
        CreateShortcut(DesktopShortcutPath, "Restore the last interrupted VR Auto-Optimizer session");
        if (recoveryRequired) CreateShortcut(StartupShortcutPath, "Automatically recover an interrupted VR Auto-Optimizer session after sign-in");
        else RemoveStartupShortcut();
    }

    public void PrepareForSession()
    {
        CreateShortcut(DesktopShortcutPath, "Restore the last interrupted VR Auto-Optimizer session");
        CreateShortcut(StartupShortcutPath, "Automatically recover an interrupted VR Auto-Optimizer session after sign-in");
    }

    public void MarkRecoveryComplete() => RemoveStartupShortcut();

    private void RemoveStartupShortcut()
    {
        if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
    }

    private void CreateShortcut(string shortcutPath, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)
            ?? throw new InvalidOperationException("Shortcut directory is unavailable."));

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = _executablePath;
            dynamicShortcut.Arguments = "--restore-last-session";
            dynamicShortcut.WorkingDirectory = AppContext.BaseDirectory;
            dynamicShortcut.IconLocation = $"{_executablePath},0";
            dynamicShortcut.Description = description;
            dynamicShortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
