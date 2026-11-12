namespace PixelMap.Rendering;

// WGSL is compiled by the browser's WebGPU implementation. No GLSL translator,
// framebuffer readback or JavaScript drawing context is involved in this path.
internal static class WebGpuMapShaders
{
    internal const string ColorSpace = """
        fn toLinear(c: vec3f) -> vec3f {
            return select(c/12.92, pow((c+0.055)/1.055, vec3f(2.4)), c > vec3f(0.04045));
        }
        fn toSrgb(c: vec3f) -> vec3f {
            return select(c*12.92, 1.055*pow(max(c, vec3f(0.0)), vec3f(1.0/2.4))-0.055, c > vec3f(0.0031308));
        }
        """;

    internal const string Common = ColorSpace + """
        struct Frame {
            screenX: vec4f, screenY: vec4f, screenW: vec4f,
            view: vec4f, ground: vec4f, lens: vec4f, world: vec4f,
            sky: vec4f, horizon: vec4f, groundColor: vec4f,
            quality: vec4f, sun: vec4f, sunColor: vec4f, eye: vec4f,
            shadowRow0: vec4f, shadowRow1: vec4f, shadowRow2: vec4f, shadowRow3: vec4f,
            post: vec4f, misc: vec4f, shadowView: vec4f,
            shadowRow4: vec4f, shadowRow5: vec4f, shadowRow6: vec4f, shadowRow7: vec4f,
            shadowRow8: vec4f, shadowRow9: vec4f, shadowRow10: vec4f, shadowRow11: vec4f,
            shadowSplits: vec4f, shadowTexels: vec4f, shadowDepths: vec4f,
        }
        @group(0) @binding(0) var<uniform> f: Frame;
        @group(0) @binding(1) var palette: texture_2d<f32>;
        @group(0) @binding(2) var rooms: texture_2d<f32>;
        @group(0) @binding(3) var roomSampler: sampler;
        @group(0) @binding(4) var shadows: texture_depth_2d_array;
        @group(0) @binding(5) var shadowSampler: sampler_comparison;
        // Matches WebGpuShadowCascades.BlendFraction. Four metadata components
        // belong to the four layers; count and near are in eye.w and sunColor.w.
        const SHADOW_BLEND_FRACTION: f32 = 0.10;

        fn style() -> u32 { return u32(f.ground.w + 0.5); }
        fn smoothStyle() -> bool { return style() == 1u || style() == 3u; }
        fn perspective() -> bool { return f.world.w > 0.5; }
        fn localFoundation(height: f32) -> f32 { return (height - f.ground.x) * f.ground.y; }
        fn screen(point: vec2f, altitude: f32) -> vec4f {
            let p = vec4f(point, altitude, 1.0);
            return vec4f(dot(f.screenX, p), dot(f.screenY, p), dot(f.screenW, p), altitude);
        }
        fn clipPosition(s: vec4f) -> vec4f {
            let depth = f.view.z * select(s.w, s.z, perspective()) + f.view.w;
            // OpenGL's -w..w depth becomes WebGPU's 0..w without changing the lens.
            return vec4f(2.0*s.x/f.view.x-s.z, s.z-2.0*s.y/f.view.y, (depth+s.z)*0.5, s.z);
        }
        fn shadowPosition(p: vec3f, layer: u32) -> vec4f {
            var x=f.shadowRow0; var y=f.shadowRow1; var z=f.shadowRow2;
            if(layer==1u){x=f.shadowRow3;y=f.shadowRow4;z=f.shadowRow5;}
            if(layer==2u){x=f.shadowRow6;y=f.shadowRow7;z=f.shadowRow8;}
            if(layer==3u){x=f.shadowRow9;y=f.shadowRow10;z=f.shadowRow11;}
            let point=vec4f(p,1.0);
            // Directional light projections are affine: three explicit rows
            // reproduce the previous mat4 transform with homogeneous W = 1.
            return vec4f(dot(x,point),dot(y,point),dot(z,point),1.0);
        }
        fn worldPosition(p: vec2f, z: f32, origin: vec3f) -> vec3f {
            return vec3f(origin.x+p.x*origin.z, -(origin.y+p.y*origin.z), z);
        }
        fn sampleShadow(world: vec3f, normal: vec3f, layer: u32) -> f32 {
            let ndl=clamp(dot(normal,f.sun.xyz),0.0,1.0);
            let texelWorld=f.shadowTexels[layer];
            // Bias follows each cascade's physical texel footprint instead of
            // imposing the old 12 cm normal offset on nearby street geometry.
            let p=shadowPosition(world+normal*texelWorld*(0.5+0.75*(1.0-ndl)),layer);
            let uv = vec2f(p.x*0.5+0.5, 0.5-p.y*0.5);
            if (any(uv < vec2f(0.0)) || any(uv > vec2f(1.0)) || p.z <= 0.0 || p.z >= 1.0) { return 1.0; }
            let texel = 1.0 / vec2f(textureDimensions(shadows));
            let bias=texelWorld*(0.05+0.15*(1.0-ndl))*f.shadowDepths[layer];
            var sum = 0.0;
            for (var y = -1; y <= 1; y++) {
                for (var x = -1; x <= 1; x++) {
                    sum+=textureSampleCompareLevel(shadows,shadowSampler,uv+vec2f(f32(x),f32(y))*texel,i32(layer),p.z-bias);
                }
            }
            return sum/9.0;
        }
        fn shadowVisibility(world: vec3f, normal: vec3f) -> f32 {
            if(f.sun.w<0.5){return 1.0;}
            let count=u32(f.eye.w+0.5);
            let depth=dot(f.shadowView,vec4f(world,1.0));
            if(count==0u||depth<f.sunColor.w||depth>f.shadowSplits[count-1u]){return 1.0;}
            var layer=0u;
            for(var i=0u;i<3u;i++){
                if(layer+1u<count&&depth>f.shadowSplits[layer]){layer+=1u;}
            }
            let result=sampleShadow(world,normal,layer);
            var start=f.sunColor.w;
            if(layer>0u){start=f.shadowSplits[layer-1u];}
            let end=f.shadowSplits[layer];
            let blendStart=end-(end-start)*SHADOW_BLEND_FRACTION;
            if(depth<=blendStart){return result;}
            var next=1.0;
            if(layer+1u<count){next=sampleShadow(world,normal,layer+1u);}
            return mix(result,next,smoothstep(blendStart,end,depth));
        }
        fn lightingLinear(albedoLinear: vec3f, world: vec3f, normalInput: vec3f, roughness: f32, metallic: f32, emission: vec3f) -> vec3f {
            let normal = normalize(normalInput);
            let view = normalize(f.eye.xyz-world);
            let light = normalize(f.sun.xyz);
            let halfDirection = normalize(view+light);
            let ndl = max(dot(normal,light),0.0);
            let ndv = max(dot(normal,view),0.001);
            let ndh = max(dot(normal,halfDirection),0.0);
            let vdh = max(dot(view,halfDirection),0.0);
            let base = albedoLinear;
            let f0 = mix(vec3f(0.04),base,metallic);
            let fresnel = f0+(vec3f(1.0)-f0)*pow(1.0-vdh,5.0);
            let a = max(0.045,roughness*roughness);
            let a2 = a*a;
            let denominator = ndh*ndh*(a2-1.0)+1.0;
            let distribution = a2/(3.14159265*denominator*denominator+0.00001);
            let k = (roughness+1.0)*(roughness+1.0)*0.125;
            let geometry = ndv/(ndv*(1.0-k)+k)*ndl/(ndl*(1.0-k)+k);
            let specular = fresnel*(distribution*geometry/max(0.004,4.0*ndv*ndl));
            let diffuse = (vec3f(1.0)-fresnel)*(1.0-metallic)*base/3.14159265;
            let visibility = shadowVisibility(world,normal);
            let direct = (diffuse+specular)*f.sunColor.rgb*f.quality.z*ndl*visibility;
            let ambientColor = mix(vec3f(0.12,0.10,0.075),vec3f(0.44,0.56,0.70),normal.z*0.5+0.5);
            let ambient = ambientColor*base*f.quality.w;
            let distance = length(f.eye.xyz-world);
            let fog = 1.0-exp(-distance*f.quality.y*exp(-max(world.z,0.0)*0.00032));
            return mix(direct+ambient+emission,toLinear(f.horizon.rgb)*0.9,clamp(fog,0.0,0.92));
        }
        fn lighting(albedoSrgb: vec3f, world: vec3f, normalInput: vec3f, roughness: f32, metallic: f32, emission: vec3f) -> vec3f {
            return lightingLinear(toLinear(albedoSrgb),world,normalInput,roughness,metallic,emission);
        }
        fn material(id: u32, part: i32) -> vec3f {
            return textureLoad(palette, vec2i(part,i32(id)),0).bgr;
        }
        fn mixBytes(a: vec3f,b: vec3f,t:f32)->vec3f { return floor(a*(1.0-t)+b*t); }
        fn lamp(seed: u32,column:i32,story:i32)->bool {
            return textureLoad(rooms,vec2i(i32((seed+u32(column)+u32(story*16))&255u),0),0).r < 0.001;
        }
        """;

    internal const string Background = Common + """
        @vertex fn vs(@builtin(vertex_index) i:u32)->@builtin(position) vec4f {
            let p=vec2f(f32((i<<1u)&2u),f32(i&2u));
            return vec4f(p*2.0-1.0,0.999999,1.0);
        }
        @fragment fn fs(@builtin(position) p:vec4f)->@location(0) vec4f {
            let row=p.y*f.view.y/f.post.y;
            var color=f.groundColor.rgb;
            if(row<f.sky.w){
                color=select(select(f.sky.rgb,f.horizon.rgb,row>=max(0.0,f.sky.w-7.0)),
                    mix(f.sky.rgb,f.horizon.rgb,smoothstep(0.0,max(1.0,f.sky.w),row)),smoothStyle());
            }
            if(smoothStyle()) { color=toLinear(color); }
            return vec4f(color,0.0);
        }
        """;

    internal const string Terrain = Common + """
        @group(1) @binding(0) var artwork: texture_2d_array<f32>;
        @group(1) @binding(1) var heights: texture_2d_array<f32>;
        @group(1) @binding(2) var terrainSampler: sampler;
        struct Tile {
            @location(0) x:vec4f, @location(1) y:vec4f, @location(2) w:vec4f,
            @location(3) layers:vec4f, @location(4) world:vec4f,
        }
        struct TerrainVertex {
            @builtin(position) position:vec4f,
            @location(0) uv:vec2f, @location(1) heightLight:vec2f,
            @location(2) @interpolate(flat) layer:i32,
            @location(3) @interpolate(flat) face:u32,
            @location(4) world:vec3f,
        }
        fn corner(index:u32)->vec2f {
            return vec2f(select(0.0,1.0,index==1u||index==2u),select(0.0,1.0,index>=2u));
        }
        fn makeVertex(tile:Tile,id:u32)->TerrainVertex {
            var o:TerrainVertex;
            let layer=i32(tile.layers.y+0.5);
            var point:vec2f;
            o.face=0u;
            if(style()<2u){
                let grid=vec2i(i32(id%17u),i32(id/17u));
                o.heightLight=textureLoad(heights,grid,layer,0).rg;
                point=vec2f(grid)*256.0;
            }else{
                let beveled=style()==3u;
                let perCell=select(20u,36u,beveled);
                let cellIndex=id/perCell;
                let local=id%perCell;
                let cell=vec2i(i32(cellIndex%8u),i32(cellIndex/8u));
                let samplePoint=cell+vec2i(1);
                o.heightLight=textureLoad(heights,samplePoint,layer,0).rg;
                let top=o.heightLight.x;
                let bevel=textureLoad(heights,vec2i(16),layer,0).r;
                let face=local/4u; let vertex=local%4u;
                o.face=face;
                var within:vec2f;
                if(face==0u){
                    within=corner(vertex);
                    if(beveled){within=vec2f(0.04)+within*0.92;}
                }else{
                    let bevelFace=beveled&&face<=4u;
                    let edge=(face-1u)%4u;
                    within=corner(select((edge+1u)%4u,edge,vertex==0u||vertex==3u));
                    let upper=vertex<2u;
                    if(bevelFace){
                        if(upper){within=vec2f(0.04)+within*0.92;}
                        o.heightLight.x=select(top-bevel,top,upper);
                    }else{
                        var neighbours=array<vec2i,4>(vec2i(0,-1),vec2i(1,0),vec2i(0,1),vec2i(-1,0));
                        let lower=min(top,textureLoad(heights,samplePoint+neighbours[edge],layer,0).r);
                        o.heightLight.x=select(lower,top,upper)-bevel;
                    }
                }
                point=(vec2f(cell)+within)*512.0;
            }
            let z=localFoundation(o.heightLight.x);
            let p=vec4f(point,z*f.ground.z,1.0);
            o.position=clipPosition(vec4f(dot(tile.x,p),dot(tile.y,p),dot(tile.w,p),p.z));
            o.world=worldPosition(point,z,tile.world.xyz);
            o.uv=point/4096.0; o.layer=i32(tile.layers.x+0.5);
            return o;
        }
        @vertex fn vs(tile:Tile,@builtin(vertex_index) id:u32)->TerrainVertex {return makeVertex(tile,id);}
        @vertex fn shadow0_vs(tile:Tile,@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            return shadowPosition(makeVertex(tile,id).world,0u);
        }
        @vertex fn shadow1_vs(tile:Tile,@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            return shadowPosition(makeVertex(tile,id).world,1u);
        }
        @vertex fn shadow2_vs(tile:Tile,@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            return shadowPosition(makeVertex(tile,id).world,2u);
        }
        @vertex fn shadow3_vs(tile:Tile,@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            return shadowPosition(makeVertex(tile,id).world,3u);
        }
        fn elevationColor(altitude:f32)->vec3f{
            var stops=array<f32,7>(-500.0,0.0,500.0,1500.0,3000.0,4500.0,6000.0);
            var colors=array<vec3f,7>(vec3f(66,106,131),vec3f(113,156,120),vec3f(166,180,124),
                vec3f(194,172,128),vec3f(167,150,145),vec3f(217,217,208),vec3f(240,238,229));
            for(var i=1u;i<7u;i++){if(altitude<=stops[i]){return mixBytes(colors[i-1u],colors[i],clamp((altitude-stops[i-1u])/(stops[i]-stops[i-1u]),0.0,1.0));}}
            return colors[6];
        }
        @fragment fn fs(v:TerrainVertex)->@location(0) vec4f {
            let dx=dpdx(v.world); let dy=dpdy(v.world);
            let sampleColor=textureSampleLevel(artwork,terrainSampler,v.uv,v.layer,0.0);
            var color=floor(sampleColor.bgr*255.0+0.5);
            if(f.horizon.w>0.5){color=mixBytes(color,elevationColor(v.heightLight.x),0.64);}
            var shade=clamp(0.72+v.heightLight.y*0.4,0.52,1.16);
            if(style()==0u||style()==2u){shade=round(shade*6.0)/6.0;}
            if(style()==2u){
                if(v.face>0u){
                    color=mixBytes(color,vec3f(126,94,65),0.68);
                    var faces=array<f32,4>(0.68,0.82,1.0,0.88);
                    shade*=faces[(v.face-1u)%4u];
                    if(fract(v.heightLight.x*0.5)<0.065){shade*=0.78;}
                }else{
                    let cell=fract(v.uv*8.0);
                    if(min(min(cell.x,1.0-cell.x),min(cell.y,1.0-cell.y))<0.018){shade*=0.88;}
                }
                let grain=vec2i(floor(v.uv*128.0));
                shade*=0.96+f32((grain.x*13+grain.y*7)&3)*0.022;
            }
            if(style()==3u){
                let mean=dot(color,vec3f(0.2126,0.7152,0.0722));
                color=clamp(vec3f(mean)+(color-vec3f(mean))*1.3+vec3f(9.0),vec3f(0.0),vec3f(255.0));
                if(v.face==0u){
                    let stud=fract(v.uv*32.0)-vec2f(0.5); let radius=length(stud);
                    let rim=smoothstep(0.13,0.16,radius)-smoothstep(0.16,0.19,radius);
                    color*=1.0+rim*select(-0.10,0.11,stud.x+stud.y<0.0);
                }
            }
            if(smoothStyle()){
                var normal=normalize(cross(dx,dy));
                if(style()==3u&&v.face>4u){
                    // Vertical block sides have no Z component to orient. Their
                    // world-space outward directions are north, east, south, west.
                    var sides=array<vec3f,4>(vec3f(0.0,1.0,0.0),vec3f(1.0,0.0,0.0),
                        vec3f(0.0,-1.0,0.0),vec3f(-1.0,0.0,0.0));
                    normal=sides[(v.face-1u)%4u];
                }else if(normal.z<0.0){normal=-normal;}
                color=lighting(color/255.0,v.world,normal,select(0.86,0.38,style()==3u),0.0,vec3f(0.0));
            }else{color=clamp(floor(color*shade),vec3f(0.0),vec3f(255.0))/255.0;}
            return vec4f(color,sampleColor.a);
        }
        """;

    internal const string Buildings = Common + WebGpuFacadeShading.Wgsl + """
        @group(1) @binding(0) var<storage,read> models:array<vec4f>;
        struct Input {
            @location(0) position:vec2f, @location(1) surface:vec3f,
            @location(2) edge:vec2f, @location(3) packed:u32,
        }
        struct BuildingVertex {
            @builtin(position) position:vec4f,
            @location(0) facade:vec2f,
            @location(1) @interpolate(flat) wall:vec4f,
            @location(2) @interpolate(flat) building:vec2f,
            @location(3) @interpolate(flat) model:vec3u,
            @location(4) world:vec3f,
            @location(5) @interpolate(flat) normal:vec3f,
        }
        fn makeVertex(v:Input)->BuildingVertex {
            var o:BuildingVertex;
            let modelIndex=v.packed>>4u;
            let model=models[modelIndex*2u]; let bounds=models[modelIndex*2u+1u];
            let foundation=localFoundation(model.x);
            let height=select(model.w,bounds.z,(v.packed&4u)!=0u);
            let along=select(0.0,v.surface.x,(v.packed&8u)!=0u);
            let altitude=(foundation+height)*f.ground.z;
            let face=v.packed&3u;
            let radius=vec2f(dot(abs(f.screenX.xy),bounds.xy),dot(abs(f.screenY.xy),bounds.xy))*0.5;
            let rise=select(model.w-bounds.z,model.w,foundation==0.0)*f.ground.z*f.lens.x;
            let tiny=!perspective()&&radius.x<0.28&&radius.y+rise<0.5;
            o.position=clipPosition(screen(v.position,altitude));
            if(tiny||(perspective()&&((face==1u&&altitude>=f.lens.y)||(face==2u&&altitude<=f.lens.y)))||(!perspective()&&face==2u)){
                o.position=vec4f(2.0,2.0,2.0,1.0);
            }
            let edgeLength=max(v.surface.x,0.00001);
            let endpoint=v.position+v.edge*(1.0-along/edgeLength);
            let endpointX=dot(f.screenX,vec4f(endpoint,0.0,1.0));
            let span=-dot(f.screenX.xy,v.edge);
            let worldLength=v.surface.x*length(f.screenX.xy);
            o.facade=vec2f(along*length(v.edge)/edgeLength*f.world.z,height);
            if(style()!=0u&&face!=0u){o.facade=v.position*f.world.z;}
            o.wall=vec4f(endpointX,span,worldLength,select(v.surface.y,v.surface.z,perspective()));
            o.building=vec2f(model.w-bounds.z,model.w);
            o.model=vec3u(u32(model.y+0.5),u32(model.z+0.5),face);
            o.world=worldPosition(v.position,foundation+height,f.world.xyz);
            o.normal=select(normalize(vec3f(v.edge.y,v.edge.x,0.000001)),vec3f(0.0,0.0,select(-1.0,1.0,face==1u)),face!=0u);
            return o;
        }
        @vertex fn vs(v:Input)->BuildingVertex {return makeVertex(v);}
        fn buildingShadow(v:Input,layer:u32)->vec4f {
            let model=models[(v.packed>>4u)*2u]; let bounds=models[(v.packed>>4u)*2u+1u];
            let height=select(model.w,bounds.z,(v.packed&4u)!=0u);
            return shadowPosition(worldPosition(v.position,localFoundation(model.x)+height,f.world.xyz),layer);
        }
        @vertex fn shadow0_vs(v:Input)->@builtin(position) vec4f {return buildingShadow(v,0u);}
        @vertex fn shadow1_vs(v:Input)->@builtin(position) vec4f {return buildingShadow(v,1u);}
        @vertex fn shadow2_vs(v:Input)->@builtin(position) vec4f {return buildingShadow(v,2u);}
        @vertex fn shadow3_vs(v:Input)->@builtin(position) vec4f {return buildingShadow(v,3u);}
        fn windowCoverage(room:vec2f,footprint:vec2f)->vec2f {
            let start=vec2f(0.17,0.23); let duty=vec2f(0.76,0.75)-start;
            let width=max(footprint,vec2f(0.0001));
            let a=room-width*0.5; let b=room+width*0.5; let first=floor(a); let last=floor(b);
            return clamp(((last-first)*duty+clamp(b-last-start,vec2f(0.0),duty)-clamp(a-first-start,vec2f(0.0),duty))/width,vec2f(0.0),vec2f(1.0));
        }
        @fragment fn fs(v:BuildingVertex,@builtin(front_facing) front:bool)->@location(0) vec4f {
            let footprint=abs(dpdx(v.facade))+abs(dpdy(v.facade));
            let face=v.model.z; let seed=v.model.x; let mat=v.model.y;
            var color=material(mat,select(2,0,face==1u));
            var filteredAlbedo=vec3f(0.0);
            var roughness=0.82; var metal=0.0; var emission=vec3f(0.0);
            if(face!=0u){
                if(style()==2u){let cell=fract(v.facade*0.5);if(min(min(cell.x,1.0-cell.x),min(cell.y,1.0-cell.y))<0.06){color*=0.78;}}
                if(style()==3u){
                    let stud=fract(v.facade*1.25)-vec2f(0.5);
                    let ring=smoothstep(0.17,0.24,length(stud))-smoothstep(0.27,0.34,length(stud));
                    color=clamp(color*1.1+vec3f(9.0/255.0),vec3f(0.0),vec3f(1.0))*(1.0+ring*select(-0.12,0.18,stud.x+stud.y<0.0));
                    roughness=0.3;
                }
            }else{
                if(!front){discard;}
                let wall=mixBytes(material(mat,2)*255.0,material(mat,1)*255.0,v.wall.w)/255.0;
                color=wall;
                if(style()==2u){
                    let block=fract(v.facade*0.5);let seam=min(min(block.x,1.0-block.x),min(block.y,1.0-block.y));
                    color*=select(1.0,0.76,seam<0.055);
                    if((i32(floor(v.facade.x*0.5))+i32(floor(v.facade.y*0.5)))%3==0){color*=0.94;}
                }else if(style()==3u){
                    let surface=robloxFacade(v.facade/vec2f(2.8,3.6),footprint/vec2f(2.8,3.6),wall,material(mat,5));
                    filteredAlbedo=surface.albedo;roughness=surface.roughness;metal=surface.metallic;
                }else if(style()==1u){
                    let roomMeters=vec2f(3.2+f32((seed>>2u)&3u)*0.6,3.2+f32(seed&3u)*0.22);
                    let surface=normalFacade(v.facade/roomMeters,footprint/roomMeters,seed,
                        wall,material(mat,5),material(mat,6),f.misc.z>0.5);
                    filteredAlbedo=surface.albedo;roughness=surface.roughness;metal=surface.metallic;emission=surface.emission;
                }else{
                    let floorMeters=3.2+f32(seed&3u)*0.22;
                    let bayMeters=3.2+f32((seed>>2u)&3u)*0.6;
                    if(perspective()||style()==1u){
                        let room=v.facade/vec2f(bayMeters,floorMeters);let within=fract(room);
                        let span=max(footprint.x/bayMeters,footprint.y/floorMeters);
                        let glass=within.y>0.23&&within.y<0.75&&within.x>0.17&&within.x<0.76;
                        let lit=lamp(seed,i32(floor(room.x)),i32(floor(room.y)));
                        let window=material(mat,select(5,6,lit));
                        if(span>0.2){
                            let axes=windowCoverage(room,footprint/vec2f(bayMeters,floorMeters));let coverage=axes.x*axes.y;
                            let lod=clamp(log2(max(1.0,span)),0.0,8.0);
                            let lamps=textureSampleLevel(rooms,roomSampler,(room+vec2f(f32(seed),0.0))/vec2f(256.0,16.0),lod).g;
                            color=mix(wall,mix(material(mat,5),material(mat,6),lamps),coverage);
                        }else if(glass){color=window;}
                    }else{
                        if(v.wall.y<0.01||v.building.x*f.ground.z*f.lens.x<0.35){discard;}
                        let floorPixels=max(0.01,f.ground.z*f.lens.x*floorMeters);
                        let bayPixels=f.ground.z*bayMeters*v.wall.y/max(0.01,v.wall.z);
                        let along=v.position.x*f.view.x/f.post.x-v.wall.x;
                        let column=i32(along/max(0.01,bayPixels)); let windowX=along-f32(column)*bayPixels;
                        let rise=v.facade.y*f.ground.z*f.lens.x;let story=i32(rise/floorPixels);let within=rise-f32(story)*floorPixels;
                        let ribbon=(seed&8u)!=0u&&v.building.y*f.ground.z*f.lens.x>9.0*floorPixels;
                        let pilasters=(seed&32u)!=0u&&v.building.y*f.ground.z*f.lens.x>7.0*floorPixels;
                        if(pilasters&&bayPixels>1.9&&(column&3)==0&&windowX<0.9){color=mixBytes(wall*255.0,material(mat,3)*255.0,0.32)/255.0;}
                        let glass=floorPixels>=1.65&&bayPixels>=1.45&&v.building.x*f.ground.z*f.lens.x>floorPixels*1.6&&
                            windowX>bayPixels*select(0.19,0.05,ribbon)&&windowX<bayPixels*select(0.68,0.95,ribbon);
                        if(glass&&within>=floorPixels*0.22&&within<floorPixels*0.75){color=material(mat,select(5,6,lamp(seed,column,story)));}
                    }
                }
            }
            if(smoothStyle()){
                var albedo=filteredAlbedo;
                if(face!=0u){albedo=toLinear(color);}
                color=lightingLinear(albedo,v.world,v.normal,roughness,metal,emission);
            }
            return vec4f(color,1.0);
        }
        """;

    internal const string Rim = Common + """
        @group(1) @binding(0) var<storage,read> models:array<vec4f>;
        struct EdgeInput {@location(0) a:vec4f,@location(1) b:vec4f,}
        struct EdgeVertex {
            @builtin(position) position:vec4f,
            @location(0) @interpolate(flat) color:vec3f,
        }
        @vertex fn vs(v:EdgeInput,@builtin(vertex_index) index:u32)->EdgeVertex {
            var o:EdgeVertex;
            let model=models[u32(v.a.w+0.5)*2u];
            let z=(localFoundation(model.x)+v.a.z)*f.ground.z;
            var a=screen(v.a.xy,z);var b=screen(v.b.xy,z);
            if(a.z<f.lens.w&&b.z<f.lens.w){o.position=vec4f(2,2,2,1);return o;}
            if(a.z<f.lens.w){a=mix(a,b,(f.lens.w-a.z)/(b.z-a.z));}
            if(b.z<f.lens.w){b=mix(b,a,(f.lens.w-b.z)/(a.z-b.z));}
            let p=a.xy/a.z;let q=b.xy/b.z;
            let delta=q-p;let direction=normalize(delta+vec2f(0.000001,0.0));let normal=vec2f(-direction.y,direction.x);
            let endpoint=index<2u;let positive=index==1u||index==3u;
            let point=select(p-direction*0.5,q+direction*0.5,endpoint)+normal*select(-0.5,0.5,positive);
            let s=select(a,b,endpoint);
            let ndc=clipPosition(s);let bias=select(0.0000001,abs(f.view.w)*0.0000005,perspective());
            o.position=vec4f((point.x*2.0/f.view.x-1.0)*s.z,(1.0-point.y*2.0/f.view.y)*s.z,ndc.z-bias*s.z,s.z);
            o.color=material(u32(model.z+0.5),select(4,3,delta.x>=0.0));
            if(smoothStyle()){o.color=toLinear(o.color);}
            return o;
        }
        @fragment fn fs(v:EdgeVertex)->@location(0) vec4f {return vec4f(v.color,1.0);}
        """;

    internal const string Resolve = """
        struct ResolveFrame {
            sceneSize:vec4f, outputSize:vec4f, settings:vec4f, effects:vec4f,
        }
        @group(0) @binding(0) var<uniform> r:ResolveFrame;
        @group(0) @binding(1) var scene:texture_2d<f32>;
        @group(0) @binding(2) var photograph:texture_2d<f32>;
        @group(0) @binding(3) var overlay:texture_2d<f32>;
        @group(0) @binding(4) var linearSampler:sampler;
        @vertex fn vs(@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            let p=vec2f(f32((id<<1u)&2u),f32(id&2u));return vec4f(p*2.0-1.0,0.0,1.0);
        }
        fn sampleScene(uv:vec2f)->vec3f{return textureSampleLevel(scene,linearSampler,uv,0.0).rgb;}
        fn luma(c:vec3f)->f32{return dot(c,vec3f(0.299,0.587,0.114));}
        fn aces(x:vec3f)->vec3f{return clamp((x*(2.51*x+0.03))/(x*(2.43*x+0.59)+0.14),vec3f(0.0),vec3f(1.0));}
        fn srgb(c:vec3f)->vec3f{return select(c*12.92,1.055*pow(max(c,vec3f(0.0)),vec3f(1.0/2.4))-0.055,c>vec3f(0.0031308));}
        @fragment fn fs(@builtin(position) p:vec4f)->@location(0) vec4f {
            let uv=p.xy/r.outputSize.xy;
            var color:vec3f;
            if(r.settings.x<0.5){
                let logical=uv*r.outputSize.zw;
                let pixel=clamp(vec2i(floor(logical/r.settings.y)),vec2i(0),vec2i(r.sceneSize.xy)-vec2i(1));
                color=textureLoad(scene,pixel,0).rgb;
                if(r.settings.z>0.0){let photo=textureLoad(photograph,pixel,0);if(photo.a>0.5){color=floor(floor(color*255.0+0.5)*(1.0-r.settings.z)+floor(photo.rgb*255.0+0.5)*r.settings.z)/255.0;}}
            }else{
                color=sampleScene(uv);
                let texel=1.0/r.sceneSize.xy;
                if(r.effects.y>0.5){
                    let nw=sampleScene(uv+vec2f(-1.0,-1.0)*texel);let ne=sampleScene(uv+vec2f(1.0,-1.0)*texel);
                    let sw=sampleScene(uv+vec2f(-1.0,1.0)*texel);let se=sampleScene(uv+vec2f(1.0,1.0)*texel);
                    let minimum=min(luma(color),min(min(luma(nw),luma(ne)),min(luma(sw),luma(se))));
                    let maximum=max(luma(color),max(max(luma(nw),luma(ne)),max(luma(sw),luma(se))));
                    if(maximum-minimum>max(0.0312,maximum*0.125)){
                        var direction=vec2f(-((luma(nw)+luma(ne))-(luma(sw)+luma(se))),((luma(nw)+luma(sw))-(luma(ne)+luma(se))));
                        let reduction=max((luma(nw)+luma(ne)+luma(sw)+luma(se))*0.03125,0.0078125);
                        direction=clamp(direction/(min(abs(direction.x),abs(direction.y))+reduction),vec2f(-8.0),vec2f(8.0))*texel;
                        color=(sampleScene(uv+direction*(-1.0/6.0))+sampleScene(uv+direction*(1.0/6.0)))*0.5;
                    }
                }
                if(r.effects.x>0.0){
                    var bloom=vec3f(0.0);
                    for(var y=-1;y<=1;y++){for(var x=-1;x<=1;x++){
                        bloom+=max(vec3f(0.0),sampleScene(uv+vec2f(f32(x),f32(y))*texel*2.5)-vec3f(1.0));
                    }}
                    color+=bloom*(r.effects.x/9.0);
                }
                if(r.settings.z>0.0){
                    let photo=textureSampleLevel(photograph,linearSampler,uv,0.0);
                    if(photo.a>0.5){color=mix(color,photo.rgb,r.settings.z);}
                }
                color=srgb(aces(color*r.effects.z));
            }
            if(r.settings.w>0.5){
                let over=textureSampleLevel(overlay,linearSampler,uv,0.0);
                color=over.bgr+color*(1.0-over.a);
            }
            if(r.effects.w>0.5){
                color=select(color/12.92,pow((color+0.055)/1.055,vec3f(2.4)),color>vec3f(0.04045));
            }
            // Uno's target uses premultiplied-alpha color. Map output is opaque.
            return vec4f(color,1.0);
        }
        """;

    internal const string Overlay = """
        struct OverlaySettings {linearOutput:vec4f,}
        @group(0) @binding(0) var atlas:texture_2d<f32>;
        @group(0) @binding(1) var atlasSampler:sampler;
        @group(0) @binding(2) var<uniform> settings:OverlaySettings;
        struct VertexIn {
            @location(0) position:vec4f,@location(1) color:vec4f,
            @location(2) uv:vec2f,@location(3) textured:f32,
        }
        struct VertexOut {
            @builtin(position) position:vec4f,@location(0) color:vec4f,
            @location(1) uv:vec2f,@location(2) @interpolate(flat) textured:f32,
        }
        @vertex fn vs(v:VertexIn)->VertexOut {var o:VertexOut;o.position=v.position;o.color=v.color;o.uv=v.uv;o.textured=v.textured;return o;}
        @fragment fn fs(v:VertexOut)->@location(0) vec4f {
            var color=v.color;
            if(v.textured>0.5){color*=textureSampleLevel(atlas,atlasSampler,v.uv,0.0).bgra;}
            if(settings.linearOutput.x>0.5&&color.a>0.0001){
                let c=color.rgb/color.a;
                color=vec4f(select(c/12.92,pow((c+0.055)/1.055,vec3f(2.4)),c>vec3f(0.04045))*color.a,color.a);
            }
            return color;
        }
        """;
}
