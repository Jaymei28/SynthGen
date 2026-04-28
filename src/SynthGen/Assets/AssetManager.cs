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

    private static string BaseDir => AppContext.BaseDirectory;
    public string ModelsPath { get; set; } = Path.Combine(BaseDir, "Assets", "models");
    public string TexturesPath { get; set; } = Path.Combine(BaseDir, "Assets", "textures");
    public string AnimationsPath { get; set; } = Path.Combine(BaseDir, "Assets", "animations");
    public string HDRIPath { get; set; } = Path.Combine(BaseDir, "Assets", "hdri");

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
                PostProcessSteps.GenerateSmoothNormals |
                PostProcessSteps.JoinIdenticalVertices |
                PostProcessSteps.ImproveCacheLocality |
                PostProcessSteps.CalculateTangentSpace |
                PostProcessSteps.LimitBoneWeights);

            if (scene == null || scene.RootNode == null) return null;

            // ── Build skeleton FIRST so mesh creation uses correct bone indices ──
            Rendering.Skeleton? skeleton = null;
            List<Rendering.SkeletalAnimationClip>? clips = null;

            bool hasBones = scene.Meshes.Any(m => m.HasBones);
            if (hasBones)
            {
                skeleton = BuildSkeleton(scene, log);
                clips = ExtractAnimationClips(scene, log);
                log?.Invoke($"[Animation] Skeleton: {skeleton?.Bones.Count ?? 0} bones, {clips?.Count ?? 0} clips");
            }

            var rootObj = new Scene.SceneObject(Path.GetFileNameWithoutExtension(path));
            List<Scene.SceneObject> flatList = new();
            
            // 1:1 Recursive Hierarchy build (pass skeleton for bone index mapping)
            BuildHierarchySimple(scene.RootNode, scene, rootObj, path, flatList, skeleton, log);

            // Wire skeleton + clips to all skinned meshes, attach AnimationPlayer
            if (skeleton != null)
            {
                foreach (var obj in flatList)
                {
                    var mr = obj.GetComponent<Scene.Components.MeshRendererComponent>();
                    if (mr?.Mesh == null || !mr.Mesh.HasSkinning) continue;

                    mr.Mesh.Skeleton = skeleton;
                    if (clips != null && clips.Count > 0)
                    {
                        mr.Mesh.Clips = clips;
                        // Auto-attach animation player
                        if (!obj.HasComponent<Scene.Components.AnimationPlayerComponent>())
                        {
                            float durationSec = clips[0].Duration / clips[0].TicksPerSecond;
                            obj.AddComponent(new Scene.Components.AnimationPlayerComponent
                            {
                                IsPlaying = true,
                                Loop = true,
                                CurrentClipIndex = 0,
                                ClipDurationSeconds = durationSec
                            });
                            log?.Invoke($"[Animation] Attached AnimationPlayer to '{obj.Name}' ({clips.Count} clips, {durationSec:F2}s)");
                        }
                    }
                }
            }

            // ── Auto-scale and Auto-position: normalize FBX centimeter units to meters ──
            // FBX files from Mixamo/Blender often use centimeters (100 units = 1 meter).
            // Detect this by measuring the model's bounding box height and scale accordingly.
            var bounds = ComputeModelBounds(flatList);
            float scaleFactor = 1.0f;
            
            if (bounds.Height > 10f) // Taller than 10 units = likely centimeter scale
            {
                float targetHeight = 1.7f; // Average human height in meters
                scaleFactor = targetHeight / bounds.Height;
                rootObj.Transform.Scale = new Vector3(scaleFactor);
                log?.Invoke($"[AssetManager] Auto-scaled model: {bounds.Height:F1} units → {targetHeight:F1}m (scale={scaleFactor:F4})");
            }
            else
            {
                log?.Invoke($"[AssetManager] Model height: {bounds.Height:F2} units (no rescale needed)");
            }

            // Offset the model so its lowest point rests precisely on the floor (Y = 0)
            // min.Y could be negative if the pivot is at the waist (like many Mixamo characters)
            if (bounds.MinY < -0.1f)
            {
                float yOffset = -bounds.MinY * scaleFactor;
                rootObj.Transform.Position.Y = yOffset;
                log?.Invoke($"[AssetManager] Applied Y-offset: {yOffset:F2}m to place feet on the ground.");
            }
            
            return rootObj;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[AssetManager] Fatal error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes the total height (Y-axis extent) of a model from all its meshes,
    /// and also returns the minimum Y value (the lowest coordinate) so we can place it on the ground.
    /// Used for auto-scaling and positioning imported FBX models.
    /// </summary>
    private (float Height, float MinY) ComputeModelBounds(List<Scene.SceneObject> flatList)
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var obj in flatList)
        {
            var mr = obj.GetComponent<Scene.Components.MeshRendererComponent>();
            if (mr?.Mesh == null) continue;
            
            var meshMin = mr.Mesh.BoundingBoxMin;
            var meshMax = mr.Mesh.BoundingBoxMax;
            
            // Transform all 8 corners of the AABB by the node's world transform
            Vector3[] corners = {
                new(meshMin.X, meshMin.Y, meshMin.Z), new(meshMax.X, meshMin.Y, meshMin.Z), 
                new(meshMax.X, meshMax.Y, meshMin.Z), new(meshMin.X, meshMax.Y, meshMin.Z),
                new(meshMin.X, meshMin.Y, meshMax.Z), new(meshMax.X, meshMin.Y, meshMax.Z), 
                new(meshMax.X, meshMax.Y, meshMax.Z), new(meshMin.X, meshMax.Y, meshMax.Z)
            };
            
            var worldMatrix = obj.GetWorldMatrix();
            foreach (var corner in corners)
            {
                var worldPos = Vector3.Transform(corner, worldMatrix);
                minY = MathF.Min(minY, worldPos.Y);
                maxY = MathF.Max(maxY, worldPos.Y);
            }
        }
        
        if (minY >= maxY) return (0f, 0f);
        return (maxY - minY, minY);
    }

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

    private bool HasMeshesRecursive(Node node)
    {
        if (node.MeshCount > 0) return true;
        foreach (var child in node.Children)
            if (!ShouldSkipNode(child.Name) && HasMeshesRecursive(child))
                return true;
        return false;
    }

    private void BuildHierarchySimple(Node node, Assimp.Scene scene, Scene.SceneObject parent, 
        string modelPath, List<Scene.SceneObject> flatList, Rendering.Skeleton? skeleton = null, Action<string>? log = null)
    {
        // 1. Create the SceneObject
        var current = new Scene.SceneObject(node.Name);
        parent.AddChild(current);
        flatList.Add(current);

        // 2. Set Local Transform (Match Assimp 1:1 with Transpose for .NET)
        var localMat = ConvertMatrix(node.Transform);
        if (Matrix4x4.Decompose(localMat, out current.Transform.Scale, out var quat, out current.Transform.Position))
        {
            current.Transform.Rotation = QuaternionToEuler(quat);
        }

        // 3. Attach Meshes
        for (int i = 0; i < node.MeshCount; i++)
        {
            var aiMesh = scene.Meshes[node.MeshIndices[i]];
            var aiMat = scene.Materials[aiMesh.MaterialIndex];
            
            // Prefer the Mesh/Material name over generic 'polySurface' node names
            string meshName = string.IsNullOrEmpty(aiMesh.Name) ? 
                (string.IsNullOrEmpty(aiMat.Name) ? $"{node.Name}_part{i}" : aiMat.Name) : aiMesh.Name;

            var mesh = CreateMeshFromAssimp(aiMesh, skeleton, log);
            mesh.SourcePath = modelPath;
            var mr = new Scene.Components.MeshRendererComponent(mesh);
            ImportMaterialProperties(aiMesh, scene, mr, modelPath, meshName, log);
            
            int texCount = (mr.Material.AlbedoTextureID > 0 ? 1 : 0) + (mr.Material.NormalTextureID > 0 ? 1 : 0);
            log?.Invoke($"[AssetManager] Final: {meshName} got {texCount} textures.");

            if (i == 0) {
                // Rename the node if the mesh has a better name (e.g. pCube172 -> Box_001)
                if (node.Name.StartsWith("p") && !meshName.StartsWith("p")) current.Name = meshName;
                current.AddComponent(mr);
            } else {
                var subMeshObj = new Scene.SceneObject(meshName);
                
                // MICRO-FIX: Prevent Z-fighting on overlapping parts
                subMeshObj.Transform.Scale = new Vector3(1.001f);
                
                subMeshObj.AddComponent(mr);
                current.AddChild(subMeshObj);
                flatList.Add(subMeshObj);
            }
        }

        // 4. Recurse into children
        foreach (var childNode in node.Children)
        {
            BuildHierarchySimple(childNode, scene, current, modelPath, flatList, skeleton, log);
        }
    }

    /// <summary>
    /// Extracts all material properties from an Assimp material into a MeshRendererComponent.
    /// </summary>
    private void ImportMaterialProperties(Assimp.Mesh aiMesh, Assimp.Scene scene, 
        Scene.Components.MeshRendererComponent mr, string modelPath, string nodeName, Action<string>? log = null)
    {
        if (aiMesh.MaterialIndex < 0 || aiMesh.MaterialIndex >= scene.MaterialCount) return;
        
        var aiMat = scene.Materials[aiMesh.MaterialIndex];
        
        bool hasInternallyBakedTexture = aiMat.HasTextureDiffuse;

        // Albedo / Diffuse texture
        if (aiMat.HasTextureDiffuse)
        {
            string bakedPath = aiMat.TextureDiffuse.FilePath;
            string? absPath = ResolveTexturePath(modelPath, bakedPath, log);
            if (absPath != null)
            {
                log?.Invoke($"[AssetManager] {nodeName} -> Albedo: '{Path.GetFileName(absPath)}'");
                mr.Material.AlbedoTextureID = LoadTexture(absPath);
                mr.Material.AlbedoTexturePath = absPath;
            }
        }
        
        // Fallback: Heuristically find Albedo (using Node name, Mesh name, and Material name!)
        if (mr.Material.AlbedoTextureID == 0)
        {
            string? absPath = AutoDiscoverHeuristicTexture(modelPath, nodeName, aiMesh.Name, aiMat.Name, new[] { "albedo", "basecolor", "diffuse", "col" });
            if (absPath != null)
            {
                log?.Invoke($"[AssetManager] {nodeName} -> Albedo (Search): '{Path.GetFileName(absPath)}'");
                mr.Material.AlbedoTextureID = LoadTexture(absPath);
                mr.Material.AlbedoTexturePath = absPath;
            } else {
                log?.Invoke($"[AssetManager] {nodeName} -> ! Albedo NOT FOUND !");
            }
        }

        // Normal map
        if (aiMat.HasTextureNormal)
        {
            string? absPath = ResolveTexturePath(modelPath, aiMat.TextureNormal.FilePath, log);
            if (absPath != null)
            {
                log?.Invoke($"[AssetManager] Mesh '{aiMesh.Name}' -> Normal: '{Path.GetFileName(absPath)}'");
                mr.Material.NormalTextureID = LoadTexture(absPath);
                mr.Material.NormalTexturePath = absPath;
            }
        }
        else if (aiMat.HasTextureHeight)
        {
            string? absPath = ResolveTexturePath(modelPath, aiMat.TextureHeight.FilePath, log);
            if (absPath != null)
            {
                log?.Invoke($"  -> Normal (Height) FOUND via baked path: '{Path.GetFileName(absPath)}'");
                mr.Material.NormalTextureID = LoadTexture(absPath);
                mr.Material.NormalTexturePath = absPath;
            }
        }
        
        // Fallback: Heuristically find Normal (DISABLED per user request to prevent wrong inferences)
        /*
        if (mr.Material.NormalTextureID == 0)
        {
            string? absPath = AutoDiscoverHeuristicTexture(modelPath, nodeName, aiMesh.Name, aiMat.Name, new[] { "normal", "nrm", "nor" });
            if (absPath != null)
            {
                log?.Invoke($"[AssetManager] {nodeName} -> Normal (Search): '{Path.GetFileName(absPath)}'");
                mr.Material.NormalTextureID = LoadTexture(absPath);
                mr.Material.NormalTexturePath = absPath;
            }
        }
        */

        // Base color
        if (aiMat.HasColorDiffuse)
        {
            var c = aiMat.ColorDiffuse;
            mr.Material.BaseColor = new Vector4(c.R, c.G, c.B, c.A);
        }
        
        // Transparency / Opacity overriding
        if (aiMat.HasOpacity && aiMat.Opacity < 1.0f)
        {
            mr.Material.BaseColor = new Vector4(mr.Material.BaseColor.X, mr.Material.BaseColor.Y, mr.Material.BaseColor.Z, aiMat.Opacity);
        }
        else if (aiMat.HasColorTransparent)
        {
            var tc = aiMat.ColorTransparent;
            float luminance = (tc.R * 0.3f) + (tc.G * 0.59f) + (tc.B * 0.11f);
            if (luminance > 0.01f) // some formats define 'transparent color' brightness as inverse opacity
            {
                mr.Material.BaseColor = new Vector4(mr.Material.BaseColor.X, mr.Material.BaseColor.Y, mr.Material.BaseColor.Z, Math.Max(0.1f, 1.0f - luminance));
            }
        }

        // Emissive
        if (aiMat.HasColorEmissive)
        {
            var e = aiMat.ColorEmissive;
            mr.Material.EmissiveColor = new Vector3(e.R, e.G, e.B);
            if (mr.Material.EmissiveColor.Length() > 0.01f)
                mr.Material.EmissiveIntensity = 1.0f;
        }

        // Defaults for PBR if not specified
        mr.Material.Smoothness = 0.5f; 
        mr.Material.Metallic = 0.0f;

        // Shininess → Smoothness (Cap it to avoid plastic look)
        if (aiMat.HasShininess)
            mr.Material.Smoothness = Math.Clamp(aiMat.Shininess / 800f, 0.1f, 0.8f);

        // Reflectivity → Metallic (Capped: many FBX export high reflectivity for matte objects)
        if (aiMat.HasReflectivity)
            mr.Material.Metallic = Math.Clamp(aiMat.Reflectivity, 0f, 0.3f); 
            
        // Final sanity check for boxes: if it's not a metal explicitly, default to low metallic
        if (aiMat.Name.ToLower().Contains("box") || aiMesh.Name.ToLower().Contains("box"))
        {
            mr.Material.Metallic = 0.0f;
            mr.Material.Smoothness = 0.2f;
        }
        
        if (aiMat.HasTwoSided)
        {
            mr.Material.DoubleSided = aiMat.IsTwoSided;
        }
    }

    private Rendering.Mesh CreateMeshFromAssimp(Assimp.Mesh aiMesh, Rendering.Skeleton? skeleton = null, Action<string>? log = null)
    {
         var verts = new List<float>();
         var inds = new List<uint>();
         bool hasUVs = aiMesh.HasTextureCoords(0);
         
         if (!hasUVs) log?.Invoke($"[Warning] Mesh '{aiMesh.Name}' has NO UV coordinates! Textures will not render correctly.");

         for (int i = 0; i < aiMesh.VertexCount; i++)
         {
             var p = aiMesh.Vertices[i];
             var n = aiMesh.HasNormals ? aiMesh.Normals[i] : new Vector3D(0, 1, 0);
             var t = aiMesh.HasTangentBasis ? aiMesh.Tangents[i] : new Vector3D(1, 0, 0);
             var uv = hasUVs ? aiMesh.TextureCoordinateChannels[0][i] : new Vector3D(0, 0, 0);
             verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, t.X, t.Y, t.Z, uv.X, 1.0f - uv.Y });
         }
         foreach (var face in aiMesh.Faces)
             if (face.IndexCount == 3) inds.AddRange(new[] { (uint)face.Indices[0], (uint)face.Indices[1], (uint)face.Indices[2] });

         // ── Extract bone data (up to 4 bones per vertex) ──
         int[]? boneIDs = null;
         float[]? boneWeights = null;
         List<Matrix4x4>? boneOffsets = null;

         if (aiMesh.HasBones)
         {
             int vertCount = aiMesh.VertexCount;
             boneIDs = new int[vertCount * 4];
             boneWeights = new float[vertCount * 4];
             boneOffsets = new List<Matrix4x4>();

             // Track how many bones have been assigned per vertex
             int[] boneSlots = new int[vertCount];

             for (int boneIdx = 0; boneIdx < aiMesh.BoneCount; boneIdx++)
             {
                 var bone = aiMesh.Bones[boneIdx];
                 boneOffsets.Add(ConvertMatrix(bone.OffsetMatrix));

                 // Map to the skeleton's bone index (critical for correct skinning)
                 int skeletonBoneIdx = boneIdx; // fallback to mesh order
                 if (skeleton != null && skeleton.BonesByName.TryGetValue(bone.Name, out var boneInfo))
                     skeletonBoneIdx = boneInfo.ID;

                 foreach (var vw in bone.VertexWeights)
                 {
                     int vi = vw.VertexID;
                     int slot = boneSlots[vi];
                     if (slot < 4)
                     {
                         boneIDs[vi * 4 + slot] = skeletonBoneIdx;
                         boneWeights[vi * 4 + slot] = vw.Weight;
                         boneSlots[vi]++;
                     }
                 }
             }
             log?.Invoke($"[Animation] Mesh '{aiMesh.Name}': {aiMesh.BoneCount} bones, {vertCount} skinned vertices");
         }

         var mesh = new Rendering.Mesh(_gl);
         mesh.Upload(verts.ToArray(), inds.ToArray(), boneIDs, boneWeights, boneOffsets);
         return mesh;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Skeleton & Animation Extraction
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a Skeleton hierarchy from an Assimp scene by collecting all bone names
    /// from all meshes and mapping them to the node tree.
    /// </summary>
    private Rendering.Skeleton? BuildSkeleton(Assimp.Scene scene, Action<string>? log = null)
    {
        // Collect all unique bone names in ORDER (preserving mesh bone array order)
        var orderedBoneNames = new List<string>();
        var boneNameSet = new HashSet<string>();
        foreach (var mesh in scene.Meshes)
        {
            if (!mesh.HasBones) continue;
            foreach (var bone in mesh.Bones)
            {
                if (boneNameSet.Add(bone.Name))
                    orderedBoneNames.Add(bone.Name);
            }
        }
        if (orderedBoneNames.Count == 0) return null;

        var skeleton = new Rendering.Skeleton();
        skeleton.GlobalInverseTransform = ConvertMatrix(scene.RootNode.Transform);
        Matrix4x4.Invert(skeleton.GlobalInverseTransform, out skeleton.GlobalInverseTransform);

        // Build the bone hierarchy by walking the Assimp node tree
        // First, create BoneInfo for every bone
        // Create BoneInfo for every bone in deterministic order
        for (int i = 0; i < orderedBoneNames.Count; i++)
        {
            var bi = new Rendering.BoneInfo { ID = i, Name = orderedBoneNames[i] };
            skeleton.BonesByName[orderedBoneNames[i]] = bi;
            skeleton.Bones.Add(bi);
        }

        // Set offset matrices from the first mesh that has each bone
        foreach (var mesh in scene.Meshes)
        {
            if (!mesh.HasBones) continue;
            foreach (var bone in mesh.Bones)
            {
                if (skeleton.BonesByName.TryGetValue(bone.Name, out var bi))
                    bi.Offset = ConvertMatrix(bone.OffsetMatrix);
            }
        }

        // Build parent-child relationships by walking the node tree
        BuildSkeletonRecursive(scene.RootNode, null, skeleton);

        // Build the FULL node tree (including non-bone nodes like Armature)
        // This is critical for correct transform propagation
        skeleton.NodeRoot = BuildNodeTree(scene.RootNode, skeleton);

        // Find the root bone (first bone in the hierarchy with no bone parent)
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Parent == null)
            {
                skeleton.Root = bone;
                break;
            }
        }

        // If no explicit bone root, use the first bone as root
        if (skeleton.Root == null && skeleton.Bones.Count > 0)
            skeleton.Root = skeleton.Bones[0];

        // Compute initial global transforms
        skeleton.UpdateHierarchy();

        log?.Invoke($"[Animation] Skeleton built: {skeleton.Bones.Count} bones, root='{skeleton.Root?.Name}'");
        return skeleton;
    }

    private void BuildSkeletonRecursive(Node node, Rendering.BoneInfo? parentBone, Rendering.Skeleton skeleton)
    {
        Rendering.BoneInfo? currentBone = null;
        if (skeleton.BonesByName.TryGetValue(node.Name, out currentBone))
        {
            currentBone.LocalTransform = ConvertMatrix(node.Transform);
            if (parentBone != null)
            {
                currentBone.Parent = parentBone;
                if (!parentBone.Children.Contains(currentBone))
                    parentBone.Children.Add(currentBone);
            }
        }

        foreach (var child in node.Children)
        {
            // Pass the current bone down if it exists, otherwise pass the parent
            BuildSkeletonRecursive(child, currentBone ?? parentBone, skeleton);
        }
    }

    /// <summary>
    /// Builds the full Assimp node tree as NodeInfo objects for the skeleton.
    /// Includes non-bone nodes (Armature, etc.) for correct transform propagation.
    /// </summary>
    private Rendering.NodeInfo BuildNodeTree(Node node, Rendering.Skeleton skeleton)
    {
        var nodeInfo = new Rendering.NodeInfo
        {
            Name = node.Name,
            LocalTransform = ConvertMatrix(node.Transform)
        };
        skeleton.NodesByName[node.Name] = nodeInfo;

        foreach (var child in node.Children)
        {
            var childNodeInfo = BuildNodeTree(child, skeleton);
            nodeInfo.Children.Add(childNodeInfo);
        }
        return nodeInfo;
    }

    /// <summary>
    /// Extracts all animation clips from an Assimp scene into SkeletalAnimationClips.
    /// </summary>
    private List<Rendering.SkeletalAnimationClip> ExtractAnimationClips(Assimp.Scene scene, Action<string>? log = null)
    {
        var clips = new List<Rendering.SkeletalAnimationClip>();
        if (!scene.HasAnimations) return clips;

        foreach (var anim in scene.Animations)
        {
            var clip = new Rendering.SkeletalAnimationClip
            {
                Name = string.IsNullOrEmpty(anim.Name) ? $"Clip_{clips.Count}" : anim.Name,
                Duration = (float)anim.DurationInTicks,
                TicksPerSecond = anim.TicksPerSecond > 0 ? (float)anim.TicksPerSecond : 24f
            };

            foreach (var ch in anim.NodeAnimationChannels)
            {
                var channel = new Rendering.AnimationChannel { NodeName = ch.NodeName };

                // Position keyframes
                foreach (var key in ch.PositionKeys)
                    channel.PositionKeys.Add(((float)key.Time, new Vector3(key.Value.X, key.Value.Y, key.Value.Z)));

                // Rotation keyframes 
                foreach (var key in ch.RotationKeys)
                    channel.RotationKeys.Add(((float)key.Time, new Quaternion(key.Value.X, key.Value.Y, key.Value.Z, key.Value.W)));

                // Scale keyframes
                foreach (var key in ch.ScalingKeys)
                    channel.ScaleKeys.Add(((float)key.Time, new Vector3(key.Value.X, key.Value.Y, key.Value.Z)));

                clip.Channels.Add(channel);
            }

            clips.Add(clip);
            log?.Invoke($"[Animation] Clip '{clip.Name}': {clip.Duration / clip.TicksPerSecond:F2}s, {clip.Channels.Count} channels");
        }
        return clips;
    }

    private string? AutoDiscoverHeuristicTexture(string modelPath, string parentName, string aiMeshName, string aiMatName, string[] keywords)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var dirsToSearch = new List<string> { dir };
        string parent = Path.GetDirectoryName(dir) ?? "";
        if (!string.IsNullOrEmpty(parent) && parent.Length > 3) dirsToSearch.Add(parent);

        var temp = dirsToSearch.ToList();
        foreach (var d in temp)
        {
            string t1 = Path.Combine(d, "textures"), t2 = Path.Combine(d, "Textures");
            if (Directory.Exists(t1)) dirsToSearch.Add(t1);
            if (Directory.Exists(t2)) dirsToSearch.Add(t2);
        }

        // CRITICAL: The file MUST contain one of these to be valid
        string[] primaryTargets = { aiMatName, aiMeshName }; 
        string[] exts = { ".png", ".jpg", ".jpeg", ".tga" };

        string? winner = null;
        int bestScore = -1;

        foreach (var d in dirsToSearch)
        {
            if (!Directory.Exists(d)) continue;
            var files = Directory.GetFiles(d);
            foreach (var file in files)
            {
                if (!exts.Contains(Path.GetExtension(file).ToLower())) continue;
                string fileName = Path.GetFileNameWithoutExtension(file).ToLower();

                foreach (var target in primaryTargets)
                {
                    if (string.IsNullOrEmpty(target)) continue;
                    
                    string tLow = target.ToLower().Split('.')[0];
                    if (fileName.Contains(tLow))
                    {
                        // SCORING ENGINE:
                        int score = tLow.Length; 

                        // Material Match Bonus
                        if (target == aiMatName) score += 200;

                        // Context Bonus (Model Name) - Helps choose between Box_001 and Box_002
                        // If the file mentions the parent model (Box_003), it is almost certainly the right one
                        if (!string.IsNullOrEmpty(parentName) && fileName.Contains(parentName.ToLower().Split('.')[0])) 
                            score += 500;

                        // Keyword Bonus
                        foreach (var kw in keywords)
                            if (fileName.Contains(kw)) { score += 100; break; }

                        // Fit Bonus
                        if (fileName == tLow) score += 150;
                        if (fileName.Contains(tLow + "_") || fileName.Contains("_" + tLow)) score += 30;

                        // Penalty for excess length 
                        score -= fileName.Length / 2;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            winner = file;
                        }
                    }
                }
            }
        }
        return winner;
    }

    private string? ResolveTexturePath(string modelPath, string relPath, Action<string>? log = null)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        string fileName = Path.GetFileName(relPath);
        
        // 1. Try exact internal path relative to model
        string p1 = Path.Combine(dir, relPath);
        if (File.Exists(p1)) return p1;

        // 2. Try normalized path (fix slash inconsistency)
        string normRel = relPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string p2 = Path.Combine(dir, normRel);
        if (File.Exists(p2)) return p2;

        // 3. Search common subfolders for the filename
        string modelName = Path.GetFileNameWithoutExtension(modelPath);
        string[] commonDirs = { 
            dir, 
            Path.Combine(dir, "textures"), 
            Path.Combine(dir, "Textures"), 
            Path.Combine(dir, $"{modelName}_Textures"), 
            Path.Combine(dir, $"{modelName}.fbm") 
        };

        foreach (var d in commonDirs)
        {
            if (!Directory.Exists(d)) continue;
            string p = Path.Combine(d, fileName);
            if (File.Exists(p)) return p;
        }

        // 4. Recursive Deep Search in model directory and PARENT directory (for siblings like 'textures')
        try {
            log?.Invoke($"  -> Performing deep search for '{fileName}' in '{dir}'...");
            var files = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories);
            if (files.Length > 0) return files[0];

            string parent = Path.GetDirectoryName(dir) ?? "";
            if (!string.IsNullOrEmpty(parent) && parent.Length > 3) // Avoid scanning drive roots
            {
                log?.Invoke($"  -> Trying sibling/parent search in: '{parent}'...");
                var pFiles = Directory.GetFiles(parent, fileName, SearchOption.AllDirectories);
                if (pFiles.Length > 0) return pFiles[0];
            }
        } catch { }

        return null;
    }

    private Vector3 QuaternionToEuler(Quaternion q)
    {
        // Rotation order: ZYX (standard for Assimp/FBX to local Euler)
        float roll = MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        float pitch = MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1.0f, 1.0f));
        float yaw = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));
        
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
            mesh.SuggestedTexturePath = AutoDiscoverTexturesFallback(path, scene, log);
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
            bool hasUVs = m.HasTextureCoords(0);
            Console.WriteLine($"[AssetManager] ProcessNodeFlatten {m.Name}: Has UVs? {hasUVs}, Vertices: {m.VertexCount}");

            for (int i = 0; i < m.VertexCount; i++) {
                var p = Vector3.Transform(new Vector3(m.Vertices[i].X, m.Vertices[i].Y, m.Vertices[i].Z), combined);
                var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(m.Normals[i].X, m.Normals[i].Y, m.Normals[i].Z), combined));
                var uv = hasUVs ? m.TextureCoordinateChannels[0][i] : new Vector3D(0, 0, 0);
                verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, 1f, 0f, 0f, uv.X, uv.Y }); // Simplified tangent
            }
            foreach (var f in m.Faces) foreach (var idx in f.Indices) inds.Add((uint)idx + offset);
            offset += (uint)m.VertexCount;
        }
        foreach (var c in node.Children) ProcessNodeFlatten(c, scene, combined, verts, inds, ref offset);
    }

    private string? AutoDiscoverTexturesFallback(string modelPath, Assimp.Scene scene, Action<string>? log = null)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        foreach (var mat in scene.Materials) if (mat.HasTextureDiffuse) {
            string? p = ResolveTexturePath(modelPath, mat.TextureDiffuse.FilePath, log);
            if (p != null) return p;
        }
        return null;
    }

    public unsafe uint LoadTexture(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        if (!Path.IsPathRooted(path))
        {
            // If path is not absolute, try to find it relative to app directory
            string full = Path.GetFullPath(path, AppDomain.CurrentDomain.BaseDirectory);
            if (File.Exists(full)) path = full;
            else {
                // Try relative to CWD as secondary fallback
                path = Path.GetFullPath(path);
            }
        }
        else {
            path = Path.GetFullPath(path); // Standardize slashes etc
        }

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
                // Use StbImageSharp for standard textures - much more reliable for OpenGL RGB order
                using var stream = File.OpenRead(path);
                var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                fixed (byte* ptr = img.Data) 
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                }
            }
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _textureCache[path] = tex;
            return tex;
        } catch { return 0; }
    }

    private Matrix4x4 ConvertMatrix(Assimp.Matrix4x4 m)
    {
        // Transpose Assimp Column-Major to .NET Row-Major
        return new Matrix4x4(
            m.A1, m.B1, m.C1, m.D1,
            m.A2, m.B2, m.C2, m.D2,
            m.A3, m.B3, m.C3, m.D3,
            m.A4, m.B4, m.C4, m.D4
        );
    }
    public uint GetTextureID(string path) => _textureCache.TryGetValue(path, out uint id) ? id : 0;
}
