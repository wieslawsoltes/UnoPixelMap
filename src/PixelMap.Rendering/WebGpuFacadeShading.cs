namespace PixelMap.Rendering;

/// <summary>Continuous-style façade filtering in room coordinates. The browser
/// fixture compiles this exact WGSL against an independent spatial reference.</summary>
internal static class WebGpuFacadeShading
{
    internal const string Wgsl = """
        struct FacadeMaterial {
            albedo: vec3f,
            roughness: f32,
            emission: vec3f,
            metallic: f32,
        }
        // Integrate a periodic rectangular window over its axis-aligned pixel
        // footprint. Reducing the phase first preserves tiny footprints on long
        // façades; subtracting endpoint fragments avoids a large primitive sum.
        fn facadeCoverage(room:vec2f,footprint:vec2f,start:vec2f,end:vec2f)->vec2f {
            let width=max(footprint,vec2f(0.0001)); let phase=fract(room);
            let a=phase-width*0.5; let b=phase+width*0.5;
            let first=floor(a); let last=floor(b); let duty=end-start;
            return clamp(((last-first)*duty+clamp(b-last-start,vec2f(0.0),duty)
                -clamp(a-first-start,vec2f(0.0),duty))/width,vec2f(0.0),vec2f(1.0));
        }
        // First moment of the local room coordinate inside the periodic window,
        // divided by the complete footprint (not by window coverage). This keeps
        // Roblox's vertical pane tint smooth through a room or mip transition.
        fn facadeFirstMoment(room:f32,footprint:f32,start:f32,end:f32)->f32 {
            let width=max(footprint,0.0001); let phase=fract(room);
            let a=phase-width*0.5; let b=phase+width*0.5;
            let first=floor(a); let last=floor(b);
            let left=clamp(a-first,start,end); let right=clamp(b-last,start,end);
            return max(0.0,((last-first)*(end*end-start*start)+right*right-left*left)/(2.0*width));
        }
        fn facadeLampSample(room:vec2f,footprint:vec2f,seed:u32)->f32 {
            let start=vec2f(0.17,0.23); let end=vec2f(0.76,0.75);
            let width=clamp(footprint,vec2f(0.0001),vec2f(1.0));
            let phase=fract(room); let a=phase-width*0.5; let b=phase+width*0.5;
            let cell=floor(a);
            let first=max(vec2f(0.0),min(b,cell+end)-max(a,cell+start));
            let second=max(vec2f(0.0),min(b,cell+1.0+end)-max(a,cell+1.0+start));
            // Up to one room per axis, bilinear weights now equal the exact
            // covered-window intersections, including story and seed wrapping.
            let centroid=cell+0.5+second/max(first+second,vec2f(0.000001));
            let period=vec2f(256.0,16.0); let whole=floor(room);
            let wrapped=whole-floor(whole/period)*period;
            let lookup=wrapped+mix(centroid,phase,smoothstep(vec2f(1.0),vec2f(2.0),footprint));
            let lod=clamp(log2(max(1.0,max(footprint.x,footprint.y))),0.0,8.0);
            return textureSampleLevel(rooms,roomSampler,
                (lookup+vec2f(f32(seed&255u),0.0))/period,lod).g;
        }
        fn normalFacade(room:vec2f,footprint:vec2f,seed:u32,wall:vec3f,
            glass:vec3f,lampColor:vec3f,night:bool)->FacadeMaterial {
            let axes=facadeCoverage(room,footprint,vec2f(0.17,0.23),vec2f(0.76,0.75));
            let coverage=axes.x*axes.y;
            let lit=coverage*facadeLampSample(room,footprint,seed);
            let glassLinear=toLinear(glass); let lampLinear=toLinear(lampColor);
            var result:FacadeMaterial;
            result.albedo=toLinear(wall)*(1.0-coverage)+glassLinear*(coverage-lit)+lampLinear*lit;
            // A single GGX lobe uses the coverage-weighted squared roughness.
            // All channels follow the same footprint; no binary specular or
            // emissive window remains behind the filtered albedo.
            result.roughness=sqrt(mix(0.82*0.82,0.18*0.18,coverage));
            result.metallic=0.28*coverage;
            result.emission=lampLinear*(2.5*lit*select(0.0,1.0,night));
            return result;
        }
        fn robloxFacade(room:vec2f,footprint:vec2f,wall:vec3f,glass:vec3f)->FacadeMaterial {
            let axes=facadeCoverage(room,footprint,vec2f(0.18,0.24),vec2f(0.82,0.78));
            let coverage=axes.x*axes.y;
            let tintWeight=coverage*0.12+axes.x*facadeFirstMoment(room.y,footprint.y,0.24,0.78)*0.22;
            let wallLinear=toLinear(clamp(wall*1.12+vec3f(10.0/255.0),vec3f(0.0),vec3f(1.0)));
            let glassLinear=toLinear(glass); let tintLinear=toLinear(vec3f(225,246,250)/255.0);
            var result:FacadeMaterial;
            result.albedo=wallLinear*(1.0-coverage)+glassLinear*coverage+(tintLinear-glassLinear)*tintWeight;
            result.roughness=sqrt(mix(0.32*0.32,0.16*0.16,coverage));
            result.metallic=0.2*coverage;
            result.emission=vec3f(0.0);
            return result;
        }
        """;
}
