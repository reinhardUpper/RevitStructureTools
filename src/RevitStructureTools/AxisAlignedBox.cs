namespace RevitStructureTools
{
    internal struct AxisAlignedBox
    {
        public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public AxisAlignedBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        // Touching/nearby boxes must still reach the exact Revit operation.
        public bool ClearlyDisjoint(AxisAlignedBox other, double tolerance)
        {
            return MinX > other.MaxX + tolerance || other.MinX > MaxX + tolerance
                || MinY > other.MaxY + tolerance || other.MinY > MaxY + tolerance
                || MinZ > other.MaxZ + tolerance || other.MinZ > MaxZ + tolerance;
        }
    }
}
