using System.Numerics;
using SynthGen.Scene;
using SynthGen.Scene.Components;
using SynthGen.Randomizers;

namespace SynthGen.Capture;

/// <summary>
/// Captures a snapshot of all mutable scene state before generation
/// and restores it afterwards so the scene returns to its original setup.
/// </summary>
public class SceneSnapshot
{
    // ── Per-object state ──────────────────────────────────────────────────
    private struct ObjectState
    {
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale;
    }

    private struct LightState
    {
        public Vector3 Color;
        public float Intensity;
    }

    private struct MaterialState
    {
        public uint AlbedoTextureID;
        public string? AlbedoTexturePath;
    }

    // ── Camera state ──────────────────────────────────────────────────────
    private struct CameraState
    {
        public float OrbitYaw, OrbitPitch, OrbitDistance;
        public Vector3 OrbitTarget;
        public float FisheyeStrength;
        public float FogDensity;
        public Vector3 FogColor;
        public float BloomThreshold, BloomIntensity;
        public float Exposure;
        public float NoiseIntensity;
        public bool NoiseLarge;
        public float SSAORadius, SSAOIntensity;
        public float WhiteBalanceTemperature, WhiteBalanceTint;
        public float BlurRadius;
        public int WeatherType;
        public float WeatherIntensity;
        public float LightningIntensity;
    }

    // ── HDRI state ────────────────────────────────────────────────────────
    private struct HdriState
    {
        public string? CurrentHDRI;
        public float CurrentStrength;
    }

    // ── Stored data ───────────────────────────────────────────────────────
    private readonly Dictionary<SceneObject, ObjectState> _objectStates = new();
    private readonly Dictionary<SceneObject, LightState> _lightStates = new();
    private readonly Dictionary<SceneObject, MaterialState> _materialStates = new();
    private CameraState? _cameraState;
    private HdriState? _hdriState;

    /// <summary>
    /// Captures the current scene state into a new snapshot.
    /// </summary>
    public static SceneSnapshot Capture(SceneGraph scene, HDRIRandomizer? hdriRandomizer)
    {
        var snap = new SceneSnapshot();

        // Capture transforms + components for every object
        foreach (var obj in scene.Objects)
        {
            snap._objectStates[obj] = new ObjectState
            {
                Position = obj.Transform.Position,
                Rotation = obj.Transform.Rotation,
                Scale = obj.Transform.Scale
            };

            var light = obj.GetComponent<LightComponent>();
            if (light != null)
            {
                snap._lightStates[obj] = new LightState
                {
                    Color = light.Color,
                    Intensity = light.Intensity
                };
            }

            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr != null)
            {
                snap._materialStates[obj] = new MaterialState
                {
                    AlbedoTextureID = mr.Material.AlbedoTextureID,
                    AlbedoTexturePath = mr.Material.AlbedoTexturePath
                };
            }
        }

        // Capture camera
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            snap._cameraState = new CameraState
            {
                OrbitYaw = cam.OrbitYaw,
                OrbitPitch = cam.OrbitPitch,
                OrbitDistance = cam.OrbitDistance,
                OrbitTarget = cam.OrbitTarget,
                FisheyeStrength = cam.FisheyeStrength,
                FogDensity = cam.FogDensity,
                FogColor = cam.FogColor,
                BloomThreshold = cam.BloomThreshold,
                BloomIntensity = cam.BloomIntensity,
                Exposure = cam.Exposure,
                NoiseIntensity = cam.NoiseIntensity,
                NoiseLarge = cam.NoiseLarge,
                SSAORadius = cam.SSAORadius,
                SSAOIntensity = cam.SSAOIntensity,
                WhiteBalanceTemperature = cam.WhiteBalanceTemperature,
                WhiteBalanceTint = cam.WhiteBalanceTint,
                BlurRadius = cam.BlurRadius,
                WeatherType = cam.WeatherType,
                WeatherIntensity = cam.WeatherIntensity,
                LightningIntensity = cam.LightningIntensity
            };
        }

        // Capture HDRI
        if (hdriRandomizer != null)
        {
            snap._hdriState = new HdriState
            {
                CurrentHDRI = hdriRandomizer.CurrentHDRI,
                CurrentStrength = hdriRandomizer.CurrentStrength
            };
        }

        return snap;
    }

    /// <summary>
    /// Restores the scene to the state captured in this snapshot.
    /// </summary>
    public void Restore(SceneGraph scene, HDRIRandomizer? hdriRandomizer)
    {
        // Restore transforms
        foreach (var obj in scene.Objects)
        {
            if (_objectStates.TryGetValue(obj, out var state))
            {
                obj.Transform.Position = state.Position;
                obj.Transform.Rotation = state.Rotation;
                obj.Transform.Scale = state.Scale;
            }

            if (_lightStates.TryGetValue(obj, out var ls))
            {
                var light = obj.GetComponent<LightComponent>();
                if (light != null)
                {
                    light.Color = ls.Color;
                    light.Intensity = ls.Intensity;
                }
            }

            if (_materialStates.TryGetValue(obj, out var ms))
            {
                var mr = obj.GetComponent<MeshRendererComponent>();
                if (mr != null)
                {
                    mr.Material.AlbedoTextureID = ms.AlbedoTextureID;
                    mr.Material.AlbedoTexturePath = ms.AlbedoTexturePath;
                }
            }
        }

        // Restore camera
        var cam = scene.ActiveCamera;
        if (cam != null && _cameraState.HasValue)
        {
            var cs = _cameraState.Value;
            cam.OrbitYaw = cs.OrbitYaw;
            cam.OrbitPitch = cs.OrbitPitch;
            cam.OrbitDistance = cs.OrbitDistance;
            cam.OrbitTarget = cs.OrbitTarget;
            cam.FisheyeStrength = cs.FisheyeStrength;
            cam.FogDensity = cs.FogDensity;
            cam.FogColor = cs.FogColor;
            cam.BloomThreshold = cs.BloomThreshold;
            cam.BloomIntensity = cs.BloomIntensity;
            cam.Exposure = cs.Exposure;
            cam.NoiseIntensity = cs.NoiseIntensity;
            cam.NoiseLarge = cs.NoiseLarge;
            cam.SSAORadius = cs.SSAORadius;
            cam.SSAOIntensity = cs.SSAOIntensity;
            cam.WhiteBalanceTemperature = cs.WhiteBalanceTemperature;
            cam.WhiteBalanceTint = cs.WhiteBalanceTint;
            cam.BlurRadius = cs.BlurRadius;
            cam.WeatherType = cs.WeatherType;
            cam.WeatherIntensity = cs.WeatherIntensity;
            cam.LightningIntensity = cs.LightningIntensity;
        }

        // Restore HDRI
        if (hdriRandomizer != null && _hdriState.HasValue)
        {
            var hs = _hdriState.Value;
            hdriRandomizer.CurrentHDRI = hs.CurrentHDRI;
            hdriRandomizer.CurrentStrength = hs.CurrentStrength;
        }
    }
}
