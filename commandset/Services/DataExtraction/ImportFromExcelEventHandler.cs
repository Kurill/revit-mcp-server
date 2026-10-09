using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCPCommandSet.Helpers;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ImportFromExcelEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public string FilePath { get; set; } = "";
        public string SheetName { get; set; } = "";
        public bool DryRun { get; set; } = true;
        public object Result { get; private set; }

        public void SetParameters(string filePath, string sheetName, bool dryRun)
        {
            FilePath = filePath;
            SheetName = sheetName;
            DryRun = dryRun;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                if (!File.Exists(FilePath))
                {
                    Result = new { success = false, error = $"File not found: {FilePath}" };
                    return;
                }

                using (var workbook = new XLWorkbook(FilePath))
                {
                    var worksheet = string.IsNullOrEmpty(SheetName)
                        ? workbook.Worksheets.First()
                        : workbook.Worksheets.Worksheet(SheetName);

                    // Read header row to find column mapping
                    var headers = new Dictionary<int, string>();
                    int lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
                    for (int c = 1; c <= lastCol; c++)
                    {
                        string header = worksheet.Cell(1, c).GetString().Trim();
                        if (!string.IsNullOrEmpty(header))
                            headers[c] = header;
                    }

                    // Find ElementId column
                    int idCol = headers.FirstOrDefault(h =>
                        h.Value.Equals("ElementId", StringComparison.OrdinalIgnoreCase)).Key;

                    if (idCol == 0)
                    {
                        Result = new { success = false, error = "No 'ElementId' column found in the Excel file. Export with includeElementId=true first." };
                        return;
                    }

                    int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
                    int skipped = 0;
                    var changes = new List<(Element Element, Parameter Param, string OldValue, string NewValue)>();

                    for (int r = 2; r <= lastRow; r++)
                    {
                        string idStr = worksheet.Cell(r, idCol).GetString().Trim();
                        if (!long.TryParse(idStr, out long idVal)) { skipped++; continue; }

#if REVIT2024_OR_GREATER
                        var elemId = new ElementId(idVal);
#else
                        var elemId = new ElementId((int)idVal);
#endif
                        var elem = doc.GetElement(elemId);
                        if (elem == null) { skipped++; continue; }

                        foreach (var kvp in headers)
                        {
                            if (kvp.Key == idCol) continue;
                            string paramName = kvp.Value;
                            if (paramName == "Category" || paramName == "Family" || paramName == "Type") continue;

                            var param = elem.LookupParameter(paramName);
                            if (param == null || param.IsReadOnly) continue;

                            string cellValue = worksheet.Cell(r, kvp.Key).GetString().Trim();
                            string current = DisplayValueParameters.Read(param);
                            // Only cells the user actually edited; rewriting every exported
                            // value would round each one through display precision.
                            if (string.IsNullOrEmpty(cellValue) || cellValue == current) continue;

                            changes.Add((elem, param, current, cellValue));
                        }
                    }

                    var preview = changes.Take(50).Select(c => new
                    {
                        elementId = c.Element.Id.ToString(),
                        parameter = c.Param.Definition.Name,
                        from = c.OldValue,
                        to = c.NewValue
                    }).ToList();
                    int elementCount = changes.Select(c => c.Element.Id).Distinct().Count();

                    if (DryRun || changes.Count == 0)
                    {
                        Result = new
                        {
                            success = true,
                            dryRun = DryRun,
                            totalRows = lastRow - 1,
                            elementsToUpdate = elementCount,
                            valuesToChange = changes.Count,
                            skippedRows = skipped,
                            changes = preview,
                            message = $"{changes.Count} value(s) on {elementCount} element(s) differ from the model" +
                                      (DryRun ? ". Nothing was written (dryRun=true)." : "; nothing to write.")
                        };
                        return;
                    }

                    if (!ConfirmationHelper.Confirm($"import {changes.Count} parameter value(s) from Excel into", elementCount))
                    {
                        Result = new { success = false, error = "Import cancelled by the user." };
                        return;
                    }

                    var errors = new List<string>();
                    using (var tx = new Transaction(doc, "Import from Excel"))
                    {
                        tx.Start();
                        foreach (var c in changes)
                        {
                            string error = DisplayValueParameters.Write(c.Param, c.NewValue);
                            if (error != null)
                                errors.Add($"Element {c.Element.Id}, '{c.Param.Definition.Name}': {error}");
                        }

                        if (tx.Commit() != TransactionStatus.Committed)
                        {
                            Result = new { success = false, error = "Revit rolled the import back.", errors = errors.Take(20).ToList() };
                            return;
                        }
                    }

                    Result = new
                    {
                        success = errors.Count == 0,
                        dryRun = false,
                        valuesWritten = changes.Count - errors.Count,
                        failed = errors.Count,
                        elementsUpdated = elementCount,
                        skippedRows = skipped,
                        errors = errors.Take(20).ToList()
                    };
                }
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => "Import From Excel";
    }
}
