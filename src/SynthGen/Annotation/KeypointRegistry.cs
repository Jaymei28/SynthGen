using System.Collections.Generic;

namespace SynthGen.Annotation;

/// <summary>
/// COCO 17-keypoint pose estimation definitions.
/// Maps keypoint indices to names and provides default bone name mappings
/// for common Mixamo/FBX skeletons.
/// </summary>
public static class KeypointRegistry
{
    /// <summary>
    /// The 17 COCO keypoint names in order (0-indexed).
    /// </summary>
    public static readonly string[] KeypointNames =
    {
        "Nose",           // 0
        "Left Eye",       // 1
        "Right Eye",      // 2
        "Left Ear",       // 3
        "Right Ear",      // 4
        "Left Shoulder",  // 5
        "Right Shoulder", // 6
        "Left Elbow",     // 7
        "Right Elbow",    // 8
        "Left Wrist",     // 9
        "Right Wrist",    // 10
        "Left Hip",       // 11
        "Right Hip",      // 12
        "Left Knee",      // 13
        "Right Knee",     // 14
        "Left Ankle",     // 15
        "Right Ankle",    // 16
    };

    /// <summary>
    /// COCO skeleton connectivity for visualization.
    /// Each pair (a, b) means keypoint a is connected to keypoint b.
    /// </summary>
    public static readonly (int, int)[] SkeletonEdges =
    {
        (0, 1), (0, 2),           // Nose → Eyes
        (1, 3), (2, 4),           // Eyes → Ears
        (5, 6),                   // Shoulders
        (5, 7), (7, 9),           // Left arm
        (6, 8), (8, 10),          // Right arm
        (5, 11), (6, 12),         // Torso
        (11, 12),                 // Hips
        (11, 13), (13, 15),       // Left leg
        (12, 14), (14, 16),       // Right leg
    };

    /// <summary>
    /// Default bone name patterns for auto-mapping Mixamo skeletons.
    /// Each keypoint maps to an array of possible bone name substrings (case-insensitive).
    /// The first match wins.
    /// </summary>
    public static readonly string[][] DefaultBonePatterns =
    {
        // 0: Nose
        new[] { "head", "Head" },
        // 1: Left Eye
        new[] { "lefteye", "LeftEye", "eye.l", "Eye_L" },
        // 2: Right Eye
        new[] { "righteye", "RightEye", "eye.r", "Eye_R" },
        // 3: Left Ear
        new[] { "leftear", "LeftEar", "ear.l" },
        // 4: Right Ear
        new[] { "rightear", "RightEar", "ear.r" },
        // 5: Left Shoulder
        new[] { "leftshoulder", "LeftShoulder", "shoulder.l", "Shoulder_L", "LeftArm" },
        // 6: Right Shoulder
        new[] { "rightshoulder", "RightShoulder", "shoulder.r", "Shoulder_R", "RightArm" },
        // 7: Left Elbow
        new[] { "leftforearm", "LeftForeArm", "elbow.l", "Elbow_L", "ForeArm_L" },
        // 8: Right Elbow
        new[] { "rightforearm", "RightForeArm", "elbow.r", "Elbow_R", "ForeArm_R" },
        // 9: Left Wrist
        new[] { "lefthand", "LeftHand", "wrist.l", "Hand_L" },
        // 10: Right Wrist
        new[] { "righthand", "RightHand", "wrist.r", "Hand_R" },
        // 11: Left Hip
        new[] { "leftupleg", "LeftUpLeg", "hip.l", "UpLeg_L", "Thigh_L" },
        // 12: Right Hip
        new[] { "rightupleg", "RightUpLeg", "hip.r", "UpLeg_R", "Thigh_R" },
        // 13: Left Knee
        new[] { "leftleg", "LeftLeg", "knee.l", "Leg_L", "Shin_L" },
        // 14: Right Knee
        new[] { "rightleg", "RightLeg", "knee.r", "Leg_R", "Shin_R" },
        // 15: Left Ankle
        new[] { "leftfoot", "LeftFoot", "ankle.l", "Foot_L" },
        // 16: Right Ankle
        new[] { "rightfoot", "RightFoot", "ankle.r", "Foot_R" },
    };

    /// <summary>
    /// Attempts to auto-map skeleton bone names to COCO keypoint indices.
    /// Returns a dictionary: keypointIndex → boneName.
    /// </summary>
    public static Dictionary<int, string> AutoMapBones(IEnumerable<string> boneNames)
    {
        var mapping = new Dictionary<int, string>();
        var boneList = new List<string>(boneNames);

        for (int kp = 0; kp < 17; kp++)
        {
            foreach (var pattern in DefaultBonePatterns[kp])
            {
                string? match = boneList.Find(b =>
                    b.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                {
                    mapping[kp] = match;
                    break;
                }
            }
        }
        return mapping;
    }
}
