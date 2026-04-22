using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using ImGuiNET;
using SynthGen.Scene;

namespace SynthGen.Randomizers.CameraRandomizers;

// ═══════════════════════════════════════════════════════════════════════════
// Camera Position Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class CameraPositionRandomizer : RandomizerBase
{
    public override string Name => "Camera Position";
    public override string Category => "Camera";

    public float MinYaw = -180f, MaxYaw = 180f;
    public float MinPitch = 5f, MaxPitch = 60f;
    public float MinDist = 5f, MaxDist = 25f;
    public string TargetObjectName { get; set; } = "Scene Center";

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;

        // Apply orbit target before randomizing angles/dist
        if (TargetObjectName != "Scene Center")
        {
            var targetObj = scene.Objects.FirstOrDefault(o => o.Name == TargetObjectName);
            if (targetObj != null)
            {
                cam.OrbitTarget = targetObj.Transform.Position;
            }
        }
        else
        {
            cam.OrbitTarget = Vector3.Zero;
        }

        cam.OrbitYaw = RandRange(rng, MinYaw, MaxYaw);
        cam.OrbitPitch = RandRange(rng, MinPitch, MaxPitch);
        cam.OrbitDistance = RandRange(rng, MinDist, MaxDist);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        // Target Picker
        var names = new List<string> { "Scene Center" };
        names.AddRange(scene.Objects.Select(o => o.Name).Where(n => !n.Contains("$AssimpFbx$")));
        
        int current = names.IndexOf(TargetObjectName);
        if (current < 0) current = 0;

        if (ImGui.Combo("Orbit Target", ref current, names.ToArray(), names.Count))
        {
            TargetObjectName = names[current];
            
            // Preview instantly if not generating
            var targetObj = scene.Objects.FirstOrDefault(o => o.Name == TargetObjectName);
            if (scene.ActiveCamera != null)
            {
                scene.ActiveCamera.OrbitTarget = (TargetObjectName == "Scene Center" || targetObj == null) 
                    ? Vector3.Zero : targetObj.Transform.Position;
            }
        }

        ImGui.DragFloatRange2("Yaw", ref MinYaw, ref MaxYaw, 1f, -180, 180);
        ImGui.DragFloatRange2("Pitch", ref MinPitch, ref MaxPitch, 1f, -89, 89);
        ImGui.DragFloatRange2("Distance", ref MinDist, ref MaxDist, 0.5f, 0.5f, 100);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Fisheye Effect (single-value, not a randomizer)
// ═══════════════════════════════════════════════════════════════════════════
public class FisheyeRandomizer : RandomizerBase
{
    public override string Name => "Fisheye";
    public override string Category => "Camera";

    public float Strength = 0.6f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        // Fisheye is an effect, not randomized — just apply the fixed value
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.FisheyeStrength = Strength;
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        if (ImGui.SliderFloat("Strength", ref Strength, 0f, 3f))
            OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null) cam.FisheyeStrength = enabled ? Strength : 0f;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Fog Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class FogRandomizer : RandomizerBase
{
    public override string Name => "Fog";
    public override string Category => "Camera";

    public float MinDensity = 0f;
    public float MaxDensity = 2f;
    public Vector3 FogColor = new(0.7f, 0.75f, 0.8f);

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.FogDensity = RandRange(rng, MinDensity, MaxDensity);
        cam.FogColor = FogColor;
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        bool changed = ImGui.DragFloatRange2("Density", ref MinDensity, ref MaxDensity, 0.05f, 0, 5);
        changed |= ImGui.ColorEdit3("Fog Color", ref FogColor);
        if (changed) OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            cam.FogDensity = enabled ? (MinDensity + MaxDensity) * 0.5f : 0f;
            cam.FogColor = FogColor;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Bloom Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class BloomRandomizer : RandomizerBase
{
    public override string Name => "Bloom";
    public override string Category => "Camera";

    public float MinThreshold = 0.5f, MaxThreshold = 1.5f;
    public float MinIntensity = 0f, MaxIntensity = 1f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.BloomThreshold = RandRange(rng, MinThreshold, MaxThreshold);
        cam.BloomIntensity = RandRange(rng, MinIntensity, MaxIntensity);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        bool changed = ImGui.DragFloatRange2("Threshold", ref MinThreshold, ref MaxThreshold, 0.05f, 0, 3);
        changed |= ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.05f, 0, 3);
        if (changed) OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            cam.BloomThreshold = (MinThreshold + MaxThreshold) * 0.5f;
            cam.BloomIntensity = enabled ? (MinIntensity + MaxIntensity) * 0.5f : 0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Exposure Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class ExposureRandomizer : RandomizerBase
{
    public override string Name => "Exposure";
    public override string Category => "Camera";

    public float MinExposure = 0.5f;
    public float MaxExposure = 3.0f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.Exposure = RandRange(rng, MinExposure, MaxExposure);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        if (ImGui.DragFloatRange2("Exposure", ref MinExposure, ref MaxExposure, 0.05f, 0.1f, 10))
            OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null) cam.Exposure = enabled ? (MinExposure + MaxExposure) * 0.5f : 1.0f;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Noise Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class NoiseRandomizer : RandomizerBase
{
    public override string Name => "Noise";
    public override string Category => "Camera";

    public float MinIntensity = 0f;
    public float MaxIntensity = 0.15f;
    public bool RandomizeLarge = true;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.NoiseIntensity = RandRange(rng, MinIntensity, MaxIntensity);
        cam.NoiseLarge = RandomizeLarge && rng.Next(2) == 0;
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        bool changed = ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.005f, 0, 0.5f);
        changed |= ImGui.Checkbox("Randomize Large/Small", ref RandomizeLarge);
        if (changed) OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            cam.NoiseIntensity = enabled ? (MinIntensity + MaxIntensity) * 0.5f : 0f;
            cam.NoiseLarge = RandomizeLarge; // show large noise if toggled
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Ambient Occlusion Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class AmbientOcclusionRandomizer : RandomizerBase
{
    public override string Name => "Ambient Occlusion";
    public override string Category => "Camera";

    public float MinRadius = 0.1f, MaxRadius = 2f;
    public float MinIntensity = 0f, MaxIntensity = 1.5f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.SSAORadius = RandRange(rng, MinRadius, MaxRadius);
        cam.SSAOIntensity = RandRange(rng, MinIntensity, MaxIntensity);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        bool changed = ImGui.DragFloatRange2("Radius", ref MinRadius, ref MaxRadius, 0.05f, 0.01f, 5);
        changed |= ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.05f, 0, 3);
        if (changed) OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            cam.SSAORadius = (MinRadius + MaxRadius) * 0.5f;
            cam.SSAOIntensity = enabled ? (MinIntensity + MaxIntensity) * 0.5f : 0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// White Balance Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class WhiteBalanceRandomizer : RandomizerBase
{
    public override string Name => "White Balance";
    public override string Category => "Camera";

    public float MinTemp = 3000f, MaxTemp = 10000f;
    public float MinTint = -1f, MaxTint = 1f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.WhiteBalanceTemperature = RandRange(rng, MinTemp, MaxTemp);
        cam.WhiteBalanceTint = RandRange(rng, MinTint, MaxTint);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        bool changed = ImGui.DragFloatRange2("Temperature (K)", ref MinTemp, ref MaxTemp, 100, 1000, 15000);
        changed |= ImGui.DragFloatRange2("Tint", ref MinTint, ref MaxTint, 0.05f, -2, 2);
        if (changed) OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            cam.WhiteBalanceTemperature = enabled ? (MinTemp + MaxTemp) * 0.5f : 6500f;
            cam.WhiteBalanceTint = enabled ? (MinTint + MaxTint) * 0.5f : 0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Blur Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class BlurRandomizer : RandomizerBase
{
    public override string Name => "Blur";
    public override string Category => "Camera";

    public float MinRadius = 0f;
    public float MaxRadius = 5f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.BlurRadius = RandRange(rng, MinRadius, MaxRadius);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        if (ImGui.DragFloatRange2("Radius", ref MinRadius, ref MaxRadius, 0.1f, 0, 10))
            OnToggle(scene, Enabled);
    }

    public override void OnToggle(SceneGraph scene, bool enabled)
    {
        var cam = scene.ActiveCamera;
        if (cam != null) cam.BlurRadius = enabled ? (MinRadius + MaxRadius) * 0.5f : 0f;
    }
}
