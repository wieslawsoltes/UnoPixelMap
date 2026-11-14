namespace PixelMap.Rendering;

/// <summary>Palette decisions, not continuous material filtering. Pure WGSL
/// helpers are also executed by the independent pixel-art readback fixture.</summary>
internal static class WebGpuPixelArtShading
{
    internal const string Wgsl = """
        // Four world-facing tones preserve the warm light/cool shadow ramp as
        // the camera turns. No screen direction or view vector enters the light.
        fn pixelWallBand(normal:vec3f,light:vec3f)->u32 {
            let facing=dot(normalize(normal.xy+vec2f(0.0000001)),normalize(light.xy));
            return min(3u,u32(max(0.0,floor((facing*0.5+0.5)*4.0))));
        }
        fn pixelCluster(footprint:vec2f)->vec2f {
            var group=select(vec2f(1.0),vec2f(2.0),footprint>vec2f(0.22,0.20));
            group=select(group,vec2f(4.0),footprint>vec2f(0.44,0.40));
            return group;
        }
        // The rectangle remains at least two art pixels across each axis. Far
        // façades become quiet planes instead of a filtered field of lit specks.
        // Columns/stories are anchored to the actual façade, not screen pixels.
        // x = wall/glass/sill, yz = coherent lamp cell, w = cluster identity.
        fn pixelFacadeCell(room:vec2f,footprint:vec2f,extent:vec2f)->vec4f {
            let group=pixelCluster(footprint);
            let cluster=room/group; let cell=floor(cluster); let within=fract(cluster);
            var part=0.0;
            if(all(footprint/group<=vec2f(0.22,0.20))&&all(extent/group>=vec2f(1.25,1.5))){
                let across=within.x>=0.20&&within.x<0.76;
                if(across&&within.y>=0.28&&within.y<0.73){part=1.0;}
                let ledge=clamp(footprint.y/group.y,0.07,0.16);
                if(across&&within.y>=0.28-ledge&&within.y<0.28){part=2.0;}
            }
            return vec4f(part,cell,group.x*4.0+group.y);
        }
        // Sparse roof service panels are visible only when the entire motif
        // has a readable footprint. A subpixel panel never becomes a lone dot.
        fn pixelRoofPart(point:vec2f,footprint:vec2f,seed:u32)->i32 {
            let spacing=vec2f(12.0,10.0);
            if(any(footprint/spacing>vec2f(0.12))){return 0;}
            let phase=fract((point+vec2f(f32(seed&3u)*3.0,f32((seed>>2u)&3u)*2.5))/spacing);
            if(phase.x<0.20||phase.x>=0.65||phase.y<0.20||phase.y>=0.65){return 0;}
            if(phase.y<0.20+max(0.04,footprint.y/spacing.y)){return 16;}
            return 15;
        }
        fn pixelEdgeVisible(projectedLength:f32)->bool {return projectedLength>=2.0;}
        fn pixelEdgePart(direction:vec2f,light:vec3f)->i32 {
            let normal=normalize(vec2f(direction.y,direction.x)+vec2f(0.0000001));
            return select(4,3,dot(normal,normalize(light.xy))>0.15);
        }
        fn pixelTerrainColor(color:vec3f,light:f32)->vec3f {
            var tone=vec3f(1.0);
            if(light<0.20){tone=vec3f(0.78,0.84,0.94);}
            else if(light<0.50){tone=vec3f(0.89,0.94,0.99);}
            else if(light>=0.84){tone=vec3f(1.05,1.02,0.97);}
            return floor(clamp(color*tone*255.0,vec3f(0.0),vec3f(255.0))+0.5)/255.0;
        }
        fn pixelSkyColor(sky:vec3f,horizon:vec3f,position:f32)->vec3f {
            let band=min(3.0,floor(clamp(position,0.0,1.0)*4.0))/3.0;
            return floor(mix(sky,horizon,band)*255.0+0.5)/255.0;
        }
        """;
}
