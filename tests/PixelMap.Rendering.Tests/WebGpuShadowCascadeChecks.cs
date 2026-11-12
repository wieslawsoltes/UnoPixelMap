using System.Numerics;
using PixelMap.Data;
using PixelMap.Rendering;

/// <summary>CPU geometry checks for the production shadow fits. MapCamera remains
/// the independent projection oracle; these checks do not emulate GPU rasterization.</summary>
internal static class WebGpuShadowCascadeChecks
{
    private static readonly Vector3 DaySun = Vector3.Normalize(new(-.45f, -.55f, .82f));
    private static readonly Vector3 DuskSun = Vector3.Normalize(new(-.45f, -.55f, .35f));
    private static readonly GpuTerrainTexture Texture = new(new uint[GpuTerrainTexture.Size * GpuTerrainTexture.Size]);

    internal static void Run()
    {
        int fits = ProjectionAndOverlap();
        LoadedCasterDepth();
        ArtworkUnits();
        StableWorldTexels();
        NearCameraResolution();
        QualityNeverReducesDensity();
        StableNorthSouthTexels();
        Console.WriteLine($"Passed {fits} WebGPU shadow fits: physical/map perspective and orthographic projections, overlapping receivers, complete offscreen caster depth, artwork units, stable geographic texels, monotonic quality and packed affine uniforms.");
    }

    private static int ProjectionAndOverlap()
    {
        int fits = 0;
        foreach (var viewport in new[] { (Width: 1200, Height: 780), (Width: 390, Height: 844) })
        foreach (var camera in Cameras(viewport.Width, viewport.Height))
        foreach (var sun in new[] { DaySun, DuskSun })
        {
            using var fixture = new Fixture(camera, viewport.Width, viewport.Height, pixelSize: 1);
            foreach (int count in new[] { 1, 2, 3, 4 })
            {
                var fit = WebGpuShadowCascades.Create(fixture.Frame, sun, count, 1536, 1800);
                CheckUniformPacking(fixture, fit);
                Assert(fit.Count == count, "A shadow fit changed the requested bounded cascade count.");
                Assert(float.IsFinite(fit.Near) && fit.Layers[^1].Far - fit.Near <= 1800.01f,
                    "The fitted receiver interval exceeded its world-metre budget.");
                var forward = CameraForward(camera);
                Near(new Vector3(fit.View.X, fit.View.Y, fit.View.Z), forward, .000002f,
                    "Cascade selection uses a different viewing direction from MapCamera.");
                if (camera.Perspective)
                {
                    var publicEye = camera.GetEye(viewport.Height);
                    Near(fit.Eye, WorldPosition(camera, publicEye.Latitude, publicEye.Longitude,
                        publicEye.AltitudeMeters), .005f, "Shadow and public physical camera eyes disagree.");
                    Near(fit.Eye, WebGpuShadowCascades.CameraEye(fixture.Frame), .000001f,
                        "The shadow fit and lighting helpers disagree about the eye.");
                }

                for (int layerIndex = 0; layerIndex < count; layerIndex++)
                {
                    var layer = fit.Layers[layerIndex];
                    Assert(float.IsFinite(layer.Far) && layer.Far > layer.Near && layer.FitNear <= layer.Near,
                        "A cascade has an empty, reversed or non-finite receiver interval.");
                    Assert(layer.BlendStart >= layer.Near && layer.BlendStart < layer.Far,
                        "The cascade blend is outside its selected depth interval.");
                    Assert(layer.TexelSize > 0 && layer.InverseDepthRange > 0 &&
                        float.IsFinite(layer.TexelSize) && float.IsFinite(layer.InverseDepthRange),
                        "Shadow texel or depth units are not finite positive world-metre quantities.");
                    CheckBiasUnits(layer, sun, 1536);
                    foreach (float depth in new[] { layer.FitNear, (layer.FitNear + layer.Far) * .5f,
                        layer.BlendStart, layer.Far })
                    foreach (float x in new[] { -1f, 0, 1 })
                    foreach (float y in new[] { -1f, 0, 1 })
                    {
                        var point = ReceiverPoint(fixture.Frame, fit, depth, x, y);
                        CheckCameraProjection(fixture.Frame, point, x, y);
                        float measuredDepth = Vector4.Dot(new(point, 1), fit.View);
                        Near(measuredDepth, depth, Math.Max(.003f, Math.Abs(depth) * .000003f),
                            "Cascade selection depth disagrees with the map projection.");
                        Inside(layer.Matrix, point, includeXY: true,
                            $"A receiver escaped cascade {layerIndex} ({camera.Perspective}, pitch {camera.Pitch}, depth {depth}).");
                    }

                    if (layerIndex + 1 < count)
                    {
                        var next = fit.Layers[layerIndex + 1];
                        Near(layer.Far, next.Near, .00001f, "Cascade selection has a gap or reordered split.");
                        Assert(next.FitNear <= layer.BlendStart + .00001f,
                            "The next cascade does not cover the complete cross-fade interval.");
                        foreach (float fraction in new[] { 0f, .5f, 1f })
                        foreach (float x in new[] { -1f, 1 })
                        foreach (float y in new[] { -1f, 1 })
                        {
                            float depth = layer.BlendStart + (layer.Far - layer.BlendStart) * fraction;
                            var point = ReceiverPoint(fixture.Frame, fit, depth, x, y);
                            Inside(layer.Matrix, point, true, "The outgoing cascade clipped its cross-fade receivers.");
                            Inside(next.Matrix, point, true, "The incoming cascade clipped its cross-fade receivers.");
                        }
                    }
                }
                fits++;
            }
        }
        return fits;
    }

    private static IEnumerable<MapCamera> Cameras(int width, int height)
    {
        foreach (bool perspective in new[] { false, true })
        foreach (double bearing in new[] { -145d, 0, 67 })
        foreach (double pitch in new[] { 0d, 55, 82 })
            yield return new(40.7087, -74.0104, 16.5, bearing, pitch, perspective, 180)
                { GroundAltitudeMeters = 1800 };
        foreach (double pitch in new[] { 0d, 55, 90, 115, 179.5 })
            yield return MapCamera.FromEye(40.7087, -74.0104, 1802, 67, pitch, width, height,
                viewDistanceMeters: 2500, groundAltitudeMeters: 1800);
        yield return MapCamera.FromEye(80, 179.9999, -318, -145, 88, width, height,
            viewDistanceMeters: 800, groundAltitudeMeters: -320);
        yield return new(-80, -179.9999, 13, 130, 75, true, 400) { GroundAltitudeMeters = -320 };
    }

    private static void LoadedCasterDepth()
    {
        foreach (double ground in new[] { -450d, 3200 })
        foreach (double exaggeration in new[] { 1d, 4 })
        foreach (int pixelSize in new[] { 1, 4 })
        {
            var camera = MapCamera.FromEye(45.875, 10.85, ground + 2, 125, 89, 1200, 780,
                groundAltitudeMeters: ground);
            using var fixture = new Fixture(camera, 1200, 780, pixelSize, exaggeration, building: true);
            var draw = fixture.Frame.Buildings.Single();
            Assert(draw.Mesh.Indices.Length > 0 && draw.Selection is { Indices.Length: 0 },
                "The caster fixture must retain a real model excluded from all viewer indices.");
            var fit = WebGpuShadowCascades.Create(fixture.Frame, DuskSun, 4, 2048, 2400);
            CheckUniformPacking(fixture, fit);
            foreach (var point in fixture.CasterCorners)
            {
                Assert(point.X >= fit.Bounds.Minimum.X - .01f && point.X <= fit.Bounds.Maximum.X + .01f &&
                    point.Y >= fit.Bounds.Minimum.Y - .01f && point.Y <= fit.Bounds.Maximum.Y + .01f &&
                    point.Z >= fit.Bounds.Minimum.Z - .01f && point.Z <= fit.Bounds.Maximum.Z + .01f,
                    "Loaded caster bounds lost a signed/exaggerated height or a viewer-rejected model.");
                foreach (var layer in fit.Layers)
                    Inside(layer.Matrix, point, includeXY: false,
                        "A loaded offscreen caster was clipped by the light's depth interval.");
            }
        }
    }

    private static void ArtworkUnits()
    {
        var camera = MapCamera.FromEye(40.7087, -74.0104, 72, 37, 88, 1200, 780,
            groundAltitudeMeters: 70);
        using var one = new Fixture(camera, 1200, 780, pixelSize: 1);
        using var four = new Fixture(camera, 1200, 780, pixelSize: 4);
        var a = WebGpuShadowCascades.Create(one.Frame, DaySun, 4, 2048, 2400);
        var b = WebGpuShadowCascades.Create(four.Frame, DaySun, 4, 2048, 2400);
        Near(a.Eye, b.Eye, .000001f, "Artwork resolution changed the physical eye.");
        Near(a.Bounds.Minimum, b.Bounds.Minimum, .00001f, "Art-pixel minimum altitude was treated as metres.");
        Near(a.Bounds.Maximum, b.Bounds.Maximum, .00001f, "Art-pixel maximum altitude was treated as metres.");
        for (int i = 0; i < a.Count; i++)
        {
            Near(a.Layers[i].Near, b.Layers[i].Near, .00001f, "Artwork resolution changed the shadow split.");
            Near(a.Layers[i].Far, b.Layers[i].Far, .00001f, "Artwork resolution changed the shadow split.");
            Near(a.Layers[i].TexelSize, b.Layers[i].TexelSize, .000001f,
                "Artwork resolution changed the physical shadow texel size.");
            foreach (var point in one.CasterCorners)
                Near(Vector3.Transform(point, a.Layers[i].Matrix), Vector3.Transform(point, b.Layers[i].Matrix),
                    .00001f, "Artwork resolution changed the light projection.");
        }
    }

    private static void StableWorldTexels()
    {
        const int resolution = 2048, width = 1200, height = 780;
        foreach (bool perspective in new[] { false, true })
        {
            var camera = perspective
                ? MapCamera.FromEye(40.7087, -74.0104, 2, 67, 85, width, height)
                : new MapCamera(40.7087, -74.0104, 17, 67, 55);
            using var initial = new Fixture(camera, width, height, 1);
            var baseline = WebGpuShadowCascades.Create(initial.Frame, DaySun, 4, resolution, 2400);
            var point = ReceiverPoint(initial.Frame, baseline,
                (baseline.Layers[0].Near + baseline.Layers[0].Far) * .5f, 0, 0);
            var previous = ShadowTexel(baseline.Layers[0].Matrix, point, resolution);
            int unchanged = 0, moved = 0;
            for (int step = 1; step <= 32; step++)
            {
                double east = step * baseline.Layers[0].TexelSize * .125;
                var shifted = camera with { Longitude = camera.Longitude + east / Meters(camera) * 360 };
                using var current = new Fixture(shifted, width, height, 1);
                var fit = WebGpuShadowCascades.Create(current.Frame, DaySun, 4, resolution, 2400);
                Near(fit.Layers[0].Radius, baseline.Layers[0].Radius, .00001f,
                    "Subtexel camera translation changed the fitted radius.");
                double actualEast = (shifted.CenterX - camera.CenterX) * Meters(camera);
                var texel = ShadowTexel(fit.Layers[0].Matrix, point - new Vector3((float)actualEast, 0, 0), resolution);
                var delta = texel - previous;
                Near(delta.X, MathF.Round(delta.X), .004f,
                    "A fixed geographic point drifted across fractional shadow texels during panning.");
                Near(delta.Y, MathF.Round(delta.Y), .004f,
                    "A fixed geographic point drifted across fractional shadow texels during panning.");
                if (delta.Length() < .01f) unchanged++; else moved++;
                previous = texel;
            }
            Assert(unchanged > 0 && moved > 0, "The stabilization fixture must exercise both retained and crossed texels.");
        }
    }

    private static void NearCameraResolution()
    {
        var camera = MapCamera.FromEye(40.7087, -74.0104, 2, -15, 85, 1200, 780);
        using var fixture = new Fixture(camera, 1200, 780, 1);
        var high = WebGpuShadowCascades.Create(fixture.Frame, DaySun, 2, 1536, 1800);
        var ultra = WebGpuShadowCascades.Create(fixture.Frame, DaySun, 4, 2048, 2400);
        float previousRadius = Math.Clamp(1200 / fixture.Frame.PixelsPerMeter * .9f, 120, 1800);
        float previousTexel = 2 * previousRadius / 1536;
        Assert(high.Layers[0].Far <= 25 && ultra.Layers[0].Far >= high.Layers[0].Far &&
            ultra.Layers[0].Far <= 25 && ultra.Layers[1].Far <= 65,
            "Ultra did not preserve High's nearest receiver interval and cover nearby facades separately.");
        Assert(high.Layers[0].TexelSize < previousTexel / 3 &&
            ultra.Layers[0].TexelSize < high.Layers[0].TexelSize * .75f,
            "The near cascades did not improve facade sampling density over the previous single fit.");
        Console.WriteLine($"Shadow near-camera world texel: previous {previousTexel:F4} m, High {high.Layers[0].TexelSize:F4} m, Ultra {ultra.Layers[0].TexelSize:F4} m (projection density, not measured GPU quality/performance).");
        var highFacade = SamplingDensity(high, 49);
        var ultraFacade = SamplingDensity(ultra, 49);
        Assert(ultraFacade.Selected < highFacade.Selected / 3,
            "Ultra's middle cascade did not improve the captured 49-metre facade's shadow sampling.");
        Console.WriteLine($"Shadow sampling at 49 m: High {highFacade.Selected:F4} m/texel, Ultra {ultraFacade.Selected:F4} m/texel.");
    }

    private static void QualityNeverReducesDensity()
    {
        var highOptions = WebGpuRenderOptions.For(WebGpuQuality.High);
        var ultraOptions = WebGpuRenderOptions.For(WebGpuQuality.Ultra);
        Assert(highOptions.ShadowCascades == 2 && highOptions.ShadowSize == 1536 &&
            ultraOptions.ShadowCascades == 4 && ultraOptions.ShadowSize == 2048,
            "The density regression must exercise the production High and Ultra presets.");
        int samples = 0;
        foreach (var viewport in new[] { (Width: 1200, Height: 780), (Width: 390, Height: 844) })
        foreach (var camera in Cameras(viewport.Width, viewport.Height))
        {
            using var fixture = new Fixture(camera, viewport.Width, viewport.Height, 1);
            var high = WebGpuShadowCascades.Create(fixture.Frame, DaySun, highOptions.ShadowCascades,
                highOptions.ShadowSize, highOptions.ShadowDistance);
            var ultra = WebGpuShadowCascades.Create(fixture.Frame, DaySun, ultraOptions.ShadowCascades,
                ultraOptions.ShadowSize, ultraOptions.ShadowDistance);
            Near(ultra.Near, high.Near, .0001f,
                "Changing quality moved the beginning of the shared receiver interval.");
            Assert(ultra.Layers[^1].Far >= high.Layers[^1].Far,
                "Ultra discarded receivers shadowed by High.");
            Assert(ultra.Layers[0].Far >= high.Layers[0].Far,
                "Ultra moved a High near receiver into a coarser layer.");

            var depths = new SortedSet<float>();
            float near = high.Near, far = high.Layers[^1].Far;
            for (int i = 0; i <= 512; i++) depths.Add(near + (far - near) * i / 512);
            // Include both sides of every selection and cross-fade boundary,
            // including values less than one ordinary sample apart.
            foreach (var fit in new[] { high, ultra })
            foreach (var layer in fit.Layers)
            {
                foreach (float boundary in new[] { layer.FitNear, layer.Near, layer.BlendStart, layer.Far })
                {
                    depths.Add(MathF.BitDecrement(boundary));
                    depths.Add(boundary);
                    depths.Add(MathF.BitIncrement(boundary));
                }
                for (int i = 1; i < 4; i++)
                    depths.Add(layer.BlendStart + (layer.Far - layer.BlendStart) * i / 4);
            }
            foreach (float depth in depths)
            {
                if (depth < near || depth > far) continue;
                var h = SamplingDensity(high, depth);
                var u = SamplingDensity(ultra, depth);
                float tolerance = Math.Max(.000001f, h.Support * .00001f);
                Assert(u.Selected <= h.Selected + tolerance && u.Support <= h.Support + tolerance,
                    $"Ultra reduced shadow density at depth {depth:F6} m ({camera.Perspective}, pitch {camera.Pitch}, " +
                    $"viewport {viewport.Width}x{viewport.Height}). High selected/support={h}, Ultra={u}.");
                samples++;
            }
        }
        Console.WriteLine($"Passed {samples} High-to-Ultra receiver-depth comparisons, including both sides of every split and cross-fade boundary.");
    }

    // Compare both the selected texel and the coarsest map contributing to a
    // cross-fade. This bound is independent of the shader's interpolation curve.
    private static (float Selected, float Support) SamplingDensity(WebGpuShadowCascades fit, float depth)
    {
        int index = 0;
        while (index + 1 < fit.Count && depth > fit.Layers[index].Far) index++;
        var layer = fit.Layers[index];
        float support = layer.TexelSize;
        if (index + 1 < fit.Count && depth > layer.BlendStart)
            support = Math.Max(support, fit.Layers[index + 1].TexelSize);
        return (layer.TexelSize, support);
    }

    private static void CheckUniformPacking(Fixture fixture, WebGpuShadowCascades fit)
    {
        Span<float> frame = stackalloc float[128];
        Span<bool> written = stackalloc bool[128];
        written.Clear();
        // Explicit ABI locations, independent of the packer's offset expression.
        ReadOnlySpan<int> rows = [14, 15, 16, 17, 21, 22, 23, 24, 25, 26, 27, 28];
        for (int i = 0; i < frame.Length; i++) frame[i] = -1000.125f - i;
        fit.WriteUniforms(frame);
        for (int i = 0; i < fit.Count; i++)
        {
            var layer = fit.Layers[i];
            var x = Row(frame, rows[i * 3]);
            var y = Row(frame, rows[i * 3 + 1]);
            var z = Row(frame, rows[i * 3 + 2]);
            foreach (var point in fixture.CasterCorners)
                CheckPoint(point, layer.Matrix, x, y, z);
            foreach (float depth in new[] { layer.FitNear, layer.Far })
            {
                CheckPoint(ReceiverPoint(fixture.Frame, fit, depth, -1, 1), layer.Matrix, x, y, z);
                CheckPoint(ReceiverPoint(fixture.Frame, fit, depth, 1, -1), layer.Matrix, x, y, z);
            }
            for (int j = 0; j < 3; j++)
                for (int component = 0; component < 4; component++) written[rows[i * 3 + j] * 4 + component] = true;
            Near(frame[29 * 4 + i], layer.Far, 0, "The packed shadow split changed units or ordering.");
            Near(frame[30 * 4 + i], layer.TexelSize, 0, "The packed texel size disagrees with the shadow matrix.");
            Near(frame[31 * 4 + i], layer.InverseDepthRange, 0, "The packed depth bias scale disagrees with the shadow matrix.");
            written[29 * 4 + i] = written[30 * 4 + i] = written[31 * 4 + i] = true;
        }
        Near(frame[12 * 4 + 3], fit.Near, 0, "The packed near plane changed units.");
        Near(frame[13 * 4 + 3], fit.Count, 0, "The packed cascade count is incorrect.");
        var view = Row(frame, 20);
        Assert(view == fit.View, "The packed view-depth projection changed.");
        written[12 * 4 + 3] = written[13 * 4 + 3] = true;
        for (int component = 0; component < 4; component++) written[20 * 4 + component] = true;
        for (int i = 0; i < frame.Length; i++)
            if (!written[i]) Near(frame[i], -1000.125f - i, 0,
                $"Packing cascades overwrote unrelated frame state or an inactive layer at float {i}.");

        static Vector4 Row(ReadOnlySpan<float> values, int slot) =>
            new(values[slot * 4], values[slot * 4 + 1], values[slot * 4 + 2], values[slot * 4 + 3]);

        static void CheckPoint(Vector3 point, Matrix4x4 matrix, Vector4 x, Vector4 y, Vector4 z)
        {
            var world = new Vector4(point, 1);
            var expected = Vector4.Transform(world, matrix);
            Near(expected.W, 1, 0, "A shadow fit requires projective W and cannot use affine packing.");
            var actual = new Vector3(Vector4.Dot(x, world), Vector4.Dot(y, world), Vector4.Dot(z, world));
            var xyz = new Vector3(expected.X, expected.Y, expected.Z);
            Near(actual, xyz, Math.Max(.000002f, xyz.Length() * .000002f),
                "Packed affine WGSL dot products disagree with the original homogeneous projection.");
        }
    }

    private static void StableNorthSouthTexels()
    {
        const int resolution = 2048, width = 1200, height = 780;
        float maximumResidual = 0;
        foreach (double latitude in new[] { 40.7087, 80d, -40.7087 })
        foreach (int direction in new[] { -1, 1 })
        {
            var camera = MapCamera.FromEye(latitude, -74.0104, 2, 67, 85, width, height);
            using var initial = new Fixture(camera, width, height, 1);
            var baseline = WebGpuShadowCascades.Create(initial.Frame, DaySun, 4, resolution, 2400);
            var point = ReceiverPoint(initial.Frame, baseline,
                (baseline.Layers[0].Near + baseline.Layers[0].Far) * .5f, 0, 0);
            double worldX = camera.CenterX + point.X / Meters(camera);
            double worldY = camera.CenterY - point.Y / Meters(camera);
            double pointLatitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * worldY))) * 180 / Math.PI;
            double pointLongitude = worldX * 360 - 180;
            double pointAltitude = camera.GroundAltitudeMeters + point.Z;
            var eye = camera.GetEye(height);
            var previous = ShadowTexel(baseline.Layers[0].Matrix, point, resolution);
            float worstResidual = 0;
            for (int step = 1; step <= 32; step++)
            {
                double north = direction * step * baseline.Layers[0].TexelSize * .125;
                double eyeY = MapCamera.MercatorY(eye.Latitude) - north / Meters(camera);
                double eyeLatitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * eyeY))) * 180 / Math.PI;
                var shifted = MapCamera.FromEye(eyeLatitude, eye.Longitude, eye.AltitudeMeters,
                    camera.Bearing, camera.Pitch, width, height, camera.ViewDistanceMeters, camera.GroundAltitudeMeters);
                using var current = new Fixture(shifted, width, height, 1);
                var fit = WebGpuShadowCascades.Create(current.Frame, DaySun, 4, resolution, 2400);
                // The rendering world changes its horizontal metre scale with
                // latitude. A stable Mercator grid may change physical radius;
                // the invariant is the fixed geographic point's sampling phase.
                var fixedPoint = WorldPosition(shifted, pointLatitude, pointLongitude, pointAltitude);
                var texel = ShadowTexel(fit.Layers[0].Matrix, fixedPoint, resolution);
                var delta = texel - previous;
                worstResidual = Math.Max(worstResidual,
                    Math.Max(Math.Abs(delta.X - MathF.Round(delta.X)), Math.Abs(delta.Y - MathF.Round(delta.Y))));
                previous = texel;
            }
            Console.WriteLine($"Shadow grid residual at {latitude:F4} degrees moving {(direction > 0 ? "north" : "south")}: {worstResidual:F6} texels.");
            maximumResidual = Math.Max(maximumResidual, worstResidual);
        }
        Assert(maximumResidual < .004f,
            "A fixed geographic point drifted across fractional shadow texels during physical north/south motion.");
    }

    private static void CheckBiasUnits(WebGpuShadowCascade layer, Vector3 sun, int resolution)
    {
        var center = Vector4.Transform(new Vector4(layer.Center, 1), layer.Matrix);
        var towardSun = Vector4.Transform(new Vector4(layer.Center + sun * layer.TexelSize, 1), layer.Matrix);
        float expected = layer.TexelSize * layer.InverseDepthRange;
        Near(center.Z - towardSun.Z, expected, Math.Max(.0000003f, expected * .0005f),
            "Texel-scaled receiver bias uses a different light-depth unit from the actual matrix.");
        var lightRight = Vector3.Normalize(new Vector3(-sun.Y, sun.X, 0));
        var a = ShadowTexel(layer.Matrix, layer.Center, resolution);
        var b = ShadowTexel(layer.Matrix, layer.Center + lightRight * layer.TexelSize, resolution);
        Near(b.X - a.X, 1, .003f, "One world shadow texel did not move one sampled shadow pixel.");
        Near(b.Y - a.Y, 0, .003f, "The shadow texel metadata changed the orthogonal light coordinate.");
    }

    // Invert the existing MapCamera projection in double precision, independently
    // of the new helper's light fit, basis construction and SliceCorners method.
    private static Vector3 ReceiverPoint(GpuMapFrame frame, WebGpuShadowCascades fit,
        float depth, float x, float y)
    {
        var camera = frame.Camera;
        double focal = MapCamera.Focal(frame.Height), ppm = camera.WorldSize / Meters(camera);
        double dx = x * frame.Width * .5, dy = y * frame.Height * .5;
        var (sp, cp) = Math.SinCos(camera.PitchRadians);
        var (sb, cb) = Math.SinCos(camera.Bearing * Math.PI / 180);
        if (camera.Perspective)
        {
            double weight = depth * ppm / focal;
            double u = dx * weight, v = cp * dy * weight + sp * focal * (1 - weight);
            double z = (-sp * dy * weight + cp * focal * (1 - weight) + camera.TargetPixels) / ppm;
            return new((float)((u * cb - v * sb) / ppm), (float)(-(u * sb + v * cb) / ppm), (float)z);
        }
        double along = depth - fit.View.W;
        return new((float)((dx * cb - dy * sb * cp) / ppm + along * sb * sp),
            (float)((-dx * sb - dy * cb * cp) / ppm + along * cb * sp),
            (float)(-dy * sp / ppm - along * cp));
    }

    private static void CheckCameraProjection(GpuMapFrame frame, Vector3 point, float x, float y)
    {
        double meters = Meters(frame.Camera);
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (frame.Camera.CenterY - point.Y / meters)))) * 180 / Math.PI;
        double longitude = (frame.Camera.CenterX + point.X / meters) * 360 - 180;
        frame.Camera.TryProject(latitude, longitude, frame.Camera.GroundAltitudeMeters + point.Z,
            frame.Width, frame.Height, out var projected);
        Assert(double.IsFinite(projected.X) && double.IsFinite(projected.Y) &&
            Math.Abs(projected.X - (x + 1) * frame.Width * .5) < .15 &&
            Math.Abs(projected.Y - (y + 1) * frame.Height * .5) < .15,
            $"The receiver-ray oracle disagreed with MapCamera ({projected.X:F4}, {projected.Y:F4}).");
    }

    private static Vector3 CameraForward(MapCamera camera)
    {
        var (sp, cp) = Math.SinCos(camera.PitchRadians);
        var (sb, cb) = Math.SinCos(camera.Bearing * Math.PI / 180);
        return new((float)(sb * sp), (float)(cb * sp), (float)-cp);
    }

    private static Vector3 WorldPosition(MapCamera camera, double latitude, double longitude, double absoluteAltitude) =>
        new((float)(MapCamera.WrapOffset((longitude + 180) / 360 - camera.CenterX) * Meters(camera)),
            (float)((camera.CenterY - MapCamera.MercatorY(latitude)) * Meters(camera)),
            (float)(absoluteAltitude - camera.GroundAltitudeMeters));

    private static double Meters(MapCamera camera) => MapCamera.EarthCircumference *
        Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);

    private static Vector2 ShadowTexel(Matrix4x4 matrix, Vector3 point, int resolution)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), matrix);
        return new((clip.X / clip.W * .5f + .5f) * resolution, (.5f - clip.Y / clip.W * .5f) * resolution);
    }

    private static void Inside(Matrix4x4 matrix, Vector3 point, bool includeXY, string message)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), matrix);
        Assert(float.IsFinite(clip.X) && float.IsFinite(clip.Y) && float.IsFinite(clip.Z) &&
            float.IsFinite(clip.W) && Math.Abs(clip.W - 1) < .000001f &&
            clip.Z >= -.0002f && clip.Z <= clip.W + .0002f &&
            (!includeXY || Math.Abs(clip.X) <= clip.W + .0002f && Math.Abs(clip.Y) <= clip.W + .0002f),
            $"{message} Clip={clip}");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TileGeometry? geometry;
        internal GpuMapFrame Frame { get; }
        internal Vector3[] CasterCorners { get; }

        internal Fixture(MapCamera camera, int width, int height, int pixelSize,
            double exaggeration = 1, bool building = false)
        {
            const int n = 1 << 14;
            int copy = (int)Math.Floor(camera.CenterX);
            var id = new TileId(14, (int)Math.Floor((camera.CenterX - copy) * n),
                Math.Clamp((int)Math.Floor(camera.CenterY * n), 0, n - 1));
            double meters = Meters(camera);
            float ppm = (float)(camera.WorldSize / (meters * pixelSize));
            float rawLow = (float)camera.GroundAltitudeMeters - 350, rawHigh = (float)camera.GroundAltitudeMeters + 250;
            var options = new TerrainOptions(ElevationSnapshot.Empty, Exaggeration: exaggeration)
                { RenderStyle = MapRenderStyle.Normal };
            var values = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride];
            int offset = 0;
            for (int y = 0; y < GpuTerrainMesh.Side; y++)
            for (int x = 0; x < GpuTerrainMesh.Side; x++)
            {
                values[offset++] = x * 256;
                values[offset++] = y * 256;
                values[offset++] = rawLow + (rawHigh - rawLow) * x / GpuTerrainMesh.Segments;
                values[offset++] = 1;
            }
            var transform = new TileProjection(camera, width, height, pixelSize).Get(id, copy).Transform;
            var mesh = new GpuTerrainMesh(new object(), id, copy, values, 1, MapRenderStyle.Normal);
            double east = (id.X / (double)n + copy - camera.CenterX) * meters;
            double south = (id.Y / (double)n - camera.CenterY) * meters;
            double unit = meters / (n * TileGeometry.Extent);
            float minimum = (float)options.LocalAltitude(camera, rawLow);
            float maximum = (float)options.LocalAltitude(camera, rawHigh);
            var points = new List<Vector3>();
            AddBox(0, 0, 4096, 4096, minimum, maximum);
            GpuBuildingDraw[] buildings = [];
            if (building)
            {
                // Buffered source geometry lies beyond the terrain tile and is
                // wholly excluded from the viewer's selected index stream.
                var feature = new MapFeature("building", "", "", "", 1, 4096, GeometryType.Polygon,
                    [[new(6000, -1000), new(6200, -1000), new(6200, -800), new(6000, -800)]],
                    1500, 40, false, 0, 0, "");
                geometry = new(new MapTile(id, [feature]), MapRenderStyle.Normal);
                var source = new GpuBuildingMesh(geometry);
                var models = (float[])source.Models.Clone();
                models[0] = (float)camera.GroundAltitudeMeters + 120;
                source = source.WithFoundations(models);
                buildings = [new(source, transform, (float)unit, new(new byte[source.Ranges.Length], [], []))];
                float foundation = (float)options.LocalAltitude(camera, models[0]);
                float low = foundation + source.Ranges[0].Model.Bottom, high = foundation + source.Ranges[0].Model.Top;
                AddBox(6000, -1000, 6200, -800, low, high);
                minimum = Math.Min(minimum, low); maximum = Math.Max(maximum, high);
            }
            Frame = new(width, height, pixelSize, camera, options, PixelPalette.Day, ppm,
                [new(mesh, Texture, transform)], buildings)
            {
                MinimumAltitude = minimum * ppm,
                MaximumAltitude = maximum * ppm
            };
            // Exercise the production inclusion order: depth bounds must retain
            // complete model heights before applying the empty viewer selection.
            if (building) Frame.Depth = new(Frame);
            CasterCorners = points.ToArray();

            void AddBox(float left, float top, float right, float bottom, float low, float high)
            {
                foreach (float x in new[] { left, right })
                foreach (float y in new[] { top, bottom })
                foreach (float z in new[] { low, high })
                    points.Add(new((float)(east + x * unit), (float)(-south - y * unit), z));
            }
        }

        public void Dispose() { Frame.Dispose(); geometry?.Dispose(); }
    }

    private static void Near(float actual, float expected, float tolerance, string message) =>
        Assert(float.IsFinite(actual) && float.IsFinite(expected) && Math.Abs(actual - expected) <= tolerance,
            $"{message} Actual={actual}, expected={expected}, tolerance={tolerance}.");

    private static void Near(Vector3 actual, Vector3 expected, float tolerance, string message) =>
        Assert(float.IsFinite(actual.X) && float.IsFinite(actual.Y) && float.IsFinite(actual.Z) &&
            Vector3.Distance(actual, expected) <= tolerance, $"{message} Actual={actual}, expected={expected}.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
