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

            // Process each dimension in the list
            foreach (var dimInfo in DimensionsToCreate)
            {
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
                        // Convert points to Revit coordinates
                        var startPoint = ConvertToInternalCoordinates(
                            dimInfo.StartPoint.X,
                            dimInfo.StartPoint.Y,
                            dimInfo.StartPoint.Z
                        );

                        var endPoint = ConvertToInternalCoordinates(
                            dimInfo.EndPoint.X,
                            dimInfo.EndPoint.Y,
                            dimInfo.EndPoint.Z
                        );

                        var linePoint = dimInfo.LinePoint != null
                            ? ConvertToInternalCoordinates(
                                dimInfo.LinePoint.X,
                                dimInfo.LinePoint.Y,
                                dimInfo.LinePoint.Z
                              )
                            : new XYZ(
                                (startPoint.X + endPoint.X) / 2,
                                (startPoint.Y + endPoint.Y) / 2 + 1.0,
                                (startPoint.Z + endPoint.Z) / 2
                              );

                        // Create dimension based on type
                        Dimension dimension = null;

                        if (dimInfo.ElementIds != null && dimInfo.ElementIds.Count > 0)
                        {
                            // Create dimension between elements
                            var dimensionDirection = (endPoint - startPoint).Normalize();
                            var anchors = new[] { startPoint, endPoint };
                            var references = new ReferenceArray();
                            foreach (var elementId in dimInfo.ElementIds)
                            {
                                var element = Doc.GetElement(new ElementId(elementId));
                                if (element != null)
                                {
                                    // Get appropriate reference for this element
                                    foreach (var reference in GetReferences(element, view, dimensionDirection, anchors, dimInfo.WallFace))
                                    {
                                        references.Append(reference);
                                    }
                                }
                            }

                            if (references.Size >= 2)
                            {
                                // Create dimension line with references
                                var line = Line.CreateBound(startPoint, endPoint);
                                dimension = Doc.Create.NewDimension(view, line, references);
                            }
                        }
                        else
                        {
                            // Create a simple dimension line between two points
                            var line = Line.CreateBound(startPoint, endPoint);
                            var dimDirection = (endPoint - startPoint).Normalize();

                            // Pick references from geometry in the view at those points
                            var refArray = new ReferenceArray();
                            var startRef = FindReferenceAtPoint(view, startPoint, dimDirection, dimInfo.WallFace);
                            var endRef = FindReferenceAtPoint(view, endPoint, dimDirection, dimInfo.WallFace);

                            if (startRef != null && endRef != null)
                            {
                                refArray.Append(startRef);
                                refArray.Append(endRef);
                                dimension = Doc.Create.NewDimension(view, line, refArray);
                            }
                        }

                        if (dimension != null)
                        {
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

                            createdDimensionIds.Add(dimension.Id.GetIntValue());
                        }

                        RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(transaction.Commit());
                    }
                    catch
                    {
                        RevitMCPCommandSet.Utils.TransactionGuard.RollBackIfStarted(transaction);
                        throw;
                    }
                }
            }

            // Set successful result
            Result = new AIResult<List<int>>
            {
                Success = true,
                Message = $"Successfully created {createdDimensionIds.Count} dimensions. ElementIds saved in Response.",
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
                var faces = new List<PlanarFace>();
                foreach (var obj in geometry)
                {
                    if (obj is Solid solid && solid.Faces.Size > 0)
                        CollectVerticalPlanarFaces(solid, faces);
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
                var faces = new List<PlanarFace>();
                foreach (var obj in geometry)
                {
                    if (obj is Solid s && s.Faces.Size > 0)
                        CollectVerticalPlanarFaces(s, faces);
                    else if (obj is GeometryInstance gi)
                    {
                        foreach (var subObj in gi.GetInstanceGeometry())
                        {
                            if (subObj is Solid ss && ss.Faces.Size > 0)
                                CollectVerticalPlanarFaces(ss, faces);
                        }
                    }
                }

                var bestRef = PickFaceReference(faces, dimensionDirection, anchors);
                if (bestRef != null)
                {
                    references.Add(bestRef);
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

    private static void CollectVerticalPlanarFaces(Solid solid, List<PlanarFace> faces)
    {
        foreach (Face face in solid.Faces)
        {
            // Skip horizontal faces (top/bottom) - useless in plan view
            if (face is PlanarFace planarFace && face.Reference != null && Math.Abs(planarFace.FaceNormal.Z) <= 0.9)
                faces.Add(planarFace);
        }
    }

    /// <summary>
    ///     Picks the face whose normal is most parallel to the dimension direction.
    ///     An element usually has two such faces (e.g. both sides of a wall), so ties are
    ///     broken by distance to the anchor points - the caller's start/end points mark
    ///     which face they mean.
    /// </summary>
    private static Reference PickFaceReference(List<PlanarFace> faces, XYZ dimensionDirection, IList<XYZ> anchors)
    {
        if (faces.Count == 0)
            return null;

        // Without direction info, take first vertical face
        if (dimensionDirection == null)
            return faces[0].Reference;

        const double parallelTolerance = 1e-3;
        var bestAlignment = faces.Max(f => Math.Abs(f.FaceNormal.DotProduct(dimensionDirection)));
        var candidates = faces
            .Where(f => Math.Abs(f.FaceNormal.DotProduct(dimensionDirection)) >= bestAlignment - parallelTolerance)
            .ToList();

        if (anchors == null || anchors.Count == 0 || candidates.Count == 1)
            return candidates[0].Reference;

        PlanarFace best = null;
        var bestDistance = double.MaxValue;
        foreach (var face in candidates)
        {
            foreach (var anchor in anchors)
            {
                // Distance from the anchor to the face's plane
                var distance = Math.Abs((anchor - face.Origin).DotProduct(face.FaceNormal));
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