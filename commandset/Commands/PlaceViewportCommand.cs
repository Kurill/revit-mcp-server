using RevitMCPCommandSet.Commands.Base;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class PlaceViewportCommand : GuardedCommandBase
    {
        private static readonly object _executionLock = new object();
        private PlaceViewportEventHandler _handler => (PlaceViewportEventHandler)Handler;

        public override string CommandName => "place_viewport";

        public PlaceViewportCommand(UIApplication uiApp)
            : base(new PlaceViewportEventHandler(), uiApp)
        {
        }

        protected override object ExecuteCore(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var viewportInfo = parameters?.ToObject<ViewportCreationInfo>();
                    if (viewportInfo == null)
                        throw new ArgumentException("Viewport creation info is required");

                    _handler.ViewportInfo = viewportInfo;

                    if (RaiseAndWaitForCompletion(15000))
                    {
                        return _handler.Result;
                    }
                    else
                    {
                        throw new TimeoutException("Place viewport timed out");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Place viewport failed: {ex.Message}");
                }
            }
        }
    }
}
