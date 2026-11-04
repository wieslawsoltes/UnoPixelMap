using System.Diagnostics;
using System.Globalization;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Retained OpenGL ES 3 terrain and model rendering with hardware clipping,
/// interpolation and depth. The caller owns the current context and destination framebuffer.</summary>
public sealed unsafe class GlMapRenderer : IDisposable
{
    private const long CacheBudget = 128L * 1024 * 1024;
    private const long FramebufferBudget = 128L * 1024 * 1024;
    private const int MaximumTerrainTextures = 96;
    private const int MaximumMeshes = 256;
    private const int MaximumTerrainDraws = 64;
    private const int TerrainInstanceStride = 14;
    private readonly GL gl;
    private readonly bool embedded;
    private readonly int maximumTextureSize;
    private readonly Dictionary<object, MeshResource> terrainMeshes = [];
    private readonly Dictionary<object, MeshResource> buildingMeshes = [];
    private readonly Dictionary<GpuTerrainTexture, TextureResource> terrainTextures = [];
    private readonly Stack<int> terrainTextureLayers = new(Enumerable.Range(0, MaximumTerrainTextures).Reverse());
    private readonly Stack<int> terrainMeshLayers = new(Enumerable.Range(0, MaximumMeshes).Reverse());
    private readonly float[] terrainInstances = new float[MaximumTerrainDraws * TerrainInstanceStride];
    private readonly float[] terrainHeightUpload = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * 2];
    private ShaderProgram? terrainProgram, buildingProgram, rimProgram, backgroundProgram, resolveProgram;
    private uint screenArray, gridIndices, roomTexture, paletteTexture;
    private uint terrainArray, terrainInstanceBuffer, terrainArtworkArray, terrainHeightArray;
    private uint framebuffer, artworkTexture, depthBuffer, photographTexture, overlayTexture;
    private int artWidth, artHeight, overlayWidth, overlayHeight;
    private PixelPalette? uploadedPalette;
    private long generation;
    private bool disposed;

    /// <summary>The implementation string of the active hardware or software GL driver.</summary>
    public string Driver { get; }

    /// <summary>Bytes submitted by the most recent draw, including the optional full-resolution overlay.</summary>
    public long LastUploadBytes { get; private set; }

    /// <summary>Draw calls submitted by the most recent frame, including the nearest-neighbor resolve.</summary>
    public int LastDrawCalls { get; private set; }

    /// <summary>CPU submission time. GPU completion and the host's framebuffer readback occur separately.</summary>
    public double LastSubmissionMilliseconds { get; private set; }

    /// <summary>Retained mesh, model and tile texture storage, excluding framebuffer attachments.</summary>
    public long ResidentBytes { get; private set; }

    public GlMapRenderer(GL gl)
    {
        this.gl = gl ?? throw new ArgumentNullException(nameof(gl));
        string version = gl.GetStringS(StringName.Version) ?? string.Empty;
        Driver = gl.GetStringS(StringName.Renderer) ?? version;
        embedded = version.Contains("OpenGL ES", StringComparison.Ordinal) || version.Contains("WebGL", StringComparison.Ordinal);
        VerifyVersion(version);
        maximumTextureSize = gl.GetInteger(GLEnum.MaxTextureSize);
        if (maximumTextureSize < GpuTerrainTexture.Size || gl.GetInteger(GLEnum.MaxVertexAttribs) < 6 ||
            gl.GetInteger(GLEnum.MaxArrayTextureLayers) < MaximumMeshes)
            throw new NotSupportedException("The GL context cannot support the map's texture or vertex layout.");
        var state = new GlState(gl, embedded);
        try
        {
            ConfigureTransferState();
            // Floating-point model and elevation textures require highp samplers too.
            // A highp arithmetic default alone still permits lowp texture lookups in GLSL ES.
            string header = embedded
                ? "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\nprecision highp sampler2DArray;\n"
                : "#version 330 core\n";
            terrainProgram = new(gl, header + GlMapShaders.Projection + GlMapShaders.TerrainVertex,
                header + GlMapShaders.TerrainFragment, "terrain");
            buildingProgram = new(gl, header + GlMapShaders.Projection + GlMapShaders.Models + GlMapShaders.BuildingVertex,
                header + GlMapShaders.Projection + GlMapShaders.BuildingFragment, "buildings");
            rimProgram = new(gl, header + GlMapShaders.Projection + GlMapShaders.Models + GlMapShaders.RimVertex,
                header + GlMapShaders.Projection + GlMapShaders.RimFragment, "roof edges");
            backgroundProgram = new(gl, header + GlMapShaders.FullScreenVertex,
                header + GlMapShaders.BackgroundFragment, "background");
            resolveProgram = new(gl, header + GlMapShaders.FullScreenVertex,
                header + GlMapShaders.ResolveFragment, "resolve");
            screenArray = gl.GenVertexArray();
            gl.BindVertexArray(screenArray);
            gridIndices = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gridIndices);
            fixed (ushort* indices = GpuTerrainMesh.Indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(GpuTerrainMesh.Indices.Length * sizeof(ushort)), indices, BufferUsageARB.StaticDraw);
            CreateTerrainArrays();
            CreateRoomTexture();
            paletteTexture = CreateTexture();
            CheckError("initializing the GPU map renderer");
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            state.Restore(gl);
        }
    }

    /// <summary>Draw an immutable prepared frame into the currently bound draw framebuffer.
    /// An optional BGRA8888 premultiplied overlay is composited at its full resolution.
    /// Its aspect ratio must match the logical viewport, including physical-pixel rounding.
    /// All modified GL state is restored before returning.</summary>
    public void Draw(GpuMapFrame frame, SKBitmap? overlay = null)
        => DrawCore(frame, overlay, false);

    /// <summary>Resolve exact art pixels, including overhead photograph blending, into
    /// an art-sized destination viewport. The caller can present that compact image with
    /// nearest-neighbor sampling and render full-resolution overlays on its Skia canvas.</summary>
    /// <remarks>The current viewport must be ceil(Width / PixelSize) by ceil(Height / PixelSize).
    /// The output is opaque and uses GL's bottom-left row origin. Present the image at
    /// its art dimensions multiplied by PixelSize and clip to the logical viewport so
    /// a partial final pixel column or row retains the original pixel grid.</remarks>
    public void DrawArtwork(GpuMapFrame frame)
        => DrawCore(frame, null, true);

    private void DrawCore(GpuMapFrame frame, SKBitmap? overlay, bool artworkOnly)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.ArtWidth <= 0 || frame.ArtHeight <= 0) return;
        if (frame.ArtWidth > maximumTextureSize || frame.ArtHeight > maximumTextureSize)
            throw new NotSupportedException("The pixel-art framebuffer exceeds the GPU texture limit.");
        if (frame.Terrain.Length > MaximumTerrainDraws)
            throw new NotSupportedException("The terrain frame exceeds the bounded GPU instance count.");
        long framebufferBytes = (long)frame.ArtWidth * frame.ArtHeight * 12 +
            (overlay is null ? 0 : (long)overlay.Width * overlay.Height * sizeof(uint));
        if (framebufferBytes > FramebufferBudget)
            throw new NotSupportedException("The map framebuffer exceeds the bounded GPU surface budget.");
        if (overlay is not null && (overlay.Width <= 0 || overlay.Height <= 0 ||
            Math.Abs((long)overlay.Width * frame.Height - (long)overlay.Height * frame.Width) > Math.Max(frame.Width, frame.Height) ||
            overlay.ColorType != SKColorType.Bgra8888 || overlay.AlphaType != SKAlphaType.Premul))
            throw new ArgumentException("The map overlay must match the viewport aspect ratio and use BGRA8888 premultiplied pixels.", nameof(overlay));
        long started = Stopwatch.GetTimestamp();
        generation++;
        LastUploadBytes = 0;
        LastDrawCalls = 0;
        foreach (var draw in frame.Terrain)
        {
            if (terrainMeshes.TryGetValue(draw.Mesh.ResourceKey, out var mesh)) mesh.Frame = generation;
            if (terrainTextures.TryGetValue(draw.Texture, out var texture)) texture.Frame = generation;
        }
        foreach (var draw in frame.Buildings)
            if (buildingMeshes.TryGetValue(draw.Mesh.ResourceKey, out var mesh)) mesh.Frame = generation;
        var state = new GlState(gl, embedded);
        if (artworkOnly && (state.ViewportWidth != frame.ArtWidth || state.ViewportHeight != frame.ArtHeight))
            throw new InvalidOperationException("The artwork destination viewport must match the frame's art-pixel dimensions.");
        bool completed = false;
        try
        {
            ConfigureTransferState();
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.CullFace);
            gl.Disable(EnableCap.ScissorTest);
            gl.Disable(EnableCap.StencilTest);
            gl.Disable(EnableCap.Dither);
            gl.Disable(EnableCap.RasterizerDiscard);
            gl.Disable(EnableCap.PolygonOffsetFill);
            gl.Disable(EnableCap.SampleAlphaToCoverage);
            if (!embedded) gl.Disable(EnableCap.FramebufferSrgb);
            gl.FrontFace(FrontFaceDirection.Ccw);
            gl.ColorMask(true, true, true, true);
            if (embedded) gl.DepthRange(0f, 1f); else gl.DepthRange(0d, 1d);
            EnsureFramebuffer(frame.ArtWidth, frame.ArtHeight);
            UploadPalette(frame.Palette);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.Viewport(0, 0, (uint)artWidth, (uint)artHeight);
            gl.Disable(EnableCap.DepthTest);
            gl.DepthMask(false);
            DrawBackground(frame);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthMask(true);
            gl.DepthFunc(DepthFunction.Lequal);
            if (embedded) gl.ClearDepth(1f); else gl.ClearDepth(1d);
            gl.Clear(ClearBufferMask.DepthBufferBit);
            DrawTerrain(frame);

            float photographOpacity = frame.PreservePhotograph
                ? (float)(209d / 255 * (1 - frame.Camera.Pitch / 30)) : 0;
            if (photographOpacity > 0)
            {
                BindTexture(0, photographTexture);
                gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)artWidth, (uint)artHeight);
            }
            DrawBuildings(frame);
            if (overlay is not null) UploadOverlay(overlay);
            Resolve(frame, state, photographOpacity, overlay is not null, artworkOnly);
            TrimCaches();
            CheckError("drawing the GPU map frame");
            completed = true;
        }
        finally
        {
            state.Restore(gl);
            if (!completed) DrainErrors();
            LastSubmissionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        CheckError("restoring the host GL state");
    }

    private void DrawBackground(GpuMapFrame frame)
    {
        var program = backgroundProgram!;
        program.Use();
        program.Vector2("uArtSize", artWidth, artHeight);
        program.Color("uGroundColor", frame.Palette.Ground);
        PixelStyle style = frame.Palette.Style;
        program.Color("uSkyColor", style == PixelStyle.Night ? 0xff273849u : style == PixelStyle.Dusk ? 0xffba969au : 0xff9bc1c8u);
        program.Color("uHorizonColor", style == PixelStyle.Night ? 0xff405264u : style == PixelStyle.Dusk ? 0xffd2aaa1u : 0xffc6d6cdu);
        float horizon = 0;
        if (frame.Camera.Perspective && frame.Camera.Pitch > 0)
            horizon = (float)((frame.Height * .5 + (MapCamera.Focal(frame.Height) * Math.Cos(frame.Camera.PitchRadians) *
                (1 / frame.Camera.FarLimit(frame.Height) - 1) + frame.Camera.TargetPixels / frame.Camera.FarLimit(frame.Height)) /
                Math.Sin(frame.Camera.PitchRadians)) / frame.PixelSize);
        program.Float("uHorizon", horizon);
        DrawFullScreen();
    }

    private void DrawTerrain(GpuMapFrame frame)
    {
        if (frame.Terrain.Length == 0) return;
        var program = terrainProgram!;
        program.Use();
        program.Integer("uArtwork", 0);
        program.Integer("uHeights", 1);
        program.Integer("uHypsometric", frame.TerrainOptions.Hypsometric ? 1 : 0);
        SetProjection(program, frame, frame.Terrain[0].Transform);
        int offset = 0;
        foreach (var draw in frame.Terrain)
        {
            var mesh = GetTerrainMesh(draw.Mesh);
            var texture = GetTerrainTexture(draw.Texture);
            var transform = draw.Transform;
            float zWeight = -transform.CosPitch * transform.InverseFocal;
            terrainInstances[offset++] = transform.XX;
            terrainInstances[offset++] = transform.XY;
            terrainInstances[offset++] = transform.CX * zWeight;
            terrainInstances[offset++] = transform.X;
            terrainInstances[offset++] = transform.YX;
            terrainInstances[offset++] = transform.YY;
            terrainInstances[offset++] = transform.CY * zWeight - transform.SinPitch;
            terrainInstances[offset++] = transform.Y;
            terrainInstances[offset++] = transform.PX;
            terrainInstances[offset++] = transform.PY;
            terrainInstances[offset++] = zWeight;
            terrainInstances[offset++] = transform.PW;
            terrainInstances[offset++] = texture.Layer;
            terrainInstances[offset++] = mesh.Layer;
        }
        gl.BindVertexArray(terrainArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, terrainInstanceBuffer);
        fixed (float* data = terrainInstances)
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(offset * sizeof(float)), data);
        LastUploadBytes += offset * sizeof(float);
        BindArrayTexture(0, terrainArtworkArray);
        BindArrayTexture(1, terrainHeightArray);
        gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)GpuTerrainMesh.Indices.Length,
            DrawElementsType.UnsignedShort, null, (uint)frame.Terrain.Length);
        LastDrawCalls++;
    }

    private void DrawBuildings(GpuMapFrame frame)
    {
        var program = buildingProgram!;
        program.Use();
        program.Integer("uModels", 0);
        program.Integer("uPalette", 1);
        program.Integer("uRooms", 2);
        BindTexture(1, paletteTexture);
        BindTexture(2, roomTexture);
        foreach (var draw in frame.Buildings)
        {
            var mesh = GetBuildingMesh(draw.Mesh);
            SetProjection(program, frame, draw.Transform);
            program.Float("uTileUnitsToMeters", draw.TileUnitsToMeters);
            program.Integer("uModelWidth", mesh.ModelWidth);
            BindTexture(0, mesh.ModelTexture);
            gl.BindVertexArray(mesh.Array);
            gl.DrawElements(PrimitiveType.Triangles, (uint)mesh.IndexCount, DrawElementsType.UnsignedInt, null);
            LastDrawCalls++;
        }

        program = rimProgram!;
        program.Use();
        program.Integer("uModels", 0);
        program.Integer("uPalette", 1);
        gl.DepthMask(false);
        foreach (var draw in frame.Buildings)
        {
            var mesh = buildingMeshes[draw.Mesh.ResourceKey];
            if (mesh.EdgeCount == 0) continue;
            SetProjection(program, frame, draw.Transform);
            program.Integer("uModelWidth", mesh.ModelWidth);
            BindTexture(0, mesh.ModelTexture);
            gl.BindVertexArray(mesh.EdgeArray);
            gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)mesh.EdgeCount);
            LastDrawCalls++;
        }
    }

    private void SetProjection(ShaderProgram program, GpuMapFrame frame, TileTransform transform)
    {
        float zWeight = -transform.CosPitch * transform.InverseFocal;
        program.Vector4("uScreenX", transform.XX, transform.XY, transform.CX * zWeight, transform.X);
        program.Vector4("uScreenY", transform.YX, transform.YY, transform.CY * zWeight - transform.SinPitch, transform.Y);
        program.Vector4("uScreenW", transform.PX, transform.PY, zWeight, transform.PW);
        program.Vector2("uArtSize", artWidth, artHeight);
        if (transform.Perspective)
        {
            float near = (float)MapCamera.Near, range = transform.Far - near;
            program.Vector2("uDepth", (transform.Far + near) / range, -2 * transform.Far * near / range);
        }
        else
        {
            float minimum = frame.MinimumAltitude - 1, maximum = frame.MaximumAltitude + 1;
            float range = Math.Max(1, maximum - minimum);
            program.Vector2("uDepth", -2 / range, (maximum + minimum) / range);
        }
        program.Vector3("uGround", (float)frame.Camera.GroundAltitudeMeters,
            frame.TerrainOptions.Relief ? (float)frame.TerrainOptions.HeightScale : 0, frame.PixelsPerMeter);
        program.Vector4("uLens", transform.SinPitch,
            transform.Perspective ? transform.TargetZ + transform.CosPitch / transform.InverseFocal : 0,
            transform.Far, 0);
        program.Integer("uPerspective", transform.Perspective ? 1 : 0);
    }

    private void Resolve(GpuMapFrame frame, GlState state, float photographOpacity, bool hasOverlay, bool artworkOnly)
    {
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)state.DrawFramebuffer);
        gl.Viewport(state.ViewportX, state.ViewportY, (uint)state.ViewportWidth, (uint)state.ViewportHeight);
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        var program = resolveProgram!;
        program.Use();
        program.Integer("uArtwork", 0);
        program.Integer("uPhotograph", 1);
        program.Integer("uOverlay", 2);
        program.Vector4("uViewport", state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight);
        program.Vector2("uLogicalSize", frame.Width, frame.Height);
        program.Integer2("uArtSize", artWidth, artHeight);
        program.Float("uPixelSize", frame.PixelSize);
        program.Float("uPhotoOpacity", photographOpacity);
        program.Integer("uHasOverlay", hasOverlay ? 1 : 0);
        program.Integer("uArtworkOnly", artworkOnly ? 1 : 0);
        BindTexture(0, artworkTexture);
        BindTexture(1, photographTexture);
        BindTexture(2, hasOverlay ? overlayTexture : roomTexture);
        DrawFullScreen();
    }

    private void DrawFullScreen()
    {
        gl.BindVertexArray(screenArray);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        LastDrawCalls++;
    }

    private void CreateTerrainArrays()
    {
        terrainArtworkArray = CreateArrayTexture();
        gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8,
            GpuTerrainTexture.Size, GpuTerrainTexture.Size, MaximumTerrainTextures, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        terrainHeightArray = CreateArrayTexture();
        gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.RG32f,
            GpuTerrainMesh.Side, GpuTerrainMesh.Side, MaximumMeshes, 0,
            PixelFormat.RG, PixelType.Float, null);
        terrainArray = gl.GenVertexArray();
        terrainInstanceBuffer = gl.GenBuffer();
        gl.BindVertexArray(terrainArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, terrainInstanceBuffer);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(terrainInstances.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gridIndices);
        for (uint attribute = 0; attribute < 4; attribute++)
        {
            gl.EnableVertexAttribArray(attribute);
            gl.VertexAttribPointer(attribute, attribute == 3 ? 2 : 4, VertexAttribPointerType.Float,
                false, TerrainInstanceStride * sizeof(float), (void*)(attribute * 4 * sizeof(float)));
            gl.VertexAttribDivisor(attribute, 1);
        }
        ResidentBytes += (long)GpuTerrainTexture.Size * GpuTerrainTexture.Size * MaximumTerrainTextures * sizeof(uint) +
            (long)GpuTerrainMesh.Side * GpuTerrainMesh.Side * MaximumMeshes * sizeof(float) * 2 +
            terrainInstances.Length * sizeof(float) + GpuTerrainMesh.Indices.Length * sizeof(ushort);
    }

    private MeshResource GetTerrainMesh(GpuTerrainMesh source)
    {
        if (!terrainMeshes.TryGetValue(source.ResourceKey, out var mesh))
        {
            while (terrainMeshLayers.Count == 0)
                if (!EvictOldest(0)) throw new NotSupportedException("The terrain mesh array has no available layer.");
            mesh = new MeshResource { Layer = terrainMeshLayers.Pop() };
            terrainMeshes.Add(source.ResourceKey, mesh);
        }
        mesh.Frame = generation;
        if (mesh.Revision != source.Revision)
        {
            for (int vertex = 0; vertex < GpuTerrainMesh.Side * GpuTerrainMesh.Side; vertex++)
            {
                terrainHeightUpload[vertex * 2] = source.Vertices[vertex * GpuTerrainMesh.VertexStride + 2];
                terrainHeightUpload[vertex * 2 + 1] = source.Vertices[vertex * GpuTerrainMesh.VertexStride + 3];
            }
            BindArrayTexture(1, terrainHeightArray);
            fixed (float* pixels = terrainHeightUpload)
                gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, mesh.Layer, GpuTerrainMesh.Side,
                    GpuTerrainMesh.Side, 1, PixelFormat.RG, PixelType.Float, pixels);
            LastUploadBytes += terrainHeightUpload.Length * sizeof(float);
            mesh.Revision = source.Revision;
        }
        return mesh;
    }

    private TextureResource GetTerrainTexture(GpuTerrainTexture source)
    {
        if (!terrainTextures.TryGetValue(source, out var texture))
        {
            while (terrainTextureLayers.Count == 0)
                if (!EvictOldest(2)) throw new NotSupportedException("The terrain artwork array has no available layer.");
            texture = new TextureResource { Layer = terrainTextureLayers.Pop() };
            terrainTextures.Add(source, texture);
            BindArrayTexture(0, terrainArtworkArray);
            fixed (uint* pixels = source.Texels)
                gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, texture.Layer,
                    GpuTerrainTexture.Size, GpuTerrainTexture.Size, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            LastUploadBytes += (long)GpuTerrainTexture.Size * GpuTerrainTexture.Size * sizeof(uint);
        }
        texture.Frame = generation;
        return texture;
    }

    private MeshResource GetBuildingMesh(GpuBuildingMesh source)
    {
        if (!buildingMeshes.TryGetValue(source.ResourceKey, out var mesh))
        {
            mesh = new MeshResource();
            buildingMeshes.Add(source.ResourceKey, mesh);
            mesh.Array = gl.GenVertexArray();
            mesh.Vertices = gl.GenBuffer();
            mesh.Indices = gl.GenBuffer();
            mesh.EdgeArray = gl.GenVertexArray();
            mesh.Edges = gl.GenBuffer();
            mesh.ModelTexture = CreateTexture();
            gl.BindVertexArray(mesh.Array);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vertices);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Indices);
            for (uint attribute = 0; attribute < 3; attribute++)
            {
                gl.EnableVertexAttribArray(attribute);
                gl.VertexAttribPointer(attribute, 4, VertexAttribPointerType.Float, false,
                    GpuBuildingMesh.VertexStride * sizeof(float), (void*)(attribute * 4 * sizeof(float)));
            }
            gl.BindVertexArray(mesh.EdgeArray);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Edges);
            for (uint attribute = 0; attribute < 6; attribute++)
            {
                gl.EnableVertexAttribArray(attribute);
                gl.VertexAttribPointer(attribute, 4, VertexAttribPointerType.Float, false,
                    GpuBuildingMesh.VertexStride * 2 * sizeof(float), (void*)(attribute * 4 * sizeof(float)));
                gl.VertexAttribDivisor(attribute, 1);
            }
        }
        mesh.Frame = generation;
        if (mesh.Revision != source.Revision)
        {
            gl.BindVertexArray(mesh.Array);
            UploadBuffer(mesh, BufferTargetARB.ArrayBuffer, mesh.Vertices, source.Vertices, ref mesh.VertexBytes);
            UploadBuffer(mesh, BufferTargetARB.ElementArrayBuffer, mesh.Indices, source.Indices, ref mesh.IndexBytes);
            gl.BindVertexArray(mesh.EdgeArray);
            UploadBuffer(mesh, BufferTargetARB.ArrayBuffer, mesh.Edges, source.RoofEdges, ref mesh.EdgeBytes);
            mesh.IndexCount = source.Indices.Length;
            mesh.EdgeCount = source.RoofEdges.Length / (GpuBuildingMesh.VertexStride * 2);
            mesh.Revision = source.Revision;
        }
        if (mesh.FoundationRevision != source.FoundationRevision)
        {
            UploadModels(mesh, source.Models);
            mesh.FoundationRevision = source.FoundationRevision;
        }
        return mesh;
    }

    private void UploadBuffer<T>(MeshResource mesh, BufferTargetARB target, uint buffer, T[] values, ref long capacity) where T : unmanaged
    {
        long bytes = (long)values.Length * sizeof(T);
        gl.BindBuffer(target, buffer);
        fixed (T* pointer = values)
        {
            if (capacity < bytes)
            {
                EnsureCacheBudget(bytes - capacity);
                gl.BufferData(target, (nuint)bytes, pointer, BufferUsageARB.DynamicDraw);
                ResidentBytes += bytes - capacity;
                mesh.Bytes += bytes - capacity;
                capacity = bytes;
            }
            else if (bytes > 0)
            {
                gl.BufferSubData(target, 0, (nuint)bytes, pointer);
            }
        }
        LastUploadBytes += bytes;
    }

    private void UploadModels(MeshResource mesh, float[] source)
    {
        int texels = Math.Max(2, (source.Length + 3) / 4);
        int width = Math.Min(maximumTextureSize, texels), height = (texels + width - 1) / width;
        if (height > maximumTextureSize) throw new NotSupportedException("The building model table exceeds the GPU texture limit.");
        long bytes = (long)width * height * 4 * sizeof(float);
        float[] pixels = source;
        if (source.Length != width * height * 4)
        {
            if (mesh.ModelUpload.Length != width * height * 4) mesh.ModelUpload = new float[width * height * 4];
            source.CopyTo(mesh.ModelUpload, 0);
            pixels = mesh.ModelUpload;
        }
        BindTexture(0, mesh.ModelTexture);
        fixed (float* pointer = pixels)
        {
            if (mesh.ModelWidth != width || mesh.ModelHeight != height)
            {
                EnsureCacheBudget(Math.Max(0, bytes - mesh.ModelBytes));
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)width, (uint)height,
                    0, PixelFormat.Rgba, PixelType.Float, pointer);
                ResidentBytes += bytes - mesh.ModelBytes;
                mesh.Bytes += bytes - mesh.ModelBytes;
                mesh.ModelBytes = bytes;
                mesh.ModelWidth = width;
                mesh.ModelHeight = height;
            }
            else
            {
                gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.Float, pointer);
            }
        }
        LastUploadBytes += bytes;
    }

    private void UploadPalette(PixelPalette palette)
    {
        if (ReferenceEquals(uploadedPalette, palette)) return;
        Span<uint> colors = stackalloc uint[palette.Materials.Length * 7];
        int index = 0;
        foreach (var material in palette.Materials)
        {
            colors[index++] = material.Roof;
            colors[index++] = material.LightWall;
            colors[index++] = material.DarkWall;
            colors[index++] = material.Rim;
            colors[index++] = material.Trim;
            colors[index++] = material.Glass;
            colors[index++] = material.Lamp;
        }
        BindTexture(0, paletteTexture);
        fixed (uint* pixels = colors)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 7, (uint)palette.Materials.Length,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        LastUploadBytes += colors.Length * sizeof(uint);
        uploadedPalette = palette;
    }

    private void CreateRoomTexture()
    {
        Span<byte> rooms = stackalloc byte[256];
        var random = new Random(859);
        for (int i = 0; i < rooms.Length; i += 2)
        {
            rooms[i] = (byte)random.Next(4);
            rooms[i + 1] = random.Next(3) == 0 ? (byte)random.Next(4) : rooms[i];
        }
        roomTexture = CreateTexture();
        BindTexture(0, roomTexture);
        fixed (byte* pixels = rooms)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, 256, 1, 0, PixelFormat.Red, PixelType.UnsignedByte, pixels);
    }

    private void UploadOverlay(SKBitmap overlay)
    {
        if (overlay.Width > maximumTextureSize || overlay.Height > maximumTextureSize)
            throw new NotSupportedException("The logical map overlay exceeds the GPU texture limit.");
        if (overlayTexture == 0) overlayTexture = CreateTexture();
        BindTexture(0, overlayTexture);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, overlay.RowBytes / sizeof(uint));
        if (overlayWidth != overlay.Width || overlayHeight != overlay.Height)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)overlay.Width, (uint)overlay.Height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)overlay.GetPixels());
            overlayWidth = overlay.Width;
            overlayHeight = overlay.Height;
        }
        else
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)overlay.Width, (uint)overlay.Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, (void*)overlay.GetPixels());
        }
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        LastUploadBytes += (long)overlay.Width * overlay.Height * sizeof(uint);
    }

    private void EnsureFramebuffer(int width, int height)
    {
        if (artWidth == width && artHeight == height) return;
        uint nextFramebuffer = 0, nextArtwork = 0, nextDepth = 0, nextPhotograph = 0;
        try
        {
            nextArtwork = CreateTexture();
            BindTexture(0, nextArtwork);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            nextPhotograph = CreateTexture();
            BindTexture(0, nextPhotograph);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            nextDepth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, nextDepth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
            nextFramebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, nextFramebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, nextArtwork, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, nextDepth);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new NotSupportedException("The GPU cannot allocate the map's RGBA8 and 24-bit depth framebuffer.");
            CheckError("allocating the pixel-art framebuffer");
            DeleteFramebuffer();
            framebuffer = nextFramebuffer;
            artworkTexture = nextArtwork;
            depthBuffer = nextDepth;
            photographTexture = nextPhotograph;
            artWidth = width;
            artHeight = height;
            nextFramebuffer = nextArtwork = nextDepth = nextPhotograph = 0;
        }
        finally
        {
            if (nextFramebuffer != 0) gl.DeleteFramebuffer(nextFramebuffer);
            if (nextArtwork != 0) gl.DeleteTexture(nextArtwork);
            if (nextDepth != 0) gl.DeleteRenderbuffer(nextDepth);
            if (nextPhotograph != 0) gl.DeleteTexture(nextPhotograph);
        }
    }

    private uint CreateTexture()
    {
        uint texture = gl.GenTexture();
        BindTexture(0, texture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private uint CreateArrayTexture()
    {
        uint texture = gl.GenTexture();
        BindArrayTexture(0, texture);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private void BindTexture(uint unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private void BindArrayTexture(uint unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        gl.BindTexture(TextureTarget.Texture2DArray, texture);
    }

    private void ConfigureTransferState()
    {
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        gl.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
        gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        gl.PixelStore(PixelStoreParameter.UnpackImageHeight, 0);
        gl.PixelStore(PixelStoreParameter.UnpackSkipImages, 0);
        for (uint unit = 0; unit < 3; unit++) gl.BindSampler(unit, 0);
    }

    private void EnsureCacheBudget(long additionalBytes)
    {
        while (ResidentBytes + additionalBytes > CacheBudget)
            if (!EvictOldest(1))
                throw new NotSupportedException("The visible map exceeds the bounded GPU geometry cache.");
    }

    private void TrimCaches()
    {
        while (buildingMeshes.Count > MaximumMeshes)
            if (!EvictOldest(1)) break;
    }

    private bool EvictOldest(int kind)
    {
        long oldest = generation;
        object? key = null;
        if (kind == 2)
        {
            foreach (var entry in terrainTextures)
                if (entry.Value.Frame < oldest) { oldest = entry.Value.Frame; key = entry.Key; }
        }
        else
        {
            foreach (var entry in kind == 0 ? terrainMeshes : buildingMeshes)
                if (entry.Value.Frame < oldest) { oldest = entry.Value.Frame; key = entry.Key; }
        }
        if (key is null) return false;
        if (kind == 2)
        {
            var texture = terrainTextures[(GpuTerrainTexture)key];
            terrainTextureLayers.Push(texture.Layer);
            terrainTextures.Remove((GpuTerrainTexture)key);
        }
        else
        {
            var cache = kind == 0 ? terrainMeshes : buildingMeshes;
            var mesh = cache[key];
            DeleteMesh(mesh);
            ResidentBytes -= mesh.Bytes;
            cache.Remove(key);
        }
        return true;
    }

    private void VerifyVersion(string version)
    {
        int start = version.IndexOf("OpenGL ES", StringComparison.Ordinal);
        string number = start >= 0 ? version[(start + 9)..] : version;
        int firstDigit = 0;
        while (firstDigit < number.Length && !char.IsAsciiDigit(number[firstDigit])) firstDigit++;
        int end = firstDigit;
        while (end < number.Length && (char.IsAsciiDigit(number[end]) || number[end] == '.')) end++;
        string[] parts = number[firstDigit..end].Split('.');
        bool valid = parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) &&
            (embedded ? major >= 3 || start < 0 && version.Contains("WebGL 2", StringComparison.Ordinal) : major > 3 || major == 3 && minor >= 3);
        if (!valid) throw new NotSupportedException($"GPU terrain requires WebGL 2, OpenGL ES 3.0 or desktop OpenGL 3.3. Received '{version}'.");
    }

    private void CheckError(string operation)
    {
        GLEnum first = gl.GetError();
        if (first == GLEnum.NoError) return;
        DrainErrors();
        throw new InvalidOperationException($"OpenGL error {first} while {operation}.");
    }

    private void DrainErrors()
    {
        for (int remaining = 32; remaining > 0 && gl.GetError() != GLEnum.NoError; remaining--) { }
    }

    private void DeleteMesh(MeshResource mesh)
    {
        if (mesh.Layer >= 0) terrainMeshLayers.Push(mesh.Layer);
        if (mesh.Array != 0) gl.DeleteVertexArray(mesh.Array);
        if (mesh.EdgeArray != 0) gl.DeleteVertexArray(mesh.EdgeArray);
        if (mesh.Vertices != 0) gl.DeleteBuffer(mesh.Vertices);
        if (mesh.Indices != 0) gl.DeleteBuffer(mesh.Indices);
        if (mesh.Edges != 0) gl.DeleteBuffer(mesh.Edges);
        if (mesh.ModelTexture != 0) gl.DeleteTexture(mesh.ModelTexture);
    }

    private void DeleteFramebuffer()
    {
        if (framebuffer != 0) gl.DeleteFramebuffer(framebuffer);
        if (artworkTexture != 0) gl.DeleteTexture(artworkTexture);
        if (depthBuffer != 0) gl.DeleteRenderbuffer(depthBuffer);
        if (photographTexture != 0) gl.DeleteTexture(photographTexture);
        framebuffer = artworkTexture = depthBuffer = photographTexture = 0;
        artWidth = artHeight = 0;
    }

    /// <summary>Release all context-owned resources while the originating GL context is current.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var mesh in terrainMeshes.Values) DeleteMesh(mesh);
        foreach (var mesh in buildingMeshes.Values) DeleteMesh(mesh);
        terrainMeshes.Clear(); buildingMeshes.Clear(); terrainTextures.Clear();
        DeleteFramebuffer();
        if (overlayTexture != 0) gl.DeleteTexture(overlayTexture);
        if (paletteTexture != 0) gl.DeleteTexture(paletteTexture);
        if (roomTexture != 0) gl.DeleteTexture(roomTexture);
        if (gridIndices != 0) gl.DeleteBuffer(gridIndices);
        if (screenArray != 0) gl.DeleteVertexArray(screenArray);
        if (terrainArray != 0) gl.DeleteVertexArray(terrainArray);
        if (terrainInstanceBuffer != 0) gl.DeleteBuffer(terrainInstanceBuffer);
        if (terrainArtworkArray != 0) gl.DeleteTexture(terrainArtworkArray);
        if (terrainHeightArray != 0) gl.DeleteTexture(terrainHeightArray);
        terrainProgram?.Dispose(); buildingProgram?.Dispose(); rimProgram?.Dispose();
        backgroundProgram?.Dispose(); resolveProgram?.Dispose();
        ResidentBytes = 0;
    }

    private sealed class MeshResource
    {
        internal uint Array, Vertices, Indices, EdgeArray, Edges, ModelTexture;
        internal long Frame, Bytes, VertexBytes, IndexBytes, EdgeBytes, ModelBytes;
        internal long Revision = long.MinValue, FoundationRevision = long.MinValue;
        internal int IndexCount, EdgeCount, ModelWidth, ModelHeight;
        internal int Layer = -1;
        internal float[] ModelUpload = [];
    }

    private sealed class TextureResource
    {
        internal int Layer;
        internal long Frame;
    }

    private sealed class ShaderProgram : IDisposable
    {
        private readonly GL gl;
        private readonly Dictionary<string, int> uniforms = [];
        private uint program;

        internal ShaderProgram(GL gl, string vertex, string fragment, string name)
        {
            this.gl = gl;
            uint vertexShader = 0, fragmentShader = 0;
            try
            {
                vertexShader = Compile(ShaderType.VertexShader, vertex, name);
                fragmentShader = Compile(ShaderType.FragmentShader, fragment, name);
                program = gl.CreateProgram();
                gl.AttachShader(program, vertexShader);
                gl.AttachShader(program, fragmentShader);
                gl.LinkProgram(program);
                gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
                if (linked == 0) throw new NotSupportedException($"The {name} GPU program could not link. {gl.GetProgramInfoLog(program)}");
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (vertexShader != 0) gl.DeleteShader(vertexShader);
                if (fragmentShader != 0) gl.DeleteShader(fragmentShader);
            }
        }

        private uint Compile(ShaderType type, string source, string name)
        {
            uint shader = gl.CreateShader(type);
            try
            {
                gl.ShaderSource(shader, source);
                gl.CompileShader(shader);
                gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
                if (compiled == 0) throw new NotSupportedException($"The {name} {type} shader could not compile. {gl.GetShaderInfoLog(shader)}");
                return shader;
            }
            catch
            {
                gl.DeleteShader(shader);
                throw;
            }
        }

        private int Location(string name)
        {
            if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
            return location;
        }

        internal void Use() => gl.UseProgram(program);
        internal void Float(string name, float value) => gl.Uniform1(Location(name), value);
        internal void Integer(string name, int value) => gl.Uniform1(Location(name), value);
        internal void Integer2(string name, int x, int y) => gl.Uniform2(Location(name), x, y);
        internal void Vector2(string name, float x, float y) => gl.Uniform2(Location(name), x, y);
        internal void Vector3(string name, float x, float y, float z) => gl.Uniform3(Location(name), x, y, z);
        internal void Vector4(string name, float x, float y, float z, float w) => gl.Uniform4(Location(name), x, y, z, w);
        internal void Color(string name, uint color) => Vector3(name, ((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f);
        public void Dispose()
        {
            if (program != 0) gl.DeleteProgram(program);
            program = 0;
        }
    }

    private readonly struct GlState
    {
        internal readonly int DrawFramebuffer, ViewportX, ViewportY, ViewportWidth, ViewportHeight;
        private readonly int readFramebuffer, program, array, vertexBuffer, unpackBuffer, renderbuffer;
        private readonly int activeTexture, texture0, texture1, texture2, sampler0, sampler1, sampler2;
        private readonly int textureArray0, textureArray1;
        private readonly int unpackAlignment, unpackRowLength, unpackSkipRows, unpackSkipPixels, unpackImageHeight, unpackSkipImages;
        private readonly int depthFunction, frontFace;
        private readonly bool blend, cull, scissor, stencil, depth, dither, rasterizerDiscard, polygonOffset, alphaCoverage;
        private readonly bool depthMask, redMask, greenMask, blueMask, alphaMask, embedded, framebufferSrgb;
        private readonly double depthClear, depthNear, depthFar;

        internal GlState(GL gl, bool embedded)
        {
            this.embedded = embedded;
            DrawFramebuffer = gl.GetInteger(GLEnum.DrawFramebufferBinding);
            readFramebuffer = gl.GetInteger(GLEnum.ReadFramebufferBinding);
            int* viewport = stackalloc int[4];
            gl.GetInteger(GetPName.Viewport, viewport);
            ViewportX = viewport[0]; ViewportY = viewport[1]; ViewportWidth = viewport[2]; ViewportHeight = viewport[3];
            program = gl.GetInteger(GLEnum.CurrentProgram);
            array = gl.GetInteger(GLEnum.VertexArrayBinding);
            vertexBuffer = gl.GetInteger(GLEnum.ArrayBufferBinding);
            unpackBuffer = gl.GetInteger(GLEnum.PixelUnpackBufferBinding);
            renderbuffer = gl.GetInteger(GLEnum.RenderbufferBinding);
            activeTexture = gl.GetInteger(GLEnum.ActiveTexture);
            gl.ActiveTexture(TextureUnit.Texture0);
            texture0 = gl.GetInteger(GLEnum.TextureBinding2D); sampler0 = gl.GetInteger(GLEnum.SamplerBinding);
            textureArray0 = gl.GetInteger(GLEnum.TextureBinding2DArray);
            gl.ActiveTexture(TextureUnit.Texture1);
            texture1 = gl.GetInteger(GLEnum.TextureBinding2D); sampler1 = gl.GetInteger(GLEnum.SamplerBinding);
            textureArray1 = gl.GetInteger(GLEnum.TextureBinding2DArray);
            gl.ActiveTexture(TextureUnit.Texture2);
            texture2 = gl.GetInteger(GLEnum.TextureBinding2D); sampler2 = gl.GetInteger(GLEnum.SamplerBinding);
            gl.ActiveTexture((TextureUnit)activeTexture);
            unpackAlignment = gl.GetInteger(GLEnum.UnpackAlignment);
            unpackRowLength = gl.GetInteger(GLEnum.UnpackRowLength);
            unpackSkipRows = gl.GetInteger(GLEnum.UnpackSkipRows);
            unpackSkipPixels = gl.GetInteger(GLEnum.UnpackSkipPixels);
            unpackImageHeight = gl.GetInteger(GLEnum.UnpackImageHeight);
            unpackSkipImages = gl.GetInteger(GLEnum.UnpackSkipImages);
            depthFunction = gl.GetInteger(GLEnum.DepthFunc);
            frontFace = gl.GetInteger(GLEnum.FrontFace);
            blend = gl.IsEnabled(EnableCap.Blend); cull = gl.IsEnabled(EnableCap.CullFace);
            scissor = gl.IsEnabled(EnableCap.ScissorTest); stencil = gl.IsEnabled(EnableCap.StencilTest);
            depth = gl.IsEnabled(EnableCap.DepthTest); dither = gl.IsEnabled(EnableCap.Dither);
            rasterizerDiscard = gl.IsEnabled(EnableCap.RasterizerDiscard); polygonOffset = gl.IsEnabled(EnableCap.PolygonOffsetFill);
            alphaCoverage = gl.IsEnabled(EnableCap.SampleAlphaToCoverage);
            framebufferSrgb = !embedded && gl.IsEnabled(EnableCap.FramebufferSrgb);
            depthMask = gl.GetBoolean(GLEnum.DepthWritemask);
            bool* mask = stackalloc bool[4];
            gl.GetBoolean(GetPName.ColorWritemask, mask);
            redMask = mask[0]; greenMask = mask[1]; blueMask = mask[2]; alphaMask = mask[3];
            if (embedded)
            {
                float* range = stackalloc float[2];
                gl.GetFloat(GetPName.DepthRange, range);
                depthNear = range[0]; depthFar = range[1];
                gl.GetFloat(GetPName.DepthClearValue, out float clear);
                depthClear = clear;
            }
            else
            {
                double* range = stackalloc double[2];
                gl.GetDouble(GetPName.DepthRange, range);
                depthNear = range[0]; depthFar = range[1];
                gl.GetDouble(GetPName.DepthClearValue, out depthClear);
            }
        }

        internal void Restore(GL gl)
        {
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)DrawFramebuffer);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)readFramebuffer);
            gl.Viewport(ViewportX, ViewportY, (uint)ViewportWidth, (uint)ViewportHeight);
            gl.UseProgram((uint)program);
            gl.BindVertexArray((uint)array);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)vertexBuffer);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, (uint)unpackBuffer);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)renderbuffer);
            gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, (uint)texture0); gl.BindSampler(0, (uint)sampler0);
            gl.BindTexture(TextureTarget.Texture2DArray, (uint)textureArray0);
            gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, (uint)texture1); gl.BindSampler(1, (uint)sampler1);
            gl.BindTexture(TextureTarget.Texture2DArray, (uint)textureArray1);
            gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, (uint)texture2); gl.BindSampler(2, (uint)sampler2);
            gl.ActiveTexture((TextureUnit)activeTexture);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, unpackAlignment);
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, unpackRowLength);
            gl.PixelStore(PixelStoreParameter.UnpackSkipRows, unpackSkipRows);
            gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, unpackSkipPixels);
            gl.PixelStore(PixelStoreParameter.UnpackImageHeight, unpackImageHeight);
            gl.PixelStore(PixelStoreParameter.UnpackSkipImages, unpackSkipImages);
            gl.DepthFunc((DepthFunction)depthFunction);
            gl.FrontFace((FrontFaceDirection)frontFace);
            gl.DepthMask(depthMask);
            gl.ColorMask(redMask, greenMask, blueMask, alphaMask);
            if (embedded)
            {
                gl.DepthRange((float)depthNear, (float)depthFar);
                gl.ClearDepth((float)depthClear);
            }
            else
            {
                gl.DepthRange(depthNear, depthFar);
                gl.ClearDepth(depthClear);
            }
            Set(gl, EnableCap.Blend, blend); Set(gl, EnableCap.CullFace, cull);
            Set(gl, EnableCap.ScissorTest, scissor); Set(gl, EnableCap.StencilTest, stencil);
            Set(gl, EnableCap.DepthTest, depth); Set(gl, EnableCap.Dither, dither);
            Set(gl, EnableCap.RasterizerDiscard, rasterizerDiscard); Set(gl, EnableCap.PolygonOffsetFill, polygonOffset);
            Set(gl, EnableCap.SampleAlphaToCoverage, alphaCoverage);
            if (!embedded) Set(gl, EnableCap.FramebufferSrgb, framebufferSrgb);
        }

        private static void Set(GL gl, EnableCap capability, bool enabled)
        {
            if (enabled) gl.Enable(capability); else gl.Disable(capability);
        }
    }
}
