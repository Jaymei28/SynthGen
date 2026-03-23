namespace SynthGen.Rendering;

/// <summary>
/// Embedded GLSL shader source strings for all render passes.
/// </summary>
public static class ShaderSources
{
    // === PBR Vertex Shader ===================================================
    public const string PBR_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 3) in vec3 aTangent;
layout(location = 4) in ivec4 aBoneIDs;
layout(location = 5) in vec4 aWeights;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;
uniform bool uHasSkinning;
uniform mat4 uBones[100];

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out vec3 vTangent;

void main() {
    mat4 skinMat = mat4(1.0);
    if (uHasSkinning) {
        skinMat = uBones[aBoneIDs.x] * aWeights.x +
                  uBones[aBoneIDs.y] * aWeights.y +
                  uBones[aBoneIDs.z] * aWeights.z +
                  uBones[aBoneIDs.w] * aWeights.w;
    }

    vec4 worldPos = uModel * skinMat * vec4(aPos, 1.0);
    vWorldPos = worldPos.xyz;
    
    mat3 skinNormal = mat3(skinMat);
    vNormal = normalize(uNormalMatrix * skinNormal * aNormal);
    vTangent = normalize(uNormalMatrix * skinNormal * aTangent);
    vUV = aUV;
    gl_Position = uProjection * uView * worldPos;
}
";

    // === PBR Fragment Shader =================================================
    public const string PBR_FRAG = @"
#version 450 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in vec3 vTangent;

uniform vec4  uBaseColor;
uniform float uSmoothness;
uniform float uMetallic;
uniform float uNormalScale;
uniform vec3  uEmissiveColor;
uniform float uEmissiveIntensity;

uniform vec3  uCameraPos;
uniform int   uHasTexture;
uniform sampler2D uAlbedoTex;
uniform int   uHasNormalMap;
uniform sampler2D uNormalTex;

// Lights
uniform vec3  uLightDir;
uniform vec3  uLightColor;
uniform float uLightIntensity;
uniform vec3  uAmbient;

out vec4 FragColor;

void main() {
    // 1. Albedo & Alpha
    vec3 albedo = uBaseColor.rgb;
    if (uHasTexture == 1) {
        vec4 texColor = texture(uAlbedoTex, vUV);
        albedo *= pow(texColor.rgb, vec3(2.2)); // sRGB to Linear
    }

    // 2. Normal Mapping (TBN)
    vec3 N = normalize(vNormal);
    if (uHasNormalMap == 1) {
        vec3 T = normalize(vTangent);
        // Gram-Schmidt orthogonalize
        T = normalize(T - dot(T, N) * N);
        vec3 B = cross(N, T);
        mat3 TBN = mat3(T, B, N);
        
        vec3 nm = texture(uNormalTex, vUV).rgb * 2.0 - 1.0;
        nm.xy *= uNormalScale;
        N = normalize(TBN * nm);
    }

    // 3. Shading
    vec3 L = normalize(-uLightDir);
    vec3 V = normalize(uCameraPos - vWorldPos);
    vec3 H = normalize(L + V);

    // Diffuse
    float diff = max(dot(N, L), 0.0);

    // Specular (Roughness-based Blinn)
    float roughness = 1.0 - uSmoothness;
    float specPower = mix(16.0, 2048.0, uSmoothness * uSmoothness);
    float spec = pow(max(dot(N, H), 0.0), specPower);
    float fresnel = uMetallic + (1.0 - uMetallic) * pow(1.0 - max(dot(V, H), 0.0), 5.0);

    vec3 diffuse = albedo * diff * uLightColor * uLightIntensity;
    vec3 specular = vec3(spec * fresnel * uSmoothness) * uLightColor * uLightIntensity;
    vec3 ambient = uAmbient * albedo;
    
    // 4. Emissive
    vec3 emissive = uEmissiveColor * uEmissiveIntensity;

    vec3 color = ambient + diffuse + specular + emissive;

    // Standard ACES-ish tone mapping + Gamma correction
    color = color / (color + vec3(1.0));
    color = pow(color, vec3(1.0/2.2));

    FragColor = vec4(color, uBaseColor.a);
}
";

    // === Segmentation Shaders ================================================
    public const string SEG_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
layout(location = 4) in ivec4 aBoneIDs;
layout(location = 5) in vec4 aWeights;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform bool uHasSkinning;
uniform mat4 uBones[100];

void main() {
    mat4 skinMat = mat4(1.0);
    if (uHasSkinning) {
        skinMat = uBones[aBoneIDs.x] * aWeights.x +
                  uBones[aBoneIDs.y] * aWeights.y +
                  uBones[aBoneIDs.z] * aWeights.z +
                  uBones[aBoneIDs.w] * aWeights.w;
    }
    gl_Position = uProjection * uView * uModel * skinMat * vec4(aPos, 1.0);
}
";

    public const string SEG_FRAG = @"
#version 450 core
uniform vec3 uSegColor;
out vec4 FragColor;

void main() {
    FragColor = vec4(uSegColor, 1.0);
}
";

    // === Depth Shaders =======================================================
    public const string DEPTH_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
layout(location = 4) in ivec4 aBoneIDs;
layout(location = 5) in vec4 aWeights;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform bool uHasSkinning;
uniform mat4 uBones[100];

out float vDepth;

void main() {
    mat4 skinMat = mat4(1.0);
    if (uHasSkinning) {
        skinMat = uBones[aBoneIDs.x] * aWeights.x +
                  uBones[aBoneIDs.y] * aWeights.y +
                  uBones[aBoneIDs.z] * aWeights.z +
                  uBones[aBoneIDs.w] * aWeights.w;
    }
    vec4 viewPos = uView * uModel * skinMat * vec4(aPos, 1.0);
    vDepth = -viewPos.z;
    gl_Position = uProjection * viewPos;
}
";

    public const string DEPTH_FRAG = @"
#version 450 core
in float vDepth;

uniform float uNear;
uniform float uFar;

out vec4 FragColor;

void main() {
    float linearDepth = (vDepth - uNear) / (uFar - uNear);
    linearDepth = clamp(linearDepth, 0.0, 1.0);
    FragColor = vec4(vec3(linearDepth), 1.0);
}
";

    // === Grid Shaders ========================================================
    public const string GRID_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;

uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vWorldPos;

void main() {
    vWorldPos = aPos;
    gl_Position = uProjection * uView * vec4(aPos, 1.0);
}
";

    public const string GRID_FRAG = @"
#version 450 core
in vec3 vWorldPos;
out vec4 FragColor;

void main() {
    float distFromCenter = length(vWorldPos.xz);
    float fade = 1.0 - smoothstep(30.0, 50.0, distFromCenter);

    // Grid lines
    vec2 grid = abs(fract(vWorldPos.xz + 0.5) - 0.5) / fwidth(vWorldPos.xz);
    float lineX = min(grid.x, 1.0);
    float lineZ = min(grid.y, 1.0);
    float line = 1.0 - min(lineX, lineZ);

    // Axis highlights
    float axisX = 1.0 - smoothstep(0.0, 0.1, abs(vWorldPos.z));
    float axisZ = 1.0 - smoothstep(0.0, 0.1, abs(vWorldPos.x));

    vec3 color = vec3(0.35) * line;
    color = mix(color, vec3(1.0, 0.3, 0.3), axisX); // X axis red
    color = mix(color, vec3(0.3, 0.3, 1.0), axisZ); // Z axis blue

    FragColor = vec4(color, line * fade * 0.5);
}
";

    // === Ocean Shaders =======================================================
    public const string OCEAN_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;
uniform vec3  uCameraPos;

// Skunkworks Wave Params
uniform float uLevel;
uniform float uWindSpeed;
uniform float uWindDirection;
uniform float uStormIntensity;
uniform float uSteepness;
uniform float uChaos;
uniform float uTimeMultiplier;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out float vHeight;

    const vec4 WAVES[8] = vec4[](
        vec4(1.0,  0.0,  0.4,  60.0),
        vec4(0.3,  0.9,  0.3,  31.0),
        vec4(-0.4, 0.7,  0.25, 17.5),
        vec4(0.6,  -0.6, 0.2,  12.3),
        vec4(-0.8, -0.2, 0.15, 7.3),
        vec4(0.2,  0.8,  0.12, 4.2),
        vec4(0.5,  0.5,  0.1,  2.7),
        vec4(-0.1, 0.9,  0.08, 1.5)
    );
    const float SPEEDS[8] = float[](1.1, 1.5, 1.9, 2.2, 2.5, 2.8, 3.2, 3.5);

    void main() {
        float angle = uWindDirection * 3.14159 / 180.0;
        float cosA = cos(angle);
        float sinA = sin(angle);
        float baseAmp = (uWindSpeed / 10.0) * (1.0 + uStormIntensity * 1.5);

        vec3 pos = aPos;
        vec3 displacement = vec3(0.0, uLevel, 0.0);
        vec3 tangent = vec3(1.0, 0.0, 0.0);
        vec3 binormal = vec3(0.0, 0.0, 1.0);

        for (int i = 0; i < 8; i++) {
            vec4 w = WAVES[i];
            
            // Direction rotation
            float dx = w.x * cosA - w.y * sinA;
            float dz = w.x * sinA + w.y * cosA;
            
            // Deterministic Chaos (more sensitive)
            float chaosVal = (fract(sin(float(i) * 785.1)) - 0.5) * uChaos;
            dx += chaosVal;
            dz += chaosVal;
            
            vec2 dir = normalize(vec2(dx, dz));
            float k = 6.28318 / w.w;
            float a = (w.w / 120.0) * baseAmp; // Significantly reduced amplitude for rounding
            float s_i = uSteepness * (w.w / 80.0); // Balanced steepness for organic feel
            
            float phase = k * (dot(dir, pos.xz) - SPEEDS[i] * uTime * uTimeMultiplier);
            float c = cos(phase);
            float s = sin(phase);
            
            displacement.y += a * s;
            displacement.x += s_i * a * dir.x * c;
            displacement.z += s_i * a * dir.y * c;
            
            float wa = k * a;
            tangent.y += dir.x * wa * c;
            tangent.x -= dir.x * dir.x * s_i * wa * s;
            tangent.z -= dir.x * dir.y * s_i * wa * s;

            binormal.y += dir.y * wa * c;
            binormal.x -= dir.x * dir.y * s_i * wa * s;
            binormal.z -= dir.y * dir.y * s_i * wa * s;
        }
        
        pos += displacement;
        vWorldPos = pos;
        vNormal = normalize(cross(binormal, tangent));
        vUV = aUV;
        vHeight = displacement.y - uLevel;

        gl_Position = uProjection * uView * vec4(pos, 1.0);
    }
    ";

    public const string OCEAN_DEPTH_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;
uniform float uLevel;
uniform float uWindSpeed;
uniform float uWindDirection;
uniform float uStormIntensity;
uniform float uSteepness;
uniform float uChaos;
uniform float uTimeMultiplier;

out float vDepth;

const vec4 WAVES[8] = vec4[](
    vec4(1.0,  0.0,  0.4,  60.0),
    vec4(0.3,  0.9,  0.3,  31.0),
    vec4(-0.4, 0.7,  0.25, 17.5),
    vec4(0.6,  -0.6, 0.2,  12.3),
    vec4(-0.8, -0.2, 0.15, 7.3),
    vec4(0.2,  0.8,  0.12, 4.2),
    vec4(0.5,  0.5,  0.1,  2.7),
    vec4(-0.1, 0.9,  0.08, 1.5)
);
const float SPEEDS[8] = float[](1.1, 1.5, 1.9, 2.2, 2.5, 2.8, 3.2, 3.5);

void main() {
    float angle = uWindDirection * 3.14159 / 180.0;
    float cosA = cos(angle); float sinA = sin(angle);
    float baseAmp = (uWindSpeed / 10.0) * (1.0 + uStormIntensity * 1.5);
    vec3 pos = aPos;
    vec3 displacement = vec3(0.0, uLevel, 0.0);
    for (int i = 0; i < 8; i++) {
        vec2 dir = normalize(vec2(WAVES[i].x * cosA - WAVES[i].y * sinA + (fract(sin(float(i) * 785.1)) - 0.5) * uChaos,
                                  WAVES[i].x * sinA + WAVES[i].y * cosA + (fract(sin(float(i) * 785.1)) - 0.5) * uChaos));
        float k = 6.28318 / WAVES[i].w;
        float a = (WAVES[i].w / 120.0) * baseAmp;
        float phase = k * (dot(dir, pos.xz) - SPEEDS[i] * uTime * uTimeMultiplier);
        displacement.y += a * sin(phase);
        displacement.x += uSteepness * (WAVES[i].w / 80.0) * a * dir.x * cos(phase);
        displacement.z += uSteepness * (WAVES[i].w / 80.0) * a * dir.y * cos(phase);
    }
    vec4 viewPos = uView * vec4(aPos + displacement, 1.0);
    vDepth = -viewPos.z;
    gl_Position = uProjection * viewPos;
}
";

    public const string OCEAN_DEPTH_FRAG = @"
#version 450 core
in float vDepth;
uniform float uNear;
uniform float uFar;
out vec4 FragColor;
void main() {
    float linearDepth = (vDepth - uNear) / (uFar - uNear);
    FragColor = vec4(vec3(clamp(linearDepth, 0.0, 1.0)), 1.0);
}
";

    public const string OCEAN_FRAG = @"
#version 450 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in float vHeight;

uniform vec3  uCameraPos;
uniform vec3  uLightDir;
uniform vec3  uLightColor;
uniform float uLightIntensity;
uniform vec3  uRefractionColor;
uniform vec3  uScatteringColor;
uniform float uFoamAmount;
uniform float uSparkleIntensity;
uniform float uMicroRipple;
uniform float uReflectionSaturation;
uniform float uHorizonFade;

uniform sampler2D uHdri;
uniform float     uHdriStrength;
uniform int       uHasHdri;
uniform float     uTime;
uniform int       uWeatherType;
uniform float     uWeatherIntensity;
uniform float     uLightning;

out vec4 FragColor;

const vec2 invAtan = vec2(0.1591, 0.3183);
vec2 SampleEquirectangularMap(vec3 v) {
    vec2 uv = vec2(atan(v.z, v.x), asin(v.y));
    uv *= invAtan;
    uv.x += 0.5;
    uv.y = 0.5 - uv.y;
    return uv;
}

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f*f*(3.0-2.0*f);
    return mix(mix(hash(i + vec2(0,0)), hash(i + vec2(1,0)), f.x),
               mix(hash(i + vec2(0,1)), hash(i + vec2(1,1)), f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 3; i++) {
        v += a * noise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec3 V = normalize(uCameraPos - vWorldPos);
    vec3 N_surf = normalize(vNormal);
    
    // Check if we are viewing the underside (underwater)
    bool isUnderwater = dot(N_surf, V) < 0.0;
    vec3 N = isUnderwater ? -N_surf : N_surf;
    
    float distToCam = length(vWorldPos - uCameraPos);
    
    // 1. High-Frequency Micro-Normal Detail
    vec2 uvDetail = vWorldPos.xz * 1.5;
    float m1 = noise(uvDetail * 4.0 + uTime * 0.2);
    float m2 = noise(uvDetail * 15.0 - uTime * 0.5);
    float microDetail = (m1 + m2 * 0.5) * uMicroRipple * smoothstep(500.0, 50.0, distToCam);
    N = normalize(N + vec3(microDetail * 0.1, 0.0, microDetail * 0.1));

    // 2. Procedural Rain Splashes & Glowy Impacts
    vec3 splashCol = vec3(0.0);
    if (uWeatherType == 1 && uWeatherIntensity > 0.1) {
        float sI = uWeatherIntensity * smoothstep(400.0, 50.0, distToCam);
        vec2 p = vWorldPos.xz * 3.5; 
        float rAcc = 0.0;
        for(int i=0; i<6; i++) { // Increased iterations from 3 to 6
            float shift = float(i) * 55.67;
            vec2 g = floor(p + shift);
            vec2 fv = fract(p + shift);
            float h = hash(g + shift);
            if (h > 0.6) { // Lowered threshold from 0.8 to 0.6
                float t = fract(uTime * 2.5 + h * 6.28);
                float d = length(fv - 0.5);
                float mask = smoothstep(0.5, 0.0, d) * (1.0 - t);
                rAcc += sin(d * 48.0 - t * 35.0) * mask * sI * 1.5;
                splashCol += vec3(0.9, 0.95, 1.0) * smoothstep(0.12, 0.0, abs(d - t * 0.5)) * mask * sI * 2.0;
            }
        }
        N = normalize(N + vec3(rAcc, 0.0, rAcc) * 0.15);
    }

    vec3 L = normalize(-uLightDir);
    vec3 R = reflect(-V, N);
    vec3 H = normalize(L + V);

    // 2. Advanced Fresnel & Refraction
    float dotNV = max(dot(N, V), 0.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - dotNV, 5.0);

    // Underwater Total Internal Reflection approximation
    if (isUnderwater) {
        fresnel = 0.1 + 0.9 * pow(1.0 - dotNV, 3.0);
    }

    float depthFactor = smoothstep(-2.0, 2.0, vHeight);
    vec3 waterBase = mix(uScatteringColor * 0.1, uRefractionColor, dotNV * 0.7 + 0.3);
    waterBase = mix(waterBase * 0.4, waterBase, depthFactor); 
    
    // If underwater, darken the base water color
    if (isUnderwater) {
        waterBase *= 0.6;
    }

    // 3. Environment Reflection
    vec3 reflection = vec3(0.0);
    if (uHasHdri == 1) {
        vec3 sampleR = R;
        // Above water: clamp to horizon. Below water: allow looking down to floor/darkness
        if (!isUnderwater) {
            sampleR.y = max(sampleR.y, 0.05); 
        }
        
        reflection = texture(uHdri, SampleEquirectangularMap(sampleR)).rgb;
        
        float lum = dot(reflection, vec3(0.299, 0.587, 0.114));
        reflection = mix(vec3(lum), reflection, uReflectionSaturation);
        reflection *= uHdriStrength;
    } else {
        reflection = mix(uScatteringColor * 0.5, vec3(0.8, 0.9, 1.0), R.y * 0.5 + 0.5);
    }

    // 4. Subsurface Scattering (Backlit Glow)
    float sss = pow(max(dot(V, -L), 0.0), 8.0) * smoothstep(-1.0, 1.0, vHeight) * 0.5;
    vec3 sssColor = uRefractionColor * sss * uLightIntensity;

    // 5. Specular Highlights & Sparkles - MATTE FINISH
    float specBase = pow(max(dot(N, H), 0.0), 16.0); // MUCH ROUGHER
    float sparkleNoise = noise(vWorldPos.xz * 100.0 + uTime * 1.5);
    float sparkles = pow(max(dot(N, H), 0.0), 12.0) * sparkleNoise * uSparkleIntensity;
    vec3 specular = (specBase * 0.15 + sparkles * 0.1) * uLightColor * uLightIntensity; 
    
    if (isUnderwater) {
        specular *= 0.2;
    }

    // 6. Organic Foam
    float foam = 0.0;
    if (!isUnderwater) {
        float crest = smoothstep(0.15, 0.45, vHeight);
        float foamNoise = fbm(vWorldPos.xz * 30.0 + uTime * 0.1);
        float foamPattern = smoothstep(0.4, 0.6, foamNoise);
        foam = crest * foamPattern * uFoamAmount;
    }

    // Final Composition - Aggressively dampen reflections for storm water
    float reflectionStrength = isUnderwater ? 0.3 : 0.25; 
    vec3 color = mix(waterBase, reflection, fresnel * reflectionStrength);
    
    color += sssColor * 0.3;
    color += specular;
    color += splashCol; // ADD GLOWY SPLASHES
    
    // LIGHTNING REFLECTION - also dampened to avoid chrome look
    color += vec3(0.7, 0.85, 1.0) * uLightning * 1.2 * dotNV;
    
    if (foam > 0.0) {
        color = mix(color, vec3(0.95, 1.0, 1.0), foam);
    }

    // Horizon Atmospheric Blend
    if (!isUnderwater) {
        float horizonMask = smoothstep(200.0, 1000.0, distToCam);
        color = mix(color, reflection, horizonMask * uHorizonFade);
    }

    color = color / (color + vec3(1.0));
    color = pow(color, vec3(1.0/2.2));
    
    // MUCH MORE OPAQUE (especially in storm)
    float baseAlpha = isUnderwater ? 0.4 : 0.85; 
    float alpha = mix(baseAlpha, 0.98, fresnel + foam + uLightning * 0.5);
    
    FragColor = vec4(color, alpha);
}
";

    // === Screen Quad (for post-processing) ===================================
    public const string SCREEN_VERT = @"
#version 450 core
layout(location = 0) in vec2 aPos;
layout(location = 0) out vec2 vUV;

void main() {
    vUV = aPos * 0.5 + vec2(0.5);
    gl_Position = vec4(aPos, 0.0, 1.0);
}
";

    // === Post-Process: Bloom =================================================
    public const string BLOOM_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uThreshold;
uniform float uIntensity;

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));
    vec3 bloom = step(uThreshold, brightness) * color;
    FragOut = vec4(color + (bloom * uIntensity), 1.0);
}
";

    // === Post-Process: Fog ===================================================
    public const string FOG_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform sampler2D uDepth;
uniform float uDensity;
uniform vec3 uFogColor;

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    float depth = texture(uDepth, vUV).r;
    float fog = clamp(1.0 - exp(-uDensity * depth * 3.0), 0.0, 1.0);
    FragOut = vec4(mix(color, uFogColor, fog), 1.0);
}
";

    public const string WEATHER_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;

uniform sampler2D uScene;
uniform sampler2D uDepth;
uniform float uIntensity;
uniform float uTime;
uniform int   uType; 
uniform float uLightning;

uniform mat4 uInvView;
uniform mat4 uInvProj;
uniform vec3 uCameraPos;

uniform float uWindSpeed;
uniform float uWindDirection;

float hash12(vec2 p) {
    vec3 p3  = fract(vec3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + vec3(33.33));
    return fract((p3.x + p3.y) * p3.z);
}

float noise_ws(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f*f*(3.0-2.0*f);
    return mix(mix(hash12(i + vec2(0,0)), hash12(i + vec2(1,0)), f.x),
               mix(hash12(i + vec2(0,1)), hash12(i + vec2(1,1)), f.x), f.y);
}

void main() {
    float rawDepth = texture(uDepth, vUV).r;
    float intensity = clamp(uIntensity, 0.0, 1.0);
    
    if (uType == 0 || intensity < 0.01) {
        FragOut = vec4(texture(uScene, vUV).rgb, 1.0);
        return;
    }

    // 1. Perspective Reconstruction
    vec4 clipPos = vec4(vUV * 2.0 - 1.0, 1.0, 1.0);
    vec4 viewPos = uInvProj * clipPos;
    viewPos /= viewPos.w;
    vec3 rayDir = normalize((uInvView * vec4(viewPos.xyz, 0.0)).xyz);

    // 2. ATMOSPHERIC FOG
    float linearDepth = rawDepth * 1000.0;
    float fog = clamp(1.0 - exp(-linearDepth * intensity * 0.0015), 0.0, 1.0);
    
    vec3 baseHaze = (uType == 1) ? vec3(0.4, 0.45, 0.5) : vec3(0.9, 0.92, 0.95);
    vec3 hazeCol = baseHaze + vec3(0.5, 0.7, 1.0) * uLightning * 3.0;
    
    vec3 sceneCol = texture(uScene, vUV).rgb;
    float gray = dot(sceneCol, vec3(0.2126, 0.7152, 0.0722));
    sceneCol = mix(sceneCol, vec3(gray), fog * 0.3 * intensity);
    
    float maxFogLimit = 0.3 + intensity * 0.3; // Much lower fog limit
    vec3 finalColor = mix(sceneCol, hazeCol, fog * maxFogLimit);

    // 3. 3D VOLUMETRIC PARTICLES
    vec3 pAcc = vec3(0.0);
    
    float windAngle = uWindDirection * 3.14159 / 180.0;
    vec3 windDir = vec3(sin(windAngle), 0, cos(windAngle));
    float tilt = (uType == 1) ? (uWindSpeed * 0.02) : (uWindSpeed * 0.05);

    if (uType == 1) { // === ADVANCED MOODY RAIN ===
        float fallSpeed = 45.0; 
        float flash = uLightning * 5.0;
        
        // 1. ATMOSPHERIC RAIN CURTAIN
        vec3 curtainP = (uCameraPos + rayDir * 40.0) * 0.1;
        float curtainNoise = noise_ws(curtainP.xz * 1.2 + vec2(0.0, uTime * 1.2));
        float curtain = smoothstep(0.2, 0.9, curtainNoise);
        pAcc += vec3(0.15, 0.25, 0.4) * curtain * intensity * 0.7;

        // 2. MULTI-LAYERED STREAKS (Column-based XZ sampling)
        for (int i = 0; i < 24; i++) { 
            float fi = float(i);
            float sliceDist = 0.4 + pow(fi, 1.45) * 1.2 + hash12(vUV + fi) * 1.5;
            if (linearDepth < sliceDist) continue;

            vec3 worldP = uCameraPos + rayDir * sliceDist;
            float moveY = uTime * 70.0 * (0.85 + hash12(vec2(fi)) * 0.3);
            float sampleY = worldP.y + moveY;
            worldP += windDir * sampleY * tilt * (1.0 + hash12(vec2(fi)) * 0.3);
            
            float scale = 2.5 + fi * 0.5;
            vec2 pxz = worldP.xz * scale;
            vec2 gxz = floor(pxz);
            float h = hash12(gxz + fi * 157.0); 
            
            if (h > 1.0 - (intensity * 0.2)) { // Wall of water density
                float ycoord = sampleY * 0.18 + h * 23.45;
                float fvY = fract(ycoord);
                float v = smoothstep(0.0, 0.1, fvY) * smoothstep(1.0, 0.9, fvY);
                
                vec2 fxz = fract(pxz);
                float streak = smoothstep(0.03, 0.0, length(fxz - 0.5));
                
                float fade = (18.0 / (sliceDist + 6.0)) * smoothstep(0.0, 1.2, sliceDist);
                vec2 refrUV = vUV + vec2(streak * 0.002 * (1.1 - sliceDist * 0.05), 0.0);
                vec3 sceneSample = texture(uScene, refrUV).rgb;
                
                vec3 rainCol = vec3(0.4, 0.55, 0.7) + vec3(0.3) * h + vec3(0.9, 0.95, 1.0) * flash;
                pAcc += (sceneSample * 0.5 + rainCol * 0.5) * streak * v * fade * intensity * 0.8;
            }
        }

        // 3. FOREGROUND BOKEH (Motion-blurred streaks)
        for (int i = 0; i < 4; i++) {
            float fi = float(i);
            float sliceDist = 0.15 + fi * 0.4;
            if (linearDepth < sliceDist) continue;
            vec3 worldP = uCameraPos + rayDir * sliceDist;
            
            float moveY = uTime * 20.0 * (1.0 + fi * 0.2);
            vec2 p_b = (worldP.xz + vec2(worldP.y * 0.05, worldP.y + moveY)) * (18.0 + fi * 8.0);
            vec2 g_b = floor(p_b);
            vec2 fv_b = fract(p_b);
            float h_b = hash12(g_b + fi * 123.0);
            if (h_b > 0.96) {
                float dx = (fv_b.x - 0.5);
                float dy = (fv_b.y - 0.5) * 0.08; 
                float d_b = sqrt(dx*dx + dy*dy);
                float bokeh = exp(-d_b * 70.0) * 0.3;
                pAcc += vec3(0.65, 0.82, 1.0) * bokeh * (1.0 - (sliceDist / 2.0)) * intensity;
            }
        }
    }
    else if (uType == 2) { // === HIGH QUALITY SNOW ===
        float fallSpeed = 2.0;
        for (int i = 0; i < 6; i++) {
            float fi = float(i);
            float sliceDist = 1.0 + fi * 8.0 + hash12(vUV + fi * 0.1) * 3.0;
            if (linearDepth < sliceDist) continue;

            vec3 worldP = uCameraPos + rayDir * sliceDist;
            float drift = sin(uTime * 0.7 + worldP.y * 0.3 + fi) * 2.0;
            worldP.x += drift + windDir.x * worldP.y * tilt;
            worldP.z += cos(uTime * 0.4 + fi) * 1.2 + windDir.z * worldP.y * tilt;
            worldP.y += uTime * fallSpeed;
            
            vec2 p = worldP.xz * 2.0 + worldP.xy * 0.05;
            vec2 g = floor(p * 10.0);
            vec2 fv = fract(p * 10.0);
            
            float h = hash12(g + fi * 531.0);
            if (h > 1.0 - (intensity * 0.18)) {
                float d = length(fv - 0.5);
                float flake = exp(-d * d * 80.0) + exp(-d * d * 18.0) * 0.4;
                float fade = 10.0 / (sliceDist + 2.0);
                pAcc += vec3(0.98, 1.0, 1.0) * flake * fade * (0.6 + h * 0.4);
            }
        }
    }

    finalColor += pAcc * intensity;
    
    if (uType == 1 && intensity > 0.7) {
        float drips = noise_ws(vUV * vec2(12.0, 1.2) + vec2(0, uTime * 1.8));
        drips = smoothstep(0.75, 1.0, drips) * (intensity - 0.7) * 0.25;
        finalColor += vec3(0.5, 0.6, 0.75) * drips;
    }

    FragOut = vec4(finalColor, 1.0);
}
";

    // === Post-Process: Fisheye ===============================================
    public const string FISHEYE_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uStrength;

void main() {
    vec2 center = vec2(0.5);
    vec2 dist = vUV - center;
    float d = length(dist);
    vec2 uv = center + dist * (1.0 + d * d * uStrength);
    FragOut = texture(uScene, uv);
}
";

    // === Post-Process: Blur ==================================================
    public const string BLUR_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uRadius;

void main() {
    vec2 texelSize = 1.0 / textureSize(uScene, 0);
    vec3 result = vec3(0.0);
    for (int x = -2; x <= 2; x++) {
        for (int y = -2; y <= 2; y++) {
            result += texture(uScene, vUV + vec2(x,y) * texelSize * uRadius).rgb;
        }
    }
    FragOut = vec4(result / 25.0, 1.0);
}
";

    // === Post-Process: Noise =================================================
    public const string NOISE_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uIntensity;
uniform float uTime;
uniform int uLarge;

float hash_n(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    float n = hash_n(vUV + fract(uTime));
    color += (n - 0.5) * uIntensity;
    FragOut = vec4(color, 1.0);
}
";

    // === Post-Process: Exposure / Tone Mapping ===============================
    public const string EXPOSURE_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uExposure;

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    FragOut = vec4(color * uExposure, 1.0);
}
";

    // === Post-Process: White Balance =========================================
    public const string WHITEBALANCE_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform float uTemperature;
uniform float uTint;

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    color.r += uTemperature * 0.1;
    color.b -= uTemperature * 0.1;
    color.g += uTint * 0.05;
    FragOut = vec4(color, 1.0);
}
";

    // === SSAO Shaders ========================================================
    public const string SSAO_FRAG = @"
#version 450 core
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 FragOut;
uniform sampler2D uScene;
uniform sampler2D uDepth;
uniform float uRadius;
uniform float uIntensity;

void main() {
    vec3 color = texture(uScene, vUV).rgb;
    float depth = texture(uDepth, vUV).r;
    float ao = 0.0;
    for (int i = 0; i < 8; i++) {
        vec2 offset = vec2(cos(float(i)), sin(float(i))) * uRadius * 0.01;
        float d = texture(uDepth, vUV + offset).r;
        if (d < depth) ao += (depth - d);
    }
    color *= (1.0 - ao * uIntensity * 10.0);
    FragOut = vec4(color, 1.0);
}
";

    // === Skybox Shaders ======================================================
    public const string SKYBOX_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProjection;
out vec3 vWorldPos;
void main() {
    vWorldPos = aPos;
    mat4 view = mat4(mat3(uView));
    vec4 pos = uProjection * view * vec4(aPos, 1.0);
    gl_Position = pos.xyww;
}
";

    public const string SKYBOX_FRAG = @"
#version 450 core
in vec3 vWorldPos;
uniform sampler2D uHdri;
uniform float uStrength;
uniform float uLightning;
out vec4 FragOut;
const vec2 invAtan = vec2(0.1591, 0.3183);
void main() {
    vec3 v = normalize(vWorldPos);
    vec2 uv = vec2(atan(v.z, v.x), asin(v.y));
    uv *= invAtan;
    uv.x += 0.5;
    uv.y = 0.5 - uv.y;
    vec3 color = texture(uHdri, uv).rgb * uStrength;
    color += vec3(0.6, 0.7, 1.0) * uLightning * 1.5;
    FragOut = vec4(color, 1.0);
}
";

    // === Selection Outline: Mask Vertex Shader ================================
    public const string OUTLINE_MASK_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

void main() {
    gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
}
";

    // === Selection Outline: Mask Fragment Shader ==============================
    public const string OUTLINE_MASK_FRAG = @"
#version 450 core
out vec4 FragOut;
void main() {
    FragOut = vec4(1.0, 1.0, 1.0, 1.0);
}
";

    // === Selection Outline: Edge Detection Composite =========================
    public const string OUTLINE_COMPOSITE_FRAG = @"
#version 450 core
in vec2 vUV;
uniform sampler2D uScene;
uniform sampler2D uMask;
uniform vec2 uTexelSize;
uniform vec4 uOutlineColor;
uniform float uOutlineWidth;
out vec4 FragOut;

void main() {
    vec3 scene = texture(uScene, vUV).rgb;
    float mask = texture(uMask, vUV).r;
    
    // Sample neighbors to detect edges
    float edge = 0.0;
    for (float x = -uOutlineWidth; x <= uOutlineWidth; x += 1.0) {
        for (float y = -uOutlineWidth; y <= uOutlineWidth; y += 1.0) {
            float neighbor = texture(uMask, vUV + vec2(x, y) * uTexelSize).r;
            edge = max(edge, neighbor);
        }
    }
    
    // Edge pixel = neighbor has mask but current pixel doesn't
    float outline = edge * (1.0 - mask);
    
    FragOut = vec4(mix(scene, uOutlineColor.rgb, outline * uOutlineColor.a), 1.0);
}
";
}
