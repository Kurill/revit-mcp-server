using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Helpers;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class SetCompoundStructureEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public long? TypeId { get; set; }
        public string TypeName { get; set; }
        public string Category { get; set; }
        public string DuplicateAsName { get; set; }
        public List<CompoundLayerInput> Layers { get; set; }
        public AIResult<object> Result { get; private set; }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            Document doc = null;
            ElementId duplicatedTypeId = null;
            try
            {
                doc = app.ActiveUIDocument.Document;

                // Resolve the HostObjAttributes type
                HostObjAttributes hostType = ResolveHostType(doc);
                if (hostType == null)
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = "Could not find the specified type. Provide a valid typeId, or typeName + category."
                    };
                    return;
                }

                // Duplicate first if requested
                if (!string.IsNullOrEmpty(DuplicateAsName))
                {
                    using (var dupTx = new Transaction(doc, "Duplicate Type for Compound Structure"))
                    {
                        dupTx.Start();
                        try
                        {
                            hostType = hostType.Duplicate(DuplicateAsName) as HostObjAttributes;
                            if (hostType == null)
                                throw new Exception("Duplicate returned null — the type may not support duplication.");
                            RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(dupTx.Commit());
                            duplicatedTypeId = hostType.Id;
                        }
                        catch
                        {
                            if (dupTx.GetStatus() == TransactionStatus.Started)
                                RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(dupTx);
                            throw;
                        }
                    }
                }

                var cs = hostType.GetCompoundStructure();
                if (cs == null)
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = $"Type '{hostType.Name}' does not have a compound structure (it may be a curtain wall or similar)."
                    };
                    return;
                }

                // User confirmation
                if (!ConfirmationHelper.Confirm(this, $"modify compound structure of type '{hostType.Name}'", 1))
                {
                    Result = new AIResult<object>
                    {
                        Success = false,
                        Message = "Operation cancelled by user",
                        Response = new { cancelled = true }
                    };
                    return;
                }

                // Build material lookup dictionary once
                var materialLookup = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                foreach (var mat in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
                {
                    materialLookup[mat.Name] = mat.Id;
                }

                // Build new layers
                var newLayers = new List<CompoundStructureLayer>();
                foreach (var layer in Layers)
                {
                    if (!Enum.TryParse<MaterialFunctionAssignment>(layer.Function, true, out var func))
                        throw new Exception($"Invalid layer function '{layer.Function}'. Valid values: Structure, Substrate, Insulation, Finish1, Finish2, Membrane, StructuralDeck.");

                    // Membrane layers must have width 0
                    double widthFeet = func == MaterialFunctionAssignment.Membrane
                        ? 0.0
                        : layer.WidthMm / 304.8;

                    // Resolve material
                    ElementId materialId = ElementId.InvalidElementId;
                    if (layer.MaterialId.HasValue)
                    {
#if REVIT2024_OR_GREATER
                        materialId = new ElementId(layer.MaterialId.Value);
#else
                        materialId = new ElementId((int)layer.MaterialId.Value);
#endif
                    }
                    else if (!string.IsNullOrEmpty(layer.MaterialName))
                    {
                        if (materialLookup.TryGetValue(layer.MaterialName, out var foundId))
                        {
                            materialId = foundId;
                        }
                        else
                        {
                            throw new Exception($"Material '{layer.MaterialName}' not found in the project.");
                        }
                    }

                    if (func != MaterialFunctionAssignment.Membrane && widthFeet <= 0)
                        throw new Exception($"Layer {newLayers.Count + 1} ({func}) must have widthMm > 0; only Membrane layers may be 0.");

                    var newLayer = new CompoundStructureLayer(widthFeet, func, materialId);
                    newLayer.LayerCapFlag = layer.Wraps ?? false;
                    newLayers.Add(newLayer);
                }

                // Reusing the old structure with SetLayers kept its shell-layer counts and
                // structural-material index, which no longer matched the new layer list and
                // made Revit reject it. Build a fresh structure and define the core explicitly.
                var newCs = CompoundStructure.CreateSimpleCompoundStructure(newLayers);
                try
                {
                    newCs.EndCap = cs.EndCap;
                    newCs.OpeningWrapping = cs.OpeningWrapping;
                }
                catch (Exception)
                {
                    // Wrapping settings are wall-only; other hosts keep the defaults.
                }

                // Core = from the first to the last Structure layer (or Substrate/StructuralDeck if
                // there is no Structure layer); everything outside it becomes shell layers.
                int coreFirst = newLayers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                int coreLast = newLayers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                if (coreFirst < 0)
                {
                    coreFirst = newLayers.FindIndex(l => l.Function == MaterialFunctionAssignment.Substrate
                                                         || l.Function == MaterialFunctionAssignment.StructuralDeck);
                    coreLast = newLayers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Substrate
                                                            || l.Function == MaterialFunctionAssignment.StructuralDeck);
                }
                if (coreFirst < 0)
                {
                    coreFirst = 0;
                    coreLast = newLayers.Count - 1;
                }
                newCs.SetNumberOfShellLayers(ShellLayerType.Exterior, coreFirst);
                newCs.SetNumberOfShellLayers(ShellLayerType.Interior, newLayers.Count - 1 - coreLast);

                int structuralIndex = newLayers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                newCs.StructuralMaterialIndex = structuralIndex >= 0 ? structuralIndex : coreFirst;

                if (!newCs.IsValid(doc, out IDictionary<int, CompoundStructureError> layerErrors,
                        out IDictionary<int, int> twoLayerErrors))
                {
                    var problems = new List<string>();
                    foreach (var kv in layerErrors)
                        problems.Add($"layer {kv.Key + 1} ({newLayers[kv.Key].Function}): {kv.Value}");
                    foreach (var kv in twoLayerErrors)
                        problems.Add($"layers {kv.Key + 1} and {kv.Value + 1} conflict");
                    throw new Exception("CompoundStructure is not valid: " +
                                        (problems.Count > 0 ? string.Join("; ", problems) : "unknown reason") +
                                        $". Core was set to layers {coreFirst + 1}-{coreLast + 1}.");
                }

                // Apply inside a transaction
                using (var transaction = new Transaction(doc, "Set Compound Structure"))
                {
                    transaction.Start();
                    try
                    {
                        hostType.SetCompoundStructure(newCs);
                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                            RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(transaction);
                        throw;
                    }
                }

                // Build result
                double totalWidthMm = 0;
                var appliedLayers = new List<object>();
                var finalCs = hostType.GetCompoundStructure();
                if (finalCs != null)
                {
                    foreach (var cl in finalCs.GetLayers())
                    {
                        double layerWidthMm = cl.Width * 304.8;
                        totalWidthMm += layerWidthMm;

                        string matName = "By Category";
                        if (cl.MaterialId != ElementId.InvalidElementId)
                        {
                            var matElem = doc.GetElement(cl.MaterialId) as Material;
                            if (matElem != null)
                                matName = matElem.Name;
                        }

                        appliedLayers.Add(new
                        {
                            function = cl.Function.ToString(),
                            widthMm = Math.Round(layerWidthMm, 2),
                            material = matName,
#if REVIT2024_OR_GREATER
                            materialId = cl.MaterialId.Value,
#else
                            materialId = (long)cl.MaterialId.IntegerValue,
#endif
                            wraps = cl.LayerCapFlag
                        });
                    }
                }

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Compound structure of type '{hostType.Name}' updated with {newLayers.Count} layer(s).",
                    Response = new
                    {
                        typeName = hostType.Name,
#if REVIT2024_OR_GREATER
                        typeId = hostType.Id.Value,
#else
                        typeId = (long)hostType.Id.IntegerValue,
#endif
                        layerCount = appliedLayers.Count,
                        totalWidthMm = Math.Round(totalWidthMm, 2),
                        layers = appliedLayers
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to set compound structure: {ex.Message}"
                };
            }
            finally
            {
                // Don't leave an orphan duplicated type behind when the change failed or was cancelled
                if (duplicatedTypeId != null && Result != null && !Result.Success)
                    DeleteDuplicatedType(doc, duplicatedTypeId);
                _resetEvent.Set();
            }
        }

        private void DeleteDuplicatedType(Document doc, ElementId typeId)
        {
            try
            {
                using (var tx = new Transaction(doc, "Delete Duplicated Type"))
                {
                    tx.Start();
                    doc.Delete(typeId);
                    RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(tx.Commit());
                }
                Result.Message += $" The duplicated type '{DuplicateAsName}' was deleted.";
            }
            catch (Exception ex)
            {
                Result.Message += $" The duplicated type '{DuplicateAsName}' could not be deleted: {ex.Message}";
            }
        }

        private HostObjAttributes ResolveHostType(Document doc)
        {
            // By TypeId
            if (TypeId.HasValue)
            {
#if REVIT2024_OR_GREATER
                var elementId = new ElementId(TypeId.Value);
#else
                var elementId = new ElementId((int)TypeId.Value);
#endif
                var elem = doc.GetElement(elementId);
                if (elem is HostObjAttributes hostById)
                    return hostById;
            }

            // By TypeName + Category
            if (!string.IsNullOrEmpty(TypeName) && !string.IsNullOrEmpty(Category))
            {
                Type typeClass = ResolveCategoryType(Category);
                if (typeClass == null)
                    throw new Exception($"Unsupported category '{Category}'. Use: Walls, Floors, Roofs, or Ceilings.");

                var match = new FilteredElementCollector(doc)
                    .OfClass(typeClass)
                    .Cast<HostObjAttributes>()
                    .FirstOrDefault(t => string.Equals(t.Name, TypeName, StringComparison.OrdinalIgnoreCase));

                return match;
            }

            // By TypeName only (search all host types)
            if (!string.IsNullOrEmpty(TypeName))
            {
                var types = new[] { typeof(WallType), typeof(FloorType), typeof(RoofType), typeof(CeilingType) };
                foreach (var typeClass in types)
                {
                    var match = new FilteredElementCollector(doc)
                        .OfClass(typeClass)
                        .Cast<HostObjAttributes>()
                        .FirstOrDefault(t => string.Equals(t.Name, TypeName, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                        return match;
                }
            }

            return null;
        }

        private Type ResolveCategoryType(string category)
        {
            switch (category?.ToLowerInvariant())
            {
                case "walls":
                case "wall":
                    return typeof(WallType);
                case "floors":
                case "floor":
                    return typeof(FloorType);
                case "roofs":
                case "roof":
                    return typeof(RoofType);
                case "ceilings":
                case "ceiling":
                    return typeof(CeilingType);
                default:
                    return null;
            }
        }

        public string GetName() => "Set Compound Structure";
    }
}
