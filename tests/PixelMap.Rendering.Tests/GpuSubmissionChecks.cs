using System.Reflection;
using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using Silk.NET.OpenGL;
using SkiaSharp;

internal static class GpuSubmissionChecks
{
    private static readonly TileId Tile = new(14, 8192, 8192);
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly EnableCap[] Capabilities =
    [
        EnableCap.Blend, EnableCap.CullFace, EnableCap.ScissorTest, EnableCap.StencilTest,
        EnableCap.DepthTest, EnableCap.Dither, EnableCap.RasterizerDiscard,
        EnableCap.PolygonOffsetFill, EnableCap.SampleAlphaToCoverage
    ];
    private static readonly GLEnum[] UnpackParameters =
    [
        GLEnum.PixelUnpackBufferBinding, GLEnum.UnpackAlignment, GLEnum.UnpackRowLength,
        GLEnum.UnpackSkipRows, GLEnum.UnpackSkipPixels, GLEnum.UnpackImageHeight, GLEnum.UnpackSkipImages
    ];

    internal static void Run()
    {
        using var context = new HeadlessGlContext(800, 500);
        var gl = context.Gl;
        using var calls = new CallProbe(gl);
        CheckUniformBits(gl, calls);
        using var scene = new PixelMapRenderer();
        using var renderer = new GlMapRenderer(calls.Gl);
        using var host = new HostResources(gl);
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI;
        double longitude = (Tile.X + .5) / (1 << Tile.Z) * 360 - 180;
        var camera = new MapCamera(latitude, longitude, 16, 23, 55, true);
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), 2, new float[] { 1500, 1500, 1500, 1500 })]);
        var terrain = new TerrainOptions(elevation);
        MapTile[] tiles = [new(Tile, [new("building", "", "", "", 1, 4096, GeometryType.Polygon,
            [new TilePoint[] { new(1900, 1900), new(2180, 1900), new(2180, 2180), new(1900, 2180) }],
            42, 0, false, 0, 0, "")])];
        using var photo = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(photo)) canvas.Clear(new SKColor(94, 136, 72));
        using var encoded = photo.Encode(SKEncodedImageFormat.Png, 100);
        ImageryTile[] imagery = [new(Tile, encoded.ToArray())];

        Reset(gl);
        using var first = scene.PrepareGpuFrame(360, 240, camera.OnTerrain(terrain, 240), tiles,
            PixelStyle.Day, 3, null, terrain);
        byte[] expected = Capture(gl, renderer, first, artwork: true);
        calls.Reset();
        Assert(expected.AsSpan().SequenceEqual(Capture(gl, renderer, first, artwork: true)),
            "Uniform reuse changed the retained frame.");
        Assert(calls.UniformCalls == 0, "An identical single-tile frame uploaded unchanged uniforms.");
        Assert(calls.ErrorCalls == 1, "A warm frame did not use one complete draw/restoration error checkpoint.");
        Assert(!calls.Queries.Contains(GLEnum.ReadFramebufferBinding) && !calls.Queries.Contains(GLEnum.RenderbufferBinding) &&
            !calls.Queries.Contains(GLEnum.DepthClearValue), "A warm vector frame queried untouched GL state.");
        Assert(!UnpackParameters.Any(calls.Queries.Contains) && calls.PixelStoreCalls == 0,
            "A cached frame queried or changed pixel unpack state without uploading pixels.");

        using var resize = scene.PrepareGpuFrame(317, 211, camera.OnTerrain(terrain, 211), tiles,
            PixelStyle.Day, 3, null, terrain);
        int cases = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (bool photograph in new[] { false, true })
        foreach (bool artwork in new[] { false, true })
        {
            int width = artwork ? 391 : 360, height = artwork ? 263 : 240;
            var view = (camera with { Pitch = photograph ? 15 : 55 }).OnTerrain(terrain, height);
            using var frame = scene.PrepareGpuFrame(width, height, view, tiles,
                photograph ? PixelStyle.Dusk : PixelStyle.Day, 3, photograph ? imagery : null, terrain, style);
            Reset(gl);
            byte[] reference = Capture(gl, renderer, frame, artwork);
            Capture(gl, renderer, resize, artwork: true);
            host.Poison();
            SetViewport(gl, frame, artwork, 7, 9);
            var before = Snapshot(gl);
            byte[] actual = Capture(gl, renderer, frame, artwork, 7, 9);
            Assert(before.OrderBy(p => p.Key).SequenceEqual(Snapshot(gl).OrderBy(p => p.Key)),
                $"{style} photograph {photograph} artwork {artwork} changed host GL state.");
            Assert(actual.AsSpan().SequenceEqual(reference),
                $"{style} photograph {photograph} artwork {artwork} changed under non-default host GL state.");
            Assert(gl.GetError() == GLEnum.NoError, "Shared-context drawing left a GL error.");
            cases++;
        }

        Reset(gl);
        using var moved = scene.PrepareGpuFrame(360, 240, (camera with { Bearing = 41 }).OnTerrain(terrain, 240),
            tiles, PixelStyle.Night, 3, null, terrain);
        byte[] movedReference = Capture(gl, renderer, moved, artwork: true);
        Capture(gl, renderer, first, artwork: true);
        host.Poison();
        SetViewport(gl, moved, artwork: true, 7, 9);
        var errorState = Snapshot(gl);
        calls.FailNextUniform4 = true;
        bool failed = false;
        try
        {
            Capture(gl, renderer, moved, artwork: true, 7, 9);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("OpenGL error ", StringComparison.Ordinal))
        {
            failed = true;
        }
        Assert(failed && !calls.FailNextUniform4, "The frame did not report an injected uniform upload error.");
        Assert(errorState.OrderBy(p => p.Key).SequenceEqual(Snapshot(gl).OrderBy(p => p.Key)),
            "A failed frame changed host GL state.");
        Assert(gl.GetError() == GLEnum.NoError, "A failed frame did not drain its GL errors.");
        calls.Reset();
        Assert(movedReference.AsSpan().SequenceEqual(Capture(gl, renderer, moved, artwork: true, 7, 9)),
            "Retrying a failed uniform upload reused unsubmitted values.");
        Assert(calls.UniformCalls > 0, "A failed frame retained its unverified uniform cache.");
        Reset(gl);
        Console.WriteLine($"Passed {cases} exact shared-GL-state draws, warm uniform/query reuse, float-bit preservation and failed-upload retry.");
        CheckTextureImages(gl, calls, scene, host, camera, tiles, imagery, terrain);
    }

    private static void CheckTextureImages(GL gl, CallProbe calls, PixelMapRenderer scene, HostResources host, MapCamera camera,
        MapTile[] tiles, ImageryTile[] imagery, TerrainOptions terrain)
    {
        using var glInterface = GRGlInterface.CreateGles(name => gl.Context.GetProcAddress(name));
        using var skia = GRContext.CreateGl(glInterface);
        Assert(skia is not null, "Skia could not create a context for same-context texture checks.");
        using var renderer = new GlMapRenderer(calls.Gl);
        var retained = new List<(SKImage Image, byte[] Pixels)>();
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        try
        {
            foreach (var style in Enum.GetValues<MapRenderStyle>())
            foreach (bool photograph in new[] { false, true })
            {
                int width = photograph ? 317 : 391, height = photograph ? 211 : 263;
                var view = (camera with { Pitch = photograph ? 15 : 55 }).OnTerrain(terrain, height);
                using var frame = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, 3,
                    photograph ? imagery : null, terrain, style);
                Reset(gl);
                byte[] bottomUp = Capture(gl, renderer, frame, artwork: true);
                int artWidth = (width + frame.PixelSize - 1) / frame.PixelSize;
                int artHeight = (height + frame.PixelSize - 1) / frame.PixelSize;
                byte[] expected = new byte[bottomUp.Length];
                int rowBytes = artWidth * sizeof(uint);
                for (int y = 0; y < artHeight; y++)
                    bottomUp.AsSpan((artHeight - y - 1) * rowBytes, rowBytes)
                        .CopyTo(expected.AsSpan(y * rowBytes, rowBytes));
                skia!.ResetContext();
                skia.Flush(submit: false);
                host.Poison();
                gl.Viewport(7, 9, 713, 419);
                var before = Snapshot(gl);
                calls.Reset();
                var image = renderer.DrawArtworkImage(frame, skia);
                Assert(!UnpackParameters.Where(p => p != GLEnum.UnpackRowLength).Any(calls.Queries.Contains) &&
                    calls.Queries.Contains(GLEnum.UnpackRowLength) && calls.Queries.Contains(GLEnum.PackRowLength) &&
                    calls.PixelStoreCalls == 2,
                    $"{style} photograph {photograph} did not limit transfer state to Skia's two row lengths.");
                Assert(calls.TextureStorageCalls == 1,
                    "A retained GPU image did not use one source-free immutable texture allocation.");
                retained.Add((image, expected));
                Assert(image.IsTextureBacked && image.Width == artWidth && image.Height == artHeight,
                    $"{style} photograph {photograph} did not retain an art-sized GPU texture.");
                var after = Snapshot(gl);
                Assert(before.OrderBy(p => p.Key).SequenceEqual(after.OrderBy(p => p.Key)),
                    $"{style} photograph {photograph} texture adoption changed host GL state. " +
                    string.Join(", ", before.Where(p => after[p.Key] != p.Value).Select(p => $"{p.Key} {p.Value} to {after[p.Key]}")));
                Assert(expected.AsSpan().SequenceEqual(ReadImage(image)),
                    $"{style} photograph {photograph} changed exact art pixels during texture adoption.");
            }

            var first = retained[0];
            Assert(first.Pixels.AsSpan().SequenceEqual(ReadImage(first.Image)),
                "Later geometry frames changed the first adopted image before composition.");
            // The hostile-state fixture intentionally enables rasterizer discard,
            // which a normal Skia host never enables and Skia does not manage.
            Reset(gl);
            skia!.ResetContext();
            const int scaledWidth = 391, scaledHeight = 263, pixelSize = 3;
            using var surface = SKSurface.Create(skia!, true,
                new SKImageInfo(scaledWidth, scaledHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
            Assert(surface is not null, "Skia could not allocate a GPU presentation surface.");
            using var replayFrame = scene.PrepareGpuFrame(scaledWidth, scaledHeight,
                camera.OnTerrain(terrain, scaledHeight), tiles, PixelStyle.Day, pixelSize, null, terrain);
            var replayImage = renderer.DrawArtworkImage(replayFrame, skia!);
            using var recorder = new SKPictureRecorder();
            var recording = recorder.BeginRecording(new SKRect(0, 0, scaledWidth, scaledHeight));
            Assert(recording.Context is null, "The replay fixture did not use a deferred picture canvas.");
            recording.DrawImage(replayImage,
                new SKRect(0, 0, replayImage.Width * pixelSize, replayImage.Height * pixelSize), nearest);
            using var recorded = recorder.EndRecording();
            replayImage.Dispose();
            surface!.Canvas.Clear(SKColors.Magenta);
            surface.Canvas.DrawPicture(recorded);
            using var laterFrame = scene.PrepareGpuFrame(347, 223,
                (camera with { Bearing = 83 }).OnTerrain(terrain, 223), tiles, PixelStyle.Night, 4, null, terrain);
            using var laterImage = renderer.DrawArtworkImage(laterFrame, skia!);
            // The previous Skia draw was still pending when the map submitted another
            // frame. Both commands must keep their original immutable texture content.
            using var composed = surface.Snapshot();
            byte[] composedPixels = ReadImage(composed);
            for (int y = 0; y < scaledHeight; y++)
            for (int x = 0; x < scaledWidth; x++)
            {
                int source = (y / pixelSize * first.Image.Width + x / pixelSize) * sizeof(uint);
                int destination = (y * scaledWidth + x) * sizeof(uint);
                Assert(first.Pixels.AsSpan(source, sizeof(uint)).SequenceEqual(composedPixels.AsSpan(destination, sizeof(uint))),
                    $"GPU presentation changed nearest-neighbor pixel {x},{y}. " +
                    $"Expected {Convert.ToHexString(first.Pixels.AsSpan(source, sizeof(uint)))}, " +
                    $"received {Convert.ToHexString(composedPixels.AsSpan(destination, sizeof(uint)))}.");
            }

            renderer.Dispose();
            skia!.ResetContext();
            foreach (var image in retained)
                Assert(image.Pixels.AsSpan().SequenceEqual(ReadImage(image.Image)),
                    "A retained image changed after later styles, viewport resizing or renderer disposal.");
            Assert(gl.GetError() == GLEnum.NoError, "Same-context texture presentation left a GL error.");
            Console.WriteLine($"Passed {retained.Count} exact same-context texture images, four styles, Earth blending, shared GL state, deferred picture replay, pending Skia drawing, resize and image lifetime checks.");
        }
        finally
        {
            foreach (var image in retained) image.Image.Dispose();
            Reset(gl);
            skia?.ResetContext();
        }

        static byte[] ReadImage(SKImage image)
        {
            using var bitmap = new SKBitmap(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            Assert(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0),
                "Skia could not read a completed texture image for validation.");
            return bitmap.GetPixelSpan().ToArray();
        }
    }

    private static void CheckUniformBits(GL gl, CallProbe calls)
    {
        using var program = new TestProgram(calls.Gl);
        program.Use();
        calls.Reset();
        foreach (int bits in new[] { 0, 0, int.MinValue, int.MinValue, 0x7fc00001, 0x7fc00001, 0x7fc00002 })
            program.Float("uValue", BitConverter.Int32BitsToSingle(bits));
        program.Float("uMissing", 1);
        program.Float("uMissing", 2);
        Assert(calls.UniformCalls == 4, "Uniform caching merged distinct float bits or submitted inactive uniforms.");
        program.Invalidate();
        program.Float("uValue", BitConverter.Int32BitsToSingle(0x7fc00002));
        Assert(calls.UniformCalls == 5, "Invalidating a shader cache did not force the next upload.");
        program.Float("uValue", 0);
        program.Integer("uValue", 0);
        Assert(calls.UniformCalls == 7 && gl.GetError() == GLEnum.InvalidOperation,
            "Uniform caching skipped GL validation when the setter type changed.");
        gl.UseProgram(0);
        Assert(gl.GetError() == GLEnum.NoError, "Uniform caching generated a GL error.");
    }

    private static byte[] Capture(GL gl, GlMapRenderer renderer, GpuMapFrame frame, bool artwork, int x = 0, int y = 0)
    {
        var (width, height) = SetViewport(gl, frame, artwork, x, y);
        if (artwork) renderer.DrawArtwork(frame); else renderer.Draw(frame);
        uint previousRead = (uint)gl.GetInteger(GLEnum.ReadFramebufferBinding);
        int packRowLength = gl.GetInteger(GLEnum.PackRowLength);
        try
        {
            gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            byte[] pixels = new byte[width * height * 4];
            gl.ReadPixels<byte>(x, y, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            return pixels;
        }
        finally
        {
            gl.PixelStore(PixelStoreParameter.PackRowLength, packRowLength);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousRead);
        }
    }

    private static (int Width, int Height) SetViewport(GL gl, GpuMapFrame frame, bool artwork, int x, int y)
    {
        int width = artwork ? (frame.Width + frame.PixelSize - 1) / frame.PixelSize : frame.Width;
        int height = artwork ? (frame.Height + frame.PixelSize - 1) / frame.PixelSize : frame.Height;
        gl.Viewport(x, y, (uint)width, (uint)height);
        return (width, height);
    }

    private static Dictionary<string, int> Snapshot(GL gl)
    {
        var result = new Dictionary<string, int>();
        int activeTexture = gl.GetInteger(GLEnum.ActiveTexture);
        foreach (var parameter in new[] { GLEnum.DrawFramebufferBinding, GLEnum.ReadFramebufferBinding,
            GLEnum.CurrentProgram, GLEnum.VertexArrayBinding, GLEnum.ArrayBufferBinding, GLEnum.ElementArrayBufferBinding,
            GLEnum.PixelUnpackBufferBinding, GLEnum.RenderbufferBinding, GLEnum.ActiveTexture, GLEnum.UnpackAlignment,
            GLEnum.UnpackRowLength, GLEnum.UnpackSkipRows, GLEnum.UnpackSkipPixels, GLEnum.UnpackImageHeight,
            GLEnum.UnpackSkipImages, GLEnum.PackRowLength, GLEnum.DepthFunc, GLEnum.FrontFace, GLEnum.CullFaceMode })
            result[parameter.ToString()] = gl.GetInteger(parameter);
        foreach (var capability in Capabilities) result[capability.ToString()] = gl.IsEnabled(capability) ? 1 : 0;
        result["DepthMask"] = gl.GetBoolean(GLEnum.DepthWritemask) ? 1 : 0;
        int[] viewport = new int[4];
        gl.GetInteger(GetPName.Viewport, out viewport[0]);
        bool[] colors = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, out colors[0]);
        for (int i = 0; i < 4; i++)
        {
            result["Viewport" + i] = viewport[i];
            result["ColorMask" + i] = colors[i] ? 1 : 0;
        }
        float[] range = new float[2];
        gl.GetFloat(GetPName.DepthRange, out range[0]);
        gl.GetFloat(GetPName.DepthClearValue, out float clear);
        result["DepthNear"] = BitConverter.SingleToInt32Bits(range[0]);
        result["DepthFar"] = BitConverter.SingleToInt32Bits(range[1]);
        result["DepthClear"] = BitConverter.SingleToInt32Bits(clear);
        for (int unit = 0; unit < 3; unit++)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + unit);
            result["Texture2D" + unit] = gl.GetInteger(GLEnum.TextureBinding2D);
            result["TextureArray" + unit] = gl.GetInteger(GLEnum.TextureBinding2DArray);
            result["Sampler" + unit] = gl.GetInteger(GLEnum.SamplerBinding);
        }
        gl.ActiveTexture((TextureUnit)activeTexture);
        return result;
    }

    private static void Reset(GL gl)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        gl.UseProgram(0);
        foreach (var capability in Capabilities) gl.Disable(capability);
        gl.DepthMask(true);
        gl.ColorMask(true, true, true, true);
        gl.DepthRange(0f, 1f);
        gl.ClearDepth(1f);
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.CullFace(GLEnum.Back);
        gl.DepthFunc(DepthFunction.Less);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
        foreach (var parameter in new[] { PixelStoreParameter.UnpackRowLength, PixelStoreParameter.UnpackSkipRows,
            PixelStoreParameter.UnpackSkipPixels, PixelStoreParameter.UnpackImageHeight, PixelStoreParameter.UnpackSkipImages })
            gl.PixelStore(parameter, 0);
        for (uint unit = 0; unit < 3; unit++) gl.BindSampler(unit, 0);
    }

    private sealed class HostResources : IDisposable
    {
        private readonly GL gl;
        private readonly TestProgram program;
        private readonly uint array, vertexBuffer, indexBuffer, unpackBuffer, renderbuffer, readFramebuffer;
        private readonly uint[] textures, textureArrays, samplers;

        internal HostResources(GL gl)
        {
            this.gl = gl;
            program = new(gl);
            array = gl.GenVertexArray();
            vertexBuffer = gl.GenBuffer();
            indexBuffer = gl.GenBuffer();
            unpackBuffer = gl.GenBuffer();
            renderbuffer = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, 1, 1);
            readFramebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, readFramebuffer);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, renderbuffer);
            Assert(gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete,
                "The independent read framebuffer fixture is incomplete.");
            textures = [gl.GenTexture(), gl.GenTexture(), gl.GenTexture()];
            textureArrays = [gl.GenTexture(), gl.GenTexture(), gl.GenTexture()];
            samplers = [gl.GenSampler(), gl.GenSampler(), gl.GenSampler()];
            Reset(gl);
        }

        internal void Poison()
        {
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
            gl.BindVertexArray(array);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indexBuffer);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, unpackBuffer);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            program.Use();
            foreach (var capability in Capabilities) gl.Enable(capability);
            gl.DepthMask(false);
            gl.ColorMask(false, true, false, true);
            gl.DepthRange(.2f, .7f);
            gl.ClearDepth(.25f);
            gl.FrontFace(FrontFaceDirection.CW);
            gl.CullFace(GLEnum.FrontAndBack);
            gl.DepthFunc(DepthFunction.Never);
            gl.Scissor(2, 3, 1, 1);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 8);
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 11);
            gl.PixelStore(PixelStoreParameter.UnpackSkipRows, 2);
            gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, 3);
            gl.PixelStore(PixelStoreParameter.UnpackImageHeight, 17);
            gl.PixelStore(PixelStoreParameter.UnpackSkipImages, 1);
            gl.PixelStore(PixelStoreParameter.PackRowLength, 13);
            for (uint unit = 0; unit < 3; unit++)
            {
                gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
                gl.BindTexture(TextureTarget.Texture2D, textures[unit]);
                gl.BindTexture(TextureTarget.Texture2DArray, textureArrays[unit]);
                gl.BindSampler(unit, samplers[unit]);
            }
            gl.ActiveTexture(TextureUnit.Texture5);
        }

        public void Dispose()
        {
            Reset(gl);
            program.Dispose();
            foreach (uint texture in textures.Concat(textureArrays)) gl.DeleteTexture(texture);
            foreach (uint sampler in samplers) gl.DeleteSampler(sampler);
            gl.DeleteVertexArray(array);
            gl.DeleteBuffer(vertexBuffer);
            gl.DeleteBuffer(indexBuffer);
            gl.DeleteBuffer(unpackBuffer);
            gl.DeleteFramebuffer(readFramebuffer);
            gl.DeleteRenderbuffer(renderbuffer);
        }
    }

    private sealed class TestProgram : IDisposable
    {
        private static readonly Type Type = typeof(GlMapRenderer).GetNestedType("ShaderProgram", BindingFlags.NonPublic)!;
        private readonly object instance;

        internal TestProgram(GL gl)
        {
            instance = Activator.CreateInstance(Type, Members, null, [gl,
                "#version 300 es\nvoid main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }",
                "#version 300 es\nprecision highp float; uniform float uValue; out vec4 color; void main() { color = vec4(uValue, 0.0, 0.0, 1.0); }",
                "uniform-state regression"], null)!;
        }

        internal void Use() => Type.GetMethod("Use", Members)!.Invoke(instance, null);
        internal void Float(string name, float value) => Type.GetMethod("Float", Members)!.Invoke(instance, [name, value]);
        internal void Integer(string name, int value) => Type.GetMethod("Integer", Members)!.Invoke(instance, [name, value]);
        internal void Invalidate() => Type.GetMethod("InvalidateUniforms", Members)!.Invoke(instance, null);
        public void Dispose() => ((IDisposable)instance).Dispose();
    }

    private sealed class CallProbe : IDisposable
    {
        private readonly GL native;
        private readonly Dictionary<string, Delegate> callbacks = [];
        internal GL Gl { get; }
        internal int UniformCalls, ErrorCalls, PixelStoreCalls, TextureStorageCalls;
        internal bool FailNextUniform4;
        internal HashSet<GLEnum> Queries { get; } = [];

        internal CallProbe(GL native)
        {
            this.native = native;
            callbacks["glUniform1f"] = new Float1((location, x) => { UniformCalls++; native.Uniform1(location, x); });
            callbacks["glUniform1i"] = new Int1((location, x) => { UniformCalls++; native.Uniform1(location, x); });
            callbacks["glUniform2i"] = new Int2((location, x, y) => { UniformCalls++; native.Uniform2(location, x, y); });
            callbacks["glUniform2f"] = new Float2((location, x, y) => { UniformCalls++; native.Uniform2(location, x, y); });
            callbacks["glUniform3f"] = new Float3((location, x, y, z) => { UniformCalls++; native.Uniform3(location, x, y, z); });
            callbacks["glUniform4f"] = new Float4((location, x, y, z, w) =>
            {
                UniformCalls++;
                if (FailNextUniform4) { FailNextUniform4 = false; location = int.MaxValue; }
                native.Uniform4(location, x, y, z, w);
            });
            callbacks["glGetError"] = new Error(() => { ErrorCalls++; return (uint)native.GetError(); });
            callbacks["glPixelStorei"] = new Store((parameter, value) =>
            {
                PixelStoreCalls++;
                native.PixelStore((PixelStoreParameter)parameter, value);
            });
            callbacks["glTexStorage2D"] = new Storage((target, levels, format, width, height) =>
            {
                TextureStorageCalls++;
                native.TexStorage2D((TextureTarget)target, levels, (SizedInternalFormat)format, width, height);
            });
            foreach (string name in new[] { "glGetIntegerv", "glGetBooleanv", "glGetFloatv" })
            {
                var original = Marshal.GetDelegateForFunctionPointer<Query>(native.Context.GetProcAddress(name));
                callbacks[name] = new Query((parameter, destination) =>
                {
                    Queries.Add((GLEnum)parameter);
                    original(parameter, destination);
                });
            }
            Gl = GL.GetApi(Resolve);
        }

        private nint Resolve(string name) => callbacks.TryGetValue(name, out var callback)
            ? Marshal.GetFunctionPointerForDelegate(callback) : native.Context.GetProcAddress(name);

        internal void Reset()
        {
            UniformCalls = ErrorCalls = PixelStoreCalls = TextureStorageCalls = 0;
            Queries.Clear();
        }

        public void Dispose() => Gl.Dispose();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Float1(int location, float x);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Float2(int location, float x, float y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Float3(int location, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Float4(int location, float x, float y, float z, float w);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Int1(int location, int x);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Int2(int location, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Error();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Query(uint parameter, nint destination);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Store(uint parameter, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Storage(uint target, uint levels, uint format, uint width, uint height);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
