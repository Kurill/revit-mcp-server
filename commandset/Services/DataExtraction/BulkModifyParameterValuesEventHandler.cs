using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Helpers;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class BulkModifyParameterValuesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        public List<long> ElementIds { get; set; } = new List<long>();
        public string CategoryName { get; set; } = "";
        public string ParameterName { get; set; } = "";
        public string Operation { get; set; } = "set"; // set, prefix, suffix, find_replace, clear
        public string Value { get; set; } = "";
        public string FindText { get; set; } = "";
        public string ReplaceText { get; set; } = "";
        public bool OnlyEmpty { get; set; } = false; // only modify empty values
        public bool DryRun { get; set; } = true;

        public AIResult<object> Result { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public void SetParameters() { TaskCompleted = false; _resetEvent.Reset(); }
        public bool WaitForCompletion(int timeoutMilliseconds = 30000) { return _resetEvent.WaitOne(timeoutMilliseconds); }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                // Get elements
                var elements = new List<Element>();
                if (ElementIds.Count > 0)
                {
                    foreach (var id in ElementIds)
                    {
#if REVIT2024_OR_GREATER
                        var elem = doc.GetElement(new ElementId(id));
#else
                        var elem = doc.GetElement(new ElementId((int)id));
#endif
                        if (elem != null) elements.Add(elem);
                    }
                }
                else if (!string.IsNullOrEmpty(CategoryName))
                {
                    // Find by category
                    var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                    foreach (var elem in collector)
                    {
                        if (CategoryResolver.CategoryMatches(doc, elem, CategoryName))
                            elements.Add(elem);
                    }
                }
                else
                {
                    throw new ArgumentException("Provide either elementIds or categoryName");
                }

                if (string.IsNullOrEmpty(ParameterName))
                    throw new ArgumentException("parameterName is required");

                int skipped = 0;
                var changes = new List<(Element Element, Parameter Param, string OldValue, string NewValue)>();

                foreach (var elem in elements)
                {
                    var param = elem.LookupParameter(ParameterName);
                    if (param == null || param.IsReadOnly)
                    {
                        skipped++;
                        continue;
                    }

                    string currentValue = DisplayValueParameters.Read(param);
                    string newValue;
                    switch (Operation.ToLower())
                    {
                        case "set":
                            if (OnlyEmpty && !string.IsNullOrEmpty(currentValue)) { skipped++; continue; }
                            newValue = Value;
                            break;
                        case "prefix":
                            if (OnlyEmpty && !string.IsNullOrEmpty(currentValue)) { skipped++; continue; }
                            newValue = Value + currentValue;
                            break;
                        case "suffix":
                            if (OnlyEmpty && !string.IsNullOrEmpty(currentValue)) { skipped++; continue; }
                            newValue = currentValue + Value;
                            break;
                        case "find_replace":
                            if (string.IsNullOrEmpty(FindText) || !currentValue.Contains(FindText)) { skipped++; continue; }
                            newValue = currentValue.Replace(FindText, ReplaceText);
                            break;
                        case "clear":
                            newValue = "";
                            break;
                        default:
                            throw new ArgumentException($"Unknown operation: {Operation}");
                    }

                    if (newValue == currentValue) { skipped++; continue; }
                    changes.Add((elem, param, currentValue, newValue));
                }

                var preview = changes.Take(50).Select(c => new
                {
                    elementId = c.Element.Id.ToString(),
                    elementName = c.Element.Name,
                    currentValue = c.OldValue,
                    newValue = c.NewValue
                }).ToList();

                if (DryRun || changes.Count == 0)
                {
                    Result = new AIResult<object>
                    {
                        Success = true,
                        Message = $"{changes.Count} element(s) would change, {skipped} skipped" +
                                  (DryRun ? ". Nothing was written (dryRun=true)." : "; nothing to write."),
                        Response = new
                        {
                            operation = Operation,
                            parameterName = ParameterName,
                            toModify = changes.Count,
                            skipped,
                            totalElements = elements.Count,
                            dryRun = DryRun,
                            preview
                        }
                    };
                    return;
                }

                if (!ConfirmationHelper.Confirm($"{Operation} '{ParameterName}' on", changes.Count))
                {
                    Result = new AIResult<object> { Success = false, Message = "Bulk modify cancelled by the user." };
                    return;
                }

                var errors = new List<string>();
                using (var transaction = new Transaction(doc, "Bulk Modify Parameter Values"))
                {
                    transaction.Start();
                    foreach (var c in changes)
                    {
                        string error = DisplayValueParameters.Write(c.Param, c.NewValue);
                        if (error != null) errors.Add($"Element {c.Element.Id}: {error}");
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit rolled the change back.");
                }

                Result = new AIResult<object>
                {
                    Success = errors.Count == 0,
                    Message = $"Modified {changes.Count - errors.Count} element(s), {skipped} skipped, {errors.Count} failed",
                    Response = new
                    {
                        operation = Operation,
                        parameterName = ParameterName,
                        modified = changes.Count - errors.Count,
                        skipped,
                        failed = errors.Count,
                        errors = errors.Take(20).ToList(),
                        totalElements = elements.Count,
                        dryRun = false
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Bulk modify failed: {ex.Message}" };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Bulk Modify Parameter Values";
    }
}
