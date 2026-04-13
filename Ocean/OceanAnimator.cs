using UnityEngine;

/// <summary>
/// Drop this script on a plane mesh in your scene.
/// It drives the OceanHDRP.shader with animated Gerstner waves
/// by setting the _TimeTotal property every frame.
/// No external SceneConfig needed for a standalone demo.
/// </summary>
[ExecuteAlways]
public class OceanAnimator : MonoBehaviour
{
    [Header("Wave Controls")]
    [Range(0, 40)]  public float windSpeed     = 12f;
    [Range(0, 360)] public float windDirection = 45f;
    [Range(0, 3)]   public float choppiness   = 1.3f;
    [Range(0, 2)]   public float chaos        = 0.8f;
    [Range(0, 1)]   public float stormIntensity = 0f;

    [Header("Look")]
    public Color refractionColor   = new Color(0f, 0.45f, 0.65f);
    public Color scatteringColor   = new Color(0f, 0.27f, 0.23f);
    [Range(0,10)]   public float absorptionDistance = 5f;
    [Range(0f,1f)]  public float smoothness  = 0.95f;
    [Range(0f,1f)]  public float transparency = 0.8f;
    [Range(0f,3f)]  public float envStrength  = 0.6f;

    [Header("Foam")]
    public bool foamEnabled = true;
    [Range(0,3)] public float foamAmount = 0.35f;

    private Material _mat;

    // Shader property IDs
    private static readonly int _Time         = Shader.PropertyToID("_TimeTotal");
    private static readonly int _WindSpeed    = Shader.PropertyToID("_WindSpeed");
    private static readonly int _WindDir      = Shader.PropertyToID("_WindDirection");
    private static readonly int _Choppiness   = Shader.PropertyToID("_Choppiness");
    private static readonly int _Chaos        = Shader.PropertyToID("_Chaos");
    private static readonly int _Storm        = Shader.PropertyToID("_Storm");
    private static readonly int _RefCol       = Shader.PropertyToID("_RefractionColor");
    private static readonly int _ScatCol      = Shader.PropertyToID("_ScatteringColor");
    private static readonly int _AbsDist      = Shader.PropertyToID("_AbsorptionDistance");
    private static readonly int _Smoothness   = Shader.PropertyToID("_Smoothness");
    private static readonly int _Transparency = Shader.PropertyToID("_Transparency");
    private static readonly int _EnvStr       = Shader.PropertyToID("_EnvStrength");
    private static readonly int _FoamEnabled  = Shader.PropertyToID("_FoamEnabled");
    private static readonly int _FoamAmount   = Shader.PropertyToID("_FoamAmount");

    void OnEnable()
    {
        var rend = GetComponent<Renderer>();
        if (rend) _mat = rend.sharedMaterial;
    }

    void Update()
    {
        if (_mat == null) return;

        _mat.SetFloat(_Time,         Application.isPlaying ? Time.time : (float)UnityEditor.EditorApplication.timeSinceStartup);
        _mat.SetFloat(_WindSpeed,    windSpeed);
        _mat.SetFloat(_WindDir,      windDirection);
        _mat.SetFloat(_Choppiness,   choppiness);
        _mat.SetFloat(_Chaos,        chaos);
        _mat.SetFloat(_Storm,        stormIntensity);
        _mat.SetColor(_RefCol,       refractionColor);
        _mat.SetColor(_ScatCol,      scatteringColor);
        _mat.SetFloat(_AbsDist,      absorptionDistance);
        _mat.SetFloat(_Smoothness,   smoothness);
        _mat.SetFloat(_Transparency, transparency);
        _mat.SetFloat(_EnvStr,       envStrength);
        _mat.SetFloat(_FoamEnabled,  foamEnabled ? 1f : 0f);
        _mat.SetFloat(_FoamAmount,   foamAmount);
    }
}
