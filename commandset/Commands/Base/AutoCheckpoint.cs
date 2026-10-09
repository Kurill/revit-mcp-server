using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;

namespace RevitMCPCommandSet.Commands.Base
{
    /// <summary>
    /// Saves a checkpoint before the first model-changing call, at most every 30 minutes
    /// per model. REVIT_MCP_AUTO_CHECKPOINT=0 (user environment) turns it off.
    /// </summary>
    public static class AutoCheckpoint
    {
        private static readonly string[] ReadOnlyPrefixes =
            { "get_", "list_", "analyze_", "check_", "find_", "measure_", "query_", "export_" };

        private static readonly HashSet<string> ReadOnlyCommands = new HashSet<string>
        {
            "say_hello", "ai_element_filter", "clash_detection", "create_checkpoint", "restore_checkpoint"
        };

        private static readonly object Lock = new object();
        private static RevitWorkEventHandler _handler;
        private static ExternalEvent _event;

        /// <summary>Must be called on Revit's API thread, which is where commands are constructed.</summary>
        public static void EnsureCreated()
        {
            if (_event != null) return;
            _handler = new RevitWorkEventHandler("Auto Checkpoint");
            _event = ExternalEvent.Create(_handler);
        }

        public static bool AppliesTo(string commandName, JObject parameters)
        {
            if (_event == null || ReadOnlyCommands.Contains(commandName)) return false;
            if (ReadOnlyPrefixes.Any(p => commandName.StartsWith(p, StringComparison.Ordinal))) return false;
            if (parameters?["dryRun"]?.Type == JTokenType.Boolean && (bool)parameters["dryRun"]) return false;
            try
            {
                return Environment.GetEnvironmentVariable("REVIT_MCP_AUTO_CHECKPOINT", EnvironmentVariableTarget.User) != "0";
            }
            catch
            {
                return true;
            }
        }

        /// <summary>A failed checkpoint does not stop the command; the reason is traced.</summary>
        public static void Run()
        {
            lock (Lock)
            {
                _handler.SetWork(app => RevitMCPCommandSet.Utils.ModelCheckpoints.CreateIfDue(app.ActiveUIDocument?.Document));
                _handler.CompletionSignal.Reset();
                _event.Raise();
                if (!_handler.CompletionSignal.WaitOne(300000))
                    System.Diagnostics.Trace.WriteLine("[RevitMCP] Auto checkpoint timed out");
                else if (_handler.Error != null)
                    System.Diagnostics.Trace.WriteLine($"[RevitMCP] Auto checkpoint failed: {_handler.Error.Message}");
            }
        }
    }
}
