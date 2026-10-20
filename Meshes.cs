using System.Windows.Media.Media3D;

namespace DynamicIsland;

static class Meshes
{
    public static MeshGeometry3D Ellipsoid(Point3D center, Vector3D radii, int slices = 36, int stacks = 24)
    {
        var mesh = new MeshGeometry3D();
        for (int stack = 0; stack <= stacks; stack++)
        {
            double phi = Math.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                double theta = -2 * Math.PI * slice / slices;
                var unit = new Vector3D(Math.Sin(phi) * Math.Cos(theta), Math.Cos(phi), Math.Sin(phi) * Math.Sin(theta));
                mesh.Positions.Add(center + new Vector3D(unit.X * radii.X, unit.Y * radii.Y, unit.Z * radii.Z));
                var normal = new Vector3D(unit.X / radii.X, unit.Y / radii.Y, unit.Z / radii.Z);
                normal.Normalize();
                mesh.Normals.Add(normal);
            }
        }
        Stitch(mesh, stacks, slices);
        mesh.Freeze();
        return mesh;
    }

    public static MeshGeometry3D Tube(Point3D[] path, double[] radii, int sides = 28)
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < path.Length; i++)
        {
            Vector3D along = path[Math.Min(i + 1, path.Length - 1)] - path[Math.Max(i - 1, 0)];
            along.Normalize();
            Vector3D right = Vector3D.CrossProduct(along, new Vector3D(0, 0, 1));
            if (right.LengthSquared < 1e-6) right = Vector3D.CrossProduct(along, new Vector3D(1, 0, 0));
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(right, along);
            for (int side = 0; side <= sides; side++)
            {
                double turn = 2 * Math.PI * side / sides;
                Vector3D normal = right * Math.Cos(turn) + up * Math.Sin(turn);
                mesh.Positions.Add(path[i] + normal * radii[i]);
                mesh.Normals.Add(normal);
            }
        }
        Stitch(mesh, path.Length - 1, sides);
        mesh.Freeze();
        return mesh;
    }

    public static Point3D[] Arc(Point3D center, double radius, double fromDegrees, double toDegrees, int steps)
    {
        var points = new Point3D[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double angle = (fromDegrees + (toDegrees - fromDegrees) * i / steps) * Math.PI / 180;
            points[i] = center + new Vector3D(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
        }
        return points;
    }

    static void Stitch(MeshGeometry3D mesh, int rows, int columns)
    {
        int stride = columns + 1;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int a = row * stride + column, b = a + stride;
                foreach (int index in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(index);
            }
        }
    }
}
