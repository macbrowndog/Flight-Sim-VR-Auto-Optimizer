using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SimVROptimizer.Core;

public static class SoftwareIdentityInspector
{
    public static int Inspect(
        IEnumerable<RunningAppCandidate> applications,
        IEnumerable<ServiceCandidate> services)
    {
        var identified = 0;
        var cache = new Dictionary<string, SoftwareIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            application.Identity = InspectCached(application.ExecutablePath, cache);
            if (application.Identity.Confidence != SoftwareIdentityConfidence.Unidentified) identified++;
        }
        foreach (var service in services)
        {
            service.Identity = InspectCached(service.ExecutablePath, cache);
            if (service.Identity.Confidence != SoftwareIdentityConfidence.Unidentified) identified++;
        }
        return identified;
    }

    private static SoftwareIdentity InspectCached(
        string? path,
        IDictionary<string, SoftwareIdentity> cache)
    {
        var resolved = ServiceExecutablePath.Resolve(path) ?? "";
        if (resolved.Length == 0) return InspectFile(null);
        if (cache.TryGetValue(resolved, out var identity)) return identity;
        identity = InspectFile(resolved);
        cache[resolved] = identity;
        return identity;
    }

    public static SoftwareIdentity InspectFile(string? path)
    {
        path = ServiceExecutablePath.Resolve(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(SoftwareIdentityConfidence.Unidentified, "", "", "", "", false, "No accessible executable path");

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var publisher = version.CompanyName?.Trim() ?? "";
            var product = First(version.ProductName, version.FileDescription);
            var productVersion = First(version.ProductVersion, version.FileVersion);
            var trusted = AuthenticodeTrust.IsTrusted(path);
            var hash = CalculateSha256(path);
            var confidence = trusted && product.Length > 0
                ? SoftwareIdentityConfidence.Verified
                : trusted
                    ? SoftwareIdentityConfidence.Identified
                    : publisher.Length > 0 && product.Length > 0
                        ? SoftwareIdentityConfidence.Likely
                        : SoftwareIdentityConfidence.Unidentified;
            var source = trusted
                ? "Local trusted signature and executable metadata"
                : product.Length > 0
                    ? "Local executable metadata; signature not verified"
                    : "Local file hash only";
            return new(confidence, publisher, product, productVersion, hash, trusted, source);
        }
        catch
        {
            return new(SoftwareIdentityConfidence.Unidentified, "", "", "", "", false, "Executable metadata could not be read");
        }
    }

    private static string CalculateSha256(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

public static class ServiceExecutablePath
{
    public static string? Resolve(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded[12..]);

        string path;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            path = closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }
        else
        {
            var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = executableEnd >= 0 ? expanded[..(executableEnd + 4)] : expanded;
        }

        path = path.Trim();
        if (!Path.IsPathRooted(path) && !path.Contains(Path.DirectorySeparatorChar))
            path = Path.Combine(Environment.SystemDirectory, path);
        return path;
    }
}

internal static class AuthenticodeTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData(filePointer);
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string filePath) => FilePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = null;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }
    }
}
