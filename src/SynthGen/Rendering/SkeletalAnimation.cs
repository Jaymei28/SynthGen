using System;
using System.Collections.Generic;
using System.Numerics;

namespace SynthGen.Rendering;

public class BoneInfo
{
    public int ID;
    public string Name = "";
    public Matrix4x4 Offset;
    public Matrix4x4 LocalTransform = Matrix4x4.Identity;
    public Matrix4x4 GlobalTransform = Matrix4x4.Identity;
    public BoneInfo? Parent;
    public List<BoneInfo> Children = new();
}

/// <summary>
/// Represents a node in the full Assimp scene tree (bones AND non-bone nodes like Armature).
/// This is needed because bone hierarchy in FBX often has non-bone intermediate nodes.
/// </summary>
public class NodeInfo
{
    public string Name = "";
    public Matrix4x4 LocalTransform = Matrix4x4.Identity;
    public Matrix4x4 GlobalTransform = Matrix4x4.Identity;
    public List<NodeInfo> Children = new();
}

public class Skeleton
{
    public BoneInfo? Root;
    public Dictionary<string, BoneInfo> BonesByName = new();
    public List<BoneInfo> Bones = new();
    public Matrix4x4 GlobalInverseTransform;

    /// <summary>
    /// Full scene node tree for hierarchy traversal (includes Armature, etc.)
    /// </summary>
    public NodeInfo? NodeRoot;
    public Dictionary<string, NodeInfo> NodesByName = new();

    public Matrix4x4[] GetFinalMatrices()
    {
        var matrices = new Matrix4x4[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            matrices[i] = Bones[i].Offset * Bones[i].GlobalTransform * GlobalInverseTransform;
        }
        return matrices;
    }

    /// <summary>
    /// Walks the FULL node tree (not just bones) to compute global transforms,
    /// then copies global transforms to matching bones.
    /// </summary>
    public void UpdateHierarchy()
    {
        if (NodeRoot != null)
        {
            UpdateNodeTree(NodeRoot, Matrix4x4.Identity);
            // Copy global transforms from nodes to bones
            foreach (var bone in Bones)
            {
                if (NodesByName.TryGetValue(bone.Name, out var node))
                    bone.GlobalTransform = node.GlobalTransform;
            }
        }
        else
        {
            // Fallback: walk bone-only hierarchy
            UpdateBoneNode(Root, Matrix4x4.Identity);
        }
    }

    private void UpdateNodeTree(NodeInfo node, Matrix4x4 parentTransform)
    {
        node.GlobalTransform = node.LocalTransform * parentTransform;
        foreach (var child in node.Children)
            UpdateNodeTree(child, node.GlobalTransform);
    }

    private void UpdateBoneNode(BoneInfo? node, Matrix4x4 parentTransform)
    {
        if (node == null) return;
        node.GlobalTransform = node.LocalTransform * parentTransform;
        foreach (var child in node.Children)
            UpdateBoneNode(child, node.GlobalTransform);
    }
}

public class AnimationChannel
{
    public string NodeName = "";
    public List<(float Time, Vector3 Position)> PositionKeys = new();
    public List<(float Time, Quaternion Rotation)> RotationKeys = new();
    public List<(float Time, Vector3 Scale)> ScaleKeys = new();

    public Matrix4x4 Sample(float time)
    {
        Vector3 pos = SamplePosition(time);
        Quaternion rot = SampleRotation(time);
        Vector3 scale = SampleScale(time);

        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
    }

    private Vector3 SamplePosition(float time)
    {
        if (PositionKeys.Count == 0) return Vector3.Zero;
        if (PositionKeys.Count == 1) return PositionKeys[0].Position;
        int i = 0;
        while (i < PositionKeys.Count - 1 && time > PositionKeys[i + 1].Time) i++;
        if (i >= PositionKeys.Count - 1) return PositionKeys[^1].Position;
        float dt = PositionKeys[i + 1].Time - PositionKeys[i].Time;
        float t = dt > 0 ? (time - PositionKeys[i].Time) / dt : 0;
        return Vector3.Lerp(PositionKeys[i].Position, PositionKeys[i + 1].Position, t);
    }

    private Quaternion SampleRotation(float time)
    {
        if (RotationKeys.Count == 0) return Quaternion.Identity;
        if (RotationKeys.Count == 1) return RotationKeys[0].Rotation;
        int i = 0;
        while (i < RotationKeys.Count - 1 && time > RotationKeys[i + 1].Time) i++;
        if (i >= RotationKeys.Count - 1) return RotationKeys[^1].Rotation;
        float dt = RotationKeys[i + 1].Time - RotationKeys[i].Time;
        float t = dt > 0 ? (time - RotationKeys[i].Time) / dt : 0;
        return Quaternion.Slerp(RotationKeys[i].Rotation, RotationKeys[i + 1].Rotation, t);
    }

    private Vector3 SampleScale(float time)
    {
        if (ScaleKeys.Count == 0) return Vector3.One;
        if (ScaleKeys.Count == 1) return ScaleKeys[0].Scale;
        int i = 0;
        while (i < ScaleKeys.Count - 1 && time > ScaleKeys[i + 1].Time) i++;
        if (i >= ScaleKeys.Count - 1) return ScaleKeys[^1].Scale;
        float dt = ScaleKeys[i + 1].Time - ScaleKeys[i].Time;
        float t = dt > 0 ? (time - ScaleKeys[i].Time) / dt : 0;
        return Vector3.Lerp(ScaleKeys[i].Scale, ScaleKeys[i + 1].Scale, t);
    }
}

public class SkeletalAnimationClip
{
    public string Name = "";
    public float Duration;
    public float TicksPerSecond;
    public List<AnimationChannel> Channels = new();

    /// <summary>
    /// Applies this animation clip to the skeleton at the given time (in seconds).
    /// Updates ALL node transforms (bones and non-bone nodes) from animation channels,
    /// then recomputes the full hierarchy.
    /// </summary>
    public void Apply(Skeleton skeleton, float time)
    {
        float ticks = time * TicksPerSecond;
        float animTime = Duration > 0 ? ticks % Duration : 0;

        foreach (var channel in Channels)
        {
            Matrix4x4 localTransform = channel.Sample(animTime);

            // Apply to full node tree (includes Armature, etc.)
            if (skeleton.NodesByName.TryGetValue(channel.NodeName, out var node))
                node.LocalTransform = localTransform;

            // Also apply directly to bone if it exists
            if (skeleton.BonesByName.TryGetValue(channel.NodeName, out var bone))
                bone.LocalTransform = localTransform;
        }
        skeleton.UpdateHierarchy();
    }
}
