using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class MeasureBetweenElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private long _elementId1;
        private long _elementId2;
        private double[] _point1;
        private double[] _point2;
        private string _measureType = "center_to_center";

        public AIResult<object> Result { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        public void SetParameters(long elementId1, long elementId2, double[] point1, double[] point2, string measureType)
        {
            _elementId1 = elementId1;
            _elementId2 = elementId2;
            _point1 = point1;
            _point2 = point2;
            _measureType = measureType ?? "center_to_center";
            TaskCompleted = false;
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

                XYZ p1 = ResolvePoint(doc, _elementId1, _point1);
                XYZ p2 = ResolvePoint(doc, _elementId2, _point2);

                if (p1 == null || p2 == null)
                    throw new ArgumentException("Must provide two valid references (element IDs or points)");

                string methodUsed = "center_to_center";
                string note = null;

                if (_measureType == "bounding_box")
                {
                    var box1 = ResolveBox(doc, _elementId1, _point1);
                    var box2 = ResolveBox(doc, _elementId2, _point2);
                    if (box1 != null && box2 != null)
                    {
                        BoxGapClosestPoints(box1.Item1, box1.Item2, box2.Item1, box2.Item2, out p1, out p2);
                        methodUsed = "bounding_box";
                        note = "Gap between axis-aligned bounding boxes (0 if they overlap).";
                    }
                    else
                    {
                        note = "Bounding box unavailable for one of the references; fell back to center_to_center.";
                    }
                }
                else if (_measureType == "closest_points")
                {
                    if (TryClosestPoints(doc, out XYZ c1, out XYZ c2, out string closestNote))
                    {
                        p1 = c1;
                        p2 = c2;
                        methodUsed = "closest_points";
                        note = closestNote;
                    }
                    else
                    {
                        var box1 = ResolveBox(doc, _elementId1, _point1);
                        var box2 = ResolveBox(doc, _elementId2, _point2);
                        if (box1 != null && box2 != null)
                        {
                            BoxGapClosestPoints(box1.Item1, box1.Item2, box2.Item1, box2.Item2, out p1, out p2);
                            methodUsed = "bounding_box";
                            note = $"{closestNote} Approximated with the gap between bounding boxes.";
                        }
                        else
                        {
                            note = $"{closestNote} Fell back to center_to_center.";
                        }
                    }
                }

                double distanceFeet = p1.DistanceTo(p2);
                double distanceMm = ConvertToMm(distanceFeet);
                double dx = ConvertToMm(Math.Abs(p2.X - p1.X));
                double dy = ConvertToMm(Math.Abs(p2.Y - p1.Y));
                double dz = ConvertToMm(Math.Abs(p2.Z - p1.Z));

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Distance ({methodUsed}): {distanceMm:F1} mm ({distanceMm / 1000:F3} m)",
                    Response = new
                    {
                        distance = Math.Round(distanceMm, 1),
                        distanceMeters = Math.Round(distanceMm / 1000, 3),
                        deltaX = Math.Round(dx, 1),
                        deltaY = Math.Round(dy, 1),
                        deltaZ = Math.Round(dz, 1),
                        point1 = FormatPoint(p1),
                        point2 = FormatPoint(p2),
                        measureType = _measureType,
                        methodUsed,
                        note
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Measure failed: {ex.Message}" };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private XYZ ResolvePoint(Document doc, long elementId, double[] point)
        {
            if (point != null && point.Length >= 3)
            {
                return new XYZ(
                    ConvertToFeet(point[0]),
                    ConvertToFeet(point[1]),
                    ConvertToFeet(point[2])
                );
            }

            if (elementId > 0)
            {
                var element = doc.GetElement(ToElementId(elementId));
                if (element == null) throw new ArgumentException($"Element {elementId} not found");

                var bb = element.get_BoundingBox(null);
                if (bb != null)
                    return (bb.Min + bb.Max) / 2.0;

                if (element.Location is LocationPoint lp) return lp.Point;
                if (element.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);

                throw new ArgumentException($"Element {elementId} has no measurable geometry");
            }

            return null;
        }

        /// <summary>
        /// Axis-aligned box (min, max) in model coordinates for an element or a point.
        /// </summary>
        private Tuple<XYZ, XYZ> ResolveBox(Document doc, long elementId, double[] point)
        {
            if (point != null && point.Length >= 3)
            {
                var p = new XYZ(ConvertToFeet(point[0]), ConvertToFeet(point[1]), ConvertToFeet(point[2]));
                return Tuple.Create(p, p);
            }
            if (elementId <= 0) return null;

            var element = doc.GetElement(ToElementId(elementId));
            var bb = element?.get_BoundingBox(null);
            if (bb == null) return null;

            var t = bb.Transform ?? Transform.Identity;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        var c = t.OfPoint(new XYZ(x, y, z));
                        minX = Math.Min(minX, c.X); minY = Math.Min(minY, c.Y); minZ = Math.Min(minZ, c.Z);
                        maxX = Math.Max(maxX, c.X); maxY = Math.Max(maxY, c.Y); maxZ = Math.Max(maxZ, c.Z);
                    }
            return Tuple.Create(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        /// <summary>
        /// Closest points between two axis-aligned boxes. Per axis: if the intervals overlap the
        /// points share the midpoint of the overlap, otherwise they sit on the facing box sides.
        /// The resulting distance is 0 when the boxes overlap.
        /// </summary>
        private static void BoxGapClosestPoints(XYZ aMin, XYZ aMax, XYZ bMin, XYZ bMax, out XYZ pa, out XYZ pb)
        {
            double[] a = new double[3], b = new double[3];
            double[] aLo = { aMin.X, aMin.Y, aMin.Z }, aHi = { aMax.X, aMax.Y, aMax.Z };
            double[] bLo = { bMin.X, bMin.Y, bMin.Z }, bHi = { bMax.X, bMax.Y, bMax.Z };
            for (int i = 0; i < 3; i++)
            {
                if (aHi[i] < bLo[i]) { a[i] = aHi[i]; b[i] = bLo[i]; }
                else if (bHi[i] < aLo[i]) { a[i] = aLo[i]; b[i] = bHi[i]; }
                else
                {
                    double mid = (Math.Max(aLo[i], bLo[i]) + Math.Min(aHi[i], bHi[i])) / 2.0;
                    a[i] = mid; b[i] = mid;
                }
            }
            pa = new XYZ(a[0], a[1], a[2]);
            pb = new XYZ(b[0], b[1], b[2]);
        }

        private const int MaxClosestPointPairs = 400000;

        /// <summary>
        /// Minimum distance between element solids (or a point and a solid).
        /// Checks every vertex / tessellated edge point of one side against the faces and edges
        /// of the other (both directions). Exact for vertex-face, vertex-edge and parallel face
        /// cases; skew edge-edge minima are approximated by edge tessellation. Returns 0 if the
        /// solids intersect.
        /// </summary>
        private bool TryClosestPoints(Document doc, out XYZ best1, out XYZ best2, out string note)
        {
            best1 = null;
            best2 = null;
            note = null;

            List<Solid> solids1 = GetReferenceSolids(doc, _elementId1, _point1, out XYZ pt1);
            List<Solid> solids2 = GetReferenceSolids(doc, _elementId2, _point2, out XYZ pt2);

            if ((solids1 == null && pt1 == null) || (solids2 == null && pt2 == null))
            {
                note = "No solid geometry found for one of the elements.";
                return false;
            }

            if (pt1 != null && pt2 != null)
            {
                best1 = pt1;
                best2 = pt2;
                note = "Both references are points.";
                return true;
            }

            // Overlap check: intersecting solids -> distance 0
            if (solids1 != null && solids2 != null)
            {
                foreach (var s1 in solids1)
                {
                    foreach (var s2 in solids2)
                    {
                        try
                        {
                            var inter = BooleanOperationsUtils.ExecuteBooleanOperation(s1, s2, BooleanOperationsType.Intersect);
                            if (inter != null && inter.Volume > 1e-9)
                            {
                                var c = inter.ComputeCentroid();
                                best1 = c;
                                best2 = c;
                                note = "Element solids intersect.";
                                return true;
                            }
                        }
                        catch
                        {
                            // Boolean can fail on some geometry; continue with surface distance
                        }
                    }
                }
            }

            List<XYZ> points1 = pt1 != null ? new List<XYZ> { pt1 } : SamplePoints(solids1);
            List<XYZ> points2 = pt2 != null ? new List<XYZ> { pt2 } : SamplePoints(solids2);
            List<Face> faces1 = solids1?.SelectMany(s => s.Faces.Cast<Face>()).ToList() ?? new List<Face>();
            List<Face> faces2 = solids2?.SelectMany(s => s.Faces.Cast<Face>()).ToList() ?? new List<Face>();
            List<Curve> edges1 = solids1?.SelectMany(s => s.Edges.Cast<Edge>()).Select(e => e.AsCurve()).ToList() ?? new List<Curve>();
            List<Curve> edges2 = solids2?.SelectMany(s => s.Edges.Cast<Edge>()).Select(e => e.AsCurve()).ToList() ?? new List<Curve>();

            long work = (long)points1.Count * (faces2.Count + edges2.Count) + (long)points2.Count * (faces1.Count + edges1.Count);
            if (work > MaxClosestPointPairs)
            {
                note = $"Geometry too complex for exact closest-point search ({work} checks).";
                return false;
            }

            double bestDist = double.MaxValue;
            XYZ b1 = null, b2 = null;

            void Consider(XYZ a, XYZ b)
            {
                double d = a.DistanceTo(b);
                if (d < bestDist) { bestDist = d; b1 = a; b2 = b; }
            }

            foreach (var p in points1)
            {
                foreach (var f in faces2)
                {
                    var r = f.Project(p);
                    if (r != null) Consider(p, r.XYZPoint);
                }
                foreach (var e in edges2)
                {
                    var r = e.Project(p);
                    if (r != null) Consider(p, r.XYZPoint);
                }
            }
            foreach (var p in points2)
            {
                foreach (var f in faces1)
                {
                    var r = f.Project(p);
                    if (r != null) Consider(r.XYZPoint, p);
                }
                foreach (var e in edges1)
                {
                    var r = e.Project(p);
                    if (r != null) Consider(r.XYZPoint, p);
                }
            }

            if (b1 == null)
            {
                note = "Closest-point search produced no result.";
                return false;
            }

            best1 = b1;
            best2 = b2;
            note = "Minimum distance between element solids (vertex/edge-to-face/edge search; skew edge-to-edge minima approximated by tessellation).";
            return true;
        }

        private List<Solid> GetReferenceSolids(Document doc, long elementId, double[] point, out XYZ pointRef)
        {
            pointRef = null;
            if (point != null && point.Length >= 3)
            {
                pointRef = new XYZ(ConvertToFeet(point[0]), ConvertToFeet(point[1]), ConvertToFeet(point[2]));
                return null;
            }
            if (elementId <= 0) return null;

            var element = doc.GetElement(ToElementId(elementId));
            if (element == null) return null;

            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
            var geom = element.get_Geometry(options);
            if (geom == null) return null;

            var solids = new List<Solid>();
            CollectSolids(geom, solids);
            return solids.Count > 0 ? solids : null;
        }

        private static void CollectSolids(GeometryElement geom, List<Solid> solids)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid s && s.Volume > 1e-9 && s.Faces.Size > 0)
                {
                    solids.Add(s);
                }
                else if (obj is GeometryInstance gi)
                {
                    var inst = gi.GetInstanceGeometry();
                    if (inst != null) CollectSolids(inst, solids);
                }
            }
        }

        private static List<XYZ> SamplePoints(List<Solid> solids)
        {
            var pts = new List<XYZ>();
            foreach (var s in solids)
            {
                foreach (Edge e in s.Edges)
                {
                    pts.AddRange(e.Tessellate());
                }
            }
            return pts;
        }

        private static double ConvertToMm(double feet)
        {
#if REVIT2022_OR_GREATER
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
#else
            return UnitUtils.ConvertFromInternalUnits(feet, DisplayUnitType.DUT_MILLIMETERS);
#endif
        }

        private static double ConvertToFeet(double mm)
        {
#if REVIT2022_OR_GREATER
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
#else
            return UnitUtils.ConvertToInternalUnits(mm, DisplayUnitType.DUT_MILLIMETERS);
#endif
        }

        private static ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        private object FormatPoint(XYZ p)
        {
            return new
            {
                x = Math.Round(ConvertToMm(p.X), 1),
                y = Math.Round(ConvertToMm(p.Y), 1),
                z = Math.Round(ConvertToMm(p.Z), 1)
            };
        }

        public string GetName() => "Measure Between Elements";
    }
}
