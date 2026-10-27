using System.Collections.Generic;
using System.Linq;

namespace RevitStructureTools
{
    internal enum CrossingKind { Wall, Slab }

    internal sealed class VerticalCrossing
    {
        public object Part;
        public int StructureId;
        public CrossingKind Kind;
        public double MinimumZ;
        public double MaximumZ;
        public double Score;
    }

    internal static class WallSlabTransition
    {
        public static VerticalCrossing FindLowerWall(IEnumerable<VerticalCrossing> crossings, double tolerance)
        {
            VerticalCrossing best = null;
            foreach (var part in crossings.GroupBy(item => item.Part, ReferenceComparer<object>.Instance))
            {
                VerticalCrossing[] walls = part.Where(item => item.Kind == CrossingKind.Wall).ToArray();
                foreach (VerticalCrossing slab in part.Where(item => item.Kind == CrossingKind.Slab))
                {
                    foreach (VerticalCrossing lower in walls)
                    {
                        if (lower.MinimumZ >= slab.MinimumZ - tolerance || lower.MaximumZ > slab.MaximumZ + tolerance) continue;
                        bool hasUpper = walls.Any(upper => upper.StructureId != lower.StructureId
                            && upper.MaximumZ > slab.MaximumZ + tolerance
                            && upper.MinimumZ >= slab.MinimumZ - tolerance
                            && lower.MaximumZ <= upper.MinimumZ + tolerance);
                        if (!hasUpper) continue;
                        if (best == null || lower.MinimumZ < best.MinimumZ
                            || (lower.MinimumZ == best.MinimumZ && lower.Score > best.Score)) best = lower;
                    }
                }
            }
            return best;
        }
    }
}
