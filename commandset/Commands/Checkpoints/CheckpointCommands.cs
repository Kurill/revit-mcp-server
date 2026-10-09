using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Base;
using RevitMCPCommandSet.Services;

namespace RevitMCPCommandSet.Commands.Checkpoints
{
    public abstract class CheckpointCommandBase : GuardedCommandBase
    {
        protected CheckpointCommandBase(string name, UIApplication uiApp)
            : base(new RevitWorkEventHandler(name), uiApp)
        {
        }

        private RevitWorkEventHandler WorkHandler => (RevitWorkEventHandler)Handler;

        protected abstract Func<UIApplication, object> BuildWork(JObject parameters);

        protected static Autodesk.Revit.DB.Document RequireDocument(UIApplication app) =>
            app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");

        protected override object ExecuteCore(JObject parameters, string requestId)
        {
            WorkHandler.SetWork(BuildWork(parameters ?? new JObject()));
            // Copying a large model can take a while.
            if (!RaiseAndWaitForCompletion(300000))
                throw new TimeoutException($"{CommandName} timed out after 300 s");
            if (WorkHandler.Error != null)
                throw new Exception($"{CommandName} failed: {WorkHandler.Error.Message}");
            return WorkHandler.Result;
        }
    }

    public class CreateCheckpointCommand : CheckpointCommandBase
    {
        public CreateCheckpointCommand(UIApplication uiApp) : base("Create Checkpoint", uiApp) { }

        public override string CommandName => "create_checkpoint";

        protected override Func<UIApplication, object> BuildWork(JObject parameters)
        {
            string label = parameters["label"]?.ToString();
            return app => RevitMCPCommandSet.Utils.ModelCheckpoints.Create(RequireDocument(app), label);
        }
    }

    public class ListCheckpointsCommand : CheckpointCommandBase
    {
        public ListCheckpointsCommand(UIApplication uiApp) : base("List Checkpoints", uiApp) { }

        public override string CommandName => "list_checkpoints";

        protected override Func<UIApplication, object> BuildWork(JObject parameters) =>
            app => RevitMCPCommandSet.Utils.ModelCheckpoints.List(RequireDocument(app));
    }

    public class RestoreCheckpointCommand : CheckpointCommandBase
    {
        public RestoreCheckpointCommand(UIApplication uiApp) : base("Restore Checkpoint", uiApp) { }

        public override string CommandName => "restore_checkpoint";

        protected override Func<UIApplication, object> BuildWork(JObject parameters)
        {
            string checkpoint = parameters["checkpoint"]?.ToString();
            if (string.IsNullOrWhiteSpace(checkpoint))
                throw new ArgumentException("checkpoint is required; call list_checkpoints for the names.");
            return app => RevitMCPCommandSet.Utils.ModelCheckpoints.Restore(app, checkpoint);
        }
    }
}
