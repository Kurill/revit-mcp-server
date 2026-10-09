using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class ClashDetectionEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public string CategoryA { get; set; } = "";
        public string CategoryB { get; set; } = "";
        public List<long> ElementIdsA { get; set; } = new List<long>();
        public List<long> ElementIdsB { get; set; } = new List<long>();
        public double Tolerance { get; set; } = 0;
        public int MaxResults { get; set; } = 100;
        public AIResult<object> Result { get; private set; }

        private const int TimeBudgetMs = 20000;

        public void SetParameters(string categoryA, string categoryB, List<long> elementIdsA, List<long> elementIdsB, double tolerance, int maxResults)
        {
            CategoryA = categoryA ?? "";
            CategoryB = categoryB ?? "";
            ElementIdsA = elementIdsA ?? new List<long>();
            ElementIdsB = elementIdsB ?? new List<long>();
            Tolerance = tolerance;
            MaxResults = maxResults > 0 ? maxResults : 100;
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

                // Get elements for set A
                var setA = GetElements(doc, ElementIdsA, CategoryA);
                // Get elements for set B
                var setB = GetElements(doc, ElementIdsB, CategoryB);

                if (setA.Count == 0 || setB.Count == 0)
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = $"Not enough elements: Set A has {setA.Count}, Set B has {setB.Count}"
                    };
                    return;
                }

                var clashes = new List<object>();
                var setBIds = setB.Select(e => e.Id).ToList();
                var budget = System.Diagnostics.Stopwatch.StartNew();
                bool timedOut = false;

                foreach (var elemA in setA)
                {
                    if (clashes.Count >= MaxResults) break;
                    if (budget.ElapsedMilliseconds > TimeBudgetMs) { timedOut = true; break; }

                    var bb = elemA.get_BoundingBox(null);
                    if (bb == null) continue;

                    // Bounding-box quick filter first; the solid check runs only on survivors.
                    var hits = new FilteredElementCollector(doc, setBIds)
                        .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max)))
                        .WherePasses(new ElementIntersectsElementFilter(elemA));

                    foreach (var elemB in hits)
                    {
                        if (clashes.Count >= MaxResults) break;
                        if (elemA.Id == elemB.Id) continue;
                        clashes.Add(new
                        {
#if REVIT2024_OR_GREATER
                            elementIdA = elemA.Id.Value,
                            elementIdB = elemB.Id.Value,
#else
                            elementIdA = elemA.Id.IntegerValue,
                            elementIdB = elemB.Id.IntegerValue,
#endif
                            elementNameA = elemA.Name,
                            elementNameB = elemB.Name,
                            categoryA = elemA.Category?.Name ?? "",
                            categoryB = elemB.Category?.Name ?? ""
                        });
                    }
                }

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Found {clashes.Count} clashes between {setA.Count} and {setB.Count} elements" +
                              (timedOut ? $" (stopped after {TimeBudgetMs / 1000} s; narrow the sets for a complete check)" : ""),
                    Response = new
                    {
                        setACount = setA.Count,
                        setBCount = setB.Count,
                        clashCount = clashes.Count,
                        maxResults = MaxResults,
                        stoppedEarly = timedOut,
                        clashes
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Clash detection failed: {ex.Message}" };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private List<Element> GetElements(Document doc, List<long> elementIds, string categoryName)
        {
            if (elementIds.Count > 0)
            {
                return elementIds
                    .Select(id => doc.GetElement(ToElementId(id)))
                    .Where(e => e != null)
                    .ToList();
            }

            if (!string.IsNullOrEmpty(categoryName))
            {
                var bic = ResolveCategoryByName(doc, categoryName);
                if (bic.HasValue)
                {
                    return new FilteredElementCollector(doc)
                        .OfCategory(bic.Value)
                        .WhereElementIsNotElementType()
                        .ToList();
                }
            }

            return new List<Element>();
        }

        private BuiltInCategory? ResolveCategoryByName(Document doc, string name)
        {
            var lowerName = name.ToLower();
            // Common category mappings
            var categoryMap = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"] = BuiltInCategory.OST_Walls,
                ["Floors"] = BuiltInCategory.OST_Floors,
                ["Roofs"] = BuiltInCategory.OST_Roofs,
                ["Doors"] = BuiltInCategory.OST_Doors,
                ["Windows"] = BuiltInCategory.OST_Windows,
                ["Columns"] = BuiltInCategory.OST_Columns,
                ["StructuralColumns"] = BuiltInCategory.OST_StructuralColumns,
                ["StructuralFraming"] = BuiltInCategory.OST_StructuralFraming,
                ["Beams"] = BuiltInCategory.OST_StructuralFraming,
                ["StructuralFoundation"] = BuiltInCategory.OST_StructuralFoundation,
                ["Pipes"] = BuiltInCategory.OST_PipeCurves,
                ["Ducts"] = BuiltInCategory.OST_DuctCurves,
                ["CableTray"] = BuiltInCategory.OST_CableTray,
                ["Conduit"] = BuiltInCategory.OST_Conduit,
                ["MechanicalEquipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["ElectricalEquipment"] = BuiltInCategory.OST_ElectricalEquipment,
                ["PlumbingFixtures"] = BuiltInCategory.OST_PlumbingFixtures,
                ["Furniture"] = BuiltInCategory.OST_Furniture,
                ["Rooms"] = BuiltInCategory.OST_Rooms,
                ["Ceilings"] = BuiltInCategory.OST_Ceilings,
                ["Stairs"] = BuiltInCategory.OST_Stairs,
                ["Railings"] = BuiltInCategory.OST_StairsRailing,
                ["GenericModels"] = BuiltInCategory.OST_GenericModel
            };

            if (categoryMap.TryGetValue(name, out var bic))
                return bic;

            return null;
        }

        private ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        public string GetName() => "Clash Detection";
    }
}
