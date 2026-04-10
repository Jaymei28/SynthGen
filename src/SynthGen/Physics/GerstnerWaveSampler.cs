using System;
using System.Numerics;

namespace SynthGen.Physics;

/// <summary>
/// Dedicated wave sampling logic based on the Skunkworks Gerstner formula.
/// Matches the HLSL shader for pixel-perfect physics/visual sync.
/// </summary>
public static class GerstnerWaveSampler
{
    public static readonly Vector4[] Waves = {
        new Vector4(1.0f,   0.2f,  0.5f,  50.0f),
        new Vector4(-0.4f,  0.8f,  0.4f,  32.0f),
        new Vector4(0.5f,  -0.6f,  0.3f,  18.0f),
        new Vector4(-0.7f, -0.4f,  0.25f, 10.0f),
        new Vector4(0.3f,   0.7f,  0.2f,  5.5f),
        new Vector4(-0.8f, -0.2f,  0.15f, 3.0f),
        new Vector4(0.4f,   0.5f,  0.1f,  1.5f),
        new Vector4(-0.2f,  0.9f,  0.08f, 0.8f)
    };
    
    public static readonly float[] Speeds = { 8.8f, 7.0f, 5.3f, 4.0f, 3.0f, 2.2f, 1.5f, 1.1f };

    public static Vector3 GetFullDisplacement(Vector3 worldPos, float time, OceanConfig settings)
    {
        if (settings == null || !settings.Enabled) return Vector3.Zero;

        float t = time * settings.TimeScale;
        float angle = settings.WindDirection * MathF.PI / 180.0f;
        float cosA = MathF.Cos(angle);
        float sinA = MathF.Sin(angle);
        
        // Map new params to old physics as a rough approximation for CPU buoyancy
        float baseAmp = (settings.WindSpeed / 10.0f);
        Vector3 displacement = Vector3.Zero;

        for (int i = 0; i < Waves.Length; i++)
        {
            Vector4 w = Waves[i];
            float dx = w.X * cosA - w.Y * sinA;
            float dz = w.X * sinA + w.Y * cosA;
            
            float ch = (MathF.Sin(i * 123.45f) % 1.0f - 0.5f) * settings.Spread;
            Vector2 dir = Vector2.Normalize(new Vector2(dx + ch, dz + ch));
            
            float k = 2.0f * MathF.PI / w.W;
            float a = (w.W / 60.0f) * baseAmp;
            float Q = (settings.Swell * 0.75f) / (k * a * 8.0f);
            
            float phase = k * (Vector2.Dot(dir, new Vector2(worldPos.X, worldPos.Z)) - Speeds[i] * t);
            float c = MathF.Cos(phase);
            float s = MathF.Sin(phase);
            
            displacement.X += Q * a * dir.X * c;
            displacement.Y += a * s;
            displacement.Z += Q * a * dir.Y * c;
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
