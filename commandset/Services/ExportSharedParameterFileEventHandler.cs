using RevitMCPCommandSet.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class ExportSharedParameterFileEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public string FilePath { get; set; } = "";
        public object Result { get; private set; }

        public void SetParameters(string filePath)
        {
            FilePath = filePath;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 30000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                if (string.IsNullOrEmpty(FilePath))
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    FilePath = Path.Combine(desktop, $"SharedParameters_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                }
                ExportPathGuard.Check(FilePath, ".txt");

                // Get current shared parameter file (if any)
                var currentFile = app.Application.SharedParametersFilename;

                // Create a temp file for export
                string tempFile = Path.GetTempFileName();

                try
                {
                    // Create empty shared parameter file
                    File.WriteAllText(tempFile, "");
                    app.Application.SharedParametersFilename = tempFile;
                    var defFile = app.Application.OpenSharedParameterFile();

                    if (defFile == null)
                    {
                        // Create fresh file structure
                        using (var writer = new StreamWriter(tempFile))
                        {
                            writer.WriteLine("# This is a Revit shared parameter file.");
                            writer.WriteLine("# Do not edit manually.");
                            writer.WriteLine("*META\tVERSION\tMINVERSION");
                            writer.WriteLine("META\t2\t1");
                            writer.WriteLine("*GROUP\tID\tNAME");
                        }
                        defFile = app.Application.OpenSharedParameterFile();
                    }

                    // Collect all shared parameters from the project
                    var bindingMap = doc.ParameterBindings;
                    var iterator = bindingMap.ForwardIterator();
                    var exportedParams = new List<object>();
                    int groupId = 1;

                    // Create a default group
                    var defaultGroup = defFile.Groups.Create("Exported Parameters");

                    while (iterator.MoveNext())
                    {
                        var definition = iterator.Key;
                        if (definition is ExternalDefinition extDef)
                        {
                            defaultGroup.Definitions.Create(
                                new ExternalDefinitionCreationOptions(extDef.Name, extDef.GetDataType())
                                {
                                    GUID = extDef.GUID,
                                    Description = extDef.Description,
                                    Visible = extDef.Visible,
                                    UserModifiable = extDef.UserModifiable
                                });
                            exportedParams.Add(new
                            {
                                name = extDef.Name,
                                guid = extDef.GUID.ToString(),
                                parameterType = extDef.GetDataType().TypeId ?? "Text"
                            });
                        }
                        // In a project the binding map keys are InternalDefinitions even
                        // for shared parameters; the GUID lives on the SharedParameterElement.
                        else if (definition is InternalDefinition intDef
                                 && doc.GetElement(intDef.Id) is SharedParameterElement shared)
                        {
                            defaultGroup.Definitions.Create(
                                new ExternalDefinitionCreationOptions(intDef.Name, intDef.GetDataType())
                                {
                                    GUID = shared.GuidValue
                                });
                            exportedParams.Add(new
                            {
                                name = intDef.Name,
                                guid = shared.GuidValue.ToString(),
                                parameterType = intDef.GetDataType().TypeId
                            });
                        }
                    }

                    // Copy result to final path
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.Copy(tempFile, FilePath, overwrite: true);

                    Result = new
                    {
                        success = true,
                        filePath = FilePath,
                        parameterCount = exportedParams.Count,
                        parameters = exportedParams
                    };
                }
                finally
                {
                    // Restore original shared parameter file
                    app.Application.SharedParametersFilename = currentFile ?? "";

                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
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

        public string GetName() => "Export Shared Parameter File";
    }
}
