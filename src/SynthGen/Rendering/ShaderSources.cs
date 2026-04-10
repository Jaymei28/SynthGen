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
uniform float uColorIntensity;

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
    albedo *= uColorIntensity;

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

    // ACES Tone Mapping
    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
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
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;
uniform sampler2DArray uDisplacementMap;
uniform sampler2DArray uNormalMap;

out vec3 vWorldPos;
out vec2 vUV;
out float vFoam;
out float vHeight;
out vec3 vViewDir;

void main() {
    float gridScale = 0.05; // Matches the tile size logic
    vec2 worldUV = (uModel * vec4(aPos, 1.0)).xz * gridScale; 
    
    // Sample FFT Displacement (Cascade 0)
    vec4 disp = texture(uDisplacementMap, vec3(worldUV, 0.0));
    vec3 displacedPos = aPos + disp.xyz;
    
    vec4 worldPos = uModel * vec4(displacedPos, 1.0);
    vWorldPos = worldPos.xyz;
    vUV = aUV;
    
    // Sample Normal Map for foam info
    vec4 normData = texture(uNormalMap, vec3(worldUV, 0.0));
    vFoam = normData.a;
    vHeight = disp.y;
    
    gl_Position = uProjection * uView * worldPos;
    vViewDir = normalize(vec3(inverse(uView)[3]) - worldPos.xyz);
}
";

    public const string OCEAN_DEPTH_VERT = @"
#version 450 core
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;
uniform float uWindSpeed;
uniform float uWindDirection;
uniform float uTimeMultiplier;

out float vDepth;

void main() {
    vec4 viewPos = uView * vec4(aPos, 1.0);
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
in vec2 vUV;
in float vFoam;
in float vHeight;
in vec3 vViewDir;

uniform vec3 uWaterColor;
uniform vec3 uFoamColor;
uniform sampler2DArray uNormalMap;
uniform sampler2D uSkybox; 
uniform vec3 uCameraPos;

out vec4 FragColor;

void main() {
    float gridScale = 0.05;
    vec2 worldUV = vWorldPos.xz * gridScale;
    
    // Unpack normal from gradient map
    vec4 nm = texture(uNormalMap, vec3(worldUV, 0.0));
    vec3 N = normalize(vec3(nm.x, 2.0, nm.y)); // Enhanced shallowing for better reflections
    
    vec3 V = normalize(uCameraPos - vWorldPos);
    vec3 L = normalize(vec3(0.5, 1.0, 0.3)); // Sun
    
    // Fresnel
    float dotNV = max(dot(N, V), 0.0);
    float fresnel = 0.04 + 0.96 * pow(1.0 - dotNV, 5.0);
    
    // Reflection
    vec3 R = reflect(-V, N);
    vec3 reflection = texture(uSkybox, R.xy * 0.5 + 0.5).rgb; // Mapping to 2D skybox for now
    
    // Deep water color
    vec3 baseColor = mix(uWaterColor * 0.2, uWaterColor, clamp(vHeight * 0.5 + 0.5, 0.0, 1.0));
    
    vec3 color = mix(baseColor, reflection, fresnel * 0.8);
    
    // Foam (Whitecaps)
    if (vFoam > 0.05) {
        color = mix(color, uFoamColor, vFoam);
    }
    
    FragColor = vec4(color, 0.98);
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
    
    // Apply barrel distortion
    vec2 uv = center + dist * (1.0 + d * d * uStrength);
    
    // Scale down by the maximum possible distortion (which occurs at the corners where d^2 = 0.5)
    // This ensures no sampled UV ever goes outside [0, 1], completely eliminating the black frame.
    float cornerDistortion = 1.0 + 0.5 * uStrength;
    uv = center + (uv - center) / cornerDistortion;
    
    // Clamp to edge to be absolutely safe
    uv = clamp(uv, 0.0, 1.0);
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
    float mask = texture(uMask, vUV).r;
    vec4 sceneColor = texture(uScene, vUV);
    
    if (mask > 0.5) {
        FragOut = sceneColor;
        return;
    }
    
    float edge = 0.0;
    int radius = int(uOutlineWidth);
    for (int x = -radius; x <= radius; x++) {
        for (int y = -radius; y <= radius; y++) {
            vec2 offset = vec2(float(x), float(y)) * uTexelSize;
            edge += texture(uMask, vUV + offset).r;
        }
    }
    
    if (edge > 0.1) {
        FragOut = uOutlineColor;
    } else {
        FragOut = sceneColor;
    }
}
";

    // === FFT OCEAN COMPUTE SHADERS ===

    public const string SPECTRA_COMPUTE = @"
#version 460
#define PI (3.141592653589793)
#define G  (9.81)
layout(local_size_x = 16, local_size_y = 16) in;
layout(rgba16f, binding = 0) restrict writeonly uniform image2DArray spectrum;

uniform struct {
	ivec2 seed;
	vec2 tile_length;
	float alpha;
	float peak_frequency;
	float wind_speed;
	float angle;
	float depth;
	float swell;
	float detail;
	float spread;
	uint cascade_index;
} pc;

vec2 hash(uvec2 x) {
	uint h32 = x.y + 374761393U + x.x*3266489917U;
    h32 = 2246822519U * (h32 ^ (h32 >> 15));
    h32 = 3266489917U * (h32 ^ (h32 >> 13));
    uint n = h32 ^ (h32 >> 16);
    uvec2 rz = uvec2(n, n*48271U);
    return vec2((rz.xy >> 1) & uvec2(0x7FFFFFFFU)) / float(0x7FFFFFFF);
}

vec2 gaussian(vec2 x) {
	float r = sqrt(-2.0 * log(max(x.x, 1e-6)));
	float theta = 2.0*PI * x.y;
	return vec2(r*cos(theta), r*sin(theta));
}

vec2 conj_complex(vec2 x) { return vec2(x.x, -x.y); }

vec2 dispersion_relation(float k) {
	float a = k*pc.depth;
	float b = tanh(clamp(a, 0.0, 10.0));
	float omega = sqrt(G*k*b);
	float d_omega = 0.5*G * (b + a*(1.0 - b*b)) / (omega + 1e-6);
	return vec2(omega, d_omega);
}

float longuet_higgins_normalization(float s) {
	float a = sqrt(s);
	return (s < 0.4) ? (0.5/PI) + s*(0.220636+s*(-0.109+s*0.090)) : inversesqrt(PI)*(a*0.5 + (1.0/a)*0.0625);
}

float longuet_higgins_function(float s, float theta) {
	return longuet_higgins_normalization(s) * pow(abs(cos(theta*0.5)), 2.0*s);
}

float hasselmann_directional_spread(float w, float w_p, float wind_speed, float theta) {
	float p = w / (w_p + 1e-6);
	float s = (w <= w_p) ? 6.97*pow(abs(p), 4.06) : 9.77*pow(abs(p), -2.33 - 1.45*(wind_speed*w_p/G - 1.17));
	float s_xi = 16.0 * tanh(w_p / (w + 1e-6)) * pc.swell*pc.swell; 
    return longuet_higgins_function(s + s_xi, theta - pc.angle);
}

float TMA_spectrum(float w, float w_p, float alpha) {
	const float beta = 1.25;
	const float gamma = 3.3; 
	float sigma = (w <= w_p) ? 0.07 : 0.09;
	float r = exp(-(w-w_p)*(w-w_p) / (2.0 * sigma*sigma * w_p*w_p + 1e-6));
	float jonswap = (alpha * G*G) / pow(w + 1e-6, 5) * exp(-beta * pow(w_p/(w+1e-6), 4)) * pow(gamma, r);
	float w_h = min(w * sqrt(pc.depth / G), 2.0);
	float kitaigorodskii = (w_h <= 1.0) ? 0.5*w_h*w_h : 1.0 - 0.5*(2.0-w_h)*(2.0-w_h);
	return jonswap * kitaigorodskii;
}

vec2 get_spectrum_amplitude(ivec2 id, ivec2 map_size) {
	vec2 dk = 2.0*PI / pc.tile_length;
	vec2 k_vec = (vec2(id) - vec2(map_size)*0.5)*dk;
	float k = length(k_vec) + 1e-6;
	float theta = atan(k_vec.x, k_vec.y);
	vec2 dispersion = dispersion_relation(k);
	float w = dispersion[0];
	float w_norm = dispersion[1] / k * dk.x*dk.y;
	float s = TMA_spectrum(w, pc.peak_frequency, pc.alpha);
	float d = mix(0.5/PI, hasselmann_directional_spread(w, pc.peak_frequency, pc.wind_speed, theta), 1.0 - pc.spread);
    d *= exp(-(1.0-pc.detail)*(1.0-pc.detail) * k*k);
	return gaussian(hash(uvec2(id + pc.seed))) * sqrt(max(0.0, 2.0 * s * d * w_norm));
}

void main() {
	ivec2 dims = imageSize(spectrum).xy;
	ivec3 gid = ivec3(gl_GlobalInvocationID.xy, pc.cascade_index);
	ivec2 id0 = gid.xy;
	ivec2 id1 = ivec2(mod(vec2(dims) - vec2(id0), vec2(dims)));
	imageStore(spectrum, gid, vec4(get_spectrum_amplitude(id0, dims), conj_complex(get_spectrum_amplitude(id1, dims))));
}
";

    public const string SPECTRA_MODULATE = @"
#version 460
#define PI 3.141592653589793
#define G  9.81
#define NUM_SPECTRA 4U
layout(local_size_x = 16, local_size_y = 16) in;

layout(rgba16f, binding = 0) restrict readonly uniform image2DArray spectrum;
layout(std430, binding = 1) restrict writeonly buffer FFTBuffer { vec2 data[]; };

uniform struct {
	vec2 tile_length;
	float depth;
	float time;
	uint cascade_index;
    uint mapSize;
} pc;

vec2 exp_complex(float x) { return vec2(cos(x), sin(x)); }
vec2 mul_complex(vec2 a, vec2 b) { return vec2(a.x*b.x - a.y*b.y, a.x*b.y + a.y*b.x); }
vec2 conj_complex(vec2 x) { return vec2(x.x, -x.y); }
float dispersion_relation(float k) { return sqrt(G*k*tanh(clamp(k*pc.depth, 0.0, 10.0))); }

void main() {
	uint map_size = pc.mapSize;
	ivec2 dims = imageSize(spectrum).xy;
	ivec2 gid = ivec2(gl_GlobalInvocationID.xy);
    uint cascade_idx = pc.cascade_index;

	vec2 k_vec = (vec2(gid) - vec2(dims)*0.5)*2.0*PI / pc.tile_length;
	float k = length(k_vec) + 1e-6;
	vec2 k_unit = k_vec / k;

	vec4 h0 = imageLoad(spectrum, ivec3(gid, cascade_idx));
	float dispersion = dispersion_relation(k) * pc.time;
	vec2 modulation = exp_complex(dispersion);
	vec2 h = mul_complex(h0.xy, modulation) + mul_complex(h0.zw, conj_complex(modulation));
	vec2 h_inv = vec2(-h.y, h.x);

	vec2 hx = h_inv * k_unit.y;
	vec2 hy = h;
	vec2 hz = h_inv * k_unit.x;
	vec2 dhy_dx = h_inv * k_vec.y;
	vec2 dhy_dz = h_inv * k_vec.x;
	vec2 dhx_dx = -h * k_vec.y * k_unit.y;
	vec2 dhz_dz = -h * k_vec.x * k_unit.x;
	vec2 dhz_dx = -h * k_vec.y * k_unit.x;

    uint m2 = map_size * map_size;
    uint offset = cascade_idx * m2 * NUM_SPECTRA * 2;
	data[offset + 0*m2 + gid.y*map_size + gid.x] = vec2(hx.x - hy.y, hx.y + hy.x);
	data[offset + 1*m2 + gid.y*map_size + gid.x] = vec2(hz.x - dhy_dx.y, hz.y + dhy_dx.x);
	data[offset + 2*m2 + gid.y*map_size + gid.x] = vec2(dhy_dz.x - dhx_dx.y, dhy_dz.y + dhx_dx.x);
	data[offset + 3*m2 + gid.y*map_size + gid.x] = vec2(dhz_dz.x - dhz_dx.y, dhz_dz.y + dhz_dx.x);
}
";

    public const string FFT_BUTTERFLY = @"
#version 460
#define PI 3.141592653589793
layout(local_size_x = 64) in;
layout(std430, binding = 0) restrict writeonly buffer Butterfly { vec4 bf[]; };
uniform uint uMapSize;

vec2 exp_complex(float x) { return vec2(cos(x), sin(x)); }

void main() {
	uint map_size = uMapSize;
	uint col = gl_GlobalInvocationID.x;
	uint stage = gl_GlobalInvocationID.y;
	uint stride = 1 << stage, mid = map_size >> (stage + 1);
	uint i = col >> stage, j = col % stride;
	vec2 twiddle = exp_complex(PI / float(stride) * float(j));
	uint r0 = stride*(i + 0) + j, r1 = stride*(i + mid) + j;
	uint w0 = stride*(2*i + 0) + j, w1 = stride*(2*i + 1) + j;
	bf[stage*map_size + w0] = vec4(uintBitsToFloat(r0), uintBitsToFloat(r1),  twiddle);
	bf[stage*map_size + w1] = vec4(uintBitsToFloat(r0), uintBitsToFloat(r1), -twiddle);
}
";

    public const string FFT_COMPUTE = @"
#version 460
#define MAX_MAP_SIZE 2048
layout(local_size_x = 1024) in;
layout(std430, binding = 0) restrict readonly buffer Butterfly { vec4 bf[]; };
layout(std430, binding = 1) restrict buffer FFT { vec2 data[]; };

shared vec2 row_shared[2 * MAX_MAP_SIZE];

uniform uint uCascadeIndex;
uniform uint uMapSize;

vec2 mul_complex(vec2 a, vec2 b) { return vec2(a.x*b.x - a.y*b.y, a.x*b.y + a.y*b.x); }

void main() {
	uint map_size = uMapSize;
	uint num_stages = findMSB(map_size);
	uint col = gl_GlobalInvocationID.x;
	uint row = gl_GlobalInvocationID.y;
	uint spectrum = gl_GlobalInvocationID.z;
    if (col >= map_size) return;

    uint m2 = map_size * map_size;
    uint cascade_offset = uCascadeIndex * m2 * 4 * 2;
	row_shared[col] = data[cascade_offset + spectrum*m2 + row*map_size + col];

	for (uint stage = 0; stage < num_stages; ++stage) {
		barrier();
		uint read_off = (stage % 2) * MAX_MAP_SIZE;
		uint write_off = ((stage + 1) % 2) * MAX_MAP_SIZE;
		vec4 b = bf[stage*map_size + col];
		uint r0 = floatBitsToUint(b.x), r1 = floatBitsToUint(b.y);
		vec2 tw = b.zw;
		vec2 upper = row_shared[read_off + r0];
		vec2 lower = row_shared[read_off + r1];
		row_shared[write_off + col] = upper + mul_complex(lower, tw);
	}
    barrier();
    // Copy to output layer of the buffer swap
	data[cascade_offset + (spectrum + 4)*m2 + row*map_size + col] = row_shared[(num_stages % 2) * MAX_MAP_SIZE + col];
}
";

    public const string FFT_TRANSPOSE = @"
#version 460
#define TILE_SIZE 32
layout(local_size_x = TILE_SIZE, local_size_y = TILE_SIZE) in;
layout(std430, binding = 1) restrict buffer FFT { vec2 data[]; };
shared vec2 tile[TILE_SIZE][TILE_SIZE+1];

uniform uint uCascadeIndex;
uniform uint uMapSize;

void main() {
	uint map_size = uMapSize;
    uint m2 = map_size * map_size;
    uint cascade_offset = uCascadeIndex * m2 * 8;
	uint spectrum = gl_GlobalInvocationID.z;

	uvec2 id = gl_GlobalInvocationID.xy;
	tile[gl_LocalInvocationID.y][gl_LocalInvocationID.x] = data[cascade_offset + (spectrum + 4)*m2 + id.y*map_size + id.x];
	barrier();
	uvec2 tid = gl_WorkGroupID.yx * TILE_SIZE + gl_LocalInvocationID.xy;
	data[cascade_offset + spectrum*m2 + tid.y*map_size + tid.x] = tile[gl_LocalInvocationID.x][gl_LocalInvocationID.y];
}
";

    public const string FFT_UNPACK = @"
#version 460
layout(local_size_x = 16, local_size_y = 16, local_size_z = 2) in;
layout(rgba16f, binding = 0) restrict writeonly uniform image2DArray displacement_map;
layout(rgba16f, binding = 1) restrict uniform image2DArray normal_map;
layout(std430, binding = 2) restrict readonly buffer FFT { vec2 data[]; };

uniform struct {
	uint cascade_index;
	float whitecap;
	float foam_grow_rate;
	float foam_decay_rate;
    uint mapSize;
} pc;

void main() {
	uint map_size = pc.mapSize;
    uint m2 = map_size * map_size;
    uint cascade_offset = pc.cascade_index * m2 * 8;
	ivec3 id = ivec3(gl_GlobalInvocationID.xy, pc.cascade_index);
	float sign_shift = float(-2*int((id.x & 1) ^ (id.y & 1)) + 1);

	if (gl_LocalInvocationID.z == 0) {
        // Displacements are in layers 4, 5, 6, 7 of the FFT output (after row/col passes)
        // Layer 0: Hx, Hy  Layer 1: Hz, dHy_dx ...
        // Wait, SPECTRA_MODULATE packed:
        // 0: hx, hy
        // 1: hz, dhy_dx
        // 2: dhy_dz, dhx_dx
        // 3: dhz_dz, dhz_dx
        
        vec2 d0 = data[cascade_offset + 4*m2 + id.y*map_size + id.x] * sign_shift;
        vec2 d1 = data[cascade_offset + 5*m2 + id.y*map_size + id.x] * sign_shift;
        
        float hx = d0.x;
        float hy = d0.y;
        float hz = d1.x;
		imageStore(displacement_map, id, vec4(hx, hy, hz, 0));
	} else {
        float dhy_dx = data[cascade_offset + 5*m2 + id.y*map_size + id.x].y * sign_shift;
        vec2 d2 = data[cascade_offset + 6*m2 + id.y*map_size + id.x] * sign_shift;
        vec2 d3 = data[cascade_offset + 7*m2 + id.y*map_size + id.x] * sign_shift;
        
        float dhy_dz = d2.x;
        float dhx_dx = d2.y;
        float dhz_dz = d3.x;
        float dhz_dx = d3.y;

        float jacobian = (1.0 + dhx_dx) * (1.0 + dhz_dz) - dhz_dx * dhz_dx;
        float foam_factor = -min(0.0, jacobian - pc.whitecap);
        
        float foam = imageLoad(normal_map, id).a;
        foam *= exp(-pc.foam_decay_rate);
        foam += foam_factor * pc.foam_grow_rate;
        foam = clamp(foam, 0.0, 1.0);

        vec2 gradient = vec2(dhy_dx, dhy_dz) / (1.0 + abs(vec2(dhx_dx, dhz_dz)));
        imageStore(normal_map, id, vec4(gradient, dhx_dx, foam));
	}
}
";
}
