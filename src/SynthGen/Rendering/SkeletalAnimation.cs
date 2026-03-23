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

public class Skeleton
{
    public BoneInfo? Root;
    public Dictionary<string, BoneInfo> BonesByName = new();
    public List<BoneInfo> Bones = new();
    public Matrix4x4 GlobalInverseTransform;

    public Matrix4x4[] GetFinalMatrices()
    {
        var matrices = new Matrix4x4[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            matrices[i] = Bones[i].Offset * Bones[i].GlobalTransform * GlobalInverseTransform;
        }
        return matrices;
    }

    public void UpdateHierarchy()
    {
        UpdateNode(Root, Matrix4x4.Identity);
    }

    private void UpdateNode(BoneInfo? node, Matrix4x4 parentTransform)
    {
        if (node == null) return;
        node.GlobalTransform = node.LocalTransform * parentTransform;
        foreach (var child in node.Children)
            UpdateNode(child, node.GlobalTransform);
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
        float t = (time - PositionKeys[i].Time) / (PositionKeys[i + 1].Time - PositionKeys[i].Time);
        return Vector3.Lerp(PositionKeys[i].Position, PositionKeys[i + 1].Position, t);
    }

    private Quaternion SampleRotation(float time)
    {
        if (RotationKeys.Count == 0) return Quaternion.Identity;
        if (RotationKeys.Count == 1) return RotationKeys[0].Rotation;
        int i = 0;
        while (i < RotationKeys.Count - 1 && time > RotationKeys[i + 1].Time) i++;
        float t = (time - RotationKeys[i].Time) / (RotationKeys[i + 1].Time - RotationKeys[i].Time);
        return Quaternion.Slerp(RotationKeys[i].Rotation, RotationKeys[i + 1].Rotation, t);
    }

    private Vector3 SampleScale(float time)
    {
        if (ScaleKeys.Count == 0) return Vector3.One;
        if (ScaleKeys.Count == 1) return ScaleKeys[0].Scale;
        int i = 0;
        while (i < ScaleKeys.Count - 1 && time > ScaleKeys[i + 1].Time) i++;
        float t = (time - ScaleKeys[i].Time) / (ScaleKeys[i + 1].Time - ScaleKeys[i].Time);
        return Vector3.Lerp(ScaleKeys[i].Scale, ScaleKeys[i + 1].Scale, t);
    }
}

public class SkeletalAnimationClip
{
    public string Name = "";
    public float Duration;
    public float TicksPerSecond;
    public List<AnimationChannel> Channels = new();

    public void Apply(Skeleton skeleton, float time)
    {
        float ticks = time * TicksPerSecond;
        float animTime = ticks % Duration;

        foreach (var channel in Channels)
        {
            if (skeleton.BonesByName.TryGetValue(channel.NodeName, out var bone))
            {
                bone.LocalTransform = channel.Sample(animTime);
            }
        }
        skeleton.UpdateHierarchy();
    }
}
