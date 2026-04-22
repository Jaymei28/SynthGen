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
            string preview = CurrentHDRI != null ? System.IO.Path.GetFileName(CurrentHDRI) : "Select HDRI...";
            if (ImGui.BeginCombo("HDRI Pool", preview))
            {
                for (int i = 0; i < HDRIPaths.Count; i++)
                {
                    string path = HDRIPaths[i];
                    string name = System.IO.Path.GetFileName(path);
                    bool selected = (path == CurrentHDRI);

                    if (ImGui.Selectable($"{name}##{i}", selected, ImGuiSelectableFlags.None, new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X - 25, 0)))
                    {
                        CurrentHDRI = path;
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"X##del{i}"))
                    {
                        try {
                            System.IO.File.Delete(path);
                            HDRIPaths.RemoveAt(i);
                            if (CurrentHDRI == path) CurrentHDRI = null;
                            NeedsRefresh = true;
                            ImGui.EndCombo();
                            return; 
                        } catch { }
                    }

                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
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
