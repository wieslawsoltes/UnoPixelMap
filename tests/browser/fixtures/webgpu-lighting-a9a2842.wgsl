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
