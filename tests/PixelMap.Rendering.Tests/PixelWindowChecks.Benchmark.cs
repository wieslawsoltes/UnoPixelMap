using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixelMap.Rendering;
using Silk.NET.OpenGL;

internal static partial class PixelWindowChecks
{
    private sealed record CostSample(string Scene, int PixelSize, int Block, string Variant, int Sample,
        double Milliseconds, long UploadBytes, int DrawCalls);

    internal static void RunCost(string[] arguments)
    {
        string output = Path.GetFullPath(arguments.FirstOrDefault(a => a.StartsWith("--pixel-output=", StringComparison.Ordinal))?
            ["--pixel-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.PixelWindowCost"));
        Directory.CreateDirectory(output);
        using var context = new HeadlessGlContext(Width, Height);
        var gl = context.Gl;
        using var probe = new PointShader(gl);
        using var point = new GlMapRenderer(probe.Gl);
        using var filtered = new GlMapRenderer(gl);
        using var scene = new PixelMapRenderer();
        var samples = new List<CostSample>();
        var summaries = new List<object>();
        var controls = new List<object>();
        foreach (var (name, distance, bearing, pixelSize) in new[]
        {
            ("near", 64d, 0d, 4), ("distant", 320d, 0d, 4), ("grazing", 160d, 75d, 4),
            ("far", 720d, 20d, 4), ("near-native", 64d, 0d, 1)
        })
        {
            var frames = new List<GpuMapFrame>();
            try
            {
                for (int phase = 0; phase < 8; phase++)
                {
                    var eye = Center.Move(90, phase * 3.8 / 8).Move(180 + bearing, distance);
                    var camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, 300, bearing, 90, Width, Height, viewDistanceMeters: 6000);
                    frames.Add(scene.PrepareGpuFrame(Width, Height, camera, Tiles, PixelStyle.Night, pixelSize));
                }
                string controlHash = Convert.ToHexString(SHA256.HashData(Capture(gl, point, frames[0])));
                string filteredHash = Convert.ToHexString(SHA256.HashData(Capture(gl, filtered, frames[0])));
                if (name.StartsWith("near", StringComparison.Ordinal))
                    Assert(controlHash == filteredHash, "The shader filter changed a fully resolved facade.");
                controls.Add(new { Scene = name, PixelSize = pixelSize, PointHash = controlHash, FilteredHash = filteredHash });
                int width = Width / pixelSize, height = Height / pixelSize;
                foreach (int block in Enumerable.Range(0, 4))
                {
                    bool candidate = block is 1 or 2;
                    var renderer = candidate ? filtered : point;
                    gl.Viewport(0, 0, (uint)width, (uint)height);
                    for (int warm = 0; warm < 16; warm++) renderer.DrawArtwork(frames[warm % frames.Count]);
                    gl.Finish();
                    for (int sample = 0; sample < 64; sample++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        renderer.DrawArtwork(frames[sample % frames.Count]);
                        gl.Finish();
                        samples.Add(new(name, pixelSize, block, candidate ? "filtered" : "point", sample,
                            Stopwatch.GetElapsedTime(start).TotalMilliseconds, renderer.LastUploadBytes, renderer.LastDrawCalls));
                    }
                }
                var before = samples.Where(s => s.Scene == name && s.Variant == "point").Select(s => s.Milliseconds).Order().ToArray();
                var after = samples.Where(s => s.Scene == name && s.Variant == "filtered").Select(s => s.Milliseconds).Order().ToArray();
                double a = before[before.Length / 2], b = after[after.Length / 2];
                summaries.Add(new { Scene = name, PixelSize = pixelSize, PointMedianMs = a, FilteredMedianMs = b,
                    ChangePercent = (b / a - 1) * 100, PointP95Ms = before[(int)Math.Ceiling(before.Length * .95) - 1],
                    FilteredP95Ms = after[(int)Math.Ceiling(after.Length * .95) - 1] });
                Console.WriteLine($"Pixel shader {name} p{pixelSize}: point {a:F3} ms, filtered {b:F3} ms ({(b/a-1)*100:+0.0;-0.0;0.0}%).");
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        Assert(probe.Replacements == 1, "The isolated shader control did not replace exactly one fragment program.");
        Assert(gl.GetError() == GLEnum.NoError, "Isolated pixel shader timing left a GL error.");
        File.WriteAllText(Path.Combine(output, "pixel-window-cost.json"), JsonSerializer.Serialize(new
        {
            Context = context.Description,
            Method = "Same retained frames, renderer, room textures and fixed-function state; the control replaces only the filter branch with false before shader compilation. Four point/filtered/filtered/point blocks, each with 16 warmups and 64 measured submissions plus glFinish. CPU preparation, allocation of samples, and readback are excluded.",
            Runtime = RuntimeInformation.FrameworkDescription, Processors = Environment.ProcessorCount,
            RendererSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GlMapRenderer).Assembly.Location))),
            probe.PointHash, probe.FilteredHash, Controls = controls, Summaries = summaries, Samples = samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Isolated pixel shader cost: {output}");
    }

    private sealed class PointShader : IDisposable
    {
        private readonly GL native;
        private readonly ShaderSource submit;
        internal readonly GL Gl;
        internal int Replacements;
        internal string PointHash = "", FilteredHash = "";

        internal PointShader(GL native)
        {
            this.native = native;
            var original = Marshal.GetDelegateForFunctionPointer<ShaderSource>(native.Context.GetProcAddress("glShaderSource"));
            submit = (shader, count, strings, lengths) =>
            {
                if (count == 1)
                {
                    nint pointer = Marshal.ReadIntPtr(strings);
                    int length = lengths == 0 ? -1 : Marshal.ReadInt32(lengths);
                    string source = length < 0 ? Marshal.PtrToStringUTF8(pointer)! : Marshal.PtrToStringUTF8(pointer, length)!;
                    if (source.Contains("vec2 windowCoverage(", StringComparison.Ordinal))
                    {
                        string point = source.Replace("if (span > 0.2)", "if (false)", StringComparison.Ordinal);
                        Assert(point != source, "The point shader control no longer locates the minification branch.");
                        FilteredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
                        PointHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(point)));
                        Replacements++;
                        native.ShaderSource(shader, point);
                        return;
                    }
                }
                original(shader, count, strings, lengths);
            };
            Gl = GL.GetApi(name => name == "glShaderSource" ? Marshal.GetFunctionPointerForDelegate(submit) : native.Context.GetProcAddress(name));
        }

        public void Dispose() => Gl.Dispose();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShaderSource(uint shader, int count, nint strings, nint lengths);
    }
}
