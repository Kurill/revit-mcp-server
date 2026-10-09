using RevitMCPCommandSet.Commands.Base;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.ViewManagement;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Commands.ViewManagement
{
    public class NavigateViewCommand : GuardedCommandBase
    {
        private static readonly object _executionLock = new object();
        private NavigateViewEventHandler _handler => (NavigateViewEventHandler)Handler;
        public override string CommandName => "navigate_view";

        public NavigateViewCommand(UIApplication uiApp)
            : base(new NavigateViewEventHandler(), uiApp) { }

        protected override object ExecuteCore(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    _handler.Action = parameters?["action"]?.Value<string>() ?? "activate";
                    _handler.ViewId = parameters?["viewId"]?.Value<long>();
                    _handler.ViewName = parameters?["viewName"]?.Value<string>();
                    _handler.ElementIds = parameters?["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
                    _handler.ZoomFactor = parameters?["zoomFactor"]?.Value<double>();

                    _handler.SetParameters();
                    if (RaiseAndWaitForCompletion(15000))
                        return _handler.Result;
                    throw new TimeoutException("Navigate view timed out");
                }
                catch (Exception ex) { throw new Exception($"Navigate view failed: {ex.Message}"); }
            }
        }
    }
}
