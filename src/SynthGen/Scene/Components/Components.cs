using System.Numerics;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SynthGen.Scene.Components;

// ── Light ──────────────────────────────────────────────────────────────────
public enum LightType { Directional, Point, Spot }

public class LightComponent
{
    public LightType LightType = LightType.Directional;
    public Vector3 Color = Vector3.One;
    public float Intensity = 1.0f;
    public Vector3 Direction = Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.5f));
    public float Range = 20f;
    public float SpotAngle = 30f;
    public bool CastShadow = true;
}

// ── Label ──────────────────────────────────────────────────────────────────
public class LabelComponent
{
    public int ClassID;
    public string ClassName = "person";
    public Vector3 SegmentationColor = new(1, 0, 0); // unique flat color
    public int InstanceID;  // auto-assigned

    [JsonIgnore]
    private static int _nextInstanceID = 1;
    
    public static void ResetInstanceCounter() => _nextInstanceID = 1;

    public LabelComponent() { InstanceID = _nextInstanceID++; }
}

// ── Mesh Renderer ──────────────────────────────────────────────────────────
public class MeshRendererComponent
{
    [JsonIgnore]
    public Rendering.Mesh? Mesh;
    public Rendering.Material Material = new();
    public bool Visible = true;

    public MeshRendererComponent() { }
    public MeshRendererComponent(Rendering.Mesh mesh)
    {
        Mesh = mesh;
    }
}

// ── Animation Player ───────────────────────────────────────────────────────
public class AnimationClip
{
    public string Name = "";
    public float Duration;
    public float TicksPerSecond = 24f;
    // Simplified: stores per-frame transforms
    public List<Matrix4x4> KeyFrames = new();
}

public class AnimationPlayerComponent
{
    public List<AnimationClip> Clips = new();
    public int CurrentClipIndex;
    public float PlaybackTime; // in seconds
    public bool IsPlaying;
    public bool Loop = true;

    /// <summary>
    /// Duration of the currently active skeletal animation clip, in seconds.
    /// Set from SkeletalAnimationClip at import time (Duration / TicksPerSecond).
    /// </summary>
    public float ClipDurationSeconds;

    public void Update(float dt)
    {
        if (!IsPlaying) return;

        // Determine duration: prefer skeletal clip duration, fallback to legacy clips
        float duration = ClipDurationSeconds;
        if (duration <= 0 && Clips.Count > 0 && CurrentClipIndex < Clips.Count)
            duration = Clips[CurrentClipIndex].Duration;
        if (duration <= 0) return;

        PlaybackTime += dt;
        if (PlaybackTime >= duration)
        {
            PlaybackTime = Loop ? PlaybackTime % duration : duration;
            if (!Loop) IsPlaying = false;
        }
    }
}

// ── Buoyant Body (Interaction Sync) ───────────────────────────────────────
public class BuoyantBodyComponent
{
    public bool Enabled = true;
    public float Waterline = 0f;    // Manual lift/sink
    public float BobIntensity = 1f; // Multiplier for buoyancy push
    public float TiltIntensity = 1f;// How much it rocks with waves
    
    public Vector3 AnchorPosition = Vector3.Zero; // The rest position
    public Vector3 LastPosition = Vector3.Zero;   // To detect manual moves
    public float Velocity = 0f;                   // for damped spring
}

// ── Randomizer Overrides ───────────────────────────────────────────────────
public class PositionRandomizerComponent
{
    public bool Enabled = true;
    public Vector3 MinBounds = new(-5, 0, -5);
    public Vector3 MaxBounds = new(5, 5, 5);
}

public class RotationRandomizerComponent
{
    public bool Enabled = true;
    public Vector3 MinAngles = new(-180, -180, -180);
    public Vector3 MaxAngles = new(180, 180, 180);
}

public class ScaleRandomizerComponent
{
    public bool Enabled = true;
    public float MinScale = 0.5f;
    public float MaxScale = 2.0f;
    public bool UniformScale = true;
}

public class TextureRandomizerComponent
{
    public bool Enabled = true;
    // For now, it will just use the global pool if enabled, 
    // but we could add specific texture filters here later.
}

public class DepthScaleComponent
{
    public bool Enabled = true;
    public float NearScale = 2.0f;
    public float FarScale = 0.3f;
    public float ReferenceDistance = 10f;
}

public class MaterialRandomizerComponent
{
    public bool Enabled = true;
    public bool RandomizeColor = true;
    public bool RandomizeTexture = false;
    public bool RandomizeEmissive = false;
    
    // Limits
    public float MinBrightness = 0.2f;
    public float MaxBrightness = 1.0f;
    public float MinEmissive = 1.0f;
    public float MaxEmissive = 5.0f;
    
    // New: Palette support
    public bool UsePalette = false;
    public List<Vector4> ColorPalette = new() { new Vector4(1,0,0,1), new Vector4(0,1,0,1), new Vector4(0,0,1,1) };
    public int SelectedColorIndex = 0;

    // Selection & Local Pool
    public string? SelectedTexturePath;
    public List<string> LocalTexturePool = new();

    // Filter by material name? (Empty = all)
    public string MaterialNameFilter = "";
}
