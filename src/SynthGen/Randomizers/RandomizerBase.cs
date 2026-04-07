using SynthGen.Scene;

namespace SynthGen.Randomizers;

/// <summary>
/// Base class for all randomizers. Each has an Enabled toggle, name, and configurable ranges.
/// </summary>
public abstract class RandomizerBase
{
    public bool Enabled { get; set; } = false;
    public abstract string Name { get; }
    public abstract string Category { get; } // "Object", "Camera", "Global", "HDRI"

    /// <summary>Apply randomization to the scene.</summary>
    public abstract void Randomize(SceneGraph scene, Random rng);

    /// <summary>Called when the randomizer is toggled on or off in the UI.</summary>
    public virtual void OnToggle(SceneGraph scene, bool enabled) { }

    /// <summary>Draw ImGui configuration UI for this randomizer.</summary>
    public abstract void DrawConfigUI(SceneGraph scene);

    /// <summary>Optional per-frame update for dynamic randomizers (lightning, etc).</summary>
    public virtual void OnUpdate(SceneGraph scene, float deltaTime) { }

    protected static float RandRange(Random rng, float min, float max)
        => min + (float)rng.NextDouble() * (max - min);

    protected static int RandRange(Random rng, int min, int max)
        => rng.Next(min, max + 1);
}
