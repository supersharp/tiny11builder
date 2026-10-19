using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
internal static class EdgeRegistration
{
    internal static void RemoveAfterFiles(string mount, Action<string> log)
    {
        ImageOperations.RequireOwnedMount(mount);
        foreach (var root in new[] { "Program Files", "Program Files (x86)" })
            if (ImageFileAccess.Exists(Path.Combine(mount, root, @"Microsoft\Edge")))
                throw new InvalidDataException("Edge files remain in the offline image. Its installed-program entry was not removed.");
        using var privileges = new ImageOperations.PrivilegeScope();
        using var software = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SOFTWARE"));
        foreach (var path in new[]
        {
            @"Microsoft\Windows\CurrentVersion\Uninstall",
            @"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        })
        {
            using var inventory = software.Root.OpenSubKey(path);
            if (inventory is null || !inventory.GetSubKeyNames().Contains("Microsoft Edge", StringComparer.OrdinalIgnoreCase)) continue;
            using (var entry = inventory.OpenSubKey("Microsoft Edge")!)
            {
                if (!string.Equals(entry.GetValue("DisplayName") as string, "Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.GetValue("Publisher") as string, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                {
                    log($"Retained offline uninstall entry with an unexpected identity: {path}\\Microsoft Edge");
                    continue;
                }
            }
            using var parent = OfflineRegistry.OpenWriteKey(software.Root, path);
            OfflineRegistry.DeleteKeyTree(parent, "Microsoft Edge");
            if (parent.GetSubKeyNames().Contains("Microsoft Edge", StringComparer.OrdinalIgnoreCase))
                throw new IOException("The offline Edge installed-program entry could not be removed.");
            log($"Removed offline Edge uninstall entry: {path}\\Microsoft Edge");
        }
    }
}
