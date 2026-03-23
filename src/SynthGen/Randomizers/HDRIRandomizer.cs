using ImGuiNET;
using SynthGen.Scene;

namespace SynthGen.Randomizers;

// ═══════════════════════════════════════════════════════════════════════════
// HDRI Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class HDRIRandomizer : RandomizerBase
{
    public override string Name => "HDRI";
    public override string Category => "HDRI";

    public float MinStrength = 0.5f;
    public float MaxStrength = 2.0f;

    // HDRI file paths from AssetManager
    public List<string> HDRIPaths { get; set; } = new();

    // Currently selected HDRI (output)
    public string? CurrentHDRI { get; set; }
    public float CurrentStrength = 1.0f;
    public bool NeedsRefresh { get; set; } = false;

    public void SelectHDRI(string path)
    {
        if (HDRIPaths.Contains(path))
        {
            CurrentHDRI = path;
        }
    }

    public override void Randomize(SceneGraph scene, Random rng)
    {
        // Randomize which HDRI to use
        if (HDRIPaths.Count > 0)
        {
            CurrentHDRI = HDRIPaths[rng.Next(HDRIPaths.Count)];
        }

        // Randomize strength
        CurrentStrength = RandRange(rng, MinStrength, MaxStrength);
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloatRange2("Strength Range", ref MinStrength, ref MaxStrength, 0.05f, 0, 5);
        
        if (HDRIPaths.Count > 0)
        {
            int currentIdx = CurrentHDRI != null ? HDRIPaths.IndexOf(CurrentHDRI) : -1;
            string[] names = HDRIPaths.ConvertAll(p => System.IO.Path.GetFileName(p)).ToArray();
            
            if (ImGui.Combo("Select HDRI", ref currentIdx, names, names.Length))
            {
                if (currentIdx >= 0) CurrentHDRI = HDRIPaths[currentIdx];
            }
        }
        else
        {
            ImGui.TextDisabled("(No HDRIs found)");
        }

        if (CurrentHDRI != null)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1), $"Active: {System.IO.Path.GetFileName(CurrentHDRI)}");
            ImGui.SliderFloat("Live Strength", ref CurrentStrength, 0, 5);
        }
        if (ImGui.Button("Import new HDRI..."))
        {
            NeedsRefresh = true;
        }
        ImGui.TextWrapped("Place .hdr files in assets/hdri/ folder");
    }
}
