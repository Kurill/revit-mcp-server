using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ModifyScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        public long ScheduleId { get; set; }
        public string ScheduleName { get; set; } = "";
        public string Action { get; set; } = "";
        public List<string> FieldNames { get; set; } = new List<string>();
        public List<ScheduleFilterInfo> Filters { get; set; } = new List<ScheduleFilterInfo>();
        public List<ScheduleSortInfo> SortFields { get; set; } = new List<ScheduleSortInfo>();
        public string NewName { get; set; } = "";
        public bool? ShowTitle { get; set; }
        public bool? ShowHeaders { get; set; }
        public bool? ShowGridLines { get; set; }
        public bool? IsItemized { get; set; }

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
                var schedule = FindSchedule(doc);

                if (schedule == null)
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = ScheduleId > 0
                            ? $"Schedule with ID {ScheduleId} not found"
                            : $"Schedule with name '{ScheduleName}' not found"
                    };
                    return;
                }

                using (var transaction = new Transaction(doc, "Modify Schedule"))
                {
                    transaction.Start();
                    try
                    {
                        string details = ExecuteAction(schedule);

                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());

                        Result = new AIResult<object>
                        {
                            Success = true,
                            Message = $"Successfully performed '{Action}' on schedule '{schedule.Name}'",
                            Response = new
                            {
#if REVIT2024_OR_GREATER
                                scheduleId = schedule.Id.Value,
#else
                                scheduleId = schedule.Id.IntegerValue,
#endif
                                scheduleName = schedule.Name,
                                action = Action,
                                details
                            }
                        };
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                            RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(transaction);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Modify schedule failed: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private ViewSchedule FindSchedule(Document doc)
        {
            if (ScheduleId > 0)
            {
                return doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(ScheduleId)) as ViewSchedule;
            }

            if (!string.IsNullOrEmpty(ScheduleName))
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(s => s.Name.Equals(ScheduleName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private string ExecuteAction(ViewSchedule schedule)
        {
            var def = schedule.Definition;

            switch (Action.ToLowerInvariant().Replace("_", ""))
            {
                case "addfield":
                    return AddFields(schedule, def);

                case "removefield":
                    return RemoveFields(schedule, def);

                case "setfilters":
                    return SetFilters(schedule, def);

                case "clearfilters":
                    def.ClearFilters();
                    return "All filters cleared";

                case "setsorting":
                    return SetSorting(schedule, def);

                case "clearsorting":
                    def.ClearSortGroupFields();
                    return "All sort/group fields cleared";

                case "rename":
                    if (string.IsNullOrEmpty(NewName))
                        throw new ArgumentException("NewName is required for rename action");
                    var oldName = schedule.Name;
                    schedule.Name = NewName;
                    return $"Renamed from '{oldName}' to '{NewName}'";

                case "setdisplayoptions":
                    return SetDisplayOptions(def);

                default:
                    throw new ArgumentException($"Unknown action: {Action}. Valid actions: add_field, remove_field, set_filters, clear_filters, set_sorting, clear_sorting, rename, set_display_options");
            }
        }

        private string AddFields(ViewSchedule schedule, ScheduleDefinition def)
        {
            if (FieldNames == null || FieldNames.Count == 0)
                throw new ArgumentException("FieldNames is required for add_field action");

            var schedulableFields = def.GetSchedulableFields();
            var added = new List<string>();
            var notFound = new List<string>();

            foreach (var fieldName in FieldNames)
            {
                // BuiltInParameter alias (language-independent) first, then display name
                var matchingField = ScheduleFieldResolver.FindSchedulableField(schedule.Document, schedulableFields, fieldName);

                if (matchingField != null)
                {
                    def.AddField(matchingField);
                    added.Add(fieldName);
                }
                else
                {
                    notFound.Add(fieldName);
                }
            }

            if (added.Count == 0)
                throw new ArgumentException($"None of the requested fields could be added (no matching schedulable field): {string.Join(", ", notFound)}");

            var result = $"Added {added.Count} field(s): {string.Join(", ", added)}";
            if (notFound.Count > 0)
                result += $". Not found: {string.Join(", ", notFound)}";
            return result;
        }

        private string RemoveFields(ViewSchedule schedule, ScheduleDefinition def)
        {
            if (FieldNames == null || FieldNames.Count == 0)
                throw new ArgumentException("FieldNames is required for remove_field action");

            var removed = new List<string>();
            var notFound = new List<string>();
            foreach (var fieldName in FieldNames)
            {
                // Name, column heading, or BuiltInParameter alias (language-independent)
                var field = ScheduleFieldResolver.FindExistingField(def, fieldName);
                if (field != null)
                {
                    def.RemoveField(field.FieldId);
                    removed.Add(fieldName);
                }
                else
                {
                    notFound.Add(fieldName);
                }
            }

            var result = $"Removed {removed.Count} field(s): {string.Join(", ", removed)}";
            if (notFound.Count > 0)
                result += $". Not found: {string.Join(", ", notFound)}";
            return result;
        }

        private string SetFilters(ViewSchedule schedule, ScheduleDefinition def)
        {
            if (Filters == null || Filters.Count == 0)
                throw new ArgumentException("Filters is required for set_filters action");

            def.ClearFilters();

            int addedCount = 0;
            var notes = new List<string>();
            var applied = new List<string>();
            foreach (var filterInfo in Filters)
            {
                // Prefers a field already in the schedule (by name, heading or BuiltInParameter alias);
                // throws a descriptive error if the field cannot be found.
                var field = ScheduleFieldResolver.ResolveFieldForFilter(schedule, filterInfo.FieldName, filterInfo.FieldIndex, notes);

                // Validates the filter type for the field and converts the value to its storage type;
                // throws (rolling back the transaction) instead of silently adding a no-op filter.
                var filter = ScheduleFieldResolver.BuildFilter(schedule, field, filterInfo.FilterType, filterInfo.FilterValue);
                def.AddFilter(filter);
                applied.Add($"{field.GetName()} {filter.FilterType} '{filterInfo.FilterValue}'");
                addedCount++;
            }

            var result = $"Set {addedCount} filter(s): {string.Join("; ", applied)}";
            if (notes.Count > 0)
                result += $". {string.Join(". ", notes)}";
            return result;
        }

        private string SetSorting(ViewSchedule schedule, ScheduleDefinition def)
        {
            if (SortFields == null || SortFields.Count == 0)
                throw new ArgumentException("SortFields is required for set_sorting action");

            def.ClearSortGroupFields();

            int addedCount = 0;
            foreach (var sortInfo in SortFields)
            {
                ScheduleFieldId fieldId = null;

                if (!string.IsNullOrEmpty(sortInfo.FieldName) &&
                    ScheduleFieldResolver.FindExistingField(def, sortInfo.FieldName) is ScheduleField namedField)
                {
                    fieldId = namedField.FieldId;
                }
                else if (sortInfo.FieldIndex >= 0 && sortInfo.FieldIndex < def.GetFieldCount())
                {
                    fieldId = def.GetField(sortInfo.FieldIndex).FieldId;
                }

                if (fieldId == null)
                    throw new ArgumentException(
                        $"Sort field '{sortInfo.FieldName}' (index {sortInfo.FieldIndex}) not found in schedule. Schedule fields: {ScheduleFieldResolver.DescribeFields(def)}");

                var sortOrder = ScheduleSortOrder.Ascending;
                if (!string.IsNullOrEmpty(sortInfo.SortOrder) &&
                    sortInfo.SortOrder.Equals("Descending", StringComparison.OrdinalIgnoreCase))
                {
                    sortOrder = ScheduleSortOrder.Descending;
                }

                var sortGroupField = new ScheduleSortGroupField(fieldId, sortOrder);
                def.AddSortGroupField(sortGroupField);
                addedCount++;
            }

            return $"Set {addedCount} sort field(s)";
        }

        private string SetDisplayOptions(ScheduleDefinition def)
        {
            var changes = new List<string>();

            if (ShowTitle.HasValue)
            {
                def.ShowTitle = ShowTitle.Value;
                changes.Add($"ShowTitle={ShowTitle.Value}");
            }
            if (ShowHeaders.HasValue)
            {
                def.ShowHeaders = ShowHeaders.Value;
                changes.Add($"ShowHeaders={ShowHeaders.Value}");
            }
            if (ShowGridLines.HasValue)
            {
                def.ShowGridLines = ShowGridLines.Value;
                changes.Add($"ShowGridLines={ShowGridLines.Value}");
            }
            if (IsItemized.HasValue)
            {
                def.IsItemized = IsItemized.Value;
                changes.Add($"IsItemized={IsItemized.Value}");
            }

            if (changes.Count == 0)
                return "No display options specified";

            return $"Updated: {string.Join(", ", changes)}";
        }

        public string GetName() => "Modify Schedule";
    }
}
