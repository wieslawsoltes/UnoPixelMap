namespace PixelMap.Rendering;

/// <summary>Linear HDR scattering. Every kernel and scale combination is normalized:
/// a uniform radiance field stays uniform, and no per-channel threshold changes hue.</summary>
internal static class WebGpuBloomShaders
{
    internal const string Common = """
        struct ResolveFrame {sceneSize:vec4f,outputSize:vec4f,settings:vec4f,effects:vec4f,}
        @group(0) @binding(0) var source:texture_2d<f32>;
        @group(0) @binding(1) var detail:texture_2d<f32>;
        @group(0) @binding(2) var linearSampler:sampler;
        @group(0) @binding(3) var<uniform> r:ResolveFrame;
        @vertex fn vs(@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            let p=vec2f(f32((id<<1u)&2u),f32(id&2u));
            return vec4f(p*2.0-1.0,0.0,1.0);
        }
        """;

    internal const string Input = """
        fn sampleInput(uv:vec2f)->vec3f {
            return textureSampleLevel(source,linearSampler,uv,0.0).rgb;
        }
        """;

    internal const string PhotographInput = """
        fn sampleInput(uv:vec2f)->vec3f {
            var color=textureSampleLevel(source,linearSampler,uv,0.0).rgb;
            if(r.settings.z>0.0){
                let photo=textureSampleLevel(detail,linearSampler,uv,0.0);
                if(photo.a>0.5){color=mix(color,photo.rgb,r.settings.z);}
            }
            return color;
        }
        """;

    // A 13-tap overlapping downsample distributes a subpixel highlight over
    // neighbouring destination pixels before decimation. Binary-exact weights
    // sum to one. The source texture's linear sampler supplies the inner 2x2 taps.
    internal const string DownsampleKernel = """
        const DOWN_TAPS:array<vec3f,13> = array<vec3f,13>(
            vec3f(-2.0,-2.0,0.03125),vec3f(0.0,-2.0,0.0625),vec3f(2.0,-2.0,0.03125),
            vec3f(-2.0,0.0,0.0625),vec3f(0.0,0.0,0.125),vec3f(2.0,0.0,0.0625),
            vec3f(-2.0,2.0,0.03125),vec3f(0.0,2.0,0.0625),vec3f(2.0,2.0,0.03125),
            vec3f(-1.0,-1.0,0.125),vec3f(1.0,-1.0,0.125),
            vec3f(-1.0,1.0,0.125),vec3f(1.0,1.0,0.125));
        fn downsample(uv:vec2f)->vec3f {
            let texel=1.0/vec2f(textureDimensions(source));
            var color=vec3f(0.0);
            for(var i=0u;i<13u;i++){
                color+=sampleInput(uv+DOWN_TAPS[i].xy*texel)*DOWN_TAPS[i].z;
            }
            return color;
        }
        """;

    internal const string DownsampleFragment = """
        @fragment fn fs(@builtin(position) p:vec4f)->@location(0) vec4f {
            let dimensions=max(vec2u(1u),(textureDimensions(source)+vec2u(1u))/2u);
            return vec4f(downsample(p.xy/vec2f(dimensions)),1.0);
        }
        """;

    internal const string Prefilter = Common + PhotographInput + DownsampleKernel + DownsampleFragment;
    internal const string Downsample = Common + Input + DownsampleKernel + DownsampleFragment;

    internal const string UpsampleKernel = """
        const UP_TAPS:array<vec3f,9> = array<vec3f,9>(
            vec3f(-1.0,-1.0,0.0625),vec3f(0.0,-1.0,0.125),vec3f(1.0,-1.0,0.0625),
            vec3f(-1.0,0.0,0.125),vec3f(0.0,0.0,0.25),vec3f(1.0,0.0,0.125),
            vec3f(-1.0,1.0,0.0625),vec3f(0.0,1.0,0.125),vec3f(1.0,1.0,0.0625));
        const BLOOM_SCATTER:f32=0.65;
        @fragment fn fs(@builtin(position) p:vec4f)->@location(0) vec4f {
            let uv=p.xy/vec2f(textureDimensions(detail));
            let texel=1.0/vec2f(textureDimensions(source));
            var wide=vec3f(0.0);
            for(var i=0u;i<9u;i++){
                wide+=textureSampleLevel(source,linearSampler,uv+UP_TAPS[i].xy*texel,0.0).rgb*UP_TAPS[i].z;
            }
            let fine=textureSampleLevel(detail,linearSampler,uv,0.0).rgb;
            return vec4f(mix(fine,wide,BLOOM_SCATTER),1.0);
        }
        """;

    internal const string Upsample = Common + UpsampleKernel;
}
