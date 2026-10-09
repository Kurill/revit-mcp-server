using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// External event handler for code execution
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        // Code execution parameters
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;

        // Execution result info
        public ExecutionResultInfo ResultInfo { get; private set; }

        // State synchronization
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        // Set code and parameters for execution
        public void SetExecutionParameters(string code, object[] parameters = null, string transactionMode = TransactionModeAuto)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode ?? TransactionModeAuto;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // Wait for completion - IWaitableExternalEventHandler implementation
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = new ExecutionResultInfo();

                if (RevitMCPCommandSet.Utils.AbandonedCalls.IsAbandoned(this))
                {
                    ResultInfo.Success = false;
                    ResultInfo.ErrorMessage = "Execution cancelled: the call had already timed out.";
                    return;
                }

                if (Helpers.ConfirmationHelper.DialogsEnabled() && !ConfirmRun(_generatedCode, _transactionMode))
                {
                    ResultInfo.Success = false;
                    ResultInfo.ErrorMessage = "Execution cancelled by the user.";
                    return;
                }

                if (_transactionMode == TransactionModeNone)
                {
                    // Let user code manage its own transactions
                    var result = CompileAndExecuteCode(
                        code: _generatedCode,
                        doc: doc,
                        parameters: _executionParameters
                    );

                    ResultInfo.Success = true;
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                }
                else
                {
                    // Default: wrap in a transaction
                    using (var transaction = new Transaction(doc, "Execute AI Code"))
                    {
                        transaction.Start();

                        var result = CompileAndExecuteCode(
                            code: _generatedCode,
                            doc: doc,
                            parameters: _executionParameters
                        );

                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());

                        ResultInfo.Success = true;
                        ResultInfo.Result = JsonConvert.SerializeObject(result);
                    }
                }
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorMessage = $"Execution failed: {ex.Message}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private bool ConfirmRun(string code, string transactionMode)
        {
            var dialog = new TaskDialog("MCP: run AI-generated code?")
            {
                MainInstruction = "An AI agent wants to run C# code inside Revit.",
                MainContent = "The code has full access to this model and to your files. " +
                              (transactionMode == TransactionModeNone
                                  ? "It manages its own transactions and may save or sync the model."
                                  : "Its model changes are committed as one undoable transaction.") +
                              "\n\nExpand the details to read it before allowing.",
                ExpandedContent = code,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            return Helpers.ConfirmationHelper.Ask(this, dialog);
        }

        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // Wrap code with a standardized entry point
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
            // User code entry point
            {code}
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);

            // Add required assembly references (deduplicate by simple name to avoid conflicts
            // caused by addins like BIM360 loading assemblies with the same name)
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .GroupBy(a => a.GetName().Name)
                .Select(g => g.First())
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // Compile code
            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                // Handle compilation result
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line}: {d.GetMessage()}"));
                    throw new Exception($"Code compilation error:\n{errors}");
                }

                // Invoke execution method via reflection
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                return executeMethod.Invoke(null, new object[] { doc, parameters });
            }
        }

        public string GetName()
        {
            return "Execute AI Code";
        }
    }

    // Execution result data structure
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
