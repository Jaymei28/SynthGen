using System.Numerics;

namespace SynthGen.Scene.Components;

/// <summary>
/// Static registry of body part groups with preset segmentation colors.
/// </summary>
public static class BodyPartGroups
{
    public static readonly (string Name, Vector3 Color)[] Presets = new[]
    {
        ("Head",       new Vector3(1.0f, 0.0f, 0.0f)),   // Red
        ("Torso",      new Vector3(0.0f, 1.0f, 0.0f)),   // Green
        ("Left Arm",   new Vector3(0.0f, 0.0f, 1.0f)),   // Blue
        ("Right Arm",  new Vector3(1.0f, 1.0f, 0.0f)),   // Yellow
        ("Left Hand",  new Vector3(1.0f, 0.0f, 1.0f)),   // Magenta
        ("Right Hand", new Vector3(0.0f, 1.0f, 1.0f)),   // Cyan
        ("Left Leg",   new Vector3(0.5f, 0.0f, 0.0f)),   // Dark Red
        ("Right Leg",  new Vector3(0.0f, 0.5f, 0.0f)),   // Dark Green
        ("Left Foot",  new Vector3(0.0f, 0.0f, 0.5f)),   // Dark Blue
        ("Right Foot", new Vector3(0.5f, 0.5f, 0.0f)),   // Dark Yellow
    };

    /// <summary>
    /// Gets the segmentation color for a body part group name.
    /// Returns null if the group is not found or empty.
    /// </summary>
    public static Vector3? GetColor(string groupName)
    {
        if (string.IsNullOrEmpty(groupName)) return null;
        foreach (var (name, color) in Presets)
            if (name == groupName) return color;
        return null;
    }

    /// <summary>
    /// Gets the index of a group name in the preset list (-1 if not found, 0 = "None").
    /// </summary>
    public static int GetIndex(string groupName)
    {
        if (string.IsNullOrEmpty(groupName)) return 0; // "None"
        for (int i = 0; i < Presets.Length; i++)
            if (Presets[i].Name == groupName) return i + 1; // +1 because 0 = "None"
        return 0;
    }

    /// <summary>
    /// Gets the group name from a dropdown index (0 = "None", 1+ = preset).
    /// </summary>
    public static string GetName(int index)
    {
        if (index <= 0 || index > Presets.Length) return "";
        return Presets[index - 1].Name;
    }

    /// <summary>
    /// Returns all group names with "None" at index 0 for dropdown use.
    /// </summary>
    public static string[] GetDropdownNames()
    {
        var names = new string[Presets.Length + 1];
        names[0] = "None";
        for (int i = 0; i < Presets.Length; i++)
            names[i + 1] = Presets[i].Name;
        return names;
    }
}
