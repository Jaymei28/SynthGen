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
        var processedSkeletons = new HashSet<Skeleton>();
        var vp = viewMatrix * projMatrix;

        foreach (var rootObj in scene.Objects)
        {
            if (rootObj.Parent != null) continue;

            // Find the first skinned mesh in this hierarchy
            var (skinnedObj, mr) = FindFirstSkinnedMesh(rootObj);
            if (skinnedObj == null || mr?.Mesh == null) continue;

            var skeleton = mr.Mesh.Skeleton;
            if (skeleton == null) continue;
            if (!processedSkeletons.Add(skeleton)) continue;

            // Evaluate animation to current pose (same as Renderer does)
            var anim = skinnedObj.GetComponent<AnimationPlayerComponent>();
            if (anim != null && mr.Mesh.Clips.Count > 0)
            {
                int clipIdx = anim.CurrentClipIndex % mr.Mesh.Clips.Count;
                mr.Mesh.Clips[clipIdx].Apply(skeleton, anim.PlaybackTime);
            }

            // Get the final bone matrices — EXACT same as Renderer line 297:
            //   matrices[i] = Bones[i].Offset * Bones[i].GlobalTransform * GlobalInverseTransform
            var finalBoneMatrices = skeleton.GetFinalMatrices();

            // Get the object's world matrix — EXACT same as Renderer line 283:
            //   var model = obj.GetWorldMatrix();
            var objectWorldMatrix = skinnedObj.GetWorldMatrix();

            // Auto-map bones if no explicit mapping provided
            var mapping = keypointBoneMap ?? KeypointRegistry.AutoMapBones(
                skeleton.BonesByName.Keys);

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
                if (!mapping.TryGetValue(kp, out var boneName) ||
                    !skeleton.BonesByName.TryGetValue(boneName, out var bone))
                {
                    annotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                // EXACT same math as UIManager.DrawOriented3DBox lines 976-1000:
                //   model = boneMatrices[boneIdx] * obj.GetWorldMatrix();
                //   worldP = Vector3.Transform(localPoint, model);
                // For a joint, the "localPoint" is the bone origin [0,0,0] in mesh space.
                // Since finalBoneMatrix maps mesh-space → animated-model-space,
                // Transform(Zero, finalBone * worldMatrix) gives the world position.
                int boneIdx = bone.ID;
                if (boneIdx < 0 || boneIdx >= finalBoneMatrices.Length)
                {
                    annotation.Keypoints[kp] = new Keypoint2D { X = 0, Y = 0, V = 0 };
                    continue;
                }

                var jointModel = finalBoneMatrices[boneIdx] * objectWorldMatrix;
                var jointWorldPos = jointModel.Translation;

                // Project 3D → 2D (same as UIManager.WorldToScreen)
                var clip = Vector4.Transform(new Vector4(jointWorldPos, 1.0f), vp);
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
        }

        return results;
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
