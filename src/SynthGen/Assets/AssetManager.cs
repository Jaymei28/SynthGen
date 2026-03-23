using System;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Assimp;
using StbImageSharp;
using TinyEXR;
using SynthGen.Rendering;
using System.Linq;
using System.Collections.Generic;

using GLTextureWrapMode = Silk.NET.OpenGL.TextureWrapMode;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace SynthGen.Assets;

/// <summary>
/// Manages asset loading and caching: FBX models, textures, HDRIs.
/// </summary>
public class AssetManager
{
    private readonly GL _gl;
    private readonly Dictionary<string, Rendering.Mesh> _meshCache = new();
    private readonly Dictionary<string, uint> _textureCache = new();

    public string ModelsPath { get; set; } = "assets/models";
    public string TexturesPath { get; set; } = "assets/textures";
    public string AnimationsPath { get; set; } = "assets/animations";
    public string HDRIPath { get; set; } = "assets/hdri";

    public AssetManager(GL gl)
    {
        _gl = gl;
        EnsureDirectories();
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(TexturesPath);
        Directory.CreateDirectory(AnimationsPath);
        Directory.CreateDirectory(HDRIPath);
    }

    public string[] GetAvailableModels()
    {
        if (!Directory.Exists(ModelsPath)) return Array.Empty<string>();
        return Directory.GetFiles(ModelsPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public string[] GetAvailableTextures()
    {
        if (!Directory.Exists(TexturesPath)) return Array.Empty<string>();
        return Directory.GetFiles(TexturesPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public string[] GetAvailableHDRIs()
    {
        if (!Directory.Exists(HDRIPath)) return Array.Empty<string>();
        return Directory.GetFiles(HDRIPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Imports a model hierarchically, spawning SceneObjects only for mesh-bearing nodes.
    /// Filters out cameras, lights, sky domes, shadow catchers, and Assimp pivot helpers.
    /// </summary>
    public Scene.SceneObject? ImportModelHierarchical(string path, Action<string>? log = null)
    {
        try
        {
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(path, 
                PostProcessSteps.Triangulate |
                PostProcessSteps.GenerateNormals |
                PostProcessSteps.FlipUVs |
                PostProcessSteps.CalculateTangentSpace);

            if (scene == null || scene.RootNode == null) return null;

            var rootObj = new Scene.SceneObject(Path.GetFileNameWithoutExtension(path));
            
            List<Scene.SceneObject> flatList = new();
            // Accumulate root transform and start recursive build
            var rootMatrix = ConvertMatrix(scene.RootNode.Transform);
            BuildHierarchyFiltered(scene.RootNode, scene, rootObj, path, flatList, rootMatrix);

            log?.Invoke($"[Assets] Hierarchical import: {Path.GetFileName(path)}. {flatList.Count} mesh objects found.");
            return rootObj;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[AssetManager] Fatal error: {ex.Message}");
            return null;
        }
    }

    // Names to skip during FBX import (cameras, lights, helper objects from 3D software)
    private static readonly string[] _skipNames = { 
        "camera", "main camera", "render", "sky", "shadow catcher", 
        "sun", "light", "environment", "hdri" 
    };

    private bool ShouldSkipNode(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLower().Trim();
        foreach (var skip in _skipNames)
            if (lower.Contains(skip)) return true;
        return false;
    }

    private bool IsAssimpPivotNode(string name)
    {
        return name.Contains("$AssimpFbx$");
    }

    /// <summary>
    /// Returns true if this node or any descendant has mesh data.
    /// </summary>
    private bool HasMeshesRecursive(Node node)
    {
        if (node.MeshCount > 0) return true;
        foreach (var child in node.Children)
            if (!ShouldSkipNode(child.Name) && HasMeshesRecursive(child))
                return true;
        return false;
    }

    private void BuildHierarchyFiltered(Node node, Assimp.Scene scene, Scene.SceneObject current, 
        string modelPath, List<Scene.SceneObject> flatList, Matrix4x4 accumulatedTransform)
    {
        flatList.Add(current);

        // Apply accumulated transform to this scene object
        Matrix4x4.Decompose(accumulatedTransform, out current.Transform.Scale, out var quat, out current.Transform.Position);
        current.Transform.Rotation = QuaternionToEuler(quat);

        // Process meshes on this node
        foreach (int meshIndex in node.MeshIndices)
        {
            var aiMesh = scene.Meshes[meshIndex];
            var mesh = CreateMeshFromAssimp(aiMesh);
            mesh.SourcePath = modelPath;
            
            var mr = new Scene.Components.MeshRendererComponent(mesh);
            
            // Material import (Unity-style: each mesh gets full material properties)
            ImportMaterialProperties(aiMesh, scene, mr, modelPath);
            
            // If the node has multiple meshes, create child objects for each
            if (!current.HasComponent<Scene.Components.MeshRendererComponent>())
            {
                current.AddComponent(mr);
            }
            else
            {
                var sibling = new Scene.SceneObject($"{current.Name}_mesh{meshIndex}");
                sibling.AddComponent(mr);
                current.AddChild(sibling);
                flatList.Add(sibling);
            }
        }

        // Recurse into children
        foreach (var childNode in node.Children)
        {
            // Skip non-mesh nodes (cameras, lights, sky, shadow catchers)
            if (ShouldSkipNode(childNode.Name)) continue;

            var childMatrix = ConvertMatrix(childNode.Transform);

            // Collapse Assimp pivot chains ($AssimpFbx$ nodes): accumulate transform, don't create object
            if (IsAssimpPivotNode(childNode.Name))
            {
                // Pass accumulated transform down, skip creating a SceneObject for this pivot
                foreach (var grandChild in childNode.Children)
                {
                    var combined = ConvertMatrix(grandChild.Transform) * childMatrix;
                    if (IsAssimpPivotNode(grandChild.Name) || HasMeshesRecursive(grandChild))
                    {
                        // Continue collapsing pivots or process mesh-bearing nodes
                        CollapseAndBuild(grandChild, scene, current, modelPath, flatList, childMatrix);
                    }
                }
                continue;
            }

            // Only create SceneObject if this child (or its descendants) has meshes
            if (!HasMeshesRecursive(childNode)) continue;

            var childObj = new Scene.SceneObject(childNode.Name);
            current.AddChild(childObj);
            BuildHierarchyFiltered(childNode, scene, childObj, modelPath, flatList, childMatrix);
        }
    }

    /// <summary>
    /// Collapses pivot node chains, accumulating transforms until a real mesh node is found.
    /// </summary>
    private void CollapseAndBuild(Node node, Assimp.Scene scene, Scene.SceneObject parent,
        string modelPath, List<Scene.SceneObject> flatList, Matrix4x4 accumulatedTransform)
    {
        var localMatrix = ConvertMatrix(node.Transform);
        var combined = localMatrix * accumulatedTransform;

        if (IsAssimpPivotNode(node.Name))
        {
            // Keep collapsing — accumulate transform and recurse
            foreach (var child in node.Children)
                CollapseAndBuild(child, scene, parent, modelPath, flatList, combined);
        }
        else if (HasMeshesRecursive(node))
        {
            // Found a real node — create SceneObject with accumulated transform
            var childObj = new Scene.SceneObject(node.Name);
            parent.AddChild(childObj);
            BuildHierarchyFiltered(node, scene, childObj, modelPath, flatList, combined);
        }
    }

    /// <summary>
    /// Extracts all material properties from an Assimp material into a MeshRendererComponent.
    /// </summary>
    private void ImportMaterialProperties(Assimp.Mesh aiMesh, Assimp.Scene scene, 
        Scene.Components.MeshRendererComponent mr, string modelPath)
    {
        if (aiMesh.MaterialIndex < 0 || aiMesh.MaterialIndex >= scene.MaterialCount) return;
        
        var aiMat = scene.Materials[aiMesh.MaterialIndex];
        
        // Albedo / Diffuse texture
        if (aiMat.HasTextureDiffuse)
        {
            string? absPath = ResolveTexturePath(modelPath, aiMat.TextureDiffuse.FilePath);
            if (absPath != null)
            {
                mr.Material.AlbedoTextureID = LoadTexture(absPath);
                mr.Material.AlbedoTexturePath = absPath;
            }
        }

        // Normal map
        if (aiMat.HasTextureNormal)
        {
            string? absPath = ResolveTexturePath(modelPath, aiMat.TextureNormal.FilePath);
            if (absPath != null)
            {
                mr.Material.NormalTextureID = LoadTexture(absPath);
                mr.Material.NormalTexturePath = absPath;
            }
        }
        else if (aiMat.HasTextureHeight)
        {
            string? absPath = ResolveTexturePath(modelPath, aiMat.TextureHeight.FilePath);
            if (absPath != null)
            {
                mr.Material.NormalTextureID = LoadTexture(absPath);
                mr.Material.NormalTexturePath = absPath;
            }
        }

        // Base color
        if (aiMat.HasColorDiffuse)
        {
            var c = aiMat.ColorDiffuse;
            mr.Material.BaseColor = new Vector4(c.R, c.G, c.B, c.A);
        }

        // Emissive
        if (aiMat.HasColorEmissive)
        {
            var e = aiMat.ColorEmissive;
            mr.Material.EmissiveColor = new Vector3(e.R, e.G, e.B);
            if (mr.Material.EmissiveColor.Length() > 0.01f)
                mr.Material.EmissiveIntensity = 1.0f;
        }

        // Shininess → Smoothness
        if (aiMat.HasShininess)
            mr.Material.Smoothness = Math.Clamp(aiMat.Shininess / 1000f, 0f, 1f);

        // Reflectivity → Metallic
        if (aiMat.HasReflectivity)
            mr.Material.Metallic = Math.Clamp(aiMat.Reflectivity, 0f, 1f);
    }

    private Rendering.Mesh CreateMeshFromAssimp(Assimp.Mesh aiMesh)
    {
         var verts = new List<float>();
         var inds = new List<uint>();
         for (int i = 0; i < aiMesh.VertexCount; i++)
         {
             var p = aiMesh.Vertices[i];
             var n = aiMesh.HasNormals ? aiMesh.Normals[i] : new Vector3D(0, 1, 0);
             var t = aiMesh.HasTangentBasis ? aiMesh.Tangents[i] : new Vector3D(1, 0, 0);
             var uv = aiMesh.HasTextureCoords(0) ? aiMesh.TextureCoordinateChannels[0][i] : new Vector3D(0, 0, 0);
             verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, t.X, t.Y, t.Z, uv.X, 1.0f - uv.Y });
         }
         foreach (var face in aiMesh.Faces)
             if (face.IndexCount == 3) inds.AddRange(new[] { (uint)face.Indices[0], (uint)face.Indices[1], (uint)face.Indices[2] });

         var mesh = new Rendering.Mesh(_gl);
         mesh.Upload(verts.ToArray(), inds.ToArray());
         return mesh;
    }

    private string? ResolveTexturePath(string modelPath, string relPath)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        string fileName = Path.GetFileName(relPath);
        string[] search = { Path.Combine(dir, relPath), Path.Combine(dir, fileName), Path.Combine(dir, "textures", fileName), Path.Combine(dir, "Textures", fileName) };
        return search.FirstOrDefault(File.Exists);
    }

    private Vector3 QuaternionToEuler(Quaternion q)
    {
        float sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        float cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float roll = MathF.Atan2(sinr_cosp, cosr_cosp);
        float sinp = 2 * (q.W * q.Y - q.Z * q.X);
        float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
        float siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        float yaw = MathF.Atan2(siny_cosp, cosy_cosp);
        return new Vector3(roll, pitch, yaw) * (180f / MathF.PI);
    }

    // Keep LoadModel for basic cases
    public Rendering.Mesh? LoadModel(string path, Action<string>? log = null)
    {
        if (_meshCache.TryGetValue(path, out var cached)) return cached;
        try {
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(path, PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.FlipUVs | PostProcessSteps.CalculateTangentSpace | PostProcessSteps.LimitBoneWeights);
            if (scene == null || scene.MeshCount == 0) return null;
            
            // Simple flatten
            var allVerts = new List<float>();
            var allInds = new List<uint>();
            uint indexOffset = 0;
            ProcessNodeFlatten(scene.RootNode, scene, Matrix4x4.Identity, allVerts, allInds, ref indexOffset);

            var mesh = new Rendering.Mesh(_gl);
            mesh.Upload(allVerts.ToArray(), allInds.ToArray());
            mesh.SourcePath = path;
            mesh.SuggestedTexturePath = AutoDiscoverTexturesFallback(path, scene);
            _meshCache[path] = mesh;
            return mesh;
        } catch { return null; }
    }

    private void ProcessNodeFlatten(Node node, Assimp.Scene scene, Matrix4x4 parentTransform, List<float> verts, List<uint> inds, ref uint offset)
    {
        var combined = ConvertMatrix(node.Transform) * parentTransform;
        foreach (int mIdx in node.MeshIndices)
        {
            var m = scene.Meshes[mIdx];
            for (int i = 0; i < m.VertexCount; i++) {
                var p = Vector3.Transform(new Vector3(m.Vertices[i].X, m.Vertices[i].Y, m.Vertices[i].Z), combined);
                var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(m.Normals[i].X, m.Normals[i].Y, m.Normals[i].Z), combined));
                var uv = m.HasTextureCoords(0) ? m.TextureCoordinateChannels[0][i] : new Vector3D(0, 0, 0);
                verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, 1f, 0f, 0f, uv.X, uv.Y }); // Simplified tangent
            }
            foreach (var f in m.Faces) foreach (var idx in f.Indices) inds.Add((uint)idx + offset);
            offset += (uint)m.VertexCount;
        }
        foreach (var c in node.Children) ProcessNodeFlatten(c, scene, combined, verts, inds, ref offset);
    }

    private string? AutoDiscoverTexturesFallback(string modelPath, Assimp.Scene scene)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        foreach (var mat in scene.Materials) if (mat.HasTextureDiffuse) {
            string? p = ResolveTexturePath(modelPath, mat.TextureDiffuse.FilePath);
            if (p != null) return p;
        }
        return null;
    }

    public unsafe uint LoadTexture(string path)
    {
        if (_textureCache.TryGetValue(path, out uint cached)) return cached;
        try {
            string ext = Path.GetExtension(path).ToLower();
            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            if (ext == ".hdr") {
                using var stream = File.OpenRead(path);
                var img = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                fixed (float* ptr = img.Data) _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.Float, ptr);
            } else if (ext == ".exr") {
                var res = Exr.LoadEXR(path, out float[] data, out int w, out int h);
                if (res == ResultCode.Success)
                {
                    fixed (float* ptr = data) 
                        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.Float, ptr);
                }
                else
                {
                    Console.WriteLine($"[AssetManager] Failed to load EXR: {path}. Error: {res}");
                    _gl.DeleteTexture(tex);
                    return 0;
                }
            } else {
                using var img = Image.Load<Rgba32>(path);
                var pixels = new byte[img.Width * img.Height * 4];
                img.CopyPixelDataTo(pixels);
                fixed (byte* ptr = pixels) _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
            }
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _textureCache[path] = tex;
            return tex;
        } catch { return 0; }
    }

    private Matrix4x4 ConvertMatrix(Assimp.Matrix4x4 m) => new Matrix4x4(m.A1, m.B1, m.C1, m.D1, m.A2, m.B2, m.C2, m.D2, m.A3, m.B3, m.C3, m.D3, m.A4, m.B4, m.C4, m.D4);
    public uint GetTextureID(string path) => _textureCache.TryGetValue(path, out uint id) ? id : 0;
}
