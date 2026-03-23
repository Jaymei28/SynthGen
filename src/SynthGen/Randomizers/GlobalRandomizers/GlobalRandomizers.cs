using System.Numerics;
using ImGuiNET;
using SynthGen.Scene;
using SynthGen.Scene.Components;

namespace SynthGen.Randomizers.GlobalRandomizers;

// ═══════════════════════════════════════════════════════════════════════════
// Weather Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public enum WeatherPreset { Sun, Rain, Storm, Wind, Snow, Cloudy }

public class WeatherRandomizer : RandomizerBase
{
    public override string Name => "Weather";
    public override string Category => "Global";

    public bool AllowRain = true;
    public bool AllowStorm = true;
    public bool AllowSun = true;
    public bool AllowWind = true;
    public bool AllowSnow = true;
    public bool AllowCloudy = true;

    // Current active weather (set by Randomize)
    public WeatherPreset CurrentWeather { get; private set; } = WeatherPreset.Sun;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        var allowed = new List<WeatherPreset>();
        if (AllowSun) allowed.Add(WeatherPreset.Sun);
        if (AllowRain) allowed.Add(WeatherPreset.Rain);
        if (AllowStorm) allowed.Add(WeatherPreset.Storm);
        if (AllowWind) allowed.Add(WeatherPreset.Wind);
        if (AllowSnow) allowed.Add(WeatherPreset.Snow);
        if (AllowCloudy) allowed.Add(WeatherPreset.Cloudy);

        if (allowed.Count == 0) return;
        var preset = allowed[rng.Next(allowed.Count)];
        ApplyWeather(scene, preset, rng);
    }

    public void ApplyWeather(SceneGraph scene, WeatherPreset preset, Random rng)
    {
        CurrentWeather = preset;
        var cam = scene.ActiveCamera;
        if (cam == null) return;

        switch (preset)
        {
            case WeatherPreset.Sun:
                cam.FogDensity = 0;
                cam.WeatherIntensity = 0;
                cam.WeatherType = 0;
                cam.Exposure = RandRange(rng, 1.0f, 1.5f);
                ApplyLightPreset(scene, new Vector3(1, 0.95f, 0.85f), RandRange(rng, 1.2f, 2.0f));
                break;
            case WeatherPreset.Rain:
                cam.FogDensity = RandRange(rng, 0.05f, 0.15f); 
                cam.WeatherIntensity = RandRange(rng, 0.8f, 1.2f);
                cam.WeatherType = 1;
                cam.FogColor = new Vector3(0.12f, 0.18f, 0.25f); // Deep Blue-Grey
                cam.Exposure = 0.85f;
                cam.WhiteBalanceTemperature = 7500f; // Colder
                ApplyLightPreset(scene, new Vector3(0.4f, 0.5f, 0.65f), RandRange(rng, 0.4f, 0.7f));
                break;
            case WeatherPreset.Storm:
                _stormBaseLight = RandRange(rng, 0.3f, 0.6f);
                cam.FogDensity = RandRange(rng, 0.05f, 0.2f);
                cam.WeatherIntensity = RandRange(rng, 1.0f, 1.5f); 
                cam.WeatherType = 1;
                cam.FogColor = new Vector3(0.1f, 0.15f, 0.22f); // Even darker blue
                cam.Exposure = 1.1f; // High exposure to compensate for lightning
                cam.WhiteBalanceTemperature = 8000f;
                ApplyLightPreset(scene, new Vector3(0.35f, 0.45f, 0.6f), _stormBaseLight);
                break;
            case WeatherPreset.Wind:
                cam.FogDensity = RandRange(rng, 0.1f, 0.3f);
                cam.WeatherIntensity = 0;
                cam.WeatherType = 0;
                cam.Exposure = RandRange(rng, 1.0f, 1.3f);
                ApplyLightPreset(scene, new Vector3(0.9f, 0.9f, 0.85f), RandRange(rng, 0.8f, 1.5f));
                break;
            case WeatherPreset.Snow:
                cam.FogDensity = RandRange(rng, 0.3f, 1.0f);
                cam.WeatherIntensity = cam.FogDensity;
                cam.WeatherType = 2;
                cam.FogColor = new Vector3(0.85f, 0.88f, 0.92f);
                cam.Exposure = RandRange(rng, 1.2f, 1.8f);
                cam.WhiteBalanceTemperature = RandRange(rng, 7000f, 9000f);
                ApplyLightPreset(scene, new Vector3(0.9f, 0.92f, 1.0f), RandRange(rng, 0.7f, 1.2f));
                break;
            case WeatherPreset.Cloudy:
                cam.FogDensity = RandRange(rng, 0.1f, 0.4f);
                cam.WeatherIntensity = 0;
                cam.WeatherType = 0;
                cam.FogColor = new Vector3(0.6f, 0.63f, 0.68f);
                cam.Exposure = RandRange(rng, 0.8f, 1.1f);
                ApplyLightPreset(scene, new Vector3(0.7f, 0.72f, 0.75f), RandRange(rng, 0.4f, 0.8f));
                break;
        }
    }

    private float _nextStrikeIn = 1.0f; // Fast first strike
    private float _flashAmt = 0f;
    private float _stormBaseLight = 0.5f;

    public override void OnUpdate(SceneGraph scene, float deltaTime)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;

        if (CurrentWeather == WeatherPreset.Storm)
        {
            _nextStrikeIn -= deltaTime;
            if (_nextStrikeIn <= 0)
            {
                _flashAmt = 1.0f;
                Random rng = new Random();
                _nextStrikeIn = RandRange(rng, 4.0f, 15.0f);
            }
        }

        if (_flashAmt > 0)
        {
            _flashAmt -= deltaTime * 1.8f; // SLOWER FADE
            _flashAmt = MathF.Max(0, _flashAmt);
            
            // Slower, more visible stuttering flicker
            float flicker = (MathF.Sin((float)DateTime.UtcNow.Ticks * 0.00002f) * 0.5f + 0.5f);
            float intensity = _flashAmt * (flicker * 0.4f + 0.6f); 
            cam.LightningIntensity = intensity;

            // DRIVE ACTUAL SCENE LIGHT for whole-world reaction
            if (CurrentWeather == WeatherPreset.Storm)
            {
                ApplyLightPreset(scene, new Vector3(0.55f, 0.65f, 1.0f), _stormBaseLight + intensity * 12.0f);
            }
        }
        else
        {
            cam.LightningIntensity = 0;
            // Restore base light if it was overridden
            if (CurrentWeather == WeatherPreset.Storm) {
                 ApplyLightPreset(scene, new Vector3(0.4f, 0.45f, 0.5f), _stormBaseLight);
            }
        }
    }

    private void ApplyLightPreset(SceneGraph scene, Vector3 color, float intensity)
    {
        foreach (var obj in scene.Objects)
        {
            var light = obj.GetComponent<LightComponent>();
            if (light != null && light.LightType == LightType.Directional)
            {
                light.Color = color;
                light.Intensity = intensity;
                break;
            }
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.Text($"Current: {CurrentWeather}");
        
        var cam = scene.ActiveCamera;
        if (cam != null)
        {
            ImGui.DragFloat("Manual Fog Density", ref cam.FogDensity, 0.01f, 0, 5);
            ImGui.ColorEdit3("Manual Fog Color", ref cam.FogColor);
            ImGui.DragFloat("Manual Weather Int.", ref cam.WeatherIntensity, 0.01f, 0, 1);
        }

        // Manual override for testing
        int current = (int)CurrentWeather;
        string[] names = Enum.GetNames(typeof(WeatherPreset));
        if (ImGui.Combo("Manual Override", ref current, names, names.Length))
        {
            ApplyWeather(scene, (WeatherPreset)current, new Random());
        }

        ImGui.Separator();
        ImGui.Checkbox("☀ Sun", ref AllowSun);
        ImGui.Checkbox("🌧 Rain", ref AllowRain);
        ImGui.Checkbox("⛈ Storm", ref AllowStorm);
        ImGui.Checkbox("💨 Wind", ref AllowWind);
        ImGui.Checkbox("❄ Snow", ref AllowSnow);
        ImGui.Checkbox("☁ Cloudy", ref AllowCloudy);
        
        ImGui.Separator();
        if (ImGui.Button("⚡ Trigger Test Strike"))
        {
            _flashAmt = 2.0f; // Extra bright test
            _nextStrikeIn = 1.0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Lighting Randomizer
// ═══════════════════════════════════════════════════════════════════════════
public class LightingRandomizer : RandomizerBase
{
    public override string Name => "Lighting";
    public override string Category => "Global";

    public float MinIntensity = 0.3f, MaxIntensity = 2.5f;
    public float MinAngle = 0f, MaxAngle = 360f;
    public float MinElevation = 15f, MaxElevation = 75f;
    public bool RandomizeColor = true;

    public override void Randomize(SceneGraph scene, Random rng)
    {
        foreach (var obj in scene.Objects)
        {
            var light = obj.GetComponent<LightComponent>();
            if (light == null) continue;

            light.Intensity = RandRange(rng, MinIntensity, MaxIntensity);

            // Random direction from angle + elevation
            float az = RandRange(rng, MinAngle, MaxAngle) * MathF.PI / 180f;
            float el = RandRange(rng, MinElevation, MaxElevation) * MathF.PI / 180f;
            obj.Transform.Rotation = new Vector3(
                -el * 180f / MathF.PI,
                az * 180f / MathF.PI,
                0
            );

            if (RandomizeColor)
            {
                float temp = RandRange(rng, 0f, 1f);
                light.Color = new Vector3(
                    0.8f + 0.2f * temp,
                    0.85f + 0.1f * (1f - temp),
                    1.0f - 0.3f * temp
                );
            }
        }
    }

    public override void DrawConfigUI(SceneGraph scene)
    {
        ImGui.DragFloatRange2("Intensity", ref MinIntensity, ref MaxIntensity, 0.05f, 0, 5);
        ImGui.DragFloatRange2("Azimuth (°)", ref MinAngle, ref MaxAngle, 1f, 0, 360);
        ImGui.DragFloatRange2("Elevation (°)", ref MinElevation, ref MaxElevation, 1f, 0, 90);
        ImGui.Checkbox("Random Color Temp", ref RandomizeColor);
    }
}
