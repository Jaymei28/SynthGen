using System.Numerics;

namespace SynthGen.Physics;

public enum WaterSurfaceType
{
    OceanSeaLake,
    River,
    Pool
}

public enum WaterGeometryType
{
    Quad,
    CustomMesh,
    Infinite
}

/// <summary>
/// Ocean configuration — expanded to match Unity HDRP WaterSurface parameters.
/// </summary>
public class OceanConfig
{
    // ── Global Settings ──
    public bool Enabled = true;
    public float Level = 0.0f;
    public bool EnableSeaSpray = true;
    public int WaveResolutionIndex = 1; // 0=512, 1=1024, 2=2048
    public int WaveMeshQualityIndex = 1; // 0=Low, 1=High, 2=Ultra
    public float UpdatesPerSecond = 50.0f;
    public Vector3 WaterColor = new(0.04f, 0.12f, 0.2f);
    public Vector3 FoamColor = new(0.9f, 0.85f, 0.85f);
    
    // ── Wave Parameters (Cascade 1) ──
    public Vector2 TileLength = new(100.0f, 100.0f);
    public float TimeScale = 1.0f;
    public float WindSpeed = 18.0f;
    public float WindDirection = 25.0f; // degrees
    public float FetchLength = 175.0f;
    public float Swell = 0.8f;
    public float Detail = 1.0f;
    public float Spread = 0.286f;
    public float Whitecap = 0.5f;
    public float FoamAmount = 4.312f;

    // ── Buoyancy (Hidden/Internal Physics) ──
    public float BuoyancyForce = 80.0f;
    public float BuoyancyDamping = 12.0f;
}

/// <summary>
/// CPU-side wave simulation for buoyancy sampling, using parameters from HDRP WaterSurface.
/// </summary>
public class OceanSimulation
{
    public OceanConfig Config { get; set; } = new();

    private float _time;

    public void Update(float totalTime)
    {
        _time = totalTime;
    }

    /// <summary>
    /// Get the full 3D displacement at a world XZ position.
    /// Delegates to the modular Skunkworks sampler.
    /// </summary>
    public Vector3 GetFullDisplacementAt(float x, float z)
    {
        return GerstnerWaveSampler.GetFullDisplacement(new Vector3(x, 0, z), _time, Config);
    }

    public float GetHeightAt(float x, float z)
    {
        return GerstnerWaveSampler.GetWaveHeight(new Vector3(x, 0, z), _time, Config);
    }

    public Vector3 GetNormalAt(float x, float z)
    {
        return GerstnerWaveSampler.GetNormal(new Vector3(x, 0, z), _time, Config);
    }
}
