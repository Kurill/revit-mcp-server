using System;
using System.IO;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Helpers;
using revit_mcp_plugin.Utils;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var pluginDir = Path.GetDirectoryName(typeof(Application).Assembly.Location);
            McpLogger.Initialize(pluginDir);
            McpLogger.Info("Application", "Plugin starting");

            // Auto-configure Claude Desktop on first run (silent, never crashes)
            ClaudeDesktopConfigurator.EnsureConfigured();

            // The server needs a UIApplication, which the first Idling event provides.
            application.Idling += AutoStartOnIdling;

            return Result.Succeeded;
        }

        private void AutoStartOnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var uiApp = sender as UIApplication;
            if (uiApp == null) return;
            uiApp.Idling -= AutoStartOnIdling;

            try
            {
                var service = SocketService.Instance;
                if (!service.IsRunning)
                {
                    service.Initialize(uiApp);
                    service.Start();
                    McpLogger.Info("Application", "MCP server auto-started");
                }
            }
            catch (Exception ex)
            {
                McpLogger.Error("Application", "MCP auto-start failed", ex);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RevitMCP] Error during shutdown: {ex.Message}");
            }

            return Result.Succeeded;
        }
    }
}
