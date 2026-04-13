using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SynthGen.Scene.Components;

namespace SynthGen.Scene;

public class SceneSerializer
{
    public class ScenarioData
    {
        public List<ObjectData> Objects { get; set; } = new();
        public CameraData? Camera { get; set; }
    }

    public class ObjectData
    {
        public string Name { get; set; } = "";
        public string? AssetPath { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 Rotation { get; set; }
        public Vector3 Scale { get; set; }
        public bool ExcludeFromRandomization { get; set; }
        public string BodyPartGroup { get; set; } = "";
        public string PoseStandard { get; set; } = "COCO";
        
        public List<ComponentData> Components { get; set; } = new();
        public List<ObjectData> Children { get; set; } = new();
    }

    public class ComponentData
    {
        public string Type { get; set; } = "";
        public string JsonData { get; set; } = "";
    }

    public class CameraData
    {
        public Vector3 Position { get; set; }
        public float OrbitYaw { get; set; }
        public float OrbitPitch { get; set; }
        public float OrbitDistance { get; set; }
        public Vector3 OrbitTarget { get; set; }
        public float FieldOfView { get; set; }
    }

    private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 256
    };

    public static void Save(SceneGraph scene, string path)
    {
        var data = new ScenarioData();
        
        foreach (var obj in scene.Objects.Where(o => o.Parent == null))
        {
            data.Objects.Add(SerializeObject(obj));
        }

        if (scene.ActiveCamera != null)
        {
            data.Camera = new CameraData
            {
                Position = scene.ActiveCamera.Transform.Position,
                OrbitYaw = scene.ActiveCamera.OrbitYaw,
                OrbitPitch = scene.ActiveCamera.OrbitPitch,
                OrbitDistance = scene.ActiveCamera.OrbitDistance,
                OrbitTarget = scene.ActiveCamera.OrbitTarget,
                FieldOfView = scene.ActiveCamera.FieldOfView
            };
        }

        var json = JsonSerializer.Serialize(data, _options);
        File.WriteAllText(path, json);
    }

    private static ObjectData SerializeObject(SceneObject obj)
    {
        var data = new ObjectData
        {
            Name = obj.Name,
            AssetPath = obj.AssetPath,
            Position = obj.Transform.Position,
            Rotation = obj.Transform.Rotation,
            Scale = obj.Transform.Scale,
            ExcludeFromRandomization = obj.ExcludeFromRandomization,
            BodyPartGroup = obj.BodyPartGroup,
            PoseStandard = obj.PoseStandard.ToString()
        };

        foreach (var comp in obj.GetAllComponents())
        {
            data.Components.Add(new ComponentData
            {
                Type = comp.GetType().Name,
                JsonData = JsonSerializer.Serialize(comp, _options)
            });
        }

        foreach (var child in obj.Children)
        {
            data.Children.Add(SerializeObject(child));
        }

        return data;
    }

    public static void Load(SceneGraph scene, string path, App.Application app, Action<string>? log = null)
    {
        if (!File.Exists(path)) return;
        
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<ScenarioData>(json, _options);
            if (data == null) return;

            scene.Clear();

            foreach (var objData in data.Objects)
            {
                var obj = DeserializeObject(objData, app, log);
                if (obj != null) scene.AddObject(obj);
            }

            if (data.Camera != null && scene.ActiveCamera != null)
            {
                var cam = scene.ActiveCamera;
                cam.Transform.Position = data.Camera.Position;
                cam.OrbitYaw = data.Camera.OrbitYaw;
                cam.OrbitPitch = data.Camera.OrbitPitch;
                cam.OrbitDistance = data.Camera.OrbitDistance;
                cam.OrbitTarget = data.Camera.OrbitTarget;
                cam.FieldOfView = data.Camera.FieldOfView;
            }
            
            log?.Invoke($"[Scenario] Loaded: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Error] Failed to load scenario: {ex.Message}");
        }
    }

    private static SceneObject? DeserializeObject(ObjectData data, App.Application app, Action<string>? log)
    {
        SceneObject? obj = null;

        if (!string.IsNullOrEmpty(data.AssetPath))
        {
            if (data.AssetPath.StartsWith("primitive:"))
            {
                string type = data.AssetPath.Substring("primitive:".Length);
                obj = new SceneObject(data.Name);
                var mr = new MeshRendererComponent();
                mr.Mesh = type switch
                {
                    "Cube" => Rendering.Mesh.CreateCube(app.GL),
                    "Sphere" => Rendering.Mesh.CreateSphere(app.GL),
                    _ => Rendering.Mesh.CreateCube(app.GL)
                };
                obj.AddComponent(mr);
            }
            else if (File.Exists(data.AssetPath))
            {
                obj = app.AssetManager.ImportModelHierarchical(data.AssetPath, log);
                if (obj != null) obj.Name = data.Name;
            }
            else
            {
                log?.Invoke($"[Warning] Missing asset: {data.AssetPath}");
                obj = new SceneObject(data.Name);
            }
        }
        else
        {
            obj = new SceneObject(data.Name);
        }

        if (obj == null) return null;

        obj.AssetPath = data.AssetPath;
        obj.Transform.Position = data.Position;
        obj.Transform.Rotation = data.Rotation;
        obj.Transform.Scale = data.Scale;
        obj.ExcludeFromRandomization = data.ExcludeFromRandomization;
        obj.BodyPartGroup = data.BodyPartGroup;
        if (Enum.TryParse<Annotation.PoseStandardType>(data.PoseStandard, out var std))
            obj.PoseStandard = std;

        foreach (var compData in data.Components)
        {
            Type? t = typeof(MeshRendererComponent).Assembly.GetType("SynthGen.Scene.Components." + compData.Type);
            if (t != null)
            {
                var comp = JsonSerializer.Deserialize(compData.JsonData, t, _options);
                if (comp != null)
                {
                    if (comp is MeshRendererComponent mrc)
                    {
                        var existing = obj.GetComponent<MeshRendererComponent>();
                        if (existing != null)
                        {
                            mrc.Mesh = existing.Mesh;
                            obj.AddComponent(mrc);
                        }
                        else obj.AddComponent(mrc);
                    }
                    else if (comp is LabelComponent lc) obj.AddComponent(lc);
                    else if (comp is LightComponent lic) obj.AddComponent(lic);
                    else if (comp is AnimationPlayerComponent apc) obj.AddComponent(apc);
                    else if (comp is BuoyantBodyComponent bbc) obj.AddComponent(bbc);
                    else if (comp is PositionRandomizerComponent prc) obj.AddComponent(prc);
                    else if (comp is RotationRandomizerComponent rrc) obj.AddComponent(rrc);
                    else if (comp is ScaleRandomizerComponent src) obj.AddComponent(src);
                    else if (comp is TextureRandomizerComponent trc) obj.AddComponent(trc);
                    else if (comp is DepthScaleComponent dsc) obj.AddComponent(dsc);
                    else if (comp is KeypointComponent kpc) obj.AddComponent(kpc);
                }
            }
        }

        foreach (var childData in data.Children)
        {
            var existingChild = obj.Children.FirstOrDefault(c => c.Name == childData.Name);
            if (existingChild != null) ApplyDataToExistingObject(existingChild, childData, app, log);
            else
            {
                var newChild = DeserializeObject(childData, app, log);
                if (newChild != null) obj.AddChild(newChild);
            }
        }

        return obj;
    }

    private static void ApplyDataToExistingObject(SceneObject obj, ObjectData data, App.Application app, Action<string>? log)
    {
        obj.Transform.Position = data.Position;
        obj.Transform.Rotation = data.Rotation;
        obj.Transform.Scale = data.Scale;
        obj.ExcludeFromRandomization = data.ExcludeFromRandomization;
        obj.BodyPartGroup = data.BodyPartGroup;
        if (Enum.TryParse<Annotation.PoseStandardType>(data.PoseStandard, out var std))
            obj.PoseStandard = std;

        foreach (var compData in data.Components)
        {
            Type? t = typeof(MeshRendererComponent).Assembly.GetType("SynthGen.Scene.Components." + compData.Type);
            if (t != null)
            {
                var comp = JsonSerializer.Deserialize(compData.JsonData, t, _options);
                if (comp != null)
                {
                    if (comp is MeshRendererComponent mrc)
                    {
                         var existing = obj.GetComponent<MeshRendererComponent>();
                         if (existing != null) { mrc.Mesh = existing.Mesh; obj.AddComponent(mrc); }
                    }
                    else if (comp is LabelComponent lc) obj.AddComponent(lc);
                    else if (comp is LightComponent lic) obj.AddComponent(lic);
                    else if (comp is AnimationPlayerComponent apc) obj.AddComponent(apc);
                    else if (comp is BuoyantBodyComponent bbc) obj.AddComponent(bbc);
                    else if (comp is PositionRandomizerComponent prc) obj.AddComponent(prc);
                    else if (comp is RotationRandomizerComponent rrc) obj.AddComponent(rrc);
                    else if (comp is ScaleRandomizerComponent src) obj.AddComponent(src);
                    else if (comp is TextureRandomizerComponent trc) obj.AddComponent(trc);
                    else if (comp is DepthScaleComponent dsc) obj.AddComponent(dsc);
                    else if (comp is KeypointComponent kpc) obj.AddComponent(kpc);
                }
            }
        }

        foreach (var childData in data.Children)
        {
            var existingChild = obj.Children.FirstOrDefault(c => c.Name == childData.Name);
            if (existingChild != null) ApplyDataToExistingObject(existingChild, childData, app, log);
            else
            {
                var newChild = DeserializeObject(childData, app, log);
                if (newChild != null) obj.AddChild(newChild);
            }
        }
    }
}
