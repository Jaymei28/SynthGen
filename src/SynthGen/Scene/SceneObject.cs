using System.Numerics;

namespace SynthGen.Scene;

/// <summary>
/// Base class for all objects in the scene (lights, cameras, 3D models).
/// </summary>
public class SceneObject
{
    public string Name { get; set; }
    public Transform Transform { get; set; } = new();
    public bool IsSelected { get; set; }
    public SceneObject? Parent { get; set; }
    public List<SceneObject> Children { get; set; } = new();

    /// <summary>When true, object randomizers skip this object and all its children.</summary>
    public bool ExcludeFromRandomization { get; set; }

    /// <summary>Body part group name for keypoint annotation (e.g., "Head", "Left Arm").</summary>
    public string BodyPartGroup { get; set; } = "";
 
    public void AddChild(SceneObject child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    // Component storage
    private readonly Dictionary<Type, object> _components = new();

    public SceneObject(string name = "Object")
    {
        Name = name;
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
