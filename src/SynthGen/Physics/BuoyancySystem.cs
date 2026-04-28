using System;
using System.Numerics;
using SynthGen.Scene;
using SynthGen.Scene.Components;

namespace SynthGen.Physics;

/// <summary>
/// Applies buoyancy forces to objects with BuoyantBodyComponent.
/// </summary>
public class BuoyancySystem
{
    private readonly OceanSimulation _ocean;

    public BuoyancySystem(OceanSimulation ocean)
    {
        _ocean = ocean;
    }

    public void Update(SceneGraph scene, float dt)
    {
        foreach (var obj in scene.Objects)
        {
            var body = obj.GetComponent<BuoyantBodyComponent>();
            if (body == null || !body.Enabled) continue;

            // Initialize anchor if needed (first run or after being moved)
            if (body.AnchorPosition == Vector3.Zero)
            {
                body.AnchorPosition = obj.Transform.Position;
            }

            // Get total 3D displacement for both vertical bobbing and horizontal drift
            // MUST sample using the undisplaced AnchorPosition, because the GPU FFT map is indexed by base world coordinates!
            Vector3 waveDisp = _ocean.GetFullDisplacementAt(body.AnchorPosition.X, body.AnchorPosition.Z);
            float waterLevel = _ocean.Config.Level;
            
            // Detect Manual Movement (if user drags the boat with the gizmo)
            if (Vector3.Distance(obj.Transform.Position, body.LastPosition) > 0.05f && body.LastPosition != Vector3.Zero)
            {
                body.AnchorPosition = obj.Transform.Position - new Vector3(waveDisp.X, 0, waveDisp.Z) * body.BobIntensity;
            }

            // 1. Vertical Bobbing (The Push)
            // Using a spring-damper for "quick up-and-down" bobbing feel
            float targetY = waterLevel + (waveDisp.Y - waterLevel) * body.BobIntensity + body.Waterline;
            float diff = targetY - obj.Transform.Position.Y;
            
            float k = 150.0f; // Spring stiffness
            float c = 12.0f;  // Damping
            body.Velocity += (diff * k - body.Velocity * c) * dt;
            float newY = obj.Transform.Position.Y + body.Velocity * dt;
 
            // 2. Horizontal Drift
            float driftX = body.AnchorPosition.X + (waveDisp.X) * body.BobIntensity;
            float driftZ = body.AnchorPosition.Z + (waveDisp.Z) * body.BobIntensity;
            
            Vector3 newPos = new Vector3(driftX, newY, driftZ);
            obj.Transform.Position = newPos;
            body.LastPosition = newPos;
 
            // 3. Tilt to match wave normal
            if (_ocean.Config.Enabled)
            {
                var normal = _ocean.GetNormalAt(body.AnchorPosition.X, body.AnchorPosition.Z);
                
                // Snappy tilt reflection
                float tiltX = MathF.Atan2(normal.Z, normal.Y) * 180f / MathF.PI;
                float tiltZ = MathF.Atan2(normal.X, normal.Y) * 180f / MathF.PI;
                
                Vector3 targetRot = new Vector3(
                    tiltX * 1.8f * body.TiltIntensity, 
                    obj.Transform.Rotation.Y, 
                    -tiltZ * 1.8f * body.TiltIntensity
                );
                
                obj.Transform.Rotation = Vector3.Lerp(obj.Transform.Rotation, targetRot, MathF.Min(1.0f, dt * 12.0f));
            }
        }
    }
}
