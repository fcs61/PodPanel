using System.Security.AccessControl;
using System.Security.Principal;

namespace WPods.Service;

/// <summary>
/// Local change: the folder protection that upstream's Updater.ProtectDataFolder did, kept after the updater was removed.
/// </summary>
internal static class DataFolder
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WPods");

    /// <summary>
    /// %ProgramData%\WPods (the service log) writable only by SYSTEM and Administrators; other users may read it.
    /// By default any user may create files under %ProgramData%.
    /// </summary>
    public static void Protect()
    {
        try
        {
            var root = new DirectoryInfo(Root);
            root.Create();
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.Warn("service", $"{root.FullName} is a link: not protected");
                return;
            }
            var sid = (WellKnownSidType t) => new SecurityIdentifier(t, null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute, All, PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(security);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("service", $"{Root} not protected: {e.Message}");
        }
    }
}
