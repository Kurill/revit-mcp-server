using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Base;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.Base
{
    /// <summary>
    /// Base for every command. The SDK's RaiseAndWaitForCompletion never resets the
    /// handler's completion event, so without this a second call returns at once with
    /// the previous call's result while Revit runs the new one later.
    /// </summary>
    public abstract class GuardedCommandBase : ExternalEventCommandBase
    {
        private readonly object _callLock = new object();
        private bool _previousCallTimedOut;

        protected GuardedCommandBase(IWaitableExternalEventHandler handler, UIApplication uiApp)
            : base(handler, uiApp)
        {
            AutoCheckpoint.EnsureCreated();
        }

        private ManualResetEvent Signal =>
            (Handler as ICompletionSignal)?.CompletionSignal
            ?? throw new InvalidOperationException($"{GetType().Name}: handler does not implement ICompletionSignal");

        public sealed override object Execute(JObject parameters, string requestId)
        {
            lock (_callLock)
            {
                // The handler's fields are written inside ExecuteCore, so a call queued in
                // Revit (typically behind a confirmation dialog) would run with the new
                // call's parameters if we let it through.
                if (_previousCallTimedOut)
                {
                    if (!Signal.WaitOne(0))
                        throw new InvalidOperationException(
                            $"{CommandName}: the previous call timed out and is still pending in Revit " +
                            "(a dialog may be open). Check the model before calling again.");
                    _previousCallTimedOut = false;
                }

                if (AutoCheckpoint.AppliesTo(CommandName, parameters))
                    AutoCheckpoint.Run();

                _lastWaitTimedOut = false;
                try
                {
                    return ExecuteCore(parameters, requestId);
                }
                catch (Exception ex) when (_lastWaitTimedOut)
                {
                    // Revit never got to run the call - say why it usually happens.
                    throw new TimeoutException($"{ex.Message}. {TimeoutHint}", ex);
                }
            }
        }

        private const string TimeoutHint =
            "Revit did not finish the command in time. If Revit is in an active tool or edit mode, " +
            "or a dialog is open, press Esc / close it and retry.";

        private bool _lastWaitTimedOut;

        protected abstract object ExecuteCore(JObject parameters, string requestId);

        protected new bool RaiseAndWaitForCompletion(int timeoutMilliseconds)
        {
            Signal.Reset();
            AbandonedCalls.Clear(Handler);
            bool completed = base.RaiseAndWaitForCompletion(timeoutMilliseconds);
            _previousCallTimedOut = !completed;
            _lastWaitTimedOut = !completed;
            // The handler may still run (e.g. after a confirmation dialog is answered);
            // ConfirmationHelper then cancels it instead of applying a change nobody is waiting for.
            if (!completed)
                AbandonedCalls.Mark(Handler);
            return completed;
        }
    }
}
