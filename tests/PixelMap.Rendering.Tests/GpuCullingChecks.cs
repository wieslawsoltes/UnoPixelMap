using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using Silk.NET.OpenGL;

/// <summary>Compare fixed-function face rejection with the fragment-discard path.
/// The reference intercepts only glEnable(CULL_FACE); geometry, shaders, camera,
/// depth tests and texture resources remain identical.</summary>
internal static class GpuCullingChecks
{
    private const int Width = 391, Height = 263;
    private static readonly TileId Tile = new(14, 8192, 8192);

    internal static void Run(GL gl)
    {
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI;
        double longitude = (Tile.X + .5) / (1 << Tile.Z) * 360 - 180;
        var camera = new MapCamera(latitude, longitude, 17, 0, 55);
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), 2, new float[] { 0, 0, 0, 0 })]);
        var terrain = new TerrainOptions(elevation);
        MapFeature[] features =
        [
            Building(1, 72, 0, Box(1750, 1750, 2020, 2100), Box(1810, 1810, 1960, 2020).Reverse().ToArray()),
            Building(2, 60, 24, Box(2010, 1990, 2090, 2110)),
            Building(3, 98, 8, Box(2200, 1850, 2310, 1980), Box(2300, 2040, 2420, 2140)),
            Building(4, 38, 0, new TilePoint[] { new(1780, 2230), new(2000, 2360), new(1910, 2230), new(1780, 2360) }),
            Building(5, 125, 0, new TilePoint[] { new(2160, 2210), new(2380, 2210), new(2380, 2300),
                new(2240, 2300), new(2240, 2400), new(2160, 2400) }),
            Building(6, 20, 20, Box(1960, 2140, 2110, 2200))
        ];
        MapTile[] tiles = [new(Tile, features)];
        var views = new List<MapCamera>();
        foreach (var (bearing, pitch) in new[] { (0d, 0d), (47d, 55d), (131d, 78d), (-97d, 35d) })
        {
            views.Add(camera with { Bearing = bearing, Pitch = pitch });
            views.Add(camera with { Bearing = bearing, Pitch = pitch, Perspective = true });
        }
        foreach (var (height, bearing, pitch) in new[]
        {
            (-8d, 0d, 35d), (1.7d, 0d, 90d), (1.7d, 89d, 89.999d),
            (1.7d, 181d, 90.001d), (40d, 271d, 110d), (140d, 43d, 45d),
            (30d, 0d, 0d), (1.7d, 0d, 179.5d)
        })
            views.Add(MapCamera.FromEye(latitude, longitude, height, bearing, pitch,
                Width, Height, viewDistanceMeters: 400, groundAltitudeMeters: 0));

        using var scene = new PixelMapRenderer();
        using var probe = new CullProbe(gl);
        using var renderer = new GlMapRenderer(probe.Gl);
        int cases = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (var view in views)
        {
            int pixelSize = 2 + cases % 3;
            using var frame = scene.PrepareGpuFrame(Width, Height, view, tiles,
                PixelStyle.Night, pixelSize, null, terrain, style);
            probe.Suppress = true;
            byte[] expected = Capture(gl, renderer, frame);
            probe.Suppress = false;
            byte[] actual = Capture(gl, renderer, frame);
            Assert(expected.AsSpan().SequenceEqual(actual),
                $"Fixed-function culling changed exact {style} artwork for camera {view}.");
            Assert(gl.GetError() == GLEnum.NoError, "Building culling left a GL error.");
            cases++;
        }
        Assert(probe.CulledDraws > 0 && probe.ReferenceDraws > 0,
            "The culling fixture did not exercise both building face submission paths.");
        Console.WriteLine($"Passed {cases} exact hardware-culling/fragment-discard comparisons: " +
            "four styles, art scales, courtyard/disconnected/intersecting/raised/flat caps, " +
            "orthographic and physical cameras above, below and inside buildings.");
    }

    private static byte[] Capture(GL gl, GlMapRenderer renderer, GpuMapFrame frame)
    {
        int width = (frame.Width + frame.PixelSize - 1) / frame.PixelSize;
        int height = (frame.Height + frame.PixelSize - 1) / frame.PixelSize;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Disable(EnableCap.CullFace);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        renderer.DrawArtwork(frame);
        byte[] pixels = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        return pixels;
    }

    private static MapFeature Building(ulong id, double top, double bottom, params TilePoint[][] rings) =>
        new("building", "", "", "", id, 4096, GeometryType.Polygon, rings, top, bottom, false, 0, 0, "");

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private sealed class CullProbe : IDisposable
    {
        private readonly GL native;
        private readonly Enable enable;
        private readonly DrawElements draw;
        internal readonly GL Gl;
        internal bool Suppress;
        internal int CulledDraws, ReferenceDraws;

        internal CullProbe(GL native)
        {
            this.native = native;
            var originalDraw = Marshal.GetDelegateForFunctionPointer<DrawElements>(native.Context.GetProcAddress("glDrawElements"));
            enable = capability =>
            {
                if (capability != (uint)EnableCap.CullFace || !Suppress)
                    native.Enable((EnableCap)capability);
            };
            draw = (mode, count, type, indices) =>
            {
                if (native.IsEnabled(EnableCap.CullFace))
                {
                    Assert(native.GetInteger(GLEnum.CullFaceMode) == (int)GLEnum.Back,
                        "The building pass did not select back-face rejection.");
                    CulledDraws++;
                }
                else ReferenceDraws++;
                originalDraw(mode, count, type, indices);
            };
            Gl = GL.GetApi(Resolve);
        }

        private nint Resolve(string name) => name switch
        {
            "glEnable" => Marshal.GetFunctionPointerForDelegate(enable),
            "glDrawElements" => Marshal.GetFunctionPointerForDelegate(draw),
            _ => native.Context.GetProcAddress(name)
        };

        public void Dispose() => Gl.Dispose();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Enable(uint capability);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DrawElements(uint mode, uint count, uint type, nint indices);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
