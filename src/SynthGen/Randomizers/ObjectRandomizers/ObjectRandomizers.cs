using System.Numerics;
using ImGuiNET;
using SynthGen.Scene;
using SynthGen.Scene.Components;

namespace SynthGen.Randomizers.ObjectRandomizers;

/// <summary>
/// Checks if an object (or any ancestor) is excluded from randomization.
/// </summary>
static class RandomizerHelper
{
    public static bool ShouldRandomize(SceneObject obj)
    {
        var current = obj;
        while (current != null)
        {
            if (current.ExcludeFromRandomization) return false;
            current = current.Parent;
        }
        return true;
    }

    public static bool IsModelRoot(SceneObject obj)
    {
        // Only randomize transforms for top-level root objects 
        // to prevent shattering characters/groups into pieces
        if (obj.Parent != null) return false;
        
        // Prevent randomizing structural cameras/lights by ensuring it has meshes
        return HasMeshDescendant(obj);
    }
    
    private static bool HasMeshDescendant(SceneObject obj)
    {
        if (obj.HasComponent<MeshRendererComponent>()) return true;
        foreach (var child in obj.Children)
        {
            if (HasMeshDescendant(child)) return true;
        }
        return false;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Position Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class PositionRandomizer : RandomizerBase
{
    public override string Name => "Position";
    public override string Category => "Object";

    public Vector3 MinBounds = new(-5, 0, -5);
    public Vector3 MaxBounds = new(5, 5, 5);

    public override void Randomize(SceneGraph scene, Random rng)
    {
        foreach (var obj in scene.Objects)
        {
            if (!RandomizerHelper.IsModelRoot(obj)) continue;
            if (!RandomizerHelper.ShouldRandomize(obj)) continue;
            
            var comp = obj.GetComponent<PositionRandomizerComponent>();
            if (comp == null || !comp.Enabled) continue;

            obj.Transform.Position = new Vector3(
                RandRange(rng, comp.MinBounds.X, comp.MaxBounds.X),
                RandRange(rng, comp.MinBounds.Y, comp.MaxBounds.Y),
                RandRange(rng, comp.MinBounds.Z, comp.MaxBounds.Z)
            );
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloat3("Min Bounds", ref MinBounds, 0.1f);
        ImGui.DragFloat3("Max Bounds", ref MaxBounds, 0.1f);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Rotation Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class RotationRandomizer : RandomizerBase
{
    public override string Name => "Rotation";
    public override string Category => "Object";

    public Vector3 MinAngles = new(-180, -180, -180);
    public Vector3 MaxAngles = new(180, 180, 180);

    public override void Randomize(SceneGraph scene, Random rng)
    {
        foreach (var obj in scene.Objects)
        {
            if (!RandomizerHelper.IsModelRoot(obj)) continue;
            if (!RandomizerHelper.ShouldRandomize(obj)) continue;
            
            var comp = obj.GetComponent<RotationRandomizerComponent>();
            if (comp == null || !comp.Enabled) continue;

            obj.Transform.Rotation = new Vector3(
                RandRange(rng, comp.MinAngles.X, comp.MaxAngles.X),
                RandRange(rng, comp.MinAngles.Y, comp.MaxAngles.Y),
                RandRange(rng, comp.MinAngles.Z, comp.MaxAngles.Z)
            );
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloat3("Min Angles", ref MinAngles, 1f);
        ImGui.DragFloat3("Max Angles", ref MaxAngles, 1f);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Scale Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class ScaleRandomizer : RandomizerBase
{
    public override string Name => "Scale";
    public override string Category => "Object";

    public float MinScale = 0.5f;
    public float MaxScale = 2.0f;
    public bool UniformScale = true;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        foreach (var obj in scene.Objects)
        {
            if (!RandomizerHelper.IsModelRoot(obj)) continue;
            if (!RandomizerHelper.ShouldRandomize(obj)) continue;

            var comp = obj.GetComponent<ScaleRandomizerComponent>();
            if (comp == null || !comp.Enabled) continue;

            if (comp.UniformScale)
            {
                float s = RandRange(rng, comp.MinScale, comp.MaxScale);
                obj.Transform.Scale = new Vector3(s, s, s);
            }
            else
            {
                obj.Transform.Scale = new Vector3(
                    RandRange(rng, comp.MinScale, comp.MaxScale),
                    RandRange(rng, comp.MinScale, comp.MaxScale),
                    RandRange(rng, comp.MinScale, comp.MaxScale)
                );
            }
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloat("Min Scale", ref MinScale, 0.05f, 0.01f, 10f);
        ImGui.DragFloat("Max Scale", ref MaxScale, 0.05f, 0.01f, 10f);
        ImGui.Checkbox("Uniform", ref UniformScale);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Texture Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class TextureRandomizer : RandomizerBase
{
    public override string Name => "Texture";
    public override string Category => "Object";

    // Will be populated from AssetManager
    public List<string> TexturePaths { get; set; } = new();
    public Func<string, uint>? LoadTextureFunc { get; set; }

    public override void Randomize(SceneGraph scene, Random rng)
    {
        if (TexturePaths.Count == 0 || LoadTextureFunc == null) return;

        foreach (var obj in scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr == null) continue;
            if (!RandomizerHelper.ShouldRandomize(obj)) continue;

            var comp = obj.GetComponent<TextureRandomizerComponent>();
            if (comp == null || !comp.Enabled) continue;

            string texPath = TexturePaths[rng.Next(TexturePaths.Count)];
            uint texId = LoadTextureFunc(texPath);
            if (texId > 0)
            {
                mr.Material.AlbedoTextureID = texId;
                mr.Material.AlbedoTexturePath = texPath;
            }
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.Text($"Available textures: {TexturePaths.Count}");
        ImGui.TextWrapped("Place textures in assets/textures/ folder");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Depth-based Scale Mapper
// ═══════════════════════════════════════════════════════════════════════════
public class DepthScaleMapper : RandomizerBase
{
    public override string Name => "Depth Scale";
    public override string Category => "Object";

    public float NearScale = 2.0f;   // scale at near plane
    public float FarScale = 0.3f;    // scale at far plane
    public float ReferenceDistance = 10f;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;

        foreach (var obj in scene.Objects)
        {
            if (!obj.HasComponent<MeshRendererComponent>()) continue;
            if (!RandomizerHelper.ShouldRandomize(obj)) continue;

            var comp = obj.GetComponent<DepthScaleComponent>();
            if (comp == null || !comp.Enabled) continue;

            float nearS = comp.NearScale;
            float farS = comp.FarScale;
            float refDist = comp.ReferenceDistance;

            float dist = Vector3.Distance(cam.Transform.Position, obj.Transform.Position);
            float t = MathF.Min(dist / refDist, 1f);
            float scale = MathF.Max(0.1f, nearS + (farS - nearS) * t);
            obj.Transform.Scale = new Vector3(scale, scale, scale);
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloat("Near Scale", ref NearScale, 0.05f, 0.1f, 5f);
        ImGui.DragFloat("Far Scale", ref FarScale, 0.05f, 0.1f, 5f);
        ImGui.DragFloat("Reference Dist", ref ReferenceDistance, 0.5f, 1f, 100f);
    }
}

