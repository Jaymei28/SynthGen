using UnityEngine;
using Skunkworks.Core;

namespace Skunkworks.Ocean
{
    [ExecuteInEditMode]
    public class WaterSurface : MonoBehaviour
    {
        public SceneConfig sceneConfig;
        public Material waterMaterial;

        // Static Property IDs for optimized updates
        private static readonly int TimeId = Shader.PropertyToID("_TimeTotal");
        private static readonly int LevelId = Shader.PropertyToID("_Level");
        private static readonly int WindSpeedId = Shader.PropertyToID("_WindSpeed");
        private static readonly int WindDirId = Shader.PropertyToID("_WindDirection");
        private static readonly int ChoppinessId = Shader.PropertyToID("_Choppiness");
        private static readonly int ChaosId = Shader.PropertyToID("_Chaos");
        private static readonly int StormId = Shader.PropertyToID("_Storm");
        
        private static readonly int RefractionColorId = Shader.PropertyToID("_RefractionColor");
        private static readonly int ScatteringColorId = Shader.PropertyToID("_ScatteringColor");
        private static readonly int AbsorbDistId = Shader.PropertyToID("_AbsorptionDistance");
        private static readonly int AmbScatId = Shader.PropertyToID("_AmbientScattering");
        private static readonly int HeightScatId = Shader.PropertyToID("_HeightScattering");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int TransparencyId = Shader.PropertyToID("_Transparency");
        private static readonly int ReflectionId = Shader.PropertyToID("_EnvStrength");

        private static readonly int FoamEnabledId = Shader.PropertyToID("_FoamEnabled");
        private static readonly int FoamAmountId = Shader.PropertyToID("_FoamAmount");

        void Update()
        {
            if (sceneConfig == null || waterMaterial == null || !sceneConfig.ocean.enabled)
                return;

            OceanSettings o = sceneConfig.ocean;

            // Sync simulation uniforms
            waterMaterial.SetFloat(TimeId, Time.time * o.timeMultiplier);
            waterMaterial.SetFloat(LevelId, o.level);
            waterMaterial.SetFloat(WindSpeedId, o.windSpeed);
            waterMaterial.SetFloat(WindDirId, o.windDirection);
            waterMaterial.SetFloat(ChoppinessId, o.choppiness);
            waterMaterial.SetFloat(ChaosId, o.chaos);
            waterMaterial.SetFloat(StormId, o.stormIntensity);

            // Sync visual uniforms
            waterMaterial.SetColor(RefractionColorId, o.refractionColor);
            waterMaterial.SetColor(ScatteringColorId, o.scatteringColor);
            waterMaterial.SetFloat(AbsorbDistId, o.absorptionDistance);
            waterMaterial.SetFloat(AmbScatId, o.ambientScattering);
            waterMaterial.SetFloat(HeightScatId, o.heightScattering);
            waterMaterial.SetFloat(SmoothnessId, o.smoothness);
            waterMaterial.SetFloat(TransparencyId, o.transparency);
            waterMaterial.SetFloat(ReflectionId, o.reflection);

            // Sync foam
            waterMaterial.SetInt(FoamEnabledId, o.foamEnabled ? 1 : 0);
            waterMaterial.SetFloat(FoamAmountId, o.foamAmount);
        }
    }
}
