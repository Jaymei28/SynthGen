using System.Numerics;
using System.Collections.Generic;

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
    public string ClassName = "object";
    public Vector3 SegmentationColor = new(1, 0, 0); // unique flat color
    public int InstanceID;  // auto-assigned

    private static int _nextInstanceID = 1;
    public LabelComponent() { InstanceID = _nextInstanceID++; }
}

// ── Mesh Renderer ──────────────────────────────────────────────────────────
public class MeshRendererComponent
{
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
    public float PlaybackTime;
    public bool IsPlaying;
    public bool Loop = true;

    public void Update(float dt)
    {
        if (!IsPlaying || Clips.Count == 0) return;
        var clip = Clips[CurrentClipIndex];
        PlaybackTime += dt;
        if (PlaybackTime >= clip.Duration)
        {
            PlaybackTime = Loop ? PlaybackTime % clip.Duration : clip.Duration;
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
