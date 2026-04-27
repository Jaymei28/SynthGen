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
        public SettingsData? Settings { get; set; }
        public List<RandomizerData> Randomizers { get; set; } = new();
    }

    public class SettingsData
    {
        public float Exposure { get; set; } = 1.0f;
        public float BloomStrength { get; set; } = 0.5f;
        public float BloomThreshold { get; set; } = 1.0f;
        public float NoiseIntensity { get; set; } = 0.05f;
        public float FogIntensity { get; set; } = 0.0f;
        public float FisheyeStrength { get; set; } = 0.0f;
        public float BlurRadius { get; set; } = 0.0f;
        public bool AmbientOcclusion { get; set; } = true;
        
        public int CaptureFrames { get; set; } = 10;
        public int SubFrames { get; set; } = 1;
        public float AnimDuration { get; set; } = 2.0f;
        
        public string? HDRIPath { get; set; }
        public float HDRIStrength { get; set; } = 1.0f;
    }

    public class RandomizerData
    {
        public string Type { get; set; } = "";
        public bool Enabled { get; set; }
        public string JsonData { get; set; } = "";
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

    public class Vector2Converter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            float x = 0, y = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string propertyName = reader.GetString()?.ToLower() ?? "";
                reader.Read();
                if (propertyName == "x") x = reader.GetSingle();
                else if (propertyName == "y") y = reader.GetSingle();
            }
            return new Vector2(x, y);
        }
        public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }
    }

    public class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            float x = 0, y = 0, z = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string propertyName = reader.GetString()?.ToLower() ?? "";
                reader.Read();
                if (propertyName == "x") x = reader.GetSingle();
                else if (propertyName == "y") y = reader.GetSingle();
                else if (propertyName == "z") z = reader.GetSingle();
            }
            return new Vector3(x, y, z);
        }
        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("z", value.Z);
            writer.WriteEndObject();
        }
    }

    public class Vector4Converter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            float x = 0, y = 0, z = 0, w = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string propertyName = reader.GetString()?.ToLower() ?? "";
                reader.Read();
                if (propertyName == "x") x = reader.GetSingle();
                else if (propertyName == "y") y = reader.GetSingle();
                else if (propertyName == "z") z = reader.GetSingle();
                else if (propertyName == "w") w = reader.GetSingle();
            }
            return new Vector4(x, y, z, w);
        }
        public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("z", value.Z);
            writer.WriteNumber("w", value.W);
            writer.WriteEndObject();
        }
    }

    private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 256,
        IncludeFields = true,
        Converters = { new Vector2Converter(), new Vector3Converter(), new Vector4Converter() }
    };

    public static void Save(App.Application app, UI.UIManager ui, string path)
    {
        var scene = app.Scene;
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

        // Save Global Settings
        var renderer = app.Renderer;
        var capture = app.CaptureManager;
        var cam = app.Scene.ActiveCamera;
        
        data.Settings = new SettingsData();
        if (cam != null)
        {
            data.Settings.Exposure = cam.Exposure;
            data.Settings.BloomStrength = cam.BloomIntensity;
            data.Settings.BloomThreshold = cam.BloomThreshold;
            data.Settings.NoiseIntensity = cam.NoiseIntensity;
            data.Settings.FogIntensity = cam.FogDensity;
            data.Settings.FisheyeStrength = cam.FisheyeStrength;
            data.Settings.BlurRadius = cam.BlurRadius;
            data.Settings.AmbientOcclusion = cam.SSAOIntensity > 0;
        }
        
        data.Settings.CaptureFrames = capture.TotalFrames;
        data.Settings.SubFrames = capture.SubFramesPerIteration;
        data.Settings.AnimDuration = capture.AnimationDuration;

        // Save Randomizers
        foreach (var r in ui.AllRandomizers)
        {
            data.Randomizers.Add(new RandomizerData
            {
                Type = r.GetType().Name,
                Enabled = r.Enabled,
                JsonData = JsonSerializer.Serialize((object)r, _options)
            });
        }

        var json = JsonSerializer.Serialize(data, _options);
        File.WriteAllText(path, json);
    }

    private static ObjectData SerializeObject(SceneObject obj)
    {
        var data = new ObjectData
        {
            Name = obj.Name,
            AssetPath = string.IsNullOrEmpty(obj.AssetPath) ? null : Path.GetFullPath(obj.AssetPath),
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

    public static void Load(SceneGraph scene, string path, App.Application app, UI.UIManager ui, Action<string>? log = null)
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
            // Restore Global Settings
            if (data.Settings != null)
            {
                var renderer = app.Renderer;
                var capture = app.CaptureManager;
                var cam = app.Scene.ActiveCamera;
                
                if (cam != null)
                {
                    cam.Exposure = data.Settings.Exposure;
                    cam.BloomIntensity = data.Settings.BloomStrength;
                    cam.BloomThreshold = data.Settings.BloomThreshold;
                    cam.NoiseIntensity = data.Settings.NoiseIntensity;
                    cam.FogDensity = data.Settings.FogIntensity;
                    cam.FisheyeStrength = data.Settings.FisheyeStrength;
                    cam.BlurRadius = data.Settings.BlurRadius;
                    cam.SSAOIntensity = data.Settings.AmbientOcclusion ? 1.0f : 0.0f;
                }
                
                capture.TotalFrames = data.Settings.CaptureFrames;
                capture.SubFramesPerIteration = data.Settings.SubFrames;
                capture.AnimationDuration = data.Settings.AnimDuration;
            }

            // Restore Randomizers
            if (data.Randomizers != null && data.Randomizers.Count > 0)
            {
                foreach (var rData in data.Randomizers)
                {
                    var existing = ui.AllRandomizers.FirstOrDefault(r => r.GetType().Name == rData.Type);
                    if (existing != null)
                    {
                        var restored = (Randomizers.RandomizerBase?)JsonSerializer.Deserialize(rData.JsonData, existing.GetType(), _options);
                        if (restored != null)
                        {
                            restored.Enabled = rData.Enabled;
                            int idx = ((IReadOnlyList<Randomizers.RandomizerBase>)ui.AllRandomizers).ToList().IndexOf(existing);
                            if (idx >= 0) ui.UpdateRandomizer(idx, restored);
                        }
                    }
                }
            }
            ui.RefreshHDRIs();
            ui.RefreshTexturePools();

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
            else if (!string.IsNullOrEmpty(data.AssetPath))
            {
                string path = data.AssetPath;
                if (!File.Exists(path))
                {
                     // Fallback 1: Try relative to current directory
                     string relPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                     if (File.Exists(relPath)) path = relPath;
                     else 
                     {
                         // Fallback 2: Try searching common assets folder
                         string fileName = Path.GetFileName(path);
                         string[] searches = { "assets/models", "assets", "../assets/models" };
                         foreach(var s in searches) {
                             string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, s, fileName);
                             if (File.Exists(p)) { path = p; break; }
                         }
                     }
                }

                if (File.Exists(path))
                {
                    obj = app.AssetManager.ImportModelHierarchical(path, log);
                    if (obj != null) obj.Name = data.Name;
                }
                else
                {
                    log?.Invoke($"[Warning] Missing asset: {data.AssetPath}");
                    obj = new SceneObject(data.Name);
                }
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
                            // Transfer metadata from JSON to existing component
                            existing.Visible = mrc.Visible;
                            existing.Material.BaseColor = mrc.Material.BaseColor;
                            existing.Material.Smoothness = mrc.Material.Smoothness;
                            existing.Material.Metallic = mrc.Material.Metallic;
                            existing.Material.NormalScale = mrc.Material.NormalScale;
                            existing.Material.ColorIntensity = mrc.Material.ColorIntensity;
                            existing.Material.EmissiveColor = mrc.Material.EmissiveColor;
                            existing.Material.EmissiveIntensity = mrc.Material.EmissiveIntensity;
                            
                            // Restore textures if paths exist
                            if (!string.IsNullOrEmpty(mrc.Material.AlbedoTexturePath))
                            {
                                existing.Material.AlbedoTexturePath = mrc.Material.AlbedoTexturePath;
                                existing.Material.AlbedoTextureID = app.AssetManager.LoadTexture(mrc.Material.AlbedoTexturePath);
                            }
                            if (!string.IsNullOrEmpty(mrc.Material.NormalTexturePath))
                            {
                                existing.Material.NormalTexturePath = mrc.Material.NormalTexturePath;
                                existing.Material.NormalTextureID = app.AssetManager.LoadTexture(mrc.Material.NormalTexturePath);
                            }
                        }
                        else 
                        {
                            // No existing component? Add the new one and attempt texture load
                            if (!string.IsNullOrEmpty(mrc.Material.AlbedoTexturePath))
                                mrc.Material.AlbedoTextureID = app.AssetManager.LoadTexture(mrc.Material.AlbedoTexturePath);
                            obj.AddComponent(mrc);
                        }
                    }
                    else if (comp is LabelComponent lc) obj.AddComponent(lc);
                    else if (comp is LightComponent lic) obj.AddComponent(lic);
                    else if (comp is AnimationPlayerComponent apc) 
                    {
                        var existing = obj.GetComponent<AnimationPlayerComponent>();
                        if (existing != null) { existing.PlaybackTime = apc.PlaybackTime; existing.CurrentClipIndex = apc.CurrentClipIndex; }
                        else obj.AddComponent(apc);
                    }
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
            var existingChild = obj.Children.FirstOrDefault(c => c.Name == childData.Name)
                             ?? obj.Children.FirstOrDefault(c => c.Name.Equals(childData.Name, StringComparison.OrdinalIgnoreCase));
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
                         if (existing != null) 
                         { 
                            existing.Visible = mrc.Visible;
                            existing.Material.BaseColor = mrc.Material.BaseColor;
                            existing.Material.Smoothness = mrc.Material.Smoothness;
                            existing.Material.Metallic = mrc.Material.Metallic;
                            existing.Material.NormalScale = mrc.Material.NormalScale;
                            existing.Material.ColorIntensity = mrc.Material.ColorIntensity;
                            existing.Material.EmissiveColor = mrc.Material.EmissiveColor;
                            existing.Material.EmissiveIntensity = mrc.Material.EmissiveIntensity;

                            if (!string.IsNullOrEmpty(mrc.Material.AlbedoTexturePath))
                            {
                                existing.Material.AlbedoTexturePath = mrc.Material.AlbedoTexturePath;
                                existing.Material.AlbedoTextureID = app.AssetManager.LoadTexture(mrc.Material.AlbedoTexturePath);
                            }
                            if (!string.IsNullOrEmpty(mrc.Material.NormalTexturePath))
                            {
                                existing.Material.NormalTexturePath = mrc.Material.NormalTexturePath;
                                existing.Material.NormalTextureID = app.AssetManager.LoadTexture(mrc.Material.NormalTexturePath);
                            }
                         }
                    }
                    else if (comp is LabelComponent lc) obj.AddComponent(lc);
                    else if (comp is LightComponent lic) obj.AddComponent(lic);
                    else if (comp is AnimationPlayerComponent apc) 
                    {
                        var existing = obj.GetComponent<AnimationPlayerComponent>();
                        if (existing != null) { existing.PlaybackTime = apc.PlaybackTime; existing.CurrentClipIndex = apc.CurrentClipIndex; }
                        else obj.AddComponent(apc);
                    }
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
            var existingChild = obj.Children.FirstOrDefault(c => c.Name == childData.Name)
                             ?? obj.Children.FirstOrDefault(c => c.Name.Equals(childData.Name, StringComparison.OrdinalIgnoreCase));
            if (existingChild != null) ApplyDataToExistingObject(existingChild, childData, app, log);
            else
            {
                var newChild = DeserializeObject(childData, app, log);
                if (newChild != null) obj.AddChild(newChild);
            }
        }
    }
}
