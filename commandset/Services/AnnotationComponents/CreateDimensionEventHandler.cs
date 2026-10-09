// 
//                       RevitAPI-Solutions
// Copyright (c) Duong Tran Quang (DTDucas) (baymax.contact@gmail.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//

using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Handles creation of dimension elements in Revit
/// </summary>
public class CreateDimensionEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
{
    #region Fields

    private UIApplication _uiApp;
    private UIDocument UiDoc => _uiApp.ActiveUIDocument;
    private Document Doc => UiDoc.Document;
    private readonly ManualResetEvent _resetEvent = new(false);
    public ManualResetEvent CompletionSignal => _resetEvent;
    private const double MILLIMETERS_TO_FEET = 1.0 / 304.8;

    #endregion

    #region Properties

    /// <summary>
    ///     List of dimensions to create
    /// </summary>
    public List<DimensionCreationInfo> DimensionsToCreate { get; private set; }

    /// <summary>
    ///     Result of the execution
    /// </summary>
    public AIResult<List<int>> Result { get; private set; }

    #endregion

    #region Public Methods

    /// <summary>
    ///     Sets parameters for dimension creation
    /// </summary>
    /// <param name="dimensions">List of dimension information</param>
    public void SetParameters(List<DimensionCreationInfo> dimensions)
    {
        DimensionsToCreate = dimensions;
        _resetEvent.Reset();
    }

    /// <summary>
    ///     Executes the dimension creation process
    /// </summary>
    /// <param name="app">UIApplication instance</param>
    public void Execute(UIApplication app)
    {
        _uiApp = app;
        try
        {
            var createdDimensionIds = new List<int>();
            var errors = new List<string>();

            // Process each dimension in the list
            for (var index = 0; index < DimensionsToCreate.Count; index++)
            {
                var dimInfo = DimensionsToCreate[index];

                // Get active view or specified view
                View view = null;
                if (dimInfo.ViewId > 0)
                {
                    var element = Doc.GetElement(new ElementId(dimInfo.ViewId));
                    view = element as View;
                }

                if (view == null)
                {
                    view = Doc.ActiveView;
                }

                using (var transaction = new Transaction(Doc, "Create Dimension"))
                {
                    transaction.Start();

                    try
                    {
                        var dimension = CreateSingleDimension(dimInfo, view, out var error);
                        if (dimension == null)
                        {
                            // Rolling back also removes a degenerate dimension and any temporary helpers
                            errors.Add($"Dimension {index + 1}: {error}");
                            RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(transaction);
                            continue;
                        }

                        // Apply dimension style if specified
                        if (dimInfo.DimensionStyleId > 0)
                        {
                            var dimensionType = Doc.GetElement(new ElementId(dimInfo.DimensionStyleId)) as DimensionType;
                            if (dimensionType != null)
                            {
                                dimension.DimensionType = dimensionType;
                            }
                        }

                        // Apply additional parameters
                        ApplyDimensionParameters(dimension, dimInfo);

                        var dimensionId = dimension.Id.GetIntValue();
                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());
                        createdDimensionIds.Add(dimensionId);
                    }
                    catch (Exception ex)
                    {
                        RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(transaction);
                        errors.Add($"Dimension {index + 1}: {ex.Message}");
                    }
                }
            }

            var message = $"Created {createdDimensionIds.Count} of {DimensionsToCreate.Count} dimension(s). ElementIds saved in Response.";
            if (errors.Count > 0)
                message += " Errors: " + string.Join(" | ", errors);

            Result = new AIResult<List<int>>
            {
                Success = errors.Count == 0,
                Message = message,
                Response = createdDimensionIds
            };
        }
        catch (Exception ex)
        {
            // Set error result
            Result = new AIResult<List<int>>
            {
                Success = false,
                Message = $"Error creating dimensions: {ex.Message}",
                Response = new List<int>()
            };
        }
        finally
        {
            // Mark as completed
            _resetEvent.Set();
        }
    }

    /// <summary>
    ///     Waits for completion of the operation
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
    /// <returns>True if operation completed within timeout</returns>
    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    /// <summary>
    ///     Gets the name of the handler
    /// </summary>
    /// <returns>Handler name</returns>
    public string GetName()
    {
        return "Create Dimension";
    }

    #endregion

    #region Private Methods

    /// <summary>
    ///     A planar reference candidate with its plane in model coordinates
    /// </summary>
    private sealed class RefCandidate
    {
        public Reference Reference;
        public XYZ Origin;
        public XYZ Normal;
    }

    // A face of a family instance must be this parallel to the dimension direction (|cos|)
    private const double PARALLEL_ALIGNMENT = 0.99;

    // The temporary reference plane used to locate family reference planes sits this far
    // behind the anchor, so it never coincides with one of them
    private const double PROBE_OFFSET_FEET = 100.0;

    // Left/Right/Front/Back first: they bound the family's footprint
    private static readonly FamilyInstanceReferenceType[] PlaneReferenceOrder =
    {
        FamilyInstanceReferenceType.Left,
        FamilyInstanceReferenceType.Right,
        FamilyInstanceReferenceType.Front,
        FamilyInstanceReferenceType.Back,
        FamilyInstanceReferenceType.CenterLeftRight,
        FamilyInstanceReferenceType.CenterFrontBack,
        FamilyInstanceReferenceType.StrongReference,
        FamilyInstanceReferenceType.WeakReference,
        FamilyInstanceReferenceType.Top,
        FamilyInstanceReferenceType.Bottom,
        FamilyInstanceReferenceType.CenterElevation
    };

    /// <summary>
    ///     Creates one dimension inside the caller's open transaction.
    ///     Returns null and sets <paramref name="error" /> when it cannot be created.
    /// </summary>
    private Dimension CreateSingleDimension(DimensionCreationInfo dimInfo, View view, out string error)
    {
        error = null;

        // Convert points to Revit coordinates
        var startPoint = ConvertToInternalCoordinates(dimInfo.StartPoint.X, dimInfo.StartPoint.Y, dimInfo.StartPoint.Z);
        var endPoint = ConvertToInternalCoordinates(dimInfo.EndPoint.X, dimInfo.EndPoint.Y, dimInfo.EndPoint.Z);
        var linePoint = dimInfo.LinePoint != null
            ? ConvertToInternalCoordinates(dimInfo.LinePoint.X, dimInfo.LinePoint.Y, dimInfo.LinePoint.Z)
            : null;

        if (startPoint.DistanceTo(endPoint) < 1e-6)
        {
            error = "startPoint and endPoint must be different points.";
            return null;
        }

        var dimensionDirection = (endPoint - startPoint).Normalize();
        var line = BuildDimensionLine(view, startPoint, endPoint, linePoint);

        var references = new ReferenceArray();
        var seen = new HashSet<string>();

        void AddReference(Reference reference)
        {
            if (reference != null && seen.Add(StableKey(reference)))
                references.Append(reference);
        }

        if (dimInfo.ElementIds != null && dimInfo.ElementIds.Count > 0)
        {
            // Create dimension between elements
            var elementIds = dimInfo.ElementIds.Distinct().ToList();
            var anchors = new[] { startPoint, endPoint };
            foreach (var elementId in elementIds)
            {
                var element = Doc.GetElement(new ElementId(elementId));
                if (element == null)
                    continue;

                // A single element is measured across itself: one reference near each point
                var anchorSets = elementIds.Count == 1
                    ? new[] { new[] { startPoint }, new[] { endPoint } }
                    : new[] { anchors };
                foreach (var anchorSet in anchorSets)
                {
                    foreach (var reference in GetReferences(element, view, dimensionDirection, anchorSet, dimInfo.WallFace))
                        AddReference(reference);
                }
            }
        }
        else
        {
            // Pick references from geometry in the view at those points
            AddReference(FindReferenceAtPoint(view, startPoint, dimensionDirection, dimInfo.WallFace));
            AddReference(FindReferenceAtPoint(view, endPoint, dimensionDirection, dimInfo.WallFace));
        }

        if (references.Size < 2)
        {
            error = $"found {references.Size} usable reference(s), at least 2 are needed. " +
                    "Check elementIds and that startPoint/endPoint lie on the faces to measure.";
            return null;
        }

        var dimension = Doc.Create.NewDimension(view, line, references);
        if (dimension == null)
        {
            error = "Revit did not create the dimension.";
            return null;
        }

        Doc.Regenerate();
        if (IsDegenerate(dimension))
        {
            Doc.Delete(dimension.Id);
            error = "the dimension has no value (its references could not be measured). " +
                    "Try other elements or place startPoint/endPoint on the faces to measure.";
            return null;
        }

        return dimension;
    }

    /// <summary>
    ///     The dimension line runs parallel to start→end; with a linePoint it is shifted
    ///     sideways so that it passes through that point.
    /// </summary>
    private static Line BuildDimensionLine(View view, XYZ startPoint, XYZ endPoint, XYZ linePoint)
    {
        if (linePoint == null)
            return Line.CreateBound(startPoint, endPoint);

        var direction = (endPoint - startPoint).Normalize();
        var offset = linePoint - startPoint;
        offset -= direction.Multiply(offset.DotProduct(direction));

        // Stay in the plane of start/end as seen by the view (e.g. ignore Z in a plan)
        var viewDirection = view?.ViewDirection;
        if (viewDirection != null && !(view is View3D))
            offset -= viewDirection.Multiply(offset.DotProduct(viewDirection));

        return Line.CreateBound(startPoint + offset, endPoint + offset);
    }

    private static bool IsDegenerate(Dimension dimension)
    {
        if (dimension.NumberOfSegments > 1)
        {
            foreach (DimensionSegment segment in dimension.Segments)
            {
                if (segment.Value == null)
                    return true;
            }

            return false;
        }

        return dimension.Value == null;
    }

    private string StableKey(Reference reference)
    {
        try
        {
            return reference.ConvertToStableRepresentation(Doc);
        }
        catch (Exception)
        {
            return Guid.NewGuid().ToString();
        }
    }

    /// <summary>
    ///     Gets references for an element for dimensioning
    /// </summary>
    /// <param name="element">Element to get references for</param>
    /// <param name="view">View context</param>
    /// <param name="dimensionDirection">Direction of the dimension line (used to pick correct wall face)</param>
    /// <param name="anchors">Points the caller placed on the intended faces; the closest parallel face wins</param>
    /// <param name="wallFace">"nearest" (default), "interior" or "exterior"</param>
    /// <returns>List of references</returns>
    private List<Reference> GetReferences(Element element, View view, XYZ dimensionDirection = null,
        IList<XYZ> anchors = null, string wallFace = "nearest")
    {
        var references = new List<Reference>();

        // Handle different element types
        if (element is Wall wall)
        {
            // Interior/exterior side faces come straight from the wall's shell layers
            var side = (wallFace ?? "nearest").ToLowerInvariant();
            if (side == "interior" || side == "exterior")
            {
                try
                {
                    var sideRefs = HostObjectUtils.GetSideFaces(wall,
                        side == "interior" ? ShellLayerType.Interior : ShellLayerType.Exterior);
                    // A side face only works when it is perpendicular to the dimension line;
                    // a dimension along the wall, or a curved wall, falls through to nearest.
                    var sideFace = sideRefs.Count > 0 ? wall.GetGeometryObjectFromReference(sideRefs[0]) as PlanarFace : null;
                    if (sideFace != null &&
                        (dimensionDirection == null || Math.Abs(sideFace.FaceNormal.DotProduct(dimensionDirection)) > 0.99))
                    {
                        references.Add(sideRefs[0]);
                        return references;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Curtain walls and similar have no shell layers - fall through to nearest
                }
            }

            // Get wall faces or edges for dimensioning
            var options = new Options();
            options.View = view;
            options.ComputeReferences = true;

            var geometry = wall.get_Geometry(options);

            if (geometry != null)
            {
                var faces = new List<RefCandidate>();
                foreach (var obj in geometry)
                {
                    if (obj is Solid solid && solid.Faces.Size > 0)
                        CollectVerticalPlanarFaces(solid, Transform.Identity, faces);
                }

                var bestRef = PickFaceReference(faces, dimensionDirection, anchors);
                if (bestRef != null)
                {
                    references.Add(bestRef);
                }
            }

            // If still no references, use the element itself
            if (references.Count == 0)
            {
                references.Add(new Reference(wall));
            }
        }
        else if (element is FamilyInstance familyInstance)
        {
            // Try to get geometric references from the family instance
            var options = new Options();
            options.View = view;
            options.ComputeReferences = true;

            var geometry = familyInstance.get_Geometry(options);
            if (geometry != null && dimensionDirection != null)
            {
                // Faces from GetInstanceGeometry carry references Revit measures in the family's
                // own coordinates (dimensions to the internal origin, or no value). Symbol
                // geometry references work; their planes are mapped to the model here.
                var faces = new List<RefCandidate>();
                CollectInstanceFaces(geometry, Transform.Identity, faces, 0);

                var bestRef = PickFaceReference(faces, dimensionDirection, anchors, PARALLEL_ALIGNMENT);
                if (bestRef != null)
                {
                    references.Add(bestRef);
                    return references;
                }

                // Meshes only (typical for downloaded furniture): use the family's reference planes
                var planeRef = PickFamilyPlaneReference(familyInstance, view, dimensionDirection, anchors);
                if (planeRef != null)
                {
                    references.Add(planeRef);
                    return references;
                }
            }

            // Fallback to generic reference
            references.Add(new Reference(familyInstance));
        }
        else
        {
            // For other element types, create a generic reference
            references.Add(new Reference(element));
        }

        return references;
    }

    private static void CollectVerticalPlanarFaces(Solid solid, Transform transform, List<RefCandidate> faces)
    {
        foreach (Face face in solid.Faces)
        {
            if (!(face is PlanarFace planarFace) || face.Reference == null)
                continue;

            var normal = transform.OfVector(planarFace.FaceNormal).Normalize();
            // Skip horizontal faces (top/bottom) - useless in plan view
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            faces.Add(new RefCandidate
            {
                Reference = face.Reference,
                Origin = transform.OfPoint(planarFace.Origin),
                Normal = normal
            });
        }
    }

    /// <summary>
    ///     Collects faces of a family instance from symbol geometry, composing the
    ///     instance transforms (including nested instances) to model coordinates.
    /// </summary>
    private static void CollectInstanceFaces(GeometryElement geometry, Transform transform, List<RefCandidate> faces, int depth)
    {
        foreach (var obj in geometry)
        {
            if (obj is Solid solid && solid.Faces.Size > 0)
            {
                CollectVerticalPlanarFaces(solid, transform, faces);
            }
            else if (obj is GeometryInstance instance && depth < 5)
            {
                var symbolGeometry = instance.GetSymbolGeometry();
                if (symbolGeometry != null)
                    CollectInstanceFaces(symbolGeometry, transform.Multiply(instance.Transform), faces, depth + 1);
            }
        }
    }

    /// <summary>
    ///     Picks the face whose normal is most parallel to the dimension direction.
    ///     An element usually has two such faces (e.g. both sides of a wall), so ties are
    ///     broken by distance to the anchor points - the caller's start/end points mark
    ///     which face they mean.
    /// </summary>
    private static Reference PickFaceReference(List<RefCandidate> faces, XYZ dimensionDirection, IList<XYZ> anchors,
        double minAlignment = 0)
    {
        if (faces.Count == 0)
            return null;

        // Without direction info, take first vertical face
        if (dimensionDirection == null)
            return faces[0].Reference;

        const double parallelTolerance = 1e-3;
        var bestAlignment = faces.Max(f => Math.Abs(f.Normal.DotProduct(dimensionDirection)));
        if (bestAlignment < minAlignment)
            return null;

        var candidates = faces
            .Where(f => Math.Abs(f.Normal.DotProduct(dimensionDirection)) >= bestAlignment - parallelTolerance)
            .ToList();

        if (anchors == null || anchors.Count == 0 || candidates.Count == 1)
            return candidates[0].Reference;

        RefCandidate best = null;
        var bestDistance = double.MaxValue;
        foreach (var face in candidates)
        {
            foreach (var anchor in anchors)
            {
                // Distance from the anchor to the face's plane
                var distance = Math.Abs((anchor - face.Origin).DotProduct(face.Normal));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = face;
                }
            }
        }

        return best.Reference;
    }

    /// <summary>
    ///     Picks the family reference plane (Left/Right/Front/Back first) perpendicular to the
    ///     dimension direction and nearest to the anchors.
    /// </summary>
    private Reference PickFamilyPlaneReference(FamilyInstance familyInstance, View view, XYZ dimensionDirection,
        IList<XYZ> anchors)
    {
        var candidates = new List<Reference>();
        var seen = new HashSet<string>();
        foreach (var referenceType in PlaneReferenceOrder)
        {
            IList<Reference> typeRefs;
            try
            {
                typeRefs = familyInstance.GetReferences(referenceType);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var reference in typeRefs)
            {
                if (seen.Add(StableKey(reference)))
                    candidates.Add(reference);
            }
        }

        if (candidates.Count == 0)
            return null;
        if (anchors == null || anchors.Count == 0)
            return candidates[0];

        var positions = MeasurePlanePositions(view, candidates, anchors[0], dimensionDirection);
        // Positions could not be measured (e.g. no reference planes in this view): take the first one
        if (positions == null)
            return candidates[0];

        Reference best = null;
        var bestDistance = double.MaxValue;
        foreach (var (reference, position) in positions)
        {
            foreach (var anchor in anchors)
            {
                var distance = Math.Abs(position - anchor.DotProduct(dimensionDirection));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = reference;
                }
            }
        }

        // null when no candidate is perpendicular to the dimension direction
        return best;
    }

    /// <summary>
    ///     Family reference planes expose no geometry, so each candidate is located by
    ///     dimensioning it against a temporary reference plane at a known position. Returns
    ///     each measurable candidate's coordinate along <paramref name="direction" />, or null
    ///     when the probe cannot be created in this view. Temporary elements are deleted.
    /// </summary>
    private List<(Reference reference, double position)> MeasurePlanePositions(View view, List<Reference> candidates,
        XYZ anchor, XYZ direction)
    {
        var viewDirection = view.ViewDirection;
        var across = viewDirection.CrossProduct(direction);
        if (across.IsZeroLength())
            return null;
        across = across.Normalize();

        var probeOrigin = anchor - direction.Multiply(PROBE_OFFSET_FEET);
        ReferencePlane probe;
        try
        {
            probe = Doc.Create.NewReferencePlane(probeOrigin, probeOrigin + across, viewDirection, view);
        }
        catch (Exception)
        {
            return null;
        }

        if (probe == null)
            return null;

        var results = new List<(Reference reference, double position)>();
        try
        {
            var probeLine = Line.CreateBound(probeOrigin, probeOrigin + direction);
            var probePosition = probeOrigin.DotProduct(direction);
            foreach (var candidate in candidates)
            {
                Dimension temp = null;
                try
                {
                    var refs = new ReferenceArray();
                    refs.Append(probe.GetReference());
                    refs.Append(candidate);
                    temp = Doc.Create.NewDimension(view, probeLine, refs);
                    if (temp == null)
                        continue;

                    Doc.Regenerate();
                    if (temp.Value == null)
                        continue;

                    // Origin is the midpoint of the single segment: it tells on which side of the
                    // probe the candidate lies; Value gives the distance.
                    var side = (temp.Origin - probeOrigin).DotProduct(direction) >= 0 ? 1.0 : -1.0;
                    results.Add((candidate, probePosition + side * temp.Value.Value));
                }
                catch (Exception)
                {
                    // Not parallel to the probe, so not usable for this dimension direction
                }
                finally
                {
                    if (temp != null && temp.IsValidObject)
                        Doc.Delete(temp.Id);
                }
            }
        }
        finally
        {
            if (probe.IsValidObject)
                Doc.Delete(probe.Id);
        }

        return results;
    }

    /// <summary>
    ///     Find a reference at a point in the view
    /// </summary>
    /// <param name="view">View to search in</param>
    /// <param name="point">Point to search at</param>
    /// <param name="dimensionDirection">Direction of the dimension line</param>
    /// <returns>Reference or null</returns>
    private Reference FindReferenceAtPoint(View view, XYZ point, XYZ dimensionDirection = null, string wallFace = "nearest")
    {
        // In a non-3D view, we can't easily use ReferenceIntersector
        // Instead, we'll use a different approach based on view type

        try
        {
            // For simplicity in this example, just pick elements near the point
            // This is a less precise method but works for all view types
            FilteredElementCollector collector = new FilteredElementCollector(Doc, view.Id);

            // Get all elements in the view
            var elements = collector
                .WhereElementIsNotElementType()
                .ToElements();

            // Try to find the closest element to the specified point
            Element closestElement = null;
            double minDistance = double.MaxValue;

            foreach (var element in elements)
            {
                // Skip elements that don't have a valid location
                if (element.Location == null)
                    continue;

                // Get the closest point on this element
                XYZ elementPoint = null;

                if (element.Location is LocationPoint locationPoint)
                {
                    elementPoint = locationPoint.Point;
                }
                else if (element.Location is LocationCurve locationCurve)
                {
                    elementPoint = locationCurve.Curve.Project(point).XYZPoint;
                }
                else
                {
                    continue;
                }

                // Calculate distance to this element
                double distance = point.DistanceTo(elementPoint);

                // Update closest element if this one is closer
                if (distance < minDistance)
                {
                    closestElement = element;
                    minDistance = distance;
                }
            }

            // If we found a close enough element, get a geometric face reference
            if (closestElement != null && minDistance < 5.0) // 5 feet tolerance
            {
                var refs = GetReferences(closestElement, view, dimensionDirection, new[] { point }, wallFace);
                if (refs.Count > 0)
                    return refs[0];
            }
        }
        catch (Exception ex)
        {
            // Log error but continue processing (suppressed to avoid blocking UI)
        }

        return null;
    }

    /// <summary>
    ///     Applies parameters to the created dimension
    /// </summary>
    /// <param name="dimension">Dimension instance</param>
    /// <param name="dimensionInfo">Dimension information</param>
    private void ApplyDimensionParameters(Dimension dimension, DimensionCreationInfo dimensionInfo)
    {
        if (dimensionInfo.Options == null) return;

        foreach (var option in dimensionInfo.Options)
        {
            var param = dimension.LookupParameter(option.Key);
            if (param == null) continue;

            if (option.Value is double doubleValue && param.StorageType == StorageType.Double)
            {
                param.Set(doubleValue * MILLIMETERS_TO_FEET);
            }
            else if (option.Value is int intValue && param.StorageType == StorageType.Integer)
            {
                param.Set(intValue);
            }
            else if (option.Value is string stringValue && param.StorageType == StorageType.String)
            {
                param.Set(stringValue);
            }
        }
    }

    /// <summary>
    ///     Converts millimeter coordinates to Revit internal coordinates (feet)
    /// </summary>
    /// <param name="x">X coordinate in millimeters</param>
    /// <param name="y">Y coordinate in millimeters</param>
    /// <param name="z">Z coordinate in millimeters</param>
    /// <returns>XYZ point in Revit coordinates</returns>
    private XYZ ConvertToInternalCoordinates(double x, double y, double z)
    {
        return new XYZ(
            x * MILLIMETERS_TO_FEET,
            y * MILLIMETERS_TO_FEET,
            z * MILLIMETERS_TO_FEET
        );
    }

    #endregion
}
