namespace PixelMap.Rendering;

// GLSL ES 3.00 also compiles on the desktop backend with a GLSL 3.30 header.
// Tile transforms are already rebased in double precision by TileProjection.
internal static class GlMapShaders
{
    internal const string Projection = """
        uniform vec4 uScreenX;
        uniform vec4 uScreenY;
        uniform vec4 uScreenW;
        uniform vec2 uArtSize;
        uniform vec2 uDepth;
        uniform vec3 uGround;
        uniform vec4 uLens;
        uniform bool uPerspective;
        uniform int uRenderStyle;

        vec4 screenPosition(vec2 point, float altitude)
        {
            vec4 position = vec4(point, altitude, 1.0);
            return vec4(dot(uScreenX, position), dot(uScreenY, position),
                        dot(uScreenW, position), altitude);
        }

        vec4 clipPosition(vec4 screen)
        {
            float depth = uDepth.x * (uPerspective ? screen.z : screen.w) + uDepth.y;
            return vec4(2.0 * screen.x / uArtSize.x - screen.z,
                        screen.z - 2.0 * screen.y / uArtSize.y, depth, screen.z);
        }

        float localFoundation(float height)
        {
            return (height - uGround.x) * uGround.y;
        }
        """;

    internal const string Models = """
        uniform sampler2D uModels;
        uniform int uModelWidth;

        vec4 modelData(int model, int part)
        {
            int index = model * 2 + part;
            return texelFetch(uModels, ivec2(index % uModelWidth, index / uModelWidth), 0);
        }

        vec2 modelRadius(vec4 bounds)
        {
            return vec2(dot(abs(uScreenX.xy), bounds.xy),
                        dot(abs(uScreenY.xy), bounds.xy)) * 0.5;
        }

        bool tinyModel(vec4 model, vec4 bounds, float foundation)
        {
            vec2 radius = modelRadius(bounds);
            float rise = (foundation == 0.0 ? model.w : model.w - bounds.z) * uGround.z * uLens.x;
            return !uPerspective && radius.x < 0.28 && radius.y + rise < 0.5;
        }
        """;

    internal const string FullScreenVertex = """
        void main()
        {
            vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    internal const string BackgroundFragment = """
        uniform vec2 uArtSize;
        uniform vec3 uGroundColor;
        uniform vec3 uSkyColor;
        uniform vec3 uHorizonColor;
        uniform float uHorizon;
        uniform int uRenderStyle;
        out vec4 outputColor;

        void main()
        {
            float row = uArtSize.y - gl_FragCoord.y;
            vec3 color = uGroundColor;
            if (row < uHorizon)
                color = (uRenderStyle == 1 || uRenderStyle == 3)
                    ? mix(uSkyColor, uHorizonColor, smoothstep(0.0, max(1.0, uHorizon), row))
                    : row >= max(0.0, uHorizon - 7.0) ? uHorizonColor : uSkyColor;
            outputColor = vec4(color, 0.0);
        }
        """;

    internal const string TerrainVertex = """
        layout(location = 0) in vec4 aScreenX;
        layout(location = 1) in vec4 aScreenY;
        layout(location = 2) in vec4 aScreenW;
        layout(location = 3) in vec2 aLayers;
        uniform sampler2DArray uHeights;
        out vec2 vUv;
        out vec2 vHeightLight;
        flat out int vArtworkLayer;
        flat out int vTerrainFace;

        vec2 corner(int index)
        {
            return vec2(index == 1 || index == 2 ? 1.0 : 0.0, index >= 2 ? 1.0 : 0.0);
        }

        void main()
        {
            int layer = int(aLayers.y + 0.5);
            vec2 point;
            vTerrainFace = 0;
            if (uRenderStyle < 2)
            {
                ivec2 grid = ivec2(gl_VertexID % 17, gl_VertexID / 17);
                vHeightLight = texelFetch(uHeights, ivec3(grid, layer), 0).rg;
                point = vec2(grid) * 256.0;
            }
            else
            {
                bool beveled = uRenderStyle == 3;
                int perCell = beveled ? 36 : 20;
                int cellIndex = gl_VertexID / perCell;
                int local = gl_VertexID % perCell;
                ivec2 cell = ivec2(cellIndex % 8, cellIndex / 8);
                ivec2 samplePoint = cell + ivec2(1);
                vHeightLight = texelFetch(uHeights, ivec3(samplePoint, layer), 0).rg;
                float top = vHeightLight.x;
                float bevel = texelFetch(uHeights, ivec3(16, 16, layer), 0).r;
                int face = local / 4, vertex = local % 4;
                vTerrainFace = face;
                vec2 within;
                if (face == 0)
                {
                    within = corner(vertex);
                    if (beveled) within = vec2(0.04) + within * 0.92;
                }
                else
                {
                    bool bevelFace = beveled && face <= 4;
                    int edge = (face - 1) % 4;
                    within = corner(vertex == 0 || vertex == 3 ? edge : (edge + 1) % 4);
                    bool upper = vertex < 2;
                    if (bevelFace)
                    {
                        if (upper) within = vec2(0.04) + within * 0.92;
                        vHeightLight.x = upper ? top : top - bevel;
                    }
                    else
                    {
                        const ivec2 neighbours[4] = ivec2[4](ivec2(0, -1), ivec2(1, 0), ivec2(0, 1), ivec2(-1, 0));
                        float lower = min(top, texelFetch(uHeights, ivec3(samplePoint + neighbours[edge], layer), 0).r);
                        vHeightLight.x = (upper ? top : lower) - bevel;
                    }
                }
                point = (vec2(cell) + within) * 512.0;
            }
            float altitude = localFoundation(vHeightLight.x) * uGround.z;
            vec4 position = vec4(point, altitude, 1.0);
            gl_Position = clipPosition(vec4(dot(aScreenX, position), dot(aScreenY, position),
                dot(aScreenW, position), altitude));
            vUv = point / 4096.0;
            vArtworkLayer = int(aLayers.x + 0.5);
        }
        """;

    internal const string TerrainFragment = """
        uniform sampler2DArray uArtwork;
        uniform bool uHypsometric;
        uniform int uRenderStyle;
        in vec2 vUv;
        in vec2 vHeightLight;
        flat in int vArtworkLayer;
        flat in int vTerrainFace;
        out vec4 outputColor;

        vec3 mixBytes(vec3 first, vec3 second, float amount)
        {
            return floor(first * (1.0 - amount) + second * amount);
        }

        vec3 elevationColor(float altitude)
        {
            const float stops[7] = float[7](-500.0, 0.0, 500.0, 1500.0, 3000.0, 4500.0, 6000.0);
            const vec3 colors[7] = vec3[7](vec3(66.0, 106.0, 131.0), vec3(113.0, 156.0, 120.0),
                vec3(166.0, 180.0, 124.0), vec3(194.0, 172.0, 128.0), vec3(167.0, 150.0, 145.0),
                vec3(217.0, 217.0, 208.0), vec3(240.0, 238.0, 229.0));
            for (int i = 1; i < 7; i++)
                if (altitude <= stops[i])
                    return mixBytes(colors[i - 1], colors[i], clamp((altitude - stops[i - 1]) /
                        (stops[i] - stops[i - 1]), 0.0, 1.0));
            return colors[6];
        }

        void main()
        {
            vec4 sampleColor = texture(uArtwork, vec3(vUv, float(vArtworkLayer)));
            vec3 color = floor(sampleColor.bgr * 255.0 + 0.5);
            if (uHypsometric)
                color = mixBytes(color, elevationColor(vHeightLight.x), 0.64);
            float shade = clamp(0.72 + vHeightLight.y * 0.4, 0.52, 1.16);
            if (uRenderStyle == 0 || uRenderStyle == 2) shade = roundEven(shade * 6.0) / 6.0;
            if (uRenderStyle == 2)
            {
                if (vTerrainFace > 0)
                {
                    color = mixBytes(color, vec3(126.0, 94.0, 65.0), 0.68);
                    const float faces[4] = float[4](0.68, 0.82, 1.0, 0.88);
                    shade *= faces[(vTerrainFace - 1) % 4];
                    if (fract(vHeightLight.x * 0.5) < 0.065) shade *= 0.78;
                }
                else
                {
                    vec2 cell = fract(vUv * 8.0);
                    if (min(min(cell.x, 1.0 - cell.x), min(cell.y, 1.0 - cell.y)) < 0.018) shade *= 0.88;
                }
                ivec2 grain = ivec2(floor(vUv * 128.0));
                shade *= 0.96 + float((grain.x * 13 + grain.y * 7) & 3) * 0.022;
            }
            if (uRenderStyle == 3)
            {
                float mean = dot(color, vec3(0.2126, 0.7152, 0.0722));
                color = clamp(vec3(mean) + (color - vec3(mean)) * 1.3 + vec3(9.0), 0.0, 255.0);
                if (vTerrainFace > 0)
                {
                    const float faces[4] = float[4](0.74, 0.88, 1.04, 0.93);
                    shade *= faces[(vTerrainFace - 1) % 4] * (vTerrainFace <= 4 ? 1.08 : 0.94);
                }
                else
                {
                    vec2 stud = fract(vUv * 32.0) - vec2(0.5);
                    float radius = length(stud);
                    float rim = smoothstep(0.13, 0.16, radius) - smoothstep(0.16, 0.19, radius);
                    shade *= 1.0 + rim * (stud.x + stud.y < 0.0 ? 0.11 : -0.10);
                }
            }
            if (abs(shade - 1.0) >= 0.001)
                color = clamp(floor(color * shade), 0.0, 255.0);
            outputColor = vec4(color / 255.0, sampleColor.a);
        }
        """;

    internal const string BuildingVertex = """
        layout(location = 0) in vec4 aPosition;
        layout(location = 1) in vec4 aSurface;
        layout(location = 2) in vec4 aEdge;
        uniform float uTileUnitsToMeters;
        out vec2 vFacade;
        flat out vec4 vWall;
        flat out vec2 vBuilding;
        flat out ivec3 vModel;

        void main()
        {
            int modelIndex = int(aPosition.w + 0.5);
            vec4 model = modelData(modelIndex, 0);
            vec4 bounds = modelData(modelIndex, 1);
            float foundation = localFoundation(model.x);
            float altitude = (foundation + aPosition.z) * uGround.z;
            int face = int(aEdge.w + 0.5);
            if (tinyModel(model, bounds, foundation) ||
                (uPerspective && ((face == 1 && altitude >= uLens.y) ||
                                  (face == 2 && altitude <= uLens.y))) ||
                (!uPerspective && face == 2))
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                return;
            }
            gl_Position = clipPosition(screenPosition(aPosition.xy, altitude));
            float edgeLength = max(aSurface.y, 0.00001);
            vec2 endpoint = aPosition.xy + aEdge.xy * (1.0 - aSurface.x / edgeLength);
            float endpointX = dot(uScreenX, vec4(endpoint, 0.0, 1.0));
            float span = -dot(uScreenX.xy, aEdge.xy);
            float worldLength = aSurface.y * length(uScreenX.xy);
            vFacade = vec2(aSurface.x * length(aEdge.xy) / edgeLength * uTileUnitsToMeters, aPosition.z);
            if (uRenderStyle != 0 && face != 0) vFacade = aPosition.xy * uTileUnitsToMeters;
            vWall = vec4(endpointX, span, worldLength, uPerspective ? aSurface.w : aSurface.z);
            vBuilding = vec2(model.w - bounds.z, model.w);
            vModel = ivec3(int(model.y + 0.5), int(model.z + 0.5), face);
        }
        """;

    internal const string BuildingFragment = """
        uniform sampler2D uPalette;
        uniform sampler2D uRooms;
        in vec2 vFacade;
        flat in vec4 vWall;
        flat in vec2 vBuilding;
        flat in ivec3 vModel;
        out vec4 outputColor;

        vec3 material(int part)
        {
            return floor(texelFetch(uPalette, ivec2(part, vModel.y), 0).bgr * 255.0 + 0.5);
        }

        vec3 mixBytes(vec3 first, vec3 second, float amount)
        {
            return floor(first * (1.0 - amount) + second * amount);
        }

        bool lamp(int column, int story)
        {
            int index = (vModel.x + column + story * 16) & 255;
            return texelFetch(uRooms, ivec2(index, 0), 0).r < 0.001;
        }

        void main()
        {
            if (vModel.z != 0)
            {
                vec3 roof = material(vModel.z == 1 ? 0 : 2);
                if (uRenderStyle == 2)
                {
                    vec2 cell = fract(vFacade * 0.5);
                    if (min(min(cell.x, 1.0 - cell.x), min(cell.y, 1.0 - cell.y)) < 0.06) roof *= 0.78;
                }
                if (uRenderStyle == 3)
                {
                    vec2 stud = fract(vFacade * 1.25) - vec2(0.5);
                    float ring = smoothstep(0.17, 0.24, length(stud)) - smoothstep(0.27, 0.34, length(stud));
                    roof = clamp(roof * 1.1 + vec3(9.0), 0.0, 255.0) * (1.0 + ring * (stud.x + stud.y < 0.0 ? 0.18 : -0.12));
                }
                outputColor = vec4(roof / 255.0, 1.0);
                return;
            }
            if (!gl_FrontFacing) discard;
            vec3 wall = mixBytes(material(2), material(1), vWall.w);
            vec3 color = wall;
            if (uRenderStyle == 2)
            {
                vec2 block = fract(vFacade * 0.5);
                float seam = min(min(block.x, 1.0 - block.x), min(block.y, 1.0 - block.y));
                color *= seam < 0.055 ? 0.76 : 1.0;
                if ((int(floor(vFacade.x * 0.5)) + int(floor(vFacade.y * 0.5))) % 3 == 0) color *= 0.94;
                outputColor = vec4(color / 255.0, 1.0);
                return;
            }
            if (uRenderStyle == 3)
            {
                vec2 pane = fract(vFacade / vec2(2.8, 3.6));
                bool window = pane.x > 0.18 && pane.x < 0.82 && pane.y > 0.24 && pane.y < 0.78;
                color = window ? material(5) : clamp(wall * 1.12 + vec3(10.0), 0.0, 255.0);
                if (window) color = mix(color, vec3(225.0, 246.0, 250.0), 0.12 + pane.y * 0.22);
                outputColor = vec4(color / 255.0, 1.0);
                return;
            }
            float floorMeters = 3.2 + float(vModel.x & 3) * 0.22;
            float bayMeters = 3.2 + float((vModel.x >> 2) & 3) * 0.6;
            if (uPerspective)
            {
                if (uGround.z * gl_FragCoord.w > 0.40)
                {
                    vec2 room = vec2(vFacade.x / bayMeters, vFacade.y / floorMeters);
                    ivec2 cell = ivec2(floor(room));
                    vec2 within = room - vec2(cell);
                    if (within.y > 0.23 && within.y < 0.75 && within.x > 0.17 && within.x < 0.76)
                        color = material(lamp(cell.x, cell.y) ? 6 : 5);
                }
            }
            else
            {
                if (vWall.y < 0.01 || vBuilding.x * uGround.z * uLens.x < 0.35) discard;
                float floorPixels = max(0.01, uGround.z * uLens.x * floorMeters);
                float bayPixels = uGround.z * bayMeters * vWall.y / max(0.01, vWall.z);
                float along = gl_FragCoord.x - vWall.x;
                int column = int(along / max(0.01, bayPixels));
                float windowX = along - float(column) * bayPixels;
                float rise = vFacade.y * uGround.z * uLens.x;
                int story = int(rise / floorPixels);
                float within = rise - float(story) * floorPixels;
                bool ribbon = (vModel.x & 8) != 0 && vBuilding.y * uGround.z * uLens.x > 9.0 * floorPixels;
                bool pilasters = (vModel.x & 32) != 0 && vBuilding.y * uGround.z * uLens.x > 7.0 * floorPixels;
                if (pilasters && bayPixels > 1.9 && (column & 3) == 0 && windowX < 0.9)
                    color = mixBytes(wall, material(3), 0.32);
                bool glass = floorPixels >= 1.65 && bayPixels >= 1.45 &&
                    vBuilding.x * uGround.z * uLens.x > floorPixels * 1.6 &&
                    windowX > bayPixels * (ribbon ? 0.05 : 0.19) &&
                    windowX < bayPixels * (ribbon ? 0.95 : 0.68);
                if (glass && within >= floorPixels * 0.22 && within < floorPixels * 0.75)
                    color = material(lamp(column, story) ? 6 : 5);
            }
            outputColor = vec4(color / 255.0, 1.0);
        }
        """;

    internal const string RimVertex = """
        layout(location = 0) in vec4 aPosition;
        layout(location = 3) in vec4 bPosition;
        flat out vec4 vSegment;
        flat out vec3 vDepth;
        flat out ivec2 vMaterial;
        flat out int vVisible;

        float planeDistance(vec4 point, int plane)
        {
            if (plane == 0) return point.z - uLens.w;
            if (plane == 1) return uLens.z - point.z;
            if (plane == 2) return point.x;
            if (plane == 3) return uArtSize.x * point.z - point.x;
            if (plane == 4) return point.y;
            return uArtSize.y * point.z - point.y;
        }

        void main()
        {
            vec4 model = modelData(int(aPosition.w + 0.5), 0);
            vec4 bounds = modelData(int(aPosition.w + 0.5), 1);
            float foundation = localFoundation(model.x);
            float altitude = (foundation + aPosition.z) * uGround.z;
            vec2 radius = modelRadius(bounds);
            bool visible = !tinyModel(model, bounds, foundation) &&
                (uPerspective ? altitude < uLens.y : radius.x >= 3.0 && radius.y >= 1.5);
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            vVisible = 0;
            if (!visible) return;
            vec4 a = screenPosition(aPosition.xy, altitude);
            vec4 b = screenPosition(bPosition.xy, altitude);
            float low = 0.0, high = 1.0;
            for (int plane = 0; plane < 6; plane++)
            {
                float first = planeDistance(a, plane), second = planeDistance(b, plane);
                if (first < 0.0 && second < 0.0) return;
                if ((first < 0.0) != (second < 0.0))
                {
                    float amount = first / (first - second);
                    if (first < 0.0) low = max(low, amount);
                    else high = min(high, amount);
                }
            }
            if (high < low) return;
            vec4 first = mix(a, b, low), second = mix(a, b, high);
            vec2 p = first.xy / first.z, q = second.xy / second.z;
            vec2 delta = q - p;
            vec2 direction = length(delta) > 0.0001 ? normalize(delta) : vec2(1.0, 0.0);
            vec2 normal = vec2(-direction.y, direction.x);
            bool endPoint = gl_VertexID == 1 || gl_VertexID == 2 || gl_VertexID == 4;
            bool positive = gl_VertexID == 2 || gl_VertexID == 4 || gl_VertexID == 5;
            vec2 point = (endPoint ? q + direction * 2.0 : p - direction * 2.0) +
                normal * (positive ? 1.5 : -1.5);
            gl_Position = vec4(point.x * 2.0 / uArtSize.x - 1.0,
                1.0 - point.y * 2.0 / uArtSize.y, 0.0, 1.0);
            vVisible = 1;
            vSegment = vec4(p, q);
            vDepth = vec3(1.0 / first.z, 1.0 / second.z, altitude);
            vMaterial = ivec2(int(model.z + 0.5), delta.x >= 0.0 ? 3 : 4);
        }
        """;

    internal const string RimFragment = """
        uniform sampler2D uPalette;
        flat in vec4 vSegment;
        flat in vec3 vDepth;
        flat in ivec2 vMaterial;
        flat in int vVisible;
        out vec4 outputColor;

        void main()
        {
            if (vVisible == 0) discard;
            vec2 pixel = floor(vec2(gl_FragCoord.x, uArtSize.y - gl_FragCoord.y));
            vec2 delta = vSegment.zw - vSegment.xy;
            float steps = max(1.0, ceil(max(abs(delta.x), abs(delta.y))));
            bool horizontal = abs(delta.x) >= abs(delta.y);
            float distance = horizontal ? delta.x : delta.y;
            float estimate = abs(distance) < 0.0001 ? 0.0 :
                ((horizontal ? pixel.x + 0.5 - vSegment.x : pixel.y + 0.5 - vSegment.y) / distance) * steps;
            int first = max(0, int(floor(estimate)) - 2);
            float nearest = -1.0;
            for (int sampleIndex = 0; sampleIndex < 6; sampleIndex++)
            {
                float index = float(first + sampleIndex);
                if (index > steps) break;
                float amount = index / steps;
                vec2 samplePixel = floor(vSegment.xy + delta * amount);
                if (all(equal(samplePixel, pixel)))
                    nearest = max(nearest, mix(vDepth.x, vDepth.y, amount));
            }
            if (nearest < 0.0) discard;
            float depth = uPerspective ? uDepth.x + uDepth.y * nearest : uDepth.x * vDepth.z + uDepth.y;
            // Match the CPU edge tolerance while leaving the opaque geometry's depth intact.
            gl_FragDepth = clamp(depth * 0.5 + 0.5 - (uPerspective ? abs(uDepth.y) * 0.0000005 : 0.0000001), 0.0, 1.0);
            outputColor = vec4(texelFetch(uPalette, ivec2(vMaterial.y, vMaterial.x), 0).bgr, 1.0);
        }
        """;

    internal const string ResolveFragment = """
        uniform sampler2D uArtwork;
        uniform sampler2D uPhotograph;
        uniform sampler2D uOverlay;
        uniform vec4 uViewport;
        uniform vec2 uLogicalSize;
        uniform ivec2 uArtSize;
        uniform float uPixelSize;
        uniform float uPhotoOpacity;
        uniform bool uHasOverlay;
        uniform bool uArtworkOnly;
        out vec4 outputColor;

        void main()
        {
            vec2 logical = vec2(gl_FragCoord.x - uViewport.x, uViewport.y + uViewport.w - gl_FragCoord.y) *
                uLogicalSize / uViewport.zw;
            ivec2 pixel = clamp(ivec2(floor(logical / uPixelSize)), ivec2(0), uArtSize - ivec2(1));
            pixel.y = uArtSize.y - 1 - pixel.y;
            if (uArtworkOnly)
                pixel = clamp(ivec2(floor(gl_FragCoord.xy - uViewport.xy)), ivec2(0), uArtSize - ivec2(1));
            vec3 color = floor(texelFetch(uArtwork, pixel, 0).rgb * 255.0 + 0.5);
            if (uPhotoOpacity > 0.0)
            {
                vec4 photograph = texelFetch(uPhotograph, pixel, 0);
                if (photograph.a > 0.5)
                    color = floor(color * (1.0 - uPhotoOpacity) +
                        floor(photograph.rgb * 255.0 + 0.5) * uPhotoOpacity);
            }
            vec3 resolved = color / 255.0;
            if (uHasOverlay)
            {
                ivec2 overlaySize = textureSize(uOverlay, 0);
                ivec2 overlayPixel = clamp(ivec2(floor(logical / uLogicalSize * vec2(overlaySize))),
                    ivec2(0), overlaySize - ivec2(1));
                vec4 overlay = texelFetch(uOverlay, overlayPixel, 0);
                resolved = overlay.bgr + resolved * (1.0 - overlay.a);
            }
            outputColor = vec4(resolved, 1.0);
        }
        """;
}
