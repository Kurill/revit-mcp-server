using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetScheduleDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public long ScheduleId { get; set; }
        public int MaxRows { get; set; } = 500;
        public AIResult<object> Result { get; private set; }

        public void SetParameters(long scheduleId, int maxRows = 500)
        {
            ScheduleId = scheduleId;
            MaxRows = maxRows;
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

                if (ScheduleId <= 0)
                {
                    Result = ListAllSchedules(doc);
                }
                else
                {
                    Result = GetScheduleData(doc);
                }
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to get schedule data: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private AIResult<object> ListAllSchedules(Document doc)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule)
                .ToList();

            var scheduleList = new List<object>();
            foreach (var schedule in schedules)
            {
                scheduleList.Add(new
                {
#if REVIT2024_OR_GREATER
                    id = schedule.Id.Value,
#else
                    id = schedule.Id.IntegerValue,
#endif
                    name = schedule.Name,
                    category = schedule.Definition.CategoryId != ElementId.InvalidElementId
                        ? Category.GetCategory(doc, schedule.Definition.CategoryId)?.Name ?? ""
                        : ""
                });
            }

            return new AIResult<object>
            {
                Success = true,
                Message = $"Found {scheduleList.Count} schedules in the project",
                Response = new
                {
                    scheduleCount = scheduleList.Count,
                    schedules = scheduleList
                }
            };
        }

        private AIResult<object> GetScheduleData(Document doc)
        {
#if REVIT2024_OR_GREATER
            var elementId = new ElementId(ScheduleId);
#else
            var elementId = new ElementId((int)ScheduleId);
#endif

            var viewSchedule = doc.GetElement(elementId) as ViewSchedule;
            if (viewSchedule == null)
            {
                return new AIResult<object>
                {
                    Success = false,
                    Message = $"No schedule found with ID {ScheduleId}"
                };
            }

            var definition = viewSchedule.Definition;
            int fieldCount = definition.GetFieldCount();

            // Read the body as displayed. The column-header row(s) Revit renders inside the body section
            // are returned as columnHeaders and excluded from rows; only visible (non-hidden) fields are columns.
            var rows = RevitMCPCommandSet.Utils.ScheduleFieldResolver.ReadBody(
                viewSchedule, MaxRows, out var columnHeaders, out int totalRows);
            int rowsToRead = rows.Count;

            var fieldNames = new List<string>();
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (!field.IsHidden)
                    fieldNames.Add(field.GetName());
            }

            // List available schedulable fields
            var availableFields = new List<object>();
            try
            {
                var schedulableFields = definition.GetSchedulableFields();
                foreach (var sf in schedulableFields)
                {
                    availableFields.Add(new
                    {
                        name = sf.GetName(doc),
                        fieldType = sf.FieldType.ToString()
                    });
                }
            }
            catch
            {
                // Some schedules may not support GetSchedulableFields
            }

            var result = new Dictionary<string, object>
            {
                ["scheduleName"] = viewSchedule.Name,
                ["columnHeaders"] = columnHeaders,
                ["fieldNames"] = fieldNames,
                ["rows"] = rows,
                ["fieldCount"] = fieldCount,
                ["rowCount"] = totalRows,
                ["returnedRows"] = rowsToRead,
                ["availableFields"] = availableFields
            };

            return new AIResult<object>
            {
                Success = true,
                Message = $"Schedule '{viewSchedule.Name}' data retrieved successfully ({rowsToRead} of {totalRows} rows)",
                Response = result
            };
        }

        public string GetName() => "Get Schedule Data";
    }
}
