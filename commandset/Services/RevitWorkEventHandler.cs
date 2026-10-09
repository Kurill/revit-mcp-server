using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>Runs one delegate on Revit's API thread and keeps its result or error.</summary>
    public class RevitWorkEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        private readonly string _name;
        private Func<UIApplication, object> _work;

        public RevitWorkEventHandler(string name)
        {
            _name = name;
        }

        public object Result { get; private set; }
        public Exception Error { get; private set; }

        public void SetWork(Func<UIApplication, object> work)
        {
            _work = work;
            Result = null;
            Error = null;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Result = _work(app);
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => _name;
    }
}
