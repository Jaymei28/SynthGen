using System;
using System.Collections.Generic;
using System.Numerics;
using SynthGen.Rendering;
using SynthGen.Scene;
using SynthGen.Scene.Components;

namespace SynthGen.Annotation;

/// <summary>
/// A single 2D keypoint with visibility flag.
/// </summary>
public struct Keypoint2D
{
    /// <summary>X coordinate in pixel space.</summary>
    public float X;
    /// <summary>Y coordinate in pixel space.</summary>
    public float Y;
    /// <summary>Visibility: 0=not labeled, 1=labeled but occluded, 2=labeled and visible.</summary>
    public int V;
}

/// <summary>
/// Keypoint annotation result for a single person/object.
/// </summary>
public class KeypointAnnotation
{
    public int ClassID;
    public int InstanceID;
    public string ClassName = "";
    
    /// <summary>Keypoint data mapped by index.</summary>
    public Dictionary<int, Keypoint2D> Keypoints = new();
    
    /// <summary>Number of visible keypoints.</summary>
    public int NumKeypoints;
    /// <summary>Bounding box [x, y, w, h] enclosing all visible keypoints.</summary>
    public float[] BBox = new float[4];

    /// <summary>The standard used for this annotation (passed along to exporters).</summary>
    public PoseStandard Standard { get; set; } = KeypointRegistry.COCO;
}

public static class KeypointAnnotator
{
    public static List<KeypointAnnotation> GenerateKeypoints(
        SceneGraph scene,
        Matrix4x4 viewMatrix,
        Matrix4x4 projMatrix,
        int imageWidth,
        int imageHeight,
        Dictionary<int, string>? keypointBoneMap = null,
        float fisheyeStrength = 0)
    {
        var results = new List<KeypointAnnotation>();
        var vp = viewMatrix * projMatrix;

        foreach (var rootObj in scene.Objects)
        {
            if (rootObj.Parent != null) continue;

            var std = KeypointRegistry.GetStandard(rootObj.PoseStandard);

            // ── Path 1: Node-based keypoints (KeypointComponent nodes) ──
            var nodeKeypoints = CollectKeypointNodes(rootObj);
            if (nodeKeypoints.Count > 0)
            {
                var annotation = new KeypointAnnotation { Standard = std };
                var label = FindLabel(rootObj);
                if (label != null)
                {
                    annotation.ClassID = label.ClassID;
                    annotation.InstanceID = label.InstanceID;
                    annotation.ClassName = label.ClassName;
                }

                int visibleCount = 0;
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                foreach (var kpIdx in std.Keypoints.Keys)
                {
                    if (!nodeKeypoints.TryGetValue(kpIdx, out var kpNode))
                    {
                        annotation.Keypoints[kpIdx] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                        continue;
                    }

                    var worldPos = kpNode.Transform.Position;
                    var clip = Vector4.Transform(new Vector4(worldPos, 1.0f), vp);
                    if (clip.W <= 0)
                    {
                        annotation.Keypoints[kpIdx] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                        continue;
                    }

                    float ndcX = clip.X / clip.W;
                    float ndcY = clip.Y / clip.W;
                    float sx = (ndcX + 1) * 0.5f * imageWidth;
                    float sy = (1 - ndcY) * 0.5f * imageHeight;

                    // Apply Fisheye Warp if active
                    if (MathF.Abs(fisheyeStrength) > 0.001f)
                    {
                        var warped = WarpFisheye(new Vector2(sx, sy), imageWidth, imageHeight, fisheyeStrength);
                        sx = warped.X;
                        sy = warped.Y;
                    }

                    bool inBounds = sx >= 0 && sx < imageWidth && sy >= 0 && sy < imageHeight;
                    annotation.Keypoints[kpIdx] = new Keypoint2D
                    {
                        X = sx,
                        Y = sy,
                        V = inBounds ? 2 : 1
                    };

                    if (inBounds)
                    {
                        visibleCount++;
                        minX = MathF.Min(minX, sx);
                        minY = MathF.Min(minY, sy);
                        maxX = MathF.Max(maxX, sx);
                        maxY = MathF.Max(maxY, sy);
                    }
                }

                annotation.NumKeypoints = visibleCount;
                if (visibleCount > 0)
                {
                    float pad = 20f;
                    annotation.BBox = new[]
                    {
                        MathF.Max(0, minX - pad),
                        MathF.Max(0, minY - pad),
                        MathF.Min(imageWidth, maxX + pad) - MathF.Max(0, minX - pad),
                        MathF.Min(imageHeight, maxY + pad) - MathF.Max(0, minY - pad)
                    };
                    results.Add(annotation);
                }
                continue; 
            }

            // ── Path 2: Bone-based keypoints (skeleton armature) ──
            var (skinnedObj, mr) = FindFirstSkinnedMesh(rootObj);
            if (skinnedObj == null || mr?.Mesh == null || mr.Mesh.Skeleton == null) continue;

            var skeleton = mr.Mesh.Skeleton;
            var anim = skinnedObj.GetComponent<AnimationPlayerComponent>();
            if (anim != null && mr.Mesh.Clips.Count > 0)
            {
                int clipIdx = anim.CurrentClipIndex % mr.Mesh.Clips.Count;
                mr.Mesh.Clips[clipIdx].Apply(skeleton, anim.PlaybackTime);
            }

            var finalBoneMatrices = skeleton.GetFinalMatrices();
            var objectWorldMatrix = skinnedObj.GetWorldMatrix();

            var mapping = keypointBoneMap ?? KeypointRegistry.AutoMapBones(std, skeleton.BonesByName.Keys);

            var boneAnnotation = new KeypointAnnotation { Standard = std };
            var boneLabel = FindLabel(rootObj);
            if (boneLabel != null)
            {
                boneAnnotation.ClassID = boneLabel.ClassID;
                boneAnnotation.InstanceID = boneLabel.InstanceID;
                boneAnnotation.ClassName = boneLabel.ClassName;
            }

            int boneVisibleCount = 0;
            float bMinX = float.MaxValue, bMinY = float.MaxValue;
            float bMaxX = float.MinValue, bMaxY = float.MinValue;

            foreach (var kpIdx in std.Keypoints.Keys)
            {
                if (!mapping.TryGetValue(kpIdx, out var boneName) ||
                    !skeleton.BonesByName.TryGetValue(boneName, out var bone))
                {
                    boneAnnotation.Keypoints[kpIdx] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                int boneIdx = bone.ID;
                if (boneIdx < 0 || boneIdx >= finalBoneMatrices.Length)
                {
                    boneAnnotation.Keypoints[kpIdx] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                var jointModel = finalBoneMatrices[boneIdx] * objectWorldMatrix;
                var jointWorldPos = jointModel.Translation;

                var clip2 = Vector4.Transform(new Vector4(jointWorldPos, 1.0f), vp);
                if (clip2.W <= 0)
                {
                    boneAnnotation.Keypoints[kpIdx] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                float ndcX = clip2.X / clip2.W;
                float ndcY = clip2.Y / clip2.W;
                float sx = (ndcX + 1) * 0.5f * imageWidth;
                float sy = (1 - ndcY) * 0.5f * imageHeight;

                // Apply Fisheye Warp if active
                if (MathF.Abs(fisheyeStrength) > 0.001f)
                {
                    var warped = WarpFisheye(new Vector2(sx, sy), imageWidth, imageHeight, fisheyeStrength);
                    sx = warped.X;
                    sy = warped.Y;
                }

                bool inBounds = sx >= 0 && sx < imageWidth && sy >= 0 && sy < imageHeight;
                boneAnnotation.Keypoints[kpIdx] = new Keypoint2D
                {
                    X = sx,
                    Y = sy,
                    V = inBounds ? 2 : 1
                };

                if (inBounds)
                {
                    boneVisibleCount++;
                    bMinX = MathF.Min(bMinX, sx);
                    bMinY = MathF.Min(bMinY, sy);
                    bMaxX = MathF.Max(bMaxX, sx);
                    bMaxY = MathF.Max(bMaxY, sy);
                }
            }

            boneAnnotation.NumKeypoints = boneVisibleCount;
            if (boneVisibleCount > 0)
            {
                float pad = 20f;
                boneAnnotation.BBox = new[]
                {
                    MathF.Max(0, bMinX - pad),
                    MathF.Max(0, bMinY - pad),
                    MathF.Min(imageWidth, bMaxX + pad) - MathF.Max(0, bMinX - pad),
                    MathF.Min(imageHeight, bMaxY + pad) - MathF.Max(0, bMinY - pad)
                };
                results.Add(boneAnnotation);
            }
        }

        return results;
    }

    /// <summary>
    /// Applies the INVERSE of the shader's lens distortion to keypoint coordinates.
    /// This keeps annotation points pinned to the character's visuals during fish-eye.
    /// </summary>
    public static Vector2 WarpFisheye(Vector2 screenPos, int width, int height, float strength)
    {
        if (MathF.Abs(strength) < 0.001f) return screenPos;
        
        // Normalize to [-0.5, 0.5] range relative to center (exactly matching shader vUV - 0.5)
        Vector2 center = new Vector2(width * 0.5f, height * 0.5f);
        Vector2 dist = (screenPos - center);
        dist.X /= width;
        dist.Y /= height;
        
        float d = dist.Length();
        if (d < 0.001f) return screenPos;

        // Solution for v: strength*v^3 + v - (d * cornerDistortion) = 0
        float cornerDistortion = 1.0f + 0.5f * strength;
        float sTarget = d * cornerDistortion;
        
        float v = d; 
        for (int i = 0; i < 5; i++)
        {
            float f = strength * v * v * v + v - sTarget;
            float df = 3.0f * strength * v * v + 1.0f;
            v -= f / df;
        }

        Vector2 warpedDist = Vector2.Normalize(dist) * v;
        return center + new Vector2(warpedDist.X * width, warpedDist.Y * height);
    }

    private static Dictionary<int, SceneObject> CollectKeypointNodes(SceneObject obj)
    {
        var result = new Dictionary<int, SceneObject>();
        CollectKeypointNodesRecursive(obj, result);
        return result;
    }

    private static void CollectKeypointNodesRecursive(SceneObject obj, Dictionary<int, SceneObject> result)
    {
        var kp = obj.GetComponent<KeypointComponent>();
        if (kp != null && kp.KeypointIndex >= 0)
        {
            result[kp.KeypointIndex] = obj;
        }
        foreach (var child in obj.Children)
        {
            CollectKeypointNodesRecursive(child, result);
        }
    }

    private static (SceneObject?, MeshRendererComponent?) FindFirstSkinnedMesh(SceneObject obj)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr?.Mesh != null && mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null)
            return (obj, mr);
        foreach (var child in obj.Children)
        {
            var result = FindFirstSkinnedMesh(child);
            if (result.Item1 != null) return result;
        }
        return (null, null);
    }

    private static LabelComponent? FindLabel(SceneObject obj)
    {
        var label = obj.GetComponent<LabelComponent>();
        if (label != null) return label;
        foreach (var child in obj.Children)
        {
            var found = FindLabel(child);
            if (found != null) return found;
        }
        return null;
    }
}

