using System;
using System.Numerics;

namespace SynthGen.Physics;

/// <summary>
/// Dedicated wave sampling logic based on the Skunkworks Gerstner formula.
/// Matches the HLSL shader for pixel-perfect physics/visual sync.
/// </summary>
public static class GerstnerWaveSampler
{
    // Increased wave count and varied directions to avoid artificial grid patterns
    public static readonly Vector4[] Waves = {
        new Vector4(1.0f,  0.0f,  0.4f,  60.0f),
        new Vector4(0.3f,  0.9f,  0.3f,  31.0f),
        new Vector4(-0.4f, 0.7f,  0.25f, 17.5f),
        new Vector4(0.6f,  -0.6f, 0.2f,  12.3f),
        new Vector4(-0.8f, -0.2f, 0.15f, 7.3f),
        new Vector4(0.2f,  0.8f,  0.12f, 4.2f),
        new Vector4(0.5f,  0.5f,  0.1f,  2.7f),
        new Vector4(-0.1f, 0.9f,  0.08f, 1.5f)
    };
    
    public static readonly float[] Speeds = { 1.1f, 1.5f, 1.9f, 2.2f, 2.5f, 2.8f, 3.2f, 3.5f };

    public static Vector3 GetFullDisplacement(Vector3 worldPos, float time, OceanConfig settings)
    {
        if (settings == null || !settings.Enabled) return Vector3.Zero;

        float angle = settings.WindDirection * MathF.PI / 180.0f;
        float cosA = MathF.Cos(angle);
        float sinA = MathF.Sin(angle);
        
        float baseAmp = (settings.LargeWindSpeed / 10.0f) * (1.0f + settings.StormIntensity * 1.5f);
        Vector3 displacement = new Vector3(0, settings.Level, 0);

        for (int i = 0; i < Waves.Length; i++)
        {
            Vector4 w = Waves[i];
            
            // Physical rotation of wind
            float dx = w.X * cosA - w.Y * sinA;
            float dz = w.X * sinA + w.Y * cosA;
            
            // Deterministic Chaos (synchronized with shader)
            float sinVal = MathF.Sin(i * 785.1f);
            float chaosVal = (sinVal - MathF.Floor(sinVal) - 0.5f) * settings.LargeChaos;
            dx += chaosVal;
            dz += chaosVal;

            Vector2 dir = Vector2.Normalize(new Vector2(dx, dz));
            float k = 2.0f * MathF.PI / w.W;
            float a = (w.W / 120.0f) * baseAmp; // Match shader smoothing
            float s_i = settings.LargeSteepness * (w.W / 80.0f); // Match shader rounding
            
            float phase = k * (Vector2.Dot(dir, new Vector2(worldPos.X, worldPos.Z)) - Speeds[i] * time * settings.TimeMultiplier);
            
            displacement.Y += a * MathF.Sin(phase);
            displacement.X += s_i * a * dir.X * MathF.Cos(phase);
            displacement.Z += s_i * a * dir.Y * MathF.Cos(phase);
        }

        return displacement;
    }

    public static float GetWaveHeight(Vector3 worldPos, float time, OceanConfig settings)
    {
        return GetFullDisplacement(worldPos, time, settings).Y;
    }

    public static Vector3 GetNormal(Vector3 worldPos, float time, OceanConfig settings)
    {
        float eps = 0.1f;
        Vector3 h0 = GetFullDisplacement(worldPos, time, settings);
        Vector3 hx = GetFullDisplacement(worldPos + new Vector3(eps, 0, 0), time, settings);
        Vector3 hz = GetFullDisplacement(worldPos + new Vector3(0, 0, eps), time, settings);
        
        Vector3 tx = new Vector3(eps, hx.Y - h0.Y, 0);
        Vector3 tz = new Vector3(0, hz.Y - h0.Y, eps);
        
        return Vector3.Normalize(Vector3.Cross(tz, tx));
    }
}
