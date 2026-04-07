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
    public List<SceneObject> SelectedObjects { get; } = new();

    public SceneObject? SelectedObject
    {
        get => SelectedObjects.LastOrDefault();
        set
        {
            SelectedObjects.Clear();
            if (value != null) SelectedObjects.Add(value);
        }
    }

    public IReadOnlyList<SceneObject> Objects => _objects;

    public void AddObject(SceneObject obj)
    {
        if (!_objects.Contains(obj)) _objects.Add(obj);
        foreach (var child in obj.Children) AddObject(child);
    }

    public void RemoveObject(SceneObject obj)
    {
        SelectedObjects.Remove(obj);
        if (SelectedObject == obj) SelectedObject = null;
        
        if (obj.Parent != null)
        {
            obj.Parent.Children.Remove(obj);
        }
        else
        {
            _objects.Remove(obj);
        }
        
        foreach (var child in obj.Children.ToList()) 
            RemoveObject(child);
    }

    public void Clear()
    {
        _objects.Clear();
        SelectedObjects.Clear();
    }

    public List<SceneObject> GetObjectsWithComponent<T>() where T : class
    {
        return _objects.Where(o => o.HasComponent<T>()).ToList();
    }

    public int ObjectCount => _objects.Count;
}
