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
    /// <summary>17 keypoints in COCO order.</summary>
    public Keypoint2D[] Keypoints = new Keypoint2D[17];
    /// <summary>Number of visible keypoints.</summary>
    public int NumKeypoints;
    /// <summary>Bounding box [x, y, w, h] enclosing all visible keypoints.</summary>
    public float[] BBox = new float[4];
}

/// <summary>
/// Projects 3D skeleton bone positions to 2D keypoints for pose estimation annotation.
/// Uses the EXACT same bone-to-screen math as UIManager.DrawOriented3DBox:
///   finalBoneMatrix * objectWorldMatrix → then project through view * proj.
/// </summary>
public static class KeypointAnnotator
{
    /// <summary>
    /// Generates keypoint annotations for all characters in the scene.
    /// Only produces ONE annotation per root-level character.
    /// </summary>
    public static List<KeypointAnnotation> GenerateKeypoints(
        SceneGraph scene,
        Matrix4x4 viewMatrix,
        Matrix4x4 projMatrix,
        int imageWidth,
        int imageHeight,
        Dictionary<int, string>? keypointBoneMap = null)
    {
        var results = new List<KeypointAnnotation>();
        var vp = viewMatrix * projMatrix;

        foreach (var rootObj in scene.Objects)
        {
            if (rootObj.Parent != null) continue;

            // ── Path 1: Node-based keypoints (KeypointComponent nodes) ──
            var nodeKeypoints = CollectKeypointNodes(rootObj);
            if (nodeKeypoints.Count > 0)
            {
                var annotation = new KeypointAnnotation();
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

                for (int kp = 0; kp < 17; kp++)
                {
                    if (!nodeKeypoints.TryGetValue(kp, out var kpNode))
                    {
                        annotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                        continue;
                    }

                    var worldPos = kpNode.Transform.Position;
                    var clip = Vector4.Transform(new Vector4(worldPos, 1.0f), vp);
                    if (clip.W <= 0)
                    {
                        annotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                        continue;
                    }

                    float ndcX = clip.X / clip.W;
                    float ndcY = clip.Y / clip.W;
                    float sx = (ndcX + 1) * 0.5f * imageWidth;
                    float sy = (1 - ndcY) * 0.5f * imageHeight;

                    bool inBounds = sx >= 0 && sx < imageWidth && sy >= 0 && sy < imageHeight;
                    annotation.Keypoints[kp] = new Keypoint2D
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
                continue; // Skip bone-based path for this object
            }

            // ── Path 2: Bone-based keypoints (skeleton armature) ──
            var (skinnedObj, mr) = FindFirstSkinnedMesh(rootObj);
            if (skinnedObj == null || mr?.Mesh == null) continue;

            var skeleton = mr.Mesh.Skeleton;
            if (skeleton == null) continue;

            var anim = skinnedObj.GetComponent<AnimationPlayerComponent>();
            if (anim != null && mr.Mesh.Clips.Count > 0)
            {
                int clipIdx = anim.CurrentClipIndex % mr.Mesh.Clips.Count;
                mr.Mesh.Clips[clipIdx].Apply(skeleton, anim.PlaybackTime);
            }

            var finalBoneMatrices = skeleton.GetFinalMatrices();
            var objectWorldMatrix = skinnedObj.GetWorldMatrix();

            var mapping = keypointBoneMap ?? KeypointRegistry.AutoMapBones(
                skeleton.BonesByName.Keys);

            var boneAnnotation = new KeypointAnnotation();
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

            for (int kp = 0; kp < 17; kp++)
            {
                if (!mapping.TryGetValue(kp, out var boneName) ||
                    !skeleton.BonesByName.TryGetValue(boneName, out var bone))
                {
                    boneAnnotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                int boneIdx = bone.ID;
                if (boneIdx < 0 || boneIdx >= finalBoneMatrices.Length)
                {
                    boneAnnotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                var jointModel = finalBoneMatrices[boneIdx] * objectWorldMatrix;
                var jointWorldPos = jointModel.Translation;

                var clip2 = Vector4.Transform(new Vector4(jointWorldPos, 1.0f), vp);
                if (clip2.W <= 0)
                {
                    boneAnnotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                float ndcX = clip2.X / clip2.W;
                float ndcY = clip2.Y / clip2.W;
                float sx = (ndcX + 1) * 0.5f * imageWidth;
                float sy = (1 - ndcY) * 0.5f * imageHeight;

                bool inBounds = sx >= 0 && sx < imageWidth && sy >= 0 && sy < imageHeight;
                boneAnnotation.Keypoints[kp] = new Keypoint2D
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
    /// Collects all KeypointComponent nodes from a hierarchy into a dictionary by index.
    /// </summary>
    private static Dictionary<int, SceneObject> CollectKeypointNodes(SceneObject obj)
    {
        var result = new Dictionary<int, SceneObject>();
        CollectKeypointNodesRecursive(obj, result);
        return result;
    }

    private static void CollectKeypointNodesRecursive(SceneObject obj, Dictionary<int, SceneObject> result)
    {
        var kp = obj.GetComponent<Scene.Components.KeypointComponent>();
        if (kp != null && kp.KeypointIndex >= 0 && kp.KeypointIndex < 17)
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
