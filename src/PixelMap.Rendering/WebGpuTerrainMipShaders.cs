namespace PixelMap.Rendering;

internal static class WebGpuTerrainMipShaders
{
    // Source is a one-level sRGB array view, so textureLoad returns linear RGB.
    // Storage textures cannot use an sRGB format: encode the averaged RGB into
    // ordinary RGBA8, then copy those bytes into the final artwork mip. BGRA
    // source channel order is deliberately unchanged; the terrain shader swizzles.
    internal const string Reduce = """
        @group(0) @binding(0) var source: texture_2d_array<f32>;
        @group(0) @binding(1) var destination: texture_storage_2d_array<rgba8unorm, write>;
        @group(0) @binding(2) var<storage, read> dirtyLayers: array<u32>;

        fn encodeSrgb(value: vec3f) -> vec3f {
            return select(value * 12.92,
                1.055 * pow(max(value, vec3f(0.0)), vec3f(1.0 / 2.4)) - 0.055,
                value > vec3f(0.0031308));
        }

        @compute @workgroup_size(8, 8, 1)
        fn reduce(@builtin(global_invocation_id) id: vec3u) {
            let extent = max(textureDimensions(source, 0) / 2u, vec2u(1u));
            if (any(id.xy >= extent) || id.z >= arrayLength(&dirtyLayers)) { return; }
            let layer = i32(dirtyLayers[id.z]);
            if (layer < 0 || layer >= i32(textureNumLayers(source)) ||
                layer >= i32(textureNumLayers(destination))) { return; }
            let p = vec2i(id.xy * 2u);
            let average = (textureLoad(source, p, layer, 0) +
                textureLoad(source, p + vec2i(1, 0), layer, 0) +
                textureLoad(source, p + vec2i(0, 1), layer, 0) +
                textureLoad(source, p + vec2i(1, 1), layer, 0)) * 0.25;
            textureStore(destination, vec2i(id.xy), layer,
                vec4f(encodeSrgb(average.rgb), average.a));
        }
        """;
}
