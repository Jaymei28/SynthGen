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
    // ── Simulation (The Formula) ──
    public bool  Enabled = true;
    public float Level = 0.0f;           
    public float TimeMultiplier = 1.0f;
    public float LargeWindSpeed = 10.0f; 
    public float WindDirection = 45.0f;  
    public float StormIntensity = 0.1f;  
    public float LargeSteepness = 0.1f;
    public float LargeChaos = 0.3f;      
    
    // ── Visual Look (Tropical Sync) ──
    public Vector3 RefractionColor = new(0.20f, 0.80f, 0.90f);
    public Vector3 ScatteringColor = new(0.10f, 0.40f, 0.50f);
    public float FoamAmount = 0.2f;
    public float SparkleIntensity = 2.0f;
    public float MicroRippleStrength = 0.8f;
    public float ReflectionSaturation = 0.6f; 
    
    // ── Buoyancy (The Push) ──
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
