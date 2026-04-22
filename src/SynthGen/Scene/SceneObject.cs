using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using SynthGen.Scene.Components;

namespace SynthGen.Scene;

/// <summary>
/// Base class for all objects in the scene (lights, cameras, 3D models).
/// </summary>
public class SceneObject
{
    public string Name { get; set; }
    public string? AssetPath { get; set; }
    public Transform Transform { get; set; } = new();
    public bool IsSelected { get; set; }
    public SceneObject? Parent { get; set; }
    public List<SceneObject> Children { get; set; } = new();

    /// <summary>When true, object randomizers skip this object and all its children.</summary>
    public bool ExcludeFromRandomization { get; set; }

    /// <summary>Body part group name for keypoint annotation (e.g., "Head", "Left Arm").</summary>
    public string BodyPartGroup { get; set; } = "";

    /// <summary>Which pose estimation standard this character uses (COCO, Fisheye, etc.)</summary>
    public Annotation.PoseStandardType PoseStandard { get; set; } = Annotation.PoseStandardType.COCO;
 
    public void AddChild(SceneObject child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    // Component storage
    private readonly Dictionary<Type, object> _components = new();

    public Vector3 PickingColor { get; }

    public SceneObject(string name = "NewObject")
    {
        Name = name;
        Transform = new Transform();

        // Generate a unique picking color based on a GUID so duplicates have unique IDs
        var id = Guid.NewGuid().GetHashCode();
        PickingColor = new Vector3(
            ((id >> 16) & 0xFF) / 255f,
            ((id >> 8) & 0xFF) / 255f,
            (id & 0xFF) / 255f
        );
    }

    public void AddComponent<T>(T component) where T : class
    {
        _components[typeof(T)] = component;
    }

    public T? GetComponent<T>() where T : class
    {
        return _components.TryGetValue(typeof(T), out var c) ? (T)c : null;
    }

    public bool HasComponent<T>() where T : class
    {
        return _components.ContainsKey(typeof(T));
    }

    public void RemoveComponent<T>() where T : class
    {
        _components.Remove(typeof(T));
    }

    public IEnumerable<object> GetAllComponents() => _components.Values;

    /// <summary>Computes the world matrix recursively from parent chain.</summary>
    public Matrix4x4 GetWorldMatrix()
    {
        var local = Transform.GetMatrix();
        return Parent != null ? local * Parent.GetWorldMatrix() : local;
    }
    public SceneObject Clone()
    {
        var clone = new SceneObject($"{Name} (Copy)");
        clone.Transform.Position = Transform.Position;
        clone.Transform.Rotation = Transform.Rotation;
        clone.Transform.Scale = Transform.Scale;
        clone.ExcludeFromRandomization = ExcludeFromRandomization;
        clone.BodyPartGroup = BodyPartGroup;

        // Clone components (shallow copy of members is usually fine for these DTO-like components)
        foreach (var entry in _components)
        {
            var type = entry.Key;
            var comp = entry.Value;

            // Manual deep-copy logic for specific components that need it
            if (comp is LabelComponent lc)
            {
                clone.AddComponent(new LabelComponent
                {
                    ClassID = lc.ClassID,
                    ClassName = lc.ClassName,
                    SegmentationColor = lc.SegmentationColor
                });
            }
            else if (comp is MeshRendererComponent mr)
            {
                clone.AddComponent(new MeshRendererComponent(mr.Mesh!)
                {
                    Material = new Rendering.Material {
                        BaseColor = mr.Material.BaseColor,
                        AlbedoTexturePath = mr.Material.AlbedoTexturePath,
                        AlbedoTextureID = mr.Material.AlbedoTextureID,
                        NormalTexturePath = mr.Material.NormalTexturePath,
                        NormalTextureID = mr.Material.NormalTextureID,
                        Smoothness = mr.Material.Smoothness,
                        Metallic = mr.Material.Metallic,
                        NormalScale = mr.Material.NormalScale,
                        ColorIntensity = mr.Material.ColorIntensity,
                        EmissiveColor = mr.Material.EmissiveColor,
                        EmissiveIntensity = mr.Material.EmissiveIntensity
                    },
                    Visible = mr.Visible
                });
            }
            else
            {
                // Simple memberwise clone for basic components
                var method = comp.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                {
                    clone.AddComponent(method.Invoke(comp, null)!);
                }
                else
                {
                    // Fallback to just adding the same instance if it's stateless, 
                    // though most of ours carry state so we should be careful.
                    clone.AddComponent(comp);
                }
            }
        }

        // Clone children recursively
        foreach (var child in Children)
        {
            clone.AddChild(child.Clone());
        }

        return clone;
    }
}

/// <summary>
/// Transform: position, rotation (Euler degrees), scale.
/// </summary>
public class Transform
{
    public Vector3 Position = Vector3.Zero;
    public Vector3 Rotation = Vector3.Zero;  // Euler degrees
    public Vector3 Scale = Vector3.One;

    public Matrix4x4 GetMatrix()
    {
        var radX = Rotation.X * MathF.PI / 180f;
        var radY = Rotation.Y * MathF.PI / 180f;
        var radZ = Rotation.Z * MathF.PI / 180f;

        return Matrix4x4.CreateScale(Scale)
             * Matrix4x4.CreateRotationX(radX)
             * Matrix4x4.CreateRotationY(radY)
             * Matrix4x4.CreateRotationZ(radZ)
             * Matrix4x4.CreateTranslation(Position);
    }
}
