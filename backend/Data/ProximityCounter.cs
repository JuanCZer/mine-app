using Notes.Api.Models;

namespace Notes.Api.Data;

// Spatial index in Earth-centred coordinates. Euclidean chord distance is monotonic
// with great-circle distance, including at the antimeridian and the poles.
internal static class ProximityCounter
{
    // Limit concurrent snapshots and return an explicit 503 when capacity is exceeded.
    private static readonly SemaphoreSlim Gate = new(2, 2);
    public static async Task<MapSectors> ExecuteAsync(IConfiguration configuration,
        Func<int, CancellationToken, Task<List<(double Latitude, double Longitude)>>> load,
        CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken)) throw new NoteRepositoryUnavailableException();
        try
        {
            var maximum = Math.Clamp(configuration.GetValue<int?>("Notes:MaximumMapLocations") ?? 50000, 1000, 200000);
            var locations = await load(maximum, cancellationToken);
            if (locations.Count > maximum) throw new NoteRepositoryUnavailableException();
            return Calculate(locations, cancellationToken);
        }
        finally { Gate.Release(); }
    }

    private const double EarthRadius = 6371000d;
    private const double RadiusMeters = 1000d;
    // Accounts only for roundoff in the Cartesian transformation (sub-millimetre).
    private static readonly double RadiusSquared = Math.Pow(2d * EarthRadius * Math.Sin(RadiusMeters / (2d * EarthRadius)) + 0.000001d, 2);

    private readonly record struct Point(double Latitude, double Longitude, double X, double Y, double Z, long Weight)
    {
        public double Axis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
    }

    private sealed class Node(Point point, Node? left, Node? right)
    {
        public Point Point { get; } = point;
        public Node? Left { get; } = left;
        public Node? Right { get; } = right;
        public long Count { get; } = point.Weight + (left?.Count ?? 0) + (right?.Count ?? 0);
        public double[] Min { get; } = Enumerable.Range(0, 3).Select(i => Math.Min(point.Axis(i), Math.Min(left?.Min[i] ?? double.PositiveInfinity, right?.Min[i] ?? double.PositiveInfinity))).ToArray();
        public double[] Max { get; } = Enumerable.Range(0, 3).Select(i => Math.Max(point.Axis(i), Math.Max(left?.Max[i] ?? double.NegativeInfinity, right?.Max[i] ?? double.NegativeInfinity))).ToArray();
    }

    public static MapSectors Calculate(IReadOnlyList<(double Latitude, double Longitude)> locations, CancellationToken cancellationToken)
    {
        // Group coincident positions only inside the index. Multiplicity represents
        // distinct incidents; the response still has one sector per incident.
        var weights = new Dictionary<(double Latitude, double Longitude), long>();
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            weights.TryGetValue(location, out var weight);
            weights[location] = weight + 1;
        }
        var points = new Point[weights.Count];
        var index = 0;
        foreach (var (location, weight) in weights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (latitude, longitude) = location;
            var lat = latitude * Math.PI / 180d;
            var lon = longitude * Math.PI / 180d;
            var r = EarthRadius * Math.Cos(lat);
            points[index++] = new Point(latitude, longitude, r * Math.Cos(lon), r * Math.Sin(lon), EarthRadius * Math.Sin(lat), weight);
        }
        var treePoints = (Point[])points.Clone();
        var root = Build(treePoints, 0, treePoints.Length, 0, cancellationToken);
        var counts = new Dictionary<(double, double), long>(points.Length);
        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counts.Add((point.Latitude, point.Longitude), Count(root, point, cancellationToken));
        }
        var result = new List<MapSector>(locations.Count);
        foreach (var (latitude, longitude) in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new MapSector(latitude, longitude, counts[(latitude, longitude)]));
        }
        return new MapSectors(locations.Count, result);
    }

    private static Node? Build(Point[] points, int start, int length, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length == 0) return null;
        var axis = depth % 3;
        Array.Sort(points, start, length, Comparer<Point>.Create((a, b) => a.Axis(axis).CompareTo(b.Axis(axis))));
        var midpoint = start + length / 2;
        return new Node(points[midpoint], Build(points, start, midpoint - start, depth + 1, cancellationToken),
            Build(points, midpoint + 1, start + length - midpoint - 1, depth + 1, cancellationToken));
    }

    private static long Count(Node? node, Point center, CancellationToken cancellationToken)
    {
        if (node is null) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        var nearest = 0d;
        var farthest = 0d;
        for (var axis = 0; axis < 3; axis++)
        {
            var value = center.Axis(axis);
            var near = Math.Max(0d, Math.Max(node.Min[axis] - value, value - node.Max[axis]));
            var far = Math.Max(Math.Abs(node.Min[axis] - value), Math.Abs(node.Max[axis] - value));
            nearest += near * near;
            farthest += far * far;
        }
        if (nearest > RadiusSquared) return 0;
        if (farthest <= RadiusSquared) return node.Count;
        var dx = center.X - node.Point.X;
        var dy = center.Y - node.Point.Y;
        var dz = center.Z - node.Point.Z;
        var own = dx * dx + dy * dy + dz * dz <= RadiusSquared ? node.Point.Weight : 0L;
        return own + Count(node.Left, center, cancellationToken) + Count(node.Right, center, cancellationToken);
    }
}
