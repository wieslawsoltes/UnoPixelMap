namespace PixelMap.Data;

/// <summary>A sparse immutable coverage tree. Prepared subtree bounds respect finer vector
/// tiles, including land-only tiles that completely replace a coarse ocean polygon.</summary>
internal sealed class ElevationSurfaceCoverage
{
    private readonly Node _root = new(new(0, 0, 0));

    private sealed class Node(TileId id)
    {
        internal readonly TileId Id = id;
        internal readonly Node?[] Children = new Node?[4];
        internal ElevationSurfaceIndex? Surface;
        internal bool HasChildren, Ocean;
    }

    internal ElevationSurfaceCoverage(IReadOnlyDictionary<TileId, ElevationSurfaceIndex> surfaces)
    {
        foreach (var (id, surface) in surfaces)
        {
            var node = _root;
            for (int zoom = 1; zoom <= id.Z; zoom++)
            {
                int x = (id.X >> (id.Z - zoom)) & 1, y = (id.Y >> (id.Z - zoom)) & 1;
                node.HasChildren = true;
                node = node.Children[x | (y << 1)] ??= new(new(zoom, node.Id.X * 2 + x, node.Id.Y * 2 + y));
            }
            node.Surface = surface;
        }
        Prepare(_root, null, default);
    }

    internal bool MayContainOcean(TileId bounds)
    {
        var node = _root;
        ElevationSurfaceIndex? surface = null;
        TileId source = default;
        for (int zoom = 0; zoom < bounds.Z; zoom++)
        {
            if (!node.Ocean) return false;
            if (node.Surface is not null) { surface = node.Surface; source = node.Id; }
            int shift = bounds.Z - zoom - 1;
            int quadrant = ((bounds.X >> shift) & 1) | (((bounds.Y >> shift) & 1) << 1);
            if (node.Children[quadrant] is not { } child) return Intersects(surface, source, bounds);
            node = child;
        }
        return node.Ocean;
    }

    private static void Prepare(Node node, ElevationSurfaceIndex? surface, TileId source)
    {
        if (node.Surface is not null) { surface = node.Surface; source = node.Id; }
        if (!node.HasChildren)
        {
            node.Ocean = Intersects(surface, source, node.Id);
            return;
        }
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            if (node.Children[quadrant] is { } child)
            {
                Prepare(child, surface, source);
                node.Ocean |= child.Ocean;
            }
            else
            {
                var bounds = new TileId(node.Id.Z + 1, node.Id.X * 2 + (quadrant & 1), node.Id.Y * 2 + (quadrant >> 1));
                node.Ocean |= Intersects(surface, source, bounds);
            }
        }
    }

    private static bool Intersects(ElevationSurfaceIndex? surface, TileId source, TileId bounds)
    {
        if (surface is null) return false;
        double scale = 1d / (1 << (bounds.Z - source.Z));
        double left = bounds.X * scale - source.X, top = bounds.Y * scale - source.Y;
        return surface.MayContainOcean(left, top, left + scale, top + scale);
    }
}
