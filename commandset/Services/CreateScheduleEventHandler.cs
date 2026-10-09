using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public ScheduleCreationInfo ScheduleInfo { get; set; }
        public AIResult<object> Result { get; private set; }

        public void SetParameters(ScheduleCreationInfo scheduleInfo)
        {
            ScheduleInfo = scheduleInfo;
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
                string scheduleType = (ScheduleInfo.Type ?? "Regular").Trim();

                using (var transaction = new Transaction(doc, "Create Schedule"))
                {
                    transaction.Start();
                    try
                    {
                        var categoryId = ResolveCategoryId(doc);
                        ViewSchedule schedule;

                        var normalizedType = scheduleType.ToLowerInvariant().Replace("_", "").Replace(" ", "");

                        switch (normalizedType)
                        {
                            case "keyschedule":
                                schedule = ViewSchedule.CreateKeySchedule(doc, categoryId);
                                break;
                            case "materialtakeoff":
                                schedule = ViewSchedule.CreateMaterialTakeoff(doc, categoryId);
                                break;
                            case "noteblock":
                                var familyId = ResolveFamilyId(doc);
                                schedule = ViewSchedule.CreateNoteBlock(doc, familyId);
                                break;
                            case "sheetlist":
                                schedule = ViewSchedule.CreateSheetList(doc);
                                break;
                            case "viewlist":
                                schedule = ViewSchedule.CreateViewList(doc);
                                break;
                            case "revisionschedule":
                                schedule = ViewSchedule.CreateRevisionSchedule(doc);
                                break;
                            case "keynotelegend":
                                schedule = ViewSchedule.CreateKeynoteLegend(doc);
                                break;
                            default:
                                schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                                break;
                        }

                        if (!string.IsNullOrEmpty(ScheduleInfo.Name))
                        {
                            try
                            {
                                schedule.Name = ScheduleInfo.Name;
                            }
                            catch
                            {
                                // Name conflict — append number suffix
                                for (int i = 2; i <= 99; i++)
                                {
                                    try
                                    {
                                        schedule.Name = $"{ScheduleInfo.Name} ({i})";
                                        break;
                                    }
                                    catch { }
                                }
                            }
                        }

                        // Apply preset if specified
                        ApplyPreset(schedule);

                        // Add fields
                        var missingFields = new List<string>();
                        var addedFields = AddFields(schedule, missingFields);

                        // Add filters
                        var notes = new List<string>();
                        AddFilters(schedule, addedFields, notes);

                        // Add sort/group fields
                        AddSortFields(schedule, addedFields);

                        // Add group fields
                        AddGroupFields(schedule, addedFields);

                        // Set display properties
                        SetDisplayProperties(schedule);

                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());

                        var message = $"Successfully created {scheduleType} schedule '{schedule.Name}'";
                        if (missingFields.Count > 0)
                            message += $". WARNING: {missingFields.Count} field(s) could not be added (no matching schedulable field): {string.Join(", ", missingFields)}";

                        Result = new AIResult<object>
                        {
                            Success = true,
                            Message = message,
                            Response = new
                            {
#if REVIT2024_OR_GREATER
                                scheduleId = schedule.Id.Value,
#else
                                scheduleId = schedule.Id.IntegerValue,
#endif
                                name = schedule.Name,
                                addedFields = addedFields.Keys.ToList(),
                                missingFields,
                                notes
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
                    Message = $"Failed to create schedule: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ElementId ResolveCategoryId(Document doc)
        {
            if (!string.IsNullOrEmpty(ScheduleInfo.CategoryName))
            {
                // Accepts OST_ names, English names ("Doors") and localized names ("Двери")
                var catId = CategoryResolver.ResolveToId(doc, ScheduleInfo.CategoryName);
                if (catId == null)
                    throw new ArgumentException($"Category '{ScheduleInfo.CategoryName}' could not be resolved");
                return catId;
            }

            if (ScheduleInfo.CategoryId > 0)
            {
#if REVIT2024_OR_GREATER
                return new ElementId((long)ScheduleInfo.CategoryId);
#else
                return new ElementId((int)ScheduleInfo.CategoryId);
#endif
            }

            // Multi-category schedule
            return ElementId.InvalidElementId;
        }

        private ElementId ResolveFamilyId(Document doc)
        {
            if (ScheduleInfo.FamilyId > 0)
            {
                return RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(ScheduleInfo.FamilyId);
            }

            // Find the first annotation symbol family as a fallback
            var collector = new FilteredElementCollector(doc);
            var family = collector.OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.FamilyCategory != null &&
                    f.FamilyCategory.CategoryType == CategoryType.Annotation);

            if (family != null)
                return family.Id;

            return ElementId.InvalidElementId;
        }

        private void ApplyPreset(ViewSchedule schedule)
        {
            if (string.IsNullOrEmpty(ScheduleInfo.Preset))
                return;

            var presetName = ScheduleInfo.Preset.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            var presetFields = GetPresetFields(presetName);

            if (presetFields == null)
                return;

            // Only populate fields if not already specified
            if (ScheduleInfo.Fields == null || ScheduleInfo.Fields.Count == 0)
            {
                ScheduleInfo.Fields = presetFields;
            }

            // Add default sort by first field if no sorts specified
            if ((ScheduleInfo.SortFields == null || ScheduleInfo.SortFields.Count == 0) &&
                ScheduleInfo.Fields != null && ScheduleInfo.Fields.Count > 0)
            {
                ScheduleInfo.SortFields = new List<ScheduleSortInfo>
                {
                    new ScheduleSortInfo
                    {
                        FieldName = ScheduleInfo.Fields[0].ParameterName,
                        SortOrder = "Ascending"
                    }
                };
            }
        }

        private List<ScheduleFieldInfo> GetPresetFields(string presetName)
        {
            switch (presetName)
            {
                case "room_finish":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Number" },
                        new ScheduleFieldInfo { ParameterName = "Name" },
                        new ScheduleFieldInfo { ParameterName = "Floor Finish" },
                        new ScheduleFieldInfo { ParameterName = "Wall Finish" },
                        new ScheduleFieldInfo { ParameterName = "Ceiling Finish" },
                        new ScheduleFieldInfo { ParameterName = "Area" },
                    };
                case "door_by_room":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Mark" },
                        new ScheduleFieldInfo { ParameterName = "Type" },
                        new ScheduleFieldInfo { ParameterName = "Width" },
                        new ScheduleFieldInfo { ParameterName = "Height" },
                        new ScheduleFieldInfo { ParameterName = "From Room: Number" },
                        new ScheduleFieldInfo { ParameterName = "From Room: Name" },
                        new ScheduleFieldInfo { ParameterName = "To Room: Number" },
                        new ScheduleFieldInfo { ParameterName = "To Room: Name" },
                    };
                case "window_by_room":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Mark" },
                        new ScheduleFieldInfo { ParameterName = "Type" },
                        new ScheduleFieldInfo { ParameterName = "Width" },
                        new ScheduleFieldInfo { ParameterName = "Height" },
                        new ScheduleFieldInfo { ParameterName = "Sill Height" },
                        new ScheduleFieldInfo { ParameterName = "Count" },
                    };
                case "wall_summary":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Family and Type" },
                        new ScheduleFieldInfo { ParameterName = "Width" },
                        new ScheduleFieldInfo { ParameterName = "Length" },
                        new ScheduleFieldInfo { ParameterName = "Area" },
                        new ScheduleFieldInfo { ParameterName = "Volume" },
                    };
                case "material_quantities":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Material: Name" },
                        new ScheduleFieldInfo { ParameterName = "Material: Area" },
                        new ScheduleFieldInfo { ParameterName = "Material: Volume" },
                        new ScheduleFieldInfo { ParameterName = "Family and Type" },
                    };
                case "family_inventory":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Family" },
                        new ScheduleFieldInfo { ParameterName = "Type" },
                        new ScheduleFieldInfo { ParameterName = "Count" },
                        new ScheduleFieldInfo { ParameterName = "Family and Type" },
                    };
                case "sheet_index":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "Sheet Number" },
                        new ScheduleFieldInfo { ParameterName = "Sheet Name" },
                        new ScheduleFieldInfo { ParameterName = "Drawn By" },
                        new ScheduleFieldInfo { ParameterName = "Checked By" },
                        new ScheduleFieldInfo { ParameterName = "Current Revision" },
                    };
                case "view_index":
                    return new List<ScheduleFieldInfo>
                    {
                        new ScheduleFieldInfo { ParameterName = "View Name" },
                        new ScheduleFieldInfo { ParameterName = "View Type" },
                        new ScheduleFieldInfo { ParameterName = "Sheet Number" },
                        new ScheduleFieldInfo { ParameterName = "Sheet Name" },
                        new ScheduleFieldInfo { ParameterName = "Title on Sheet" },
                    };
                default:
                    return null;
            }
        }

        private Dictionary<string, ScheduleFieldId> AddFields(ViewSchedule schedule, List<string> missingFields)
        {
            var addedFields = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);
            var schedulableFields = schedule.Definition.GetSchedulableFields();

            if (ScheduleInfo.Fields == null || ScheduleInfo.Fields.Count == 0)
                return addedFields;

            foreach (var fieldInfo in ScheduleInfo.Fields)
            {
                // Resolve by explicit parameter id, then BuiltInParameter alias (language-independent), then display name
                SchedulableField matchingField = null;
                if (fieldInfo.ParameterId != 0)
                    matchingField = schedulableFields.FirstOrDefault(sf => sf.ParameterId.GetValue() == fieldInfo.ParameterId);
                if (matchingField == null)
                    matchingField = ScheduleFieldResolver.FindSchedulableField(schedule.Document, schedulableFields, fieldInfo.ParameterName);

                if (matchingField == null)
                {
                    missingFields?.Add(string.IsNullOrEmpty(fieldInfo.ParameterName) ? $"parameterId {fieldInfo.ParameterId}" : fieldInfo.ParameterName);
                    continue;
                }

                var addedField = schedule.Definition.AddField(matchingField);
                addedFields[fieldInfo.ParameterName] = addedField.FieldId;

                if (!string.IsNullOrEmpty(fieldInfo.Heading))
                    addedField.ColumnHeading = fieldInfo.Heading;

                if (fieldInfo.IsHidden)
                    addedField.IsHidden = true;

                if (!string.IsNullOrEmpty(fieldInfo.HorizontalAlignment))
                {
                    switch (fieldInfo.HorizontalAlignment.ToLower())
                    {
                        case "center":
                            addedField.HorizontalAlignment = ScheduleHorizontalAlignment.Center;
                            break;
                        case "right":
                            addedField.HorizontalAlignment = ScheduleHorizontalAlignment.Right;
                            break;
                        default:
                            addedField.HorizontalAlignment = ScheduleHorizontalAlignment.Left;
                            break;
                    }
                }

                if (fieldInfo.GridColumnWidth.HasValue && fieldInfo.GridColumnWidth.Value > 0)
                {
                    addedField.GridColumnWidth = fieldInfo.GridColumnWidth.Value;
                }
            }

            return addedFields;
        }

        private void AddFilters(ViewSchedule schedule, Dictionary<string, ScheduleFieldId> addedFields, List<string> notes)
        {
            if (ScheduleInfo.Filters == null || ScheduleInfo.Filters.Count == 0)
                return;

            var definition = schedule.Definition;
            foreach (var filterInfo in ScheduleInfo.Filters)
            {
                ScheduleField field;
                if (!string.IsNullOrEmpty(filterInfo.FieldName) &&
                    addedFields.TryGetValue(filterInfo.FieldName, out var resolvedFieldId))
                {
                    field = definition.GetField(resolvedFieldId);
                }
                else
                {
                    // Prefers fields already in the schedule; throws a descriptive error if nothing matches
                    field = ScheduleFieldResolver.ResolveFieldForFilter(schedule, filterInfo.FieldName, filterInfo.FieldIndex, notes);
                }

                // Validates the filter type against the field and converts the value to the field's storage type
                var filter = ScheduleFieldResolver.BuildFilter(schedule, field, filterInfo.FilterType, filterInfo.FilterValue);
                definition.AddFilter(filter);
            }
        }

        private void AddSortFields(ViewSchedule schedule, Dictionary<string, ScheduleFieldId> addedFields)
        {
            if (ScheduleInfo.SortFields == null || ScheduleInfo.SortFields.Count == 0)
                return;

            foreach (var sortInfo in ScheduleInfo.SortFields)
            {
                ScheduleFieldId fieldId = null;

                if (!string.IsNullOrEmpty(sortInfo.FieldName) &&
                    addedFields.TryGetValue(sortInfo.FieldName, out var resolvedFieldId))
                {
                    fieldId = resolvedFieldId;
                }
                else if (!string.IsNullOrEmpty(sortInfo.FieldName) &&
                         ScheduleFieldResolver.FindExistingField(schedule.Definition, sortInfo.FieldName) is ScheduleField existingSort)
                {
                    fieldId = existingSort.FieldId;
                }
                else if (sortInfo.FieldIndex >= 0 && sortInfo.FieldIndex < schedule.Definition.GetFieldCount())
                {
                    fieldId = schedule.Definition.GetField(sortInfo.FieldIndex).FieldId;
                }

                if (fieldId == null)
                    continue;

                var sortOrder = ScheduleSortOrder.Ascending;
                if (!string.IsNullOrEmpty(sortInfo.SortOrder) &&
                    sortInfo.SortOrder.Equals("Descending", StringComparison.OrdinalIgnoreCase))
                {
                    sortOrder = ScheduleSortOrder.Descending;
                }

                var sortGroupField = new ScheduleSortGroupField(fieldId, sortOrder);
                schedule.Definition.AddSortGroupField(sortGroupField);
            }
        }

        private void AddGroupFields(ViewSchedule schedule, Dictionary<string, ScheduleFieldId> addedFields)
        {
            if (ScheduleInfo.GroupFields == null || ScheduleInfo.GroupFields.Count == 0)
                return;

            foreach (var groupInfo in ScheduleInfo.GroupFields)
            {
                ScheduleFieldId fieldId = null;

                if (!string.IsNullOrEmpty(groupInfo.FieldName) &&
                    addedFields.TryGetValue(groupInfo.FieldName, out var resolvedFieldId))
                {
                    fieldId = resolvedFieldId;
                }
                else if (!string.IsNullOrEmpty(groupInfo.FieldName) &&
                         ScheduleFieldResolver.FindExistingField(schedule.Definition, groupInfo.FieldName) is ScheduleField existingGroup)
                {
                    fieldId = existingGroup.FieldId;
                }
                else if (groupInfo.FieldIndex >= 0 && groupInfo.FieldIndex < schedule.Definition.GetFieldCount())
                {
                    fieldId = schedule.Definition.GetField(groupInfo.FieldIndex).FieldId;
                }

                if (fieldId == null)
                    continue;

                var sortOrder = ScheduleSortOrder.Ascending;
                if (!string.IsNullOrEmpty(groupInfo.SortOrder) &&
                    groupInfo.SortOrder.Equals("Descending", StringComparison.OrdinalIgnoreCase))
                {
                    sortOrder = ScheduleSortOrder.Descending;
                }

                var sortGroupField = new ScheduleSortGroupField(fieldId, sortOrder);
                sortGroupField.ShowHeader = groupInfo.ShowHeader;
                sortGroupField.ShowFooter = groupInfo.ShowFooter;
                sortGroupField.ShowBlankLine = groupInfo.ShowBlankLine;
                schedule.Definition.AddSortGroupField(sortGroupField);
            }
        }

        private void SetDisplayProperties(ViewSchedule schedule)
        {
            var definition = schedule.Definition;

            if (ScheduleInfo.ShowTitle.HasValue)
                definition.ShowTitle = ScheduleInfo.ShowTitle.Value;

            if (ScheduleInfo.ShowHeaders.HasValue)
                definition.ShowHeaders = ScheduleInfo.ShowHeaders.Value;

            if (ScheduleInfo.ShowGridLines.HasValue)
                definition.ShowGridLines = ScheduleInfo.ShowGridLines.Value;

            if (ScheduleInfo.IsItemized.HasValue)
                definition.IsItemized = ScheduleInfo.IsItemized.Value;

            if (ScheduleInfo.ShowGrandTotal.HasValue)
                definition.ShowGrandTotal = ScheduleInfo.ShowGrandTotal.Value;

            if (ScheduleInfo.ShowGrandTotalCount.HasValue)
                definition.ShowGrandTotalCount = ScheduleInfo.ShowGrandTotalCount.Value;

            if (!string.IsNullOrEmpty(ScheduleInfo.GrandTotalTitle))
                definition.GrandTotalTitle = ScheduleInfo.GrandTotalTitle;

            if (ScheduleInfo.IncludeLinkedFiles.HasValue)
            {
                if (definition.CanIncludeLinkedFiles())
                {
                    definition.IncludeLinkedFiles = ScheduleInfo.IncludeLinkedFiles.Value;
                }
            }
        }

        public string GetName() => "Create Schedule";
    }
}
