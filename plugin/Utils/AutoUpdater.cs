using System;
using System.Diagnostics;
using System.IO;
using revit_mcp_plugin.Helpers;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// Hands the update to auto-update.ps1 (shipped in the plugin folder by the release
    /// workflow), which waits for Revit to exit because Revit locks the add-in DLLs.
    /// </summary>
    public static class AutoUpdater
    {
        public static string InstalledVersion(string pluginDir)
        {
            string versionFile = Path.Combine(pluginDir, "version.txt");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
        }

        public static void ScheduleAfterExit(string pluginDir, string revitYear)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("REVIT_MCP_AUTO_UPDATE") == "0") return;
                // Source builds have no version.txt; updating would overwrite them.
                if (InstalledVersion(pluginDir) == null) return;

                string script = Path.Combine(pluginDir, "auto-update.ps1");
                if (!File.Exists(script)) return;

                // The update replaces the plugin folder, including this script.
                string runCopy = Path.Combine(Path.GetTempPath(), $"revit-mcp-auto-update-{revitYear}.ps1");
                File.Copy(script, runCopy, true);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                                $"-File \"{runCopy}\" -Year {revitYear} -PluginDir \"{pluginDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                McpLogger.Info("AutoUpdater", "Update check scheduled for after Revit exits");
            }
            catch (Exception ex)
            {
                McpLogger.Error("AutoUpdater", "Could not schedule the update check", ex);
            }
        }
    }
}
