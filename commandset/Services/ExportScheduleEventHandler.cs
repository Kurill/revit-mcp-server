using RevitMCPCommandSet.Utils;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class ExportScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public long ScheduleId { get; set; }
        public string ExportPath { get; set; }
        public string Delimiter { get; set; }
        public bool IncludeHeaders { get; set; } = true;
        public AIResult<object> Result { get; private set; }

        public void SetParameters(long scheduleId, string exportPath, string delimiter, bool includeHeaders)
        {
            ScheduleId = scheduleId;
            ExportPath = exportPath;
            Delimiter = delimiter;
            IncludeHeaders = includeHeaders;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

#if REVIT2024_OR_GREATER
                var elementId = new ElementId(ScheduleId);
#else
                var elementId = new ElementId((int)ScheduleId);
#endif

                var schedule = doc.GetElement(elementId) as ViewSchedule;
                if (schedule == null)
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = $"No schedule found with ID {ScheduleId}"
                    };
                    return;
                }

                var exportPath = ExportPath;
                if (string.IsNullOrEmpty(exportPath))
                    exportPath = Path.Combine(Path.GetTempPath(), $"schedule_{ScheduleId}.txt");

                ExportPathGuard.Check(exportPath, ".txt", ".csv", ".tsv");
                string directory = Path.GetDirectoryName(exportPath);
                string filename = Path.GetFileName(exportPath);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // Read the schedule body as displayed; the header row Revit renders inside the body
                // is returned separately so it is not written twice.
                var dataRows = ScheduleFieldResolver.ReadBody(schedule, int.MaxValue, out var headers, out int rowCount);

                string delimChar = "\t";
                switch (Delimiter?.ToLower())
                {
                    case "comma": delimChar = ","; break;
                    case "space": delimChar = " "; break;
                    case "semicolon": delimChar = ";"; break;
                }

                var lines = new List<string>();

                // Add column headers
                if (IncludeHeaders && headers.Count > 0)
                    lines.Add(string.Join(delimChar, headers));

                // Add body rows
                foreach (var cells in dataRows)
                    lines.Add(string.Join(delimChar, cells));

                File.WriteAllLines(exportPath, lines);

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Successfully exported schedule '{schedule.Name}' ({lines.Count} lines) to '{exportPath}'",
                    Response = new
                    {
                        exportPath = exportPath,
                        scheduleName = schedule.Name,
                        rowCount = rowCount,
                        lineCount = lines.Count
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to export schedule: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => "Export Schedule";
    }
}
