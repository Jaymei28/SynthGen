using System.Numerics;

namespace SynthGen.Rendering;

/// <summary>
/// PBR-lite material properties + texture references.
/// </summary>
public class Material
{
    public Vector4 BaseColor = new(1.0f, 1.0f, 1.0f, 1.0f);
    public float Smoothness = 0.5f;
    public float Metallic = 0.0f;
    public float NormalScale = 1.0f;
    public Vector3 EmissiveColor = Vector3.Zero;
    public float EmissiveIntensity = 1.0f;

    public uint AlbedoTextureID;
    public uint NormalTextureID;
    public uint MaskMapTextureID; // Metallic(R), AO(G), Detail(B), Smoothness(A)
    
    public string? AlbedoTexturePath;
    public string? NormalTexturePath;
    public string? MaskTexturePath;
}
