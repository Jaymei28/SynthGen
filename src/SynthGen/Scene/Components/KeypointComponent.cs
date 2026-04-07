namespace SynthGen.Scene.Components;

/// <summary>
/// Marks a SceneObject as a COCO keypoint marker for pose estimation.
/// Attach to empty child nodes positioned at joint locations on a 3D model.
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

    /// <summary>
    /// Human-readable keypoint name.
    /// </summary>
    public string KeypointName = "";

    public KeypointComponent(int index, string name)
    {
        KeypointIndex = index;
        KeypointName = name;
    }
}
