using System.Security.AccessControl;
using System.Security.Principal;

namespace GHCPSpendTray.App.Platform;

internal static class InstallationPaths
{
    public static string NormalizeAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('\0') || path.Contains('"') || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("GHCPSpendTray requires an absolute local drive path.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        // Win32 strips trailing dots/spaces; reject ambiguous spellings before any native call.
        foreach (var component in fullPath[3..].Split(Path.DirectorySeparatorChar))
        {
            if (component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(component))
                throw new ArgumentException("GHCPSpendTray paths cannot contain reserved names, invalid characters, or components ending in dots or spaces.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsDeviceName(string component)
    {
        var name = component.Split('.')[0].TrimEnd(' ');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 &&
                (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                (name[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    public static bool SamePath(string first, string second) =>
        string.Equals(NormalizeAbsolute(first), NormalizeAbsolute(second), StringComparison.OrdinalIgnoreCase);

    public static bool IsWithin(string path, string directory) =>
        SamePath(path, directory) ||
        NormalizeAbsolute(path).StartsWith(NormalizeAbsolute(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void RejectReparseAncestors(string path)
    {
        var fullPath = NormalizeAbsolute(path);
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"GHCPSpendTray cannot use a reparse-point installation or data path: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void EnsurePrivateDirectory(string path)
    {
        path = NormalizeAbsolute(path);
        RejectReparseAncestors(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Windows did not supply the current user's security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.Create(security);
        RejectReparseAncestors(path);
        directory.SetAccessControl(security);
        RejectReparseAncestors(path);
    }

}
