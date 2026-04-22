namespace SynthGen.Scene.Components;

/// <summary>
/// Marks a SceneObject as a COCO keypoint marker for pose estimation.
/// Attach to empty child nodes positioned at joint locations on a 3D model.
/// When BoundBoneName is set, the keypoint automatically follows the skeleton bone animation.
/// </summary>
public class KeypointComponent
{
    /// <summary>
    /// COCO keypoint index (0-16):
    ///  0=Nose, 1=LeftEye, 2=RightEye, 3=LeftEar, 4=RightEar,
    ///  5=LeftShoulder, 6=RightShoulder, 7=LeftElbow, 8=RightElbow,
    ///  9=LeftWrist, 10=RightWrist, 11=LeftHip, 12=RightHip,
    /// 13=LeftKnee, 14=RightKnee, 15=LeftAnkle, 16=RightAnkle
    /// </summary>
    public int KeypointIndex;

    /// <summary>Human-readable keypoint name.</summary>
    public string KeypointName = "";

    /// <summary>
    /// The skeleton bone name this keypoint is bound to.
    /// When set, the keypoint's position is updated each frame from the animated bone.
    /// </summary>
    public string? BoundBoneName;

    /// <summary>
    /// Optional local offset from the bone's origin (e.g., to fine-tune position).
    /// Applied in bone-local space after the bone's world transform.
    /// </summary>
    public System.Numerics.Vector3 BoneOffset = System.Numerics.Vector3.Zero;

    public KeypointComponent() { }

    public KeypointComponent(int index, string name)
    {
        KeypointIndex = index;
        KeypointName = name;
    }
}
