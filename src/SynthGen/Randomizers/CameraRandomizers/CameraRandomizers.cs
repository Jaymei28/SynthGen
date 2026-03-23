using System.Numerics;
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

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.OrbitYaw = RandRange(rng, MinYaw, MaxYaw);
        cam.OrbitPitch = RandRange(rng, MinPitch, MaxPitch);
        cam.OrbitDistance = RandRange(rng, MinDist, MaxDist);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloatRange2("Yaw", ref MinYaw, ref MaxYaw, 1f, -180, 180);
        ImGui.DragFloatRange2("Pitch", ref MinPitch, ref MaxPitch, 1f, -89, 89);
        ImGui.DragFloatRange2("Distance", ref MinDist, ref MaxDist, 0.5f, 0.5f, 100);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Fisheye Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class FisheyeRandomizer : RandomizerBase
{
    public override string Name => "Fisheye";
    public override string Category => "Camera";

    public float MinStrength = 0f;
    public float MaxStrength = 1.5f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;
        cam.FisheyeStrength = RandRange(rng, MinStrength, MaxStrength);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloatRange2("Strength", ref MinStrength, ref MaxStrength, 0.05f, 0, 3);
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
        ImGui.DragFloatRange2("Density", ref MinDensity, ref MaxDensity, 0.05f, 0, 5);
        ImGui.ColorEdit3("Fog Color", ref FogColor);
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
        ImGui.DragFloatRange2("Threshold", ref MinThreshold, ref MaxThreshold, 0.05f, 0, 3);
        ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.05f, 0, 3);
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
        ImGui.DragFloatRange2("Exposure", ref MinExposure, ref MaxExposure, 0.05f, 0.1f, 10);
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
        ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.005f, 0, 0.5f);
        ImGui.Checkbox("Randomize Large/Small", ref RandomizeLarge);
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
        ImGui.DragFloatRange2("Radius", ref MinRadius, ref MaxRadius, 0.05f, 0.01f, 5);
        ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.05f, 0, 3);
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
        ImGui.DragFloatRange2("Temperature (K)", ref MinTemp, ref MaxTemp, 100, 1000, 15000);
        ImGui.DragFloatRange2("Tint", ref MinTint, ref MaxTint, 0.05f, -2, 2);
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
        ImGui.DragFloatRange2("Radius", ref MinRadius, ref MaxRadius, 0.1f, 0, 10);
    }
}
