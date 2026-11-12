using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private void DrawLabels(SKCanvas canvas, int w, int h, double zoom, int pixelSize, PixelPalette colors)
        => PrepareLabels(canvas, w, h, zoom, pixelSize, colors, null);

    private void PrepareLabels(SKCanvas? canvas, int w, int h, double zoom, int pixelSize, PixelPalette colors, WebGpuSceneOverlay? overlay)
    {
        candidates.Clear(); names.Clear(); occupied.Clear();
        var elevation = CurrentTerrain;
        float meters = elevation is null ? 0 : (float)(terrainCamera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(terrainCamera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
        float lift = elevation is null ? 0 : Math.Max(4, .75f / meters);
        foreach (var (tile, transform, _) in visible)
        foreach (var label in tile.Labels)
        {
            if (label.Street && zoom < 15 || label.Priority > 9 && zoom < 9) continue;
            SKPoint screen;
            float occlusionAltitude = 4;
            if (elevation is not null)
            {
                float altitude = GroundAltitude(tile, label.Point, elevation, terrainCamera);
                var point = transform.Vertex(label.Point.X, label.Point.Y, altitude * meters);
                if (point.W < transform.Near || point.W > transform.Far) continue;
                screen = point.Point;
                occlusionAltitude = altitude + lift;
            }
            else
            {
                float weight = transform.Weight(label.Point.X, label.Point.Y);
                if (weight < transform.Near || weight > transform.Far) continue;
                screen = transform.At(label.Point.X, label.Point.Y);
            }
            var art = screen;
            screen.X *= pixelSize; screen.Y *= pixelSize;
            if (screen.X < 0 || screen.X >= w || screen.Y < 0 || screen.Y >= h) continue;
            float dx = screen.X - w * .5f, dy = screen.Y - h * .5f;
            candidates.Add(new(label, screen, art, dx * dx + dy * dy, occlusionAltitude, candidates.Count));
        }
        candidates.Sort(CompareLabels);
        Span<int> selected = stackalloc int[90];
        int selectedCount;
        bool resolvedOcclusion = false;
        while (true)
        {
            names.Clear(); occupied.Clear();
            selectedCount = 0;
            bool ambiguous = false;
            for (int first = 0; first < candidates.Count && selectedCount < selected.Length;)
            {
                int end = first + 1;
                if (!resolvedOcclusion)
                    while (end < candidates.Count && CompareLabels(candidates[first], candidates[end]) == 0) end++;
                int winner = -1;
                float winnerX = 0, winnerY = 0;
                SKRect winnerBounds = default;
                // A tie only matters if different visible placements can survive
                // the names and collision bounds established by earlier groups.
                // Buffered copies often differ by a projection ULP while producing
                // exactly the same rounded glyph position and collision rectangle.
                for (int index = first; index < end; index++)
                {
                    var candidate = candidates[index];
                    if (!Placement(candidate, out float x, out float y, out var bounds)) continue;
                    if (!resolvedOcclusion && Covered(index)) continue;
                    if (winner >= 0)
                    {
                        var prior = candidates[winner];
                        if (prior.Label.Text != candidate.Label.Text || prior.Label.Street != candidate.Label.Street ||
                            winnerX != x || winnerY != y || winnerBounds != bounds)
                        {
                            ambiguous = true;
                            break;
                        }
                        continue;
                    }
                    winner = index; winnerX = x; winnerY = y; winnerBounds = bounds;
                }
                if (ambiguous) break;
                if (winner >= 0)
                {
                    names.Add(candidates[winner].Label.Text);
                    occupied.Add(winnerBounds);
                    selected[selectedCount++] = winner;
                }
                first = end;
            }
            if (!ambiguous) break;
            // List.Sort is unstable: removing hidden entries before sorting can
            // change the winner of a genuine visible tie. Restart with the exact
            // original source-order compaction and sort, before emitting any glyphs.
            // Reuse earlier visibility results so the compatibility path never
            // queries a candidate more often than the original eager implementation.
            candidates.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            int retained = 0;
            for (int index = 0; index < candidates.Count; index++)
                if (!Covered(index)) candidates[retained++] = candidates[index];
            candidates.RemoveRange(retained, candidates.Count - retained);
            candidates.Sort(CompareLabels);
            resolvedOcclusion = true;
        }
        for (int index = 0; index < selectedCount; index++)
        {
            var candidate = candidates[selected[index]];
            var label = candidate.Label;
            float size = label.Street ? 12f : 14f;
            float x = MathF.Round(candidate.Screen.X - label.TextWidth * .5f), y = MathF.Round(candidate.Screen.Y);
            if (overlay is not null)
            {
                overlay.AddLabel(label.Text, size, x, y, colors, font.Typeface);
                continue;
            }
            if (label.Glyphs is null)
            {
                font.Size = size;
                label.Glyphs = SKTextBlob.Create(label.Text, font);
            }
            if (label.Glyphs is not { } glyphs) continue;
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2.8f;
            paint.Color = new(colors.LabelHalo);
            canvas!.DrawText(glyphs, x, y, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = new(colors.Label);
            canvas!.DrawText(glyphs, x, y, paint);
        }

        bool Placement(in LabelCandidate candidate, out float x, out float y, out SKRect bounds)
        {
            x = y = 0; bounds = default;
            var label = candidate.Label;
            if (names.Contains(label.Text)) return false;
            float size = label.Street ? 12f : 14f;
            float tw = label.TextWidth;
            if (float.IsNaN(tw))
            {
                font.Size = size;
                label.TextWidth = tw = font.MeasureText(label.Text);
            }
            if (tw <= 0 || tw > w * .8f) return false;
            x = MathF.Round(candidate.Screen.X - tw * .5f); y = MathF.Round(candidate.Screen.Y);
            float pad = label.Street ? 22 : 7;
            bounds = new SKRect(x - pad, y - size - 9, x + tw + pad, y + 9);
            if (bounds.Left < 1 || bounds.Right > w - 1 || bounds.Top < 1 || bounds.Bottom > h - 1) return false;
            foreach (var prior in occupied)
                if (bounds.IntersectsWith(prior)) return false;
            return true;
        }

        bool Covered(int index)
        {
            var candidate = candidates[index];
            if (candidate.Occlusion != 0) return candidate.Occlusion == 2;
            bool covered = (elevation is not null || candidate.Label.Street) &&
                buildings.CoversAt(candidate.Art.X, candidate.Art.Y, candidate.Altitude);
            candidates[index] = candidate with { Occlusion = covered ? (byte)2 : (byte)1 };
            return covered;
        }
    }

    private static int CompareLabels(LabelCandidate a, LabelCandidate b)
    {
        int order = a.Label.Priority.CompareTo(b.Label.Priority);
        return order == 0 ? a.Distance.CompareTo(b.Distance) : order;
    }
}
