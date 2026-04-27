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
        float fisheyeStrength = 0,
        float fovDegrees = 60)
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
                else
                {
                    // Fallback for pose tasks: assume class 0 "person" if keypoints exist
                    annotation.ClassID = 0;
                    annotation.ClassName = "person";
                    annotation.InstanceID = rootObj.GetHashCode();
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

                    var worldPos = kpNode.GetWorldMatrix().Translation;
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
                        var warped = WarpFisheye(new Vector2(sx, sy), imageWidth, imageHeight, fisheyeStrength, fovDegrees);
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
            else
            {
                boneAnnotation.ClassID = 0;
                boneAnnotation.ClassName = "person";
                boneAnnotation.InstanceID = rootObj.GetHashCode();
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

                // Chain: Bone -> Armature Root -> Model Space -> World Space
                var jointModel = skeleton.Bones[boneIdx].GlobalTransform * skeleton.GlobalInverseTransform * objectWorldMatrix;
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
                    var warped = WarpFisheye(new Vector2(sx, sy), imageWidth, imageHeight, fisheyeStrength, fovDegrees);
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
    /// Applies the lens distortion matching the shader to keypoint coordinates.
    /// This keeps annotation points pinned to the character's visuals during fish-eye.
    /// </summary>
    public static Vector2 WarpFisheye(Vector2 screenPos, int width, int height, float strength, float fovDegrees)
    {
        if (MathF.Abs(strength) < 0.001f) return screenPos;

        float aspect = (float)width / MathF.Max(1, height);
        Vector2 center = new Vector2(width * 0.5f, height * 0.5f);

        // Normalize to [-0.5, 0.5] (matching shader's vUV - 0.5)
        Vector2 p = (screenPos - center);
        p.X /= width;
        p.Y /= height;
        p.X *= aspect; // Aspect correction (matching shader)

        float d_undistorted = p.Length();
        if (d_undistorted < 0.001f) return screenPos;

        // The shader maps distorted distance 'r' to undistorted 'sampleD' via:
        // sampleD = tan(r * halfFOV) / tan(halfFOV)
        // We need the inverse: find 'r' given 'sampleD' (undistorted distance)
        
        float sampleD = d_undistorted / 0.5f;
        
        float rawFOV = fovDegrees + (strength * 100f);
        rawFOV = MathF.Min(MathF.Max(rawFOV, 1f), 175f);
        float halfFOV = (rawFOV * 0.5f) * (MathF.PI / 180f);

        // Solving sampleD = tan(theta) / tan(halfFOV)
        // theta = atan(sampleD * tan(halfFOV))
        float theta = MathF.Atan(sampleD * MathF.Tan(halfFOV));
        
        // r = theta / halfFOV
        float r = theta / halfFOV;
        float d_distorted = r * 0.5f;

        // Map back to pixel space
        Vector2 warpedDir = (p / d_undistorted) * d_distorted;
        warpedDir.X /= aspect;

        return center + new Vector2(warpedDir.X * width, warpedDir.Y * height);
    }

    /// <summary>
    /// Inverse of the fisheye shader: maps a click position on the distorted viewport
    /// back to the corresponding position in the undistorted segmentation/picking buffer.
    /// The shader maps: sampleUV = tan(r * halfFOV) / tan(halfFOV)
    /// So inverse is: r_undistorted = atan(r_distorted * tan(halfFOV)) / halfFOV
    /// </summary>
    public static Vector2 UnwarpFisheye(Vector2 screenPos, int width, int height, float strength, float fovDegrees)
    {
        if (MathF.Abs(strength) < 0.001f) return screenPos;

        float aspect = (float)width / MathF.Max(1, height);
        Vector2 center = new Vector2(width * 0.5f, height * 0.5f);

        // Normalize to [-0.5, 0.5] (matching shader's vUV - 0.5)
        Vector2 p = (screenPos - center);
        p.X /= width;
        p.Y /= height;
        p.X *= aspect; // Aspect correction (matching shader)

        float d = p.Length();
        if (d < 0.001f) return screenPos;
        if (d > 0.5f) return screenPos; // Outside circular mask

        // r is normalized distance [0..1] within the 0.5 radius circle
        float r = d / 0.5f;

        float rawFOV = fovDegrees + (strength * 100f);
        rawFOV = MathF.Min(MathF.Max(rawFOV, 1f), 175f);
        float halfFOV = (rawFOV * 0.5f) * (MathF.PI / 180f);

        // Shader does: theta = r * halfFOV, sampleD = tan(theta) / tan(halfFOV)
        // So: sampleD is the UV in the rectilinear render
        float theta = r * halfFOV;
        float sampleD = MathF.Tan(theta) / MathF.Tan(halfFOV);

        // Map back to pixel space
        Vector2 sampleDir = (p / d) * (sampleD * 0.5f);
        sampleDir.X /= aspect;

        return center + new Vector2(sampleDir.X * width, sampleDir.Y * height);
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

