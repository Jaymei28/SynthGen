using UnityEngine;
using Skunkworks.Core;

namespace Skunkworks.Ocean
{
    public static class GerstnerWaveSampler
    {
        // Must match the Wave parameters in the HLSL shader
        private static readonly Vector4[] Waves = {
            new Vector4(1.0f, 0.0f, 0.4f, 12.0f),
            new Vector4(0.7f, 0.7f, 0.3f, 7.5f),
            new Vector4(0.2f, 1.0f, 0.25f, 4.2f),
            new Vector4(-0.5f, 0.3f, 0.2f, 2.1f)
        };
        
        private static readonly float[] Speeds = { 1.1f, 1.5f, 1.9f, 2.2f };

        public static float GetWaveHeight(Vector3 worldPos, float time, OceanSettings settings)
        {
            if (settings == null || !settings.enabled) return 0f;

            float angle = settings.windDirection * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angle);
            float sinA = Mathf.Sin(angle);
            
            float baseAmp = (settings.windSpeed / 10.0f) * (1.0f + settings.stormIntensity * 1.5f);
            float y = 0.0f;

            for (int i = 0; i < Waves.Length; i++)
            {
                Vector4 w = Waves[i];
                
                // Physical rotation of wind
                float dx = w.x * cosA - w.y * sinA;
                float dz = w.x * sinA + w.y * cosA;
                
                // Deterministic Chaos
                float chaosVal = (Mathf.Repeat(Mathf.Sin(i * 432.1f), 1.0f) - 0.5f) * settings.chaos;
                dx += chaosVal;
                dz += chaosVal;

                Vector2 dir = new Vector2(dx, dz).normalized;
                float k = 2.0f * Mathf.PI / w.w;
                float a = (w.w / 40.0f) * baseAmp;
                
                float phase = k * (Vector2.Dot(dir, new Vector2(worldPos.x, worldPos.z)) - Speeds[i] * time * settings.timeMultiplier);
                y += a * Mathf.Sin(phase);
            }

            return y + settings.level;
        }

        public static Vector3 GetNormal(Vector3 worldPos, float time, OceanSettings settings)
        {
            float eps = 0.1f;
            float h0 = GetWaveHeight(worldPos, time, settings);
            float hx = GetWaveHeight(worldPos + new Vector3(eps, 0, 0), time, settings);
            float hz = GetWaveHeight(worldPos + new Vector3(0, 0, eps), time, settings);
            
            Vector3 tx = new Vector3(eps, hx - h0, 0);
            Vector3 tz = new Vector3(0, hz - h0, eps);
            
            return Vector3.Cross(tz, tx).normalized;
        }
    }
}
