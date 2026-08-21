using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace BackupMonitor.Shared.Security;

/// <summary>Windows 本地敏感目录和文件的最小 ACL 工具；非 Windows 环境保持普通文件行为。</summary>
public static class SecureFileSystem
{
    public static void CreateDirectory(
        string path,
        bool enforceAcl = true,
        bool includeCurrentUser = false)
    {
        Directory.CreateDirectory(path);
        if (enforceAcl)
            ApplyDirectoryAcl(path, includeCurrentUser: includeCurrentUser);
    }

    public static void ApplyDirectoryAcl(
        string path,
        bool enforceAcl = true,
        bool includeCurrentUser = false)
    {
        if (!enforceAcl || !OperatingSystem.IsWindows() || !Directory.Exists(path))
            return;

        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        ClearAndProtect(security, includeCurrentUser ? GetCurrentUserSid() : null);
        AddAdministrators(security);
        info.SetAccessControl(security);
    }

    public static void ApplyFileAcl(
        string path,
        bool enforceAcl = true,
        bool includeCurrentUser = false)
    {
        if (!enforceAcl || !OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        ClearAndProtect(security, includeCurrentUser ? GetCurrentUserSid() : null);
        AddAdministrators(security);
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ClearAndProtect(
        FileSystemSecurity security,
        SecurityIdentifier? additionalIdentity = null)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);

        if (additionalIdentity is not null)
            AddFullControl(security, additionalIdentity);
    }

    [SupportedOSPlatform("windows")]
    private static void AddAdministrators(FileSystemSecurity security)
    {
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControl(FileSystemSecurity security, SecurityIdentifier identity)
    {
        var inheritanceFlags = security is DirectorySecurity
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }
}
