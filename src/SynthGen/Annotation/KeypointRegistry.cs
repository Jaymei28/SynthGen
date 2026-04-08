using System.Collections.Generic;

namespace SynthGen.Annotation;

/// <summary>
/// COCO 17-keypoint pose estimation definitions.
/// Maps keypoint indices to names and provides default bone name mappings
/// for common Mixamo/FBX skeletons.
/// </summary>
public enum PoseStandardType { COCO, Fisheye }

public class PoseStandard
{
    public string Name = "";
    public Dictionary<int, string> Keypoints = new();
    public (int, int)[] Edges = System.Array.Empty<(int, int)>();
    public Dictionary<int, string[]> BonePatterns = new();
}

public static class KeypointRegistry
{
    public static readonly PoseStandard COCO = new PoseStandard
    {
        Name = "COCO 17",
        Keypoints = new Dictionary<int, string>
        {
            {0, "Nose"}, {1, "Left Eye"}, {2, "Right Eye"}, {3, "Left Ear"}, {4, "Right Ear"},
            {5, "Left Shoulder"}, {6, "Right Shoulder"}, {7, "Left Elbow"}, {8, "Right Elbow"},
            {9, "Left Wrist"}, {10, "Right Wrist"}, {11, "Left Hip"}, {12, "Right Hip"},
            {13, "Left Knee"}, {14, "Right Knee"}, {15, "Left Ankle"}, {16, "Right Ankle"}
        },
        Edges = new[] {
            (0, 1), (0, 2), (1, 3), (2, 4), (5, 6), (5, 7), (7, 9), (6, 8), (8, 10), 
            (5, 11), (6, 12), (11, 12), (11, 13), (13, 15), (12, 14), (14, 16)
        },
        BonePatterns = new Dictionary<int, string[]>
        {
            {0, new[]{"head"}}, {1, new[]{"lefteye"}}, {2, new[]{"righteye"}}, {3, new[]{"leftear"}}, {4, new[]{"rightear"}},
            {5, new[]{"leftshoulder", "Shoulder_L"}}, {6, new[]{"rightshoulder", "Shoulder_R"}},
            {7, new[]{"leftforearm", "Elbow_L"}}, {8, new[]{"rightforearm", "Elbow_R"}},
            {9, new[]{"lefthand", "Hand_L"}}, {10, new[]{"righthand", "Hand_R"}},
            {11, new[]{"leftupleg", "Thigh_L"}}, {12, new[]{"rightupleg", "Thigh_R"}},
            {13, new[]{"leftleg", "Shin_L"}}, {14, new[]{"rightleg", "Shin_R"}},
            {15, new[]{"leftfoot", "Foot_L"}}, {16, new[]{"rightfoot", "Foot_R"}}
        }
    };

    public static readonly PoseStandard Fisheye = new PoseStandard
    {
        Name = "Fisheye Custom",
        Keypoints = new Dictionary<int, string>
        {
            {0, "Head"},
            {1, "Chest"},
            {2, "Left Shoulder"},
            {3, "Right Shoulder"},
            {4, "Left Elbow"},
            {5, "Right Elbow"},
            {6, "Left Hand"},
            {7, "Right Hand"}
        },
        Edges = new[] {
            (0, 1),        // Head to Chest
            (1, 2), (1, 3), // Chest to Shoulders
            (2, 4), (4, 6), // Left arm
            (3, 5), (5, 7)  // Right arm
        },
        BonePatterns = new Dictionary<int, string[]>
        {
            {0, new[]{"head"}},
            {1, new[]{"spine02", "Spine02", "Chest", "spine1", "Spine1"}},
            {2, new[]{"leftshoulder", "Shoulder_L"}},
            {3, new[]{"rightshoulder", "Shoulder_R"}},
            {4, new[]{"leftforearm", "Elbow_L"}},
            {5, new[]{"rightforearm", "Elbow_R"}},
            {6, new[]{"lefthand", "Hand_L"}},
            {7, new[]{"righthand", "Hand_R"}}
        }
    };

    public static PoseStandard GetStandard(PoseStandardType type) => type switch
    {
        PoseStandardType.Fisheye => Fisheye,
        _ => COCO
    };

    public static Dictionary<int, string> AutoMapBones(PoseStandard std, IEnumerable<string> boneNames)
    {
        var mapping = new Dictionary<int, string>();
        var boneList = new List<string>(boneNames);

        foreach (var kvp in std.BonePatterns)
        {
            int kpIdx = kvp.Key;
            foreach (var pattern in kvp.Value)
            {
                string? match = boneList.Find(b => b.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                {
                    mapping[kpIdx] = match;
                    break;
                }
            }
        }
        return mapping;
    }
}
