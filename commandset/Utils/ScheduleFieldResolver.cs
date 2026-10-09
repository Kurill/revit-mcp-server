using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Language-independent helpers for schedule fields, filters and table reading.
    /// Field names are resolved via BuiltInParameter ids first (so English names such as
    /// "Mark" or "From Room: Number" work on localized Revit), then by display name.
    /// </summary>
    public static class ScheduleFieldResolver
    {
        private class FieldAlias
        {
            public string[] Bips;          // BuiltInParameter names (parsed at runtime; unknown names are skipped)
            public string[] FieldTypes;    // ScheduleFieldType names; null = default preference
        }

        private static FieldAlias A(params string[] bips) => new FieldAlias { Bips = bips };
        private static FieldAlias AT(string[] fieldTypes, params string[] bips) => new FieldAlias { Bips = bips, FieldTypes = fieldTypes };

        // English schedule field names → built-in parameter candidates (first match present in the schedule wins)
        private static readonly Dictionary<string, FieldAlias> Aliases =
            new Dictionary<string, FieldAlias>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mark", A("ALL_MODEL_MARK") },
                { "Type Mark", A("ALL_MODEL_TYPE_MARK") },
                { "Comments", A("ALL_MODEL_INSTANCE_COMMENTS") },
                { "Type Comments", A("ALL_MODEL_TYPE_COMMENTS") },
                { "Description", A("ALL_MODEL_DESCRIPTION") },
                { "Type", A("ELEM_TYPE_PARAM", "SYMBOL_NAME_PARAM") },
                { "Type Name", A("SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME") },
                { "Family", A("ELEM_FAMILY_PARAM") },
                { "Family and Type", A("ELEM_FAMILY_AND_TYPE_PARAM") },
                { "Width", A("DOOR_WIDTH", "WINDOW_WIDTH", "FAMILY_WIDTH_PARAM", "WALL_ATTR_WIDTH_PARAM", "FURNITURE_WIDTH", "GENERIC_WIDTH") },
                { "Height", A("DOOR_HEIGHT", "WINDOW_HEIGHT", "FAMILY_HEIGHT_PARAM", "FURNITURE_HEIGHT", "GENERIC_HEIGHT") },
                { "Sill Height", A("INSTANCE_SILL_HEIGHT_PARAM") },
                { "Head Height", A("INSTANCE_HEAD_HEIGHT_PARAM") },
                { "Level", A("FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "ROOM_LEVEL_ID", "WALL_BASE_CONSTRAINT", "LEVEL_PARAM") },
                { "Length", A("CURVE_ELEM_LENGTH") },
                { "Area", A("ROOM_AREA", "HOST_AREA_COMPUTED") },
                { "Volume", A("ROOM_VOLUME", "HOST_VOLUME_COMPUTED") },
                { "Perimeter", A("ROOM_PERIMETER", "HOST_PERIMETER_COMPUTED") },
                { "Number", A("ROOM_NUMBER") },
                { "Name", A("ROOM_NAME") },
                { "Department", A("ROOM_DEPARTMENT") },
                { "Floor Finish", A("ROOM_FINISH_FLOOR") },
                { "Wall Finish", A("ROOM_FINISH_WALL") },
                { "Ceiling Finish", A("ROOM_FINISH_CEILING") },
                { "Base Finish", A("ROOM_FINISH_BASE") },
                { "Count", AT(new[] { "Count" }) },
                { "From Room: Number", AT(new[] { "FromRoom" }, "ROOM_NUMBER") },
                { "From Room: Name", AT(new[] { "FromRoom" }, "ROOM_NAME") },
                { "To Room: Number", AT(new[] { "ToRoom" }, "ROOM_NUMBER") },
                { "To Room: Name", AT(new[] { "ToRoom" }, "ROOM_NAME") },
                { "Material: Name", AT(new[] { "Material", "MaterialQuantity" }, "MATERIAL_NAME") },
                { "Material: Area", AT(new[] { "MaterialQuantity", "Material" }, "MATERIAL_AREA") },
                { "Material: Volume", AT(new[] { "MaterialQuantity", "Material" }, "MATERIAL_VOLUME") },
                { "Sheet Number", A("SHEET_NUMBER", "VIEWPORT_SHEET_NUMBER") },
                { "Sheet Name", A("SHEET_NAME", "VIEWPORT_SHEET_NAME") },
                { "Drawn By", A("SHEET_DRAWN_BY") },
                { "Checked By", A("SHEET_CHECKED_BY") },
                { "Designed By", A("SHEET_DESIGNED_BY") },
                { "Approved By", A("SHEET_APPROVED_BY") },
                { "Current Revision", A("SHEET_CURRENT_REVISION") },
                { "View Name", A("VIEW_NAME") },
                { "View Type", A("VIEW_TYPE") },
                { "Title on Sheet", A("VIEW_DESCRIPTION") },
            };

        // Field types that only "count" as a plain parameter match when explicitly requested
        private static readonly HashSet<string> RelatedElementFieldTypes =
            new HashSet<string>(StringComparer.Ordinal) { "FromRoom", "ToRoom", "Room", "Space", "ProjectInfo", "RevitLinkInstance", "RevitLinkType", "Material", "MaterialQuantity", "StructuralMaterial" };

        private static List<FieldAlias> GetAliases(string name)
        {
            var result = new List<FieldAlias>();
            if (string.IsNullOrWhiteSpace(name)) return result;
            var trimmed = name.Trim();

            if (Aliases.TryGetValue(trimmed, out var alias))
                result.Add(alias);

            // Allow BuiltInParameter enum names directly (e.g. "ALL_MODEL_MARK")
            if (char.IsLetter(trimmed[0]) && Enum.TryParse<BuiltInParameter>(trimmed, true, out var bip) &&
                Enum.IsDefined(typeof(BuiltInParameter), bip))
            {
                result.Add(A(bip.ToString()));
            }
            return result;
        }

        private static ElementId BipId(string bipName)
        {
            if (Enum.TryParse<BuiltInParameter>(bipName, out var bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
                return new ElementId(bip);
            return null;
        }

        private static int FieldTypeRank(string fieldType, string[] allowed)
        {
            if (allowed != null)
            {
                int idx = Array.IndexOf(allowed, fieldType);
                return idx >= 0 ? idx : -1;
            }
            if (fieldType == "Instance") return 0;
            if (fieldType == "ElementType") return 1;
            return RelatedElementFieldTypes.Contains(fieldType) ? -1 : 2;
        }

        /// <summary>
        /// Pick the best candidate (by alias order, then field-type preference) from items exposing ParameterId/FieldType.
        /// </summary>
        private static T PickByAlias<T>(IEnumerable<T> items, string name, Func<T, ElementId> paramId, Func<T, string> fieldType) where T : class
        {
            var list = items.ToList();
            foreach (var alias in GetAliases(name))
            {
                if (alias.Bips == null || alias.Bips.Length == 0)
                {
                    // Field-type-only alias (e.g. Count)
                    var byType = list
                        .Select(i => new { i, rank = FieldTypeRank(fieldType(i), alias.FieldTypes) })
                        .Where(x => x.rank >= 0)
                        .OrderBy(x => x.rank)
                        .FirstOrDefault();
                    if (byType != null) return byType.i;
                    continue;
                }

                foreach (var bipName in alias.Bips)
                {
                    var id = BipId(bipName);
                    if (id == null) continue;
                    var match = list
                        .Where(i => paramId(i) != null && paramId(i).Equals(id))
                        .Select(i => new { i, rank = FieldTypeRank(fieldType(i), alias.FieldTypes) })
                        .Where(x => x.rank >= 0)
                        .OrderBy(x => x.rank)
                        .FirstOrDefault();
                    if (match != null) return match.i;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolve a schedulable field by name (English alias / BuiltInParameter name / localized display name).
        /// When several fields share the same display name, built-in Instance/Type fields are preferred.
        /// </summary>
        public static SchedulableField FindSchedulableField(Document doc, IList<SchedulableField> fields, string name)
        {
            if (fields == null || string.IsNullOrWhiteSpace(name)) return null;

            var byAlias = PickByAlias(fields, name, f => f.ParameterId, f => f.FieldType.ToString());
            if (byAlias != null) return byAlias;

            var trimmed = name.Trim();
            var byName = fields.Where(f =>
            {
                try { return string.Equals(f.GetName(doc)?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();

            if (byName.Count <= 1) return byName.FirstOrDefault();

            return byName
                .OrderBy(f => FieldTypeRank(f.FieldType.ToString(), null) < 0 ? 9 : FieldTypeRank(f.FieldType.ToString(), null))
                .ThenBy(f => f.ParameterId != null && f.ParameterId.GetValue() < 0 ? 0 : 1)
                .First();
        }

        /// <summary>
        /// Find a field already present in the schedule definition by name, column heading, or English alias.
        /// </summary>
        public static ScheduleField FindExistingField(ScheduleDefinition def, string name)
        {
            if (def == null || string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            var fields = new List<ScheduleField>();
            for (int i = 0; i < def.GetFieldCount(); i++)
                fields.Add(def.GetField(i));

            var byName = fields.FirstOrDefault(f => string.Equals(f.GetName()?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            var byHeading = fields.FirstOrDefault(f => string.Equals(f.ColumnHeading?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (byHeading != null) return byHeading;

            return PickByAlias(fields, trimmed, f => f.ParameterId, f => f.FieldType.ToString());
        }

        /// <summary>
        /// Names of all fields currently in the schedule (for error messages).
        /// </summary>
        public static string DescribeFields(ScheduleDefinition def)
        {
            var names = new List<string>();
            for (int i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(i);
                names.Add($"[{i}] {f.GetName()}");
            }
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }

        /// <summary>
        /// Resolve the field a filter/sort refers to. Prefers fields already in the schedule; if the name is not in
        /// the schedule, the matching schedulable field is added as a hidden field (Revit requires filter fields to be
        /// part of the definition). Throws ArgumentException with a descriptive message if nothing matches.
        /// </summary>
        public static ScheduleField ResolveFieldForFilter(ViewSchedule schedule, string fieldName, int fieldIndex, List<string> notes)
        {
            var def = schedule.Definition;

            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                var existing = FindExistingField(def, fieldName);
                if (existing != null) return existing;

                var sf = FindSchedulableField(schedule.Document, def.GetSchedulableFields(), fieldName);
                if (sf != null)
                {
                    var added = def.AddField(sf);
                    added.IsHidden = true;
                    notes?.Add($"Field '{fieldName}' was not in the schedule; added it as a hidden field to filter on");
                    return added;
                }

                throw new ArgumentException(
                    $"Field '{fieldName}' not found in schedule '{schedule.Name}' nor among its schedulable fields. Schedule fields: {DescribeFields(def)}");
            }

            if (fieldIndex >= 0 && fieldIndex < def.GetFieldCount())
                return def.GetField(fieldIndex);

            throw new ArgumentException(
                $"Filter needs a valid fieldName or fieldIndex (got index {fieldIndex}). Schedule fields: {DescribeFields(def)}");
        }

        private static readonly HashSet<string> SubstringFilterTypes = new HashSet<string>(StringComparer.Ordinal)
            { "Contains", "NotContains", "BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith" };

        private static readonly HashSet<string> PresenceFilterTypes = new HashSet<string>(StringComparer.Ordinal)
            { "HasValue", "HasNoValue", "HasParameter" };

        /// <summary>
        /// Build a ScheduleFilter whose value matches the field's storage type. Throws ArgumentException naming the
        /// field and the reason when the filter type is not valid for the field.
        /// </summary>
        public static ScheduleFilter BuildFilter(ViewSchedule schedule, ScheduleField field, string filterTypeName, string value)
        {
            var doc = schedule.Document;
            string fname = field.GetName();

            if (string.IsNullOrWhiteSpace(filterTypeName) ||
                !Enum.TryParse<ScheduleFilterType>(filterTypeName.Trim(), true, out var filterType) ||
                !Enum.IsDefined(typeof(ScheduleFilterType), filterType) ||
                char.IsDigit(filterTypeName.Trim()[0]))
            {
                throw new ArgumentException(
                    $"Unknown filterType '{filterTypeName}' for field '{fname}'. Valid: {string.Join(", ", Enum.GetNames(typeof(ScheduleFilterType)))}");
            }

            string ft = filterType.ToString();

            var definition = schedule.Definition;
            if (!definition.CanFilter())
                throw new ArgumentException($"Schedule '{schedule.Name}' cannot be filtered.");

            if (PresenceFilterTypes.Contains(ft))
            {
                if (ft != "HasParameter" && !definition.CanFilterByValuePresence(field.FieldId))
                    throw new ArgumentException($"Field '{fname}' does not support filter type '{ft}'.");
                return new ScheduleFilter(field.FieldId, filterType);
            }

            if (ft.StartsWith("IsAssociatedWithGlobalParameter") || ft.StartsWith("IsNotAssociatedWithGlobalParameter"))
            {
                if (!long.TryParse(value, out var gpId))
                    throw new ArgumentException($"Filter type '{ft}' on field '{fname}' requires a global parameter element id as value.");
                return new ScheduleFilter(field.FieldId, filterType, ElementIdExtensions.FromLong(gpId));
            }

            var storage = GetFieldStorageType(doc, schedule, field);
            value = value ?? string.Empty;

            if (SubstringFilterTypes.Contains(ft))
            {
                if (!definition.CanFilterBySubstring(field.FieldId) ||(storage.HasValue && storage.Value != StorageType.String))
                {
                    throw new ArgumentException(
                        $"Filter type '{ft}' is not valid for field '{fname}' (storage type: {(storage.HasValue ? storage.Value.ToString() : "unknown")}); " +
                        "substring filters (Contains/BeginsWith/EndsWith...) only work on text fields. " +
                        (storage == StorageType.ElementId
                            ? "This field references an element (e.g. a type); use Equal/NotEqual with the element name or id, or filter on a text field such as 'Type Name' / 'Family and Type'."
                            : "Use Equal/NotEqual/GreaterThan/LessThan... instead."));
                }
                return new ScheduleFilter(field.FieldId, filterType, value);
            }

            // Value comparison (Equal, NotEqual, GreaterThan, ...)
            if (!definition.CanFilterByValue(field.FieldId))
                throw new ArgumentException($"Field '{fname}' does not support value filters such as '{ft}'.");

            switch (storage)
            {
                case StorageType.Integer:
                    {
                        var v = value.Trim().ToLowerInvariant();
                        int iv;
                        if (v == "true" || v == "yes" || v == "да") iv = 1;
                        else if (v == "false" || v == "no" || v == "нет") iv = 0;
                        else if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                            throw new ArgumentException($"Field '{fname}' stores integers; value '{value}' is not an integer.");
                        return new ScheduleFilter(field.FieldId, filterType, iv);
                    }
                case StorageType.Double:
                    {
                        if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                            throw new ArgumentException($"Field '{fname}' stores numbers; value '{value}' is not a number.");
                        return new ScheduleFilter(field.FieldId, filterType, ToInternalUnits(doc, field, dv));
                    }
                case StorageType.ElementId:
                    {
                        if (ft != "Equal" && ft != "NotEqual")
                            throw new ArgumentException(
                                $"Filter type '{ft}' is not valid for field '{fname}' (storage type: ElementId). Use Equal/NotEqual with an element name or id.");
                        var id = ResolveElementIdValue(doc, schedule, field, value);
                        if (id == null)
                            throw new ArgumentException(
                                $"Field '{fname}' references elements (ElementId); could not find an element named or with id '{value}' among this schedule's values.");
                        return new ScheduleFilter(field.FieldId, filterType, id);
                    }
                default:
                    return new ScheduleFilter(field.FieldId, filterType, value);
            }
        }

        private static double ToInternalUnits(Document doc, ScheduleField field, double displayValue)
        {
#if REVIT2022_OR_GREATER
            try
            {
                var spec = field.GetSpecTypeId();
                if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                {
                    var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                    return UnitUtils.ConvertToInternalUnits(displayValue, unit);
                }
            }
            catch { }
#endif
            return displayValue;
        }

        private static IEnumerable<Element> SampleElements(Document doc, ViewSchedule schedule)
        {
            var catId = schedule.Definition.CategoryId;
            FilteredElementCollector collector;
            try
            {
                collector = catId != null && catId != ElementId.InvalidElementId
                    ? new FilteredElementCollector(doc).OfCategoryId(catId).WhereElementIsNotElementType()
                    : new FilteredElementCollector(doc, schedule.Id);
            }
            catch
            {
                return Enumerable.Empty<Element>();
            }
            return collector.Take(500);
        }

        private static Parameter GetFieldParameter(Document doc, Element element, ScheduleField field)
        {
            Element target;
            var fieldType = field.FieldType.ToString();
            if (fieldType == "Instance") target = element;
            else if (fieldType == "ElementType")
            {
                var typeId = element.GetTypeId();
                target = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
            }
            else return null;

            if (target == null) return null;

            var pid = field.ParameterId;
            if (pid == null || pid == ElementId.InvalidElementId) return null;
            long raw = pid.GetValue();
            if (raw < 0)
            {
                var bip = (BuiltInParameter)(int)raw;
                return Enum.IsDefined(typeof(BuiltInParameter), bip) ? target.get_Parameter(bip) : null;
            }
            if (doc.GetElement(pid) is ParameterElement pe)
                return target.get_Parameter(pe.GetDefinition());
            return null;
        }

        /// <summary>
        /// Determine the storage type of a schedule field by inspecting elements in the schedule's category.
        /// Returns null when it cannot be determined (e.g. calculated or related-element fields).
        /// </summary>
        public static StorageType? GetFieldStorageType(Document doc, ViewSchedule schedule, ScheduleField field)
        {
            try
            {
                foreach (var e in SampleElements(doc, schedule))
                {
                    var p = GetFieldParameter(doc, e, field);
                    if (p != null && p.StorageType != StorageType.None) return p.StorageType;
                }
            }
            catch { }
            return null;
        }

        private static ElementId ResolveElementIdValue(Document doc, ViewSchedule schedule, ScheduleField field, string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (long.TryParse(trimmed, out var raw))
                return ElementIdExtensions.FromLong(raw);

            try
            {
                foreach (var e in SampleElements(doc, schedule))
                {
                    var p = GetFieldParameter(doc, e, field);
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    var id = p.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) continue;
                    var name = doc.GetElement(id)?.Name;
                    if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.AsValueString(), trimmed, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Read the body of a schedule as displayed. The column-header row(s) that Revit places inside the body
        /// section when ShowHeaders is on are returned as <paramref name="headers"/> and not as data rows.
        /// </summary>
        public static List<List<string>> ReadBody(ViewSchedule schedule, int maxRows, out List<string> headers, out int totalDataRows)
        {
            var def = schedule.Definition;
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            int firstRow = body.FirstRowNumber;
            int lastRow = firstRow + body.NumberOfRows; // exclusive
            int firstCol = body.FirstColumnNumber;
            int colCount = body.NumberOfColumns;

            List<string> ReadRow(int r)
            {
                var cells = new List<string>(colCount);
                for (int c = firstCol; c < firstCol + colCount; c++)
                {
                    string text;
                    try { text = schedule.GetCellText(SectionType.Body, r, c); }
                    catch
                    {
                        try { text = body.GetCellText(r, c); } catch { text = ""; }
                    }
                    cells.Add(text ?? "");
                }
                return cells;
            }

            // Visible field headings/names, in column order
            var headings = new List<string>();
            var names = new List<string>();
            for (int i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(i);
                if (f.IsHidden) continue;
                headings.Add(f.ColumnHeading ?? "");
                names.Add(f.GetName() ?? "");
            }

            headers = headings.Count == colCount ? new List<string>(headings) : null;
            int dataStart = firstRow;

            if (def.ShowHeaders && body.NumberOfRows > 0)
            {
                int matchedRow = -1;
                int scanEnd = Math.Min(lastRow, firstRow + 4); // header grouping can add rows above the column headers
                for (int r = firstRow; r < scanEnd; r++)
                {
                    var cells = ReadRow(r);
                    if (RowMatches(cells, headings) || RowMatches(cells, names))
                    {
                        matchedRow = r;
                        headers = cells;
                        break;
                    }
                }

                if (matchedRow >= 0)
                {
                    dataStart = matchedRow + 1;
                }
                else
                {
                    // Revit always renders the column-header row as the first body row when headers are shown
                    headers = ReadRow(firstRow);
                    dataStart = firstRow + 1;
                }
            }

            if (headers == null)
                headers = headings.Count > 0 ? headings : new List<string>();

            totalDataRows = Math.Max(0, lastRow - dataStart);
            int rowsToRead = Math.Min(totalDataRows, Math.Max(0, maxRows));
            var rows = new List<List<string>>(rowsToRead);
            for (int r = dataStart; r < dataStart + rowsToRead; r++)
                rows.Add(ReadRow(r));
            return rows;
        }

        private static bool RowMatches(List<string> cells, List<string> expected)
        {
            int n = Math.Min(cells.Count, expected.Count);
            if (n == 0) return false;
            int matches = 0, nonEmpty = 0;
            for (int i = 0; i < n; i++)
            {
                var e = (expected[i] ?? "").Trim();
                if (e.Length == 0) continue;
                nonEmpty++;
                if (string.Equals((cells[i] ?? "").Trim(), e, StringComparison.OrdinalIgnoreCase)) matches++;
            }
            return nonEmpty > 0 && matches * 2 >= nonEmpty;
        }
    }
}
