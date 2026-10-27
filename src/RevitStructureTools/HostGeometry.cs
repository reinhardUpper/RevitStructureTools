using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitStructureTools
{
    internal sealed class HostMatch
    {
        public Element Host;
        public string Mark;
        public string Reason;
        public double Score;
        public double? MinimumZ;
    }

    // Per-command caches: never retain Revit elements or geometry across documents or command runs.
    internal sealed class HostGeometry : IDisposable
    {
        private sealed class IntersectionData
        {
            public double Score;
            public double? MinimumZ;
            public double? TraceMinimumZ;
            public double? MaximumZ;
            public bool Complete;
            public bool Possible;
        }

        private const double EpsLength = 1e-7;
        private const double EpsVolume = 1e-9;
        private readonly Document document;
        private readonly Options options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };
        private readonly Options sleeveOptions = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
        private readonly Dictionary<int, List<Solid>> solids = new Dictionary<int, List<Solid>>();
        private readonly Dictionary<int, List<Solid>> sleeves = new Dictionary<int, List<Solid>>();
        private readonly Dictionary<int, List<Solid>> wallEnvelopes = new Dictionary<int, List<Solid>>();
        private readonly Dictionary<int, List<FamilyInstance>> nested = new Dictionary<int, List<FamilyInstance>>();
        private readonly Dictionary<int, BoundingBoxXYZ> hostBoxes = new Dictionary<int, BoundingBoxXYZ>();
        private readonly Dictionary<int, string> marks = new Dictionary<int, string>();
        private readonly Dictionary<int, string> prefixes = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> family231 = new Dictionary<int, bool>();
        private readonly Dictionary<int, BoundingBoxXYZ> analysisBoxes = new Dictionary<int, BoundingBoxXYZ>();
        private readonly Dictionary<int, BoundingBoxXYZ> curveBoxes = new Dictionary<int, BoundingBoxXYZ>();
        private readonly Dictionary<int, List<Curve>> ownedCurves = new Dictionary<int, List<Curve>>();
        private readonly Dictionary<int, List<Curve>> areaCurves = new Dictionary<int, List<Curve>>();
        private readonly Dictionary<Curve, object> curveParts
            = new Dictionary<Curve, object>(ReferenceComparer<Curve>.Instance);
        private readonly Dictionary<Solid, AxisAlignedBox?> solidBounds
            = new Dictionary<Solid, AxisAlignedBox?>(ReferenceComparer<Solid>.Instance);
        private readonly Dictionary<Curve, AxisAlignedBox?> lineBounds
            = new Dictionary<Curve, AxisAlignedBox?>(ReferenceComparer<Curve>.Instance);
        private readonly Dictionary<Tuple<double, double, double, double, double, double>, List<Element>> candidateSets
            = new Dictionary<Tuple<double, double, double, double, double, double>, List<Element>>();
        private readonly ReferencePairCache<Curve, Solid, IntersectionData> curveIntersections
            = new ReferencePairCache<Curve, Solid, IntersectionData>();
        private readonly ReferencePairCache<Solid, Solid, IntersectionData> solidIntersections
            = new ReferencePairCache<Solid, Solid, IntersectionData>();
        private readonly ReferencePairCache<Solid, Solid, IntersectionData> strictSolidIntersections
            = new ReferencePairCache<Solid, Solid, IntersectionData>();
        public readonly List<string> Diagnostics = new List<string>();

        public HostGeometry(Document document) { this.document = document; }

        public static bool CategoryIs(Element element, BuiltInCategory category)
        { return element.Category != null && element.Category.Id.IntegerValue == (int)category; }

        public static bool IsFamilyRebar(Element element)
        { return element is FamilyInstance && CategoryIs(element, BuiltInCategory.OST_Rebar); }

        public static bool IsTarget(Element element)
        {
            return element is Rebar || element is RebarInSystem || element is AreaReinforcement
                || IsFamilyRebar(element) || CategoryIs(element, BuiltInCategory.OST_GenericModel)
                || CategoryIs(element, BuiltInCategory.OST_Windows);
        }

        public List<FamilyInstance> NestedRebars(Element element)
        {
            int id = element.Id.IntegerValue;
            if (nested.TryGetValue(id, out List<FamilyInstance> cached)) return cached;
            var result = new List<FamilyInstance>();
            var visited = new HashSet<int>();
            WalkNested(element as FamilyInstance, visited, child =>
            {
                if (IsFamilyRebar(child)) result.Add(child);
            });
            nested[id] = result;
            return result;
        }

        public void WalkNested(FamilyInstance parent, HashSet<int> visited, Action<FamilyInstance> action)
        {
            if (parent == null) return;
            foreach (ElementId id in parent.GetSubComponentIds())
            {
                if (!visited.Add(id.IntegerValue)) continue;
                var child = document.GetElement(id) as FamilyInstance;
                if (child == null) continue;
                action(child);
                WalkNested(child, visited, action);
            }
        }

        public string Prefix(Element element)
        {
            int id = element.Id.IntegerValue;
            if (prefixes.TryGetValue(id, out string cached)) return cached;
            string value = Parameters.ReadWithType(document, element, Names.Prefix);
            if (value == null)
                value = NestedRebars(element).Any(child => Parameters.ReadWithType(document, child, Names.Prefix) == "В") ? "В" : null;
            prefixes[id] = value;
            return value;
        }

        public bool Is231(Element element)
        {
            int id = element.Id.IntegerValue;
            if (family231.TryGetValue(id, out bool cached)) return cached;
            var current = element as FamilyInstance;
            var visited = new HashSet<int>();
            while (current != null && visited.Add(current.Id.IntegerValue))
            {
                if (current.Symbol.Family.Name.Contains("231")) return family231[id] = true;
                current = current.SuperComponent as FamilyInstance;
            }
            return family231[id] = false;
        }

        private bool IsRebarStyle(GeometryObject geometry)
        {
            ElementId id = geometry.GraphicsStyleId;
            if (id == ElementId.InvalidElementId) return false;
            Category category = (document.GetElement(id) as GraphicsStyle)?.GraphicsStyleCategory;
            while (category != null)
            {
                if (category.Id.IntegerValue == (int)BuiltInCategory.OST_Rebar) return true;
                category = category.Parent;
            }
            return false;
        }

        private void CollectSolids(GeometryElement geometry, List<Solid> result, bool rebarOnly = false, bool inherited = false)
        {
            if (geometry == null) return;
            foreach (GeometryObject item in geometry)
            {
                bool classified = inherited || (rebarOnly && IsRebarStyle(item));
                if (item is Solid solid)
                {
                    if (solid.Faces.Size > 0 && solid.Volume > EpsVolume && (!rebarOnly || classified)) result.Add(solid);
                }
                else if (item is GeometryInstance instance)
                    CollectSolids(instance.GetInstanceGeometry(), result, rebarOnly, classified);
            }
        }

        private List<Solid> ElementSolids(Element element)
        {
            int id = element.Id.IntegerValue;
            if (solids.TryGetValue(id, out List<Solid> cached)) return cached;
            var result = new List<Solid>();
            try { CollectSolids(element.get_Geometry(options), result); }
            catch (Exception error) { Diagnostics.Add("ID " + id + ": получение геометрии: " + error.Message); }
            solids[id] = result;
            return result;
        }

        private List<Solid> SleeveSolids(Element element)
        {
            int id = element.Id.IntegerValue;
            if (sleeves.TryGetValue(id, out List<Solid> cached)) return cached;
            var result = new List<Solid>();
            List<FamilyInstance> parts = IsFamilyRebar(element)
                ? new List<FamilyInstance> { (FamilyInstance)element } : NestedRebars(element);
            if (parts.Count != 0)
                foreach (FamilyInstance part in parts) CollectSolids(part.get_Geometry(sleeveOptions), result);
            else
                CollectSolids(element.get_Geometry(sleeveOptions), result, true);
            sleeves[id] = result;
            return result;
        }

        private List<Solid> AnalysisSolids(Element element, out bool usedNested)
        {
            List<FamilyInstance> parts = NestedRebars(element);
            if (parts.Count != 0)
            {
                List<Solid> result = parts.SelectMany(ElementSolids).ToList();
                if (result.Count != 0) { usedNested = true; return result; }
            }
            usedNested = false;
            return ElementSolids(element);
        }

        private static BoundingBoxXYZ BoundingBox(Element element)
        {
            try { return element.get_BoundingBox(null); }
            catch { return null; }
        }

        private static IEnumerable<XYZ> BoxCorners(BoundingBoxXYZ box)
        {
            if (box == null) yield break;
            foreach (double x in new[] { box.Min.X, box.Max.X })
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    foreach (double z in new[] { box.Min.Z, box.Max.Z })
                        yield return box.Transform.OfPoint(new XYZ(x, y, z));
        }

        private static BoundingBoxXYZ UnionBoxes(IEnumerable<BoundingBoxXYZ> boxes)
        {
            XYZ[] points = boxes.SelectMany(BoxCorners).ToArray();
            if (points.Length == 0) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                Max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z))
            };
        }

        private static BoundingBoxXYZ SolidBox(IEnumerable<Solid> items)
        { return UnionBoxes(items.Select(solid => solid.GetBoundingBox())); }

        private BoundingBoxXYZ AnalysisBox(Element element)
        {
            int id = element.Id.IntegerValue;
            if (!analysisBoxes.TryGetValue(id, out BoundingBoxXYZ box))
                analysisBoxes[id] = box = ComputeAnalysisBox(element);
            return box;
        }

        private BoundingBoxXYZ ComputeAnalysisBox(Element element)
        {
            if (Is231(element)) return SolidBox(SleeveSolids(element));
            List<FamilyInstance> parts = NestedRebars(element);
            if (parts.Count != 0)
            {
                BoundingBoxXYZ union = UnionBoxes(parts.Select(BoundingBox));
                if (union != null) return union;
            }
            return BoundingBox(element);
        }

        private BoundingBoxXYZ HostBox(Element element)
        {
            int id = element.Id.IntegerValue;
            if (!hostBoxes.TryGetValue(id, out BoundingBoxXYZ result)) hostBoxes[id] = result = BoundingBox(element);
            return result;
        }

        private string HostMark(Element element)
        {
            int id = element.Id.IntegerValue;
            if (!marks.TryGetValue(id, out string result)) marks[id] = result = Parameters.ReadWithType(document, element, Names.Mark);
            return result;
        }

        private List<Element> Candidates(Element element, BoundingBoxXYZ geometryBox = null)
        {
            BoundingBoxXYZ box = geometryBox ?? AnalysisBox(element);
            if (box == null) return new List<Element>();
            var key = Tuple.Create(box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
            if (candidateSets.TryGetValue(key, out List<Element> cached)) return cached;
            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_StructuralFoundation
            };
            using (var outline = new Outline(box.Min, box.Max))
            using (var filter = new ElementMulticategoryFilter(categories))
            using (var boxFilter = new BoundingBoxIntersectsFilter(outline))
            using (var collector = new FilteredElementCollector(document))
            {
                // Foundation slabs are Floor objects in OST_StructuralFoundation; isolated footings are excluded.
                List<Element> result = collector.WherePasses(filter).WherePasses(boxFilter).WhereElementIsNotElementType()
                    .Where(host => !CategoryIs(host, BuiltInCategory.OST_StructuralFoundation) || host is Floor).ToList();
                candidateSets[key] = result;
                return result;
            }
        }

        private static double LoopArea(CurveLoop loop, PlanarFace face)
        {
            var coords = new List<UV>();
            foreach (Curve curve in loop)
            {
                IList<XYZ> points = curve.Tessellate();
                foreach (XYZ point in points.Take(Math.Max(0, points.Count - 1)))
                {
                    XYZ delta = point.Subtract(face.Origin);
                    coords.Add(new UV(delta.DotProduct(face.XVector), delta.DotProduct(face.YVector)));
                }
            }
            double area = 0;
            for (int i = 0; i < coords.Count; i++)
            {
                UV next = coords[(i + 1) % coords.Count];
                area += coords[i].U * next.V - next.U * coords[i].V;
            }
            return Math.Abs(area) / 2;
        }

        private List<Solid> HostSolids(Element host)
        {
            if (!(host is Wall wall)) return ElementSolids(host);
            int id = wall.Id.IntegerValue;
            if (wallEnvelopes.TryGetValue(id, out List<Solid> cached)) return cached;
            if (!((wall.Location as LocationCurve)?.Curve is Line))
                throw new InvalidOperationException("Стена ID " + id + " криволинейная; восстановление контура без отверстий не поддержано");
            var result = new List<Solid>();
            try
            {
                foreach (Reference reference in HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior))
                {
                    if (!(wall.GetGeometryObjectFromReference(reference) is PlanarFace face))
                        throw new InvalidOperationException("Боковая грань стены ID " + id + " не плоская");
                    IList<CurveLoop> loops = face.GetEdgesAsCurveLoops();
                    if (loops.Count == 0) continue;
                    CurveLoop outer = loops.OrderByDescending(loop => LoopArea(loop, face)).First();
                    result.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { outer }, face.FaceNormal.Negate(), wall.Width));
                }
                if (result.Count == 0) throw new InvalidOperationException("Не получен внешний контур стены ID " + id);
                wallEnvelopes[id] = result;
                return result;
            }
            catch
            {
                foreach (Solid solid in result) solid.Dispose();
                throw;
            }
        }

        private static double Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b, out double? z)
        {
            z = null;
            if (a == null || b == null) return 0;
            double dx = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
            double dy = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
            double dz = Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z);
            if (dx <= 0 || dy <= 0 || dz <= 0) return 0;
            z = Math.Max(a.Min.Z, b.Min.Z);
            return dx * dy * dz;
        }

        private static void SolidVerticalRange(Solid solid, out double minimum, out double maximum)
        {
            var points = new List<XYZ>();
            foreach (Edge edge in solid.Edges) points.AddRange(edge.Tessellate());
            minimum = points.Count != 0 ? points.Min(point => point.Z) : solid.ComputeCentroid().Z;
            maximum = points.Count != 0 ? points.Max(point => point.Z) : minimum;
        }

        private double SolidIntersection(List<Solid> targets, List<Solid> hosts, bool strict, Element structure,
            List<VerticalCrossing> crossings, out double? minZ, out bool possible, out bool complete)
        {
            double volume = 0;
            double? lowest = null;
            bool succeeded = false;
            bool allSucceeded = true;
            var traces = new Dictionary<object, VerticalCrossing>(ReferenceComparer<object>.Instance);
            foreach (Solid target in targets)
            {
                foreach (Solid host in hosts)
                {
                    ReferencePairCache<Solid, Solid, IntersectionData> cache = strict ? strictSolidIntersections : solidIntersections;
                    if (!cache.TryGet(target, host, out IntersectionData data))
                    {
                        data = CalculateSolidPair(target, host, strict);
                        cache.Add(target, host, data);
                    }
                    succeeded |= data.Possible;
                    allSucceeded &= data.Complete;
                    volume += data.Score;
                    if (data.MinimumZ.HasValue && (!lowest.HasValue || data.MinimumZ.Value < lowest.Value)) lowest = data.MinimumZ;
                    AddCrossing(traces, target, structure, data.Score, data.TraceMinimumZ, data.MaximumZ);
                }
            }
            minZ = lowest;
            possible = succeeded;
            complete = succeeded && allSucceeded;
            if (complete) crossings.AddRange(traces.Values);
            return volume;
        }

        private IntersectionData CalculateSolidPair(Solid target, Solid host, bool strict)
        {
            var data = new IntersectionData { Complete = true };
            if (ClearlyDisjoint(SolidBounds(target), SolidBounds(host)))
            {
                data.Possible = true;
                return data;
            }
            try
            {
                using (Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(target, host, BooleanOperationsType.Intersect))
                {
                    data.Possible = true;
                    if (intersection != null && intersection.Volume > EpsVolume)
                    {
                        data.Score = intersection.Volume;
                        if (strict) data.MinimumZ = SolidBox(new[] { intersection })?.Min.Z;
                        else
                        {
                            SolidVerticalRange(intersection, out double minimum, out double maximum);
                            data.TraceMinimumZ = minimum;
                            data.MaximumZ = maximum;
                            data.MinimumZ = minimum;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                if (strict) throw;
                data.Complete = false;
                Diagnostics.Add("Булево пересечение: " + error.Message);
            }
            return data;
        }

        private List<Curve> RebarCurves(Rebar rebar)
        {
            int id = rebar.Id.IntegerValue;
            if (ownedCurves.TryGetValue(id, out List<Curve> cached)) return cached;
            var result = new List<Curve>();
            int positions = Math.Max(1, rebar.NumberOfBarPositions);
            for (int index = 0; index < positions; index++)
            {
                if (!rebar.DoesBarExistAtPosition(index)) continue;
                try
                {
                    object part = new object();
                    foreach (Curve curve in rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, index))
                    {
                        result.Add(curve);
                        curveParts[curve] = part;
                    }
                }
                catch (Exception error)
                {
                    Diagnostics.Add("ID " + rebar.Id.IntegerValue + ": осевые линии: " + error.Message);
                    break;
                }
            }
            ownedCurves[id] = result;
            return result;
        }

        private List<Curve> SystemRebarCurves(RebarInSystem rebar)
        {
            int id = rebar.Id.IntegerValue;
            if (ownedCurves.TryGetValue(id, out List<Curve> cached)) return cached;
            // GetCenterlineCurves returns the first bar only. Apply each position's
            // transform so the complete set participates in spatial comparison.
            var result = new List<Curve>();
            IList<Curve> first = rebar.GetCenterlineCurves(false, false, false);
            try
            {
                for (int index = 0; index < rebar.NumberOfBarPositions; index++)
                {
                    if (!rebar.DoesBarExistAtPosition(index)) continue;
                    using (Transform position = rebar.GetBarPositionTransform(index))
                    {
                        object part = new object();
                        foreach (Curve curve in first)
                        {
                            Curve transformed = curve.CreateTransformed(position);
                            result.Add(transformed);
                            curveParts[transformed] = part;
                        }
                    }
                }
                ownedCurves[id] = result;
                return result;
            }
            catch
            {
                foreach (Curve curve in result) curve.Dispose();
                throw;
            }
            finally { foreach (Curve curve in first) curve.Dispose(); }
        }

        private List<Curve> AreaRebarCurves(AreaReinforcement area)
        {
            int id = area.Id.IntegerValue;
            if (areaCurves.TryGetValue(id, out List<Curve> cached)) return cached;
            var result = new List<Curve>();
            foreach (ElementId rebarId in area.GetRebarInSystemIds())
            {
                var rebar = document.GetElement(rebarId) as RebarInSystem;
                if (rebar != null) result.AddRange(SystemRebarCurves(rebar));
            }
            areaCurves[id] = result;
            return result;
        }

        private static BoundingBoxXYZ CurveBox(IEnumerable<Curve> curves)
        {
            XYZ[] points = curves.SelectMany(curve => curve.Tessellate()).ToArray();
            if (points.Length == 0) return null;
            // Expand by the intersection tolerance so straight horizontal/vertical
            // bars also produce a non-degenerate outline for the coarse filter.
            return new BoundingBoxXYZ
            {
                Min = new XYZ(points.Min(p => p.X) - EpsLength, points.Min(p => p.Y) - EpsLength, points.Min(p => p.Z) - EpsLength),
                Max = new XYZ(points.Max(p => p.X) + EpsLength, points.Max(p => p.Y) + EpsLength, points.Max(p => p.Z) + EpsLength)
            };
        }

        private double CurveIntersection(IEnumerable<Curve> curves, List<Solid> hosts, Element structure,
            List<VerticalCrossing> crossings, out double? minZ, out bool complete)
        {
            double length = 0;
            double? lowest = null;
            bool allSucceeded = hosts.Count != 0;
            var traces = new Dictionary<object, VerticalCrossing>(ReferenceComparer<object>.Instance);
            using (var settings = new SolidCurveIntersectionOptions())
            {
                foreach (Curve curve in curves)
                {
                    foreach (Solid solid in hosts)
                    {
                        if (!curveIntersections.TryGet(curve, solid, out IntersectionData data))
                        {
                            data = CalculateCurvePair(curve, solid, settings);
                            curveIntersections.Add(curve, solid, data);
                        }
                        allSucceeded &= data.Complete;
                        length += data.Score;
                        if (data.MinimumZ.HasValue && (!lowest.HasValue || data.MinimumZ.Value < lowest.Value)) lowest = data.MinimumZ;
                        if (curveParts.TryGetValue(curve, out object part))
                            AddCrossing(traces, part, structure, data.Score, data.MinimumZ, data.MaximumZ);
                    }
                }
            }
            minZ = lowest;
            complete = allSucceeded;
            if (complete) crossings.AddRange(traces.Values);
            return length;
        }

        private IntersectionData CalculateCurvePair(Curve curve, Solid solid, SolidCurveIntersectionOptions settings)
        {
            var data = new IntersectionData { Complete = true };
            if (ClearlyDisjoint(LineBounds(curve), SolidBounds(solid))) return data;
            try
            {
                using (SolidCurveIntersection intersection = solid.IntersectWithCurve(curve, settings))
                {
                    for (int i = 0; i < intersection.SegmentCount; i++)
                    {
                        using (Curve segment = intersection.GetCurveSegment(i))
                        {
                            if (segment.Length <= EpsLength) continue;
                            data.Score += segment.Length;
                            IList<XYZ> points = segment.Tessellate();
                            double z = points.Min(point => point.Z);
                            double maximum = points.Max(point => point.Z);
                            if (!data.MinimumZ.HasValue || z < data.MinimumZ.Value) data.MinimumZ = z;
                            if (!data.MaximumZ.HasValue || maximum > data.MaximumZ.Value) data.MaximumZ = maximum;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                data.Complete = false;
                Diagnostics.Add("Пересечение оси арматуры: " + error.Message);
            }
            return data;
        }

        private static bool ClearlyDisjoint(AxisAlignedBox? left, AxisAlignedBox? right)
        {
            return left.HasValue && right.HasValue && left.Value.ClearlyDisjoint(right.Value, EpsLength);
        }

        private AxisAlignedBox? SolidBounds(Solid solid)
        {
            if (solidBounds.TryGetValue(solid, out AxisAlignedBox? cached)) return cached;
            AxisAlignedBox? result = null;
            try
            {
                // Transform all eight box corners into model coordinates.
                // Never use tessellated vertices as conservative bounds of a solid.
                BoundingBoxXYZ box = SolidBox(new[] { solid });
                if (box != null) result = new AxisAlignedBox(box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
            }
            catch { /* If bounds are unavailable, retain the exact geometry operation. */ }
            solidBounds[solid] = result;
            return result;
        }

        private AxisAlignedBox? LineBounds(Curve curve)
        {
            if (lineBounds.TryGetValue(curve, out AxisAlignedBox? cached)) return cached;
            AxisAlignedBox? result = null;
            try
            {
                // Endpoint bounds are exact for bounded straight lines only.
                // Arcs/splines retain the full Revit calculation.
                if (curve is Line && curve.IsBound)
                {
                    XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
                    result = new AxisAlignedBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z),
                        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                }
            }
            catch { }
            lineBounds[curve] = result;
            return result;
        }

        private HostMatch Select(List<HostMatch> valid, Element element, out string reason, bool usePrefix = true)
        {
            HostMatch selected = DecisionRules.ChooseHost(valid, usePrefix && Prefix(element) == "В", item => item.Score, item => item.MinimumZ);
            reason = selected == null ? "Нет реального пересечения" : selected.Reason;
            return selected;
        }

        private static void AddCrossing(Dictionary<object, VerticalCrossing> traces, object part, Element structure,
            double score, double? minimumZ, double? maximumZ)
        {
            if (score <= 0 || !minimumZ.HasValue || !maximumZ.HasValue) return;
            CrossingKind kind;
            if (structure is Wall) kind = CrossingKind.Wall;
            else if (structure is Floor) kind = CrossingKind.Slab;
            else return;
            if (!traces.TryGetValue(part, out VerticalCrossing trace))
            {
                traces.Add(part, trace = new VerticalCrossing
                {
                    Part = part, StructureId = structure.Id.IntegerValue, Kind = kind,
                    MinimumZ = minimumZ.Value, MaximumZ = maximumZ.Value
                });
            }
            trace.MinimumZ = Math.Min(trace.MinimumZ, minimumZ.Value);
            trace.MaximumZ = Math.Max(trace.MaximumZ, maximumZ.Value);
            trace.Score += score;
        }

        private static bool TryWallSlabTransition(List<VerticalCrossing> crossings, List<HostMatch> valid,
            out HostMatch selected, out string reason)
        {
            VerticalCrossing lower = WallSlabTransition.FindLowerWall(crossings, EpsLength);
            selected = lower == null ? null : valid.FirstOrDefault(item => item.Host.Id.IntegerValue == lower.StructureId);
            if (lower == null) { reason = null; return false; }
            reason = selected == null ? "У нижней стены перехода через плиту не заполнена Марка"
                : "Переход «верхняя стена → плита → нижняя стена»: марка нижней стены";
            if (selected != null) selected.Reason = reason;
            return true;
        }

        private HostMatch FindCurveHost(Element element, List<Curve> curves, string method, bool useCurveBounds, out string reason, out bool noIntersection)
        {
            noIntersection = false;
            if (curves.Count == 0)
            {
                reason = element is AreaReinforcement
                    ? "Нет геометрии стержней армирования по площади; запись пропущена"
                    : "Нет осевых линий арматуры; запись пропущена";
                return null;
            }
            var valid = new List<HostMatch>();
            var crossings = new List<VerticalCrossing>();
            bool anyIntersection = false;
            BoundingBoxXYZ candidateBox;
            if (useCurveBounds)
            {
                int id = element.Id.IntegerValue;
                if (!curveBoxes.TryGetValue(id, out candidateBox)) curveBoxes[id] = candidateBox = CurveBox(curves);
            }
            else candidateBox = AnalysisBox(element);
            bool analysisComplete = candidateBox != null;
            foreach (Element host in Candidates(element, candidateBox))
            {
                double length = CurveIntersection(curves, HostSolids(host), host, crossings, out double? z, out bool complete);
                analysisComplete &= complete;
                if (length <= EpsLength) continue;
                anyIntersection = true;
                string mark = HostMark(host);
                if (mark == null) continue;
                valid.Add(new HostMatch { Host = host, Mark = mark, Score = length, MinimumZ = z, Reason = method });
            }
            HostMatch selected;
            if (analysisComplete && TryWallSlabTransition(crossings, valid, out selected, out reason)) return selected;
            selected = Select(valid, element, out reason);
            noIntersection = selected == null && analysisComplete && !anyIntersection;
            return selected;
        }

        public HostMatch FindHost(Element element, out string reason, out bool noIntersection)
        {
            noIntersection = false;
            if (element is RebarInSystem system)
                return FindCurveHost(element, SystemRebarCurves(system), "Системная арматура: длина пересечения осевых линий всех позиций", true, out reason, out noIntersection);
            if (element is AreaReinforcement area)
                return FindCurveHost(element, AreaRebarCurves(area), "Армирование по площади: длина пересечения осевых линий стержней", true, out reason, out noIntersection);
            if (element is Rebar rebar)
                return FindCurveHost(element, RebarCurves(rebar), "Rebar: длина пересечения осевых линий", false, out reason, out noIntersection);
            List<Element> candidates = Candidates(element);
            var valid = new List<HostMatch>();
            // The original window route uses bounding-box overlap, without prefix prioritisation.
            if (CategoryIs(element, BuiltInCategory.OST_Windows))
            {
                BoundingBoxXYZ box = BoundingBox(element);
                bool anyOverlap = false;
                bool boxesAvailable = box != null;
                foreach (Element host in candidates)
                {
                    BoundingBoxXYZ hostBox = HostBox(host);
                    boxesAvailable &= hostBox != null;
                    double volume = Overlap(box, hostBox, out double? z);
                    if (volume <= EpsVolume) continue;
                    anyOverlap = true;
                    string mark = HostMark(host);
                    if (mark == null) continue;
                    valid.Add(new HostMatch { Host = host, Mark = mark, Score = volume, MinimumZ = z, Reason = "Окно: пересечение габаритов" });
                }
                HostMatch windowMatch = Select(valid, element, out reason, false);
                noIntersection = windowMatch == null && boxesAvailable && !anyOverlap;
                return windowMatch;
            }
            bool strict = Is231(element);
            bool usedNested = false;
            List<Solid> targets = strict ? SleeveSolids(element) : AnalysisSolids(element, out usedNested);
            if (strict && targets.Count == 0)
            {
                reason = "231: не найдены тела вложенной гильзы категории «Несущая арматура». Габарит родителя не используется.";
                return null;
            }
            BoundingBoxXYZ targetBox = AnalysisBox(element);
            var problems = new List<string>();
            var crossings = new List<VerticalCrossing>();
            bool anySolidIntersection = false;
            bool solidAnalysisComplete = targets.Count != 0 && targetBox != null;
            foreach (Element host in candidates)
            {
                try
                {
                    List<Solid> hostSolids = HostSolids(host);
                    double volume = SolidIntersection(targets, hostSolids, strict, host, crossings, out double? z, out bool possible, out bool complete);
                    solidAnalysisComplete &= complete;
                    if (!strict && !possible)
                        volume = Overlap(targetBox, HostBox(host), out z);
                    if (volume <= EpsVolume) continue;
                    anySolidIntersection = true;
                    string mark = HostMark(host);
                    if (mark == null) continue;
                    string method = strict ? (host is Wall ? "231: внешний контур стены без внутренних вырезов" : "231: реальная геометрия плиты")
                        : (usedNested ? "По геометрии вложенной несущей арматуры" : "По геометрии элемента");
                    if (!strict && !possible) method += " (резервное пересечение габаритов, как в Python)";
                    valid.Add(new HostMatch { Host = host, Mark = mark, Score = volume, MinimumZ = z, Reason = method });
                }
                catch (Exception error)
                {
                    if (!strict) throw;
                    problems.Add("Основа ID " + host.Id.IntegerValue + ": " + error.Message);
                }
            }
            if (problems.Count != 0)
            {
                reason = "231: ошибка проверки; запись пропущена. " + string.Join("; ", problems.Take(3));
                return null;
            }
            HostMatch solidMatch;
            if (!strict && solidAnalysisComplete && TryWallSlabTransition(crossings, valid, out solidMatch, out reason)) return solidMatch;
            solidMatch = Select(valid, element, out reason);
            noIntersection = solidMatch == null && solidAnalysisComplete && !anySolidIntersection;
            return solidMatch;
        }

        public void Dispose()
        {
            curveIntersections.Clear();
            solidIntersections.Clear();
            strictSolidIntersections.Clear();
            solidBounds.Clear(); lineBounds.Clear(); candidateSets.Clear();
            foreach (Curve curve in ownedCurves.Values.SelectMany(items => items).Distinct(ReferenceComparer<Curve>.Instance)) curve.Dispose();
            ownedCurves.Clear(); areaCurves.Clear(); curveBoxes.Clear();
            curveParts.Clear();
            prefixes.Clear(); family231.Clear(); analysisBoxes.Clear();
            foreach (Solid solid in wallEnvelopes.Values.SelectMany(items => items)) solid.Dispose();
            options.Dispose();
            sleeveOptions.Dispose();
            // Element geometry is owned by Revit and is released with these per-command caches.
            solids.Clear(); sleeves.Clear(); nested.Clear(); hostBoxes.Clear(); wallEnvelopes.Clear();
        }
    }
}
