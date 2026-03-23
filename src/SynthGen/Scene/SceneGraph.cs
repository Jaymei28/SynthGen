using System.Numerics;
using System.Collections.Generic;
using System.Linq;

namespace SynthGen.Scene;

/// <summary>
/// Manages all objects in the scene as a flat list for rendering and picking,
/// but supports hierarchical relationships for transformations.
/// </summary>
public class SceneGraph
{
    private readonly List<SceneObject> _objects = new();

    public Camera? ActiveCamera { get; set; }
    public SceneObject? SelectedObject { get; set; }

    public IReadOnlyList<SceneObject> Objects => _objects;

    public void AddObject(SceneObject obj)
    {
        if (!_objects.Contains(obj)) _objects.Add(obj);
        foreach (var child in obj.Children) AddObject(child);
    }

    public void RemoveObject(SceneObject obj)
    {
        if (SelectedObject == obj) SelectedObject = null;
        _objects.Remove(obj);
        foreach (var child in obj.Children) RemoveObject(child);
    }

    public void Clear()
    {
        _objects.Clear();
        SelectedObject = null;
    }

    public List<SceneObject> GetObjectsWithComponent<T>() where T : class
    {
        return _objects.Where(o => o.HasComponent<T>()).ToList();
    }

    public int ObjectCount => _objects.Count;
}
