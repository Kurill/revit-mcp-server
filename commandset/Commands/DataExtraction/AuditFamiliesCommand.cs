using RevitMCPCommandSet.Commands.Base;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class AuditFamiliesCommand : GuardedCommandBase
    {
        private static readonly object _executionLock = new object();
        private AuditFamiliesEventHandler _handler => (AuditFamiliesEventHandler)Handler;

        public override string CommandName => "audit_families";

        public AuditFamiliesCommand(UIApplication uiApp)
            : base(new AuditFamiliesEventHandler(), uiApp) { }

        protected override object ExecuteCore(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    _handler.IncludeUnused = parameters?["includeUnused"]?.Value<bool>() ?? true;
                    _handler.CategoryFilter = parameters?["categoryFilter"]?.Value<string>() ?? "";
                    var limit = parameters?["limit"]?.Value<int?>() ?? 50;
                    _handler.Limit = limit > 0 ? limit : 50;

                    _handler.SetParameters();

                    if (RaiseAndWaitForCompletion(60000))
                        return _handler.Result;

                    throw new TimeoutException("Audit families timed out");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Audit families failed: {ex.Message}");
                }
            }
        }
    }
}
