using System.Numerics;
using Silk.NET.Input;

namespace SynthGen.Scene;

/// <summary>
/// Camera with both orbit controls (middle-mouse drag) and free-fly mode (right-mouse + WASD).
/// </summary>
public class Camera : SceneObject
{
    public float FieldOfView = 60f;
    public float NearPlane = 0.1f;
    public float FarPlane = 1000f; // Increased for fly mode
    public bool IsActive = true;

    // Orbit camera parameters
    public float OrbitYaw = 0f;
    public float OrbitPitch = 25f;
    public float OrbitDistance = 12f;
    public Vector3 OrbitTarget = Vector3.Zero;

    // Free-fly state
    private bool _isFlyMode = false;
    private float _flyYaw = 0f;
    private float _flyPitch = 0f;
    private Vector3 _flyPos = new(0, 2, 10);
    public bool IsFlyMode => _isFlyMode;

    // Post-process settings
    public float FisheyeStrength = 0f;
    public float FogDensity = 0f;
    public Vector3 FogColor = new(0.7f, 0.75f, 0.8f);
    public float BloomThreshold = 1.0f;
    public float BloomIntensity = 0f;
    public float Exposure = 1.0f;
    public float NoiseIntensity = 0f;
    public bool NoiseLarge = false;
    public float SSAORadius = 0.5f;
    public float SSAOIntensity = 0f;
    public float WhiteBalanceTemperature = 6500f;
    public float WhiteBalanceTint = 0f;
    public float BlurRadius = 0f;
    public int   WeatherType = 0;      // 0: None, 1: Rain, 2: Snow
    public float WeatherIntensity = 0f; // 0 to 1 intensity factor
    public float LightningIntensity = 0f; // Flash factor for storm

    public Camera() : base("Camera") { }

    public Matrix4x4 GetViewMatrix()
    {
        if (_isFlyMode)
        {
            var (forward, _, up) = GetFlyVectors();
            Transform.Position = _flyPos;
            return Matrix4x4.CreateLookAt(_flyPos, _flyPos + forward, up);
        }
        else
        {
            float yawRad = OrbitYaw * MathF.PI / 180f;
            float pitchRad = OrbitPitch * MathF.PI / 180f;
            var pos = new Vector3(
                OrbitTarget.X + OrbitDistance * MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                OrbitTarget.Y + OrbitDistance * MathF.Sin(pitchRad),
                OrbitTarget.Z + OrbitDistance * MathF.Cos(pitchRad) * MathF.Cos(yawRad)
            );
            Transform.Position = pos;
            return Matrix4x4.CreateLookAt(pos, OrbitTarget, Vector3.UnitY);
        }
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        return Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfView * MathF.PI / 180f,
            aspectRatio,
            NearPlane,
            FarPlane
        );
    }

    private (Vector3 forward, Vector3 right, Vector3 up) GetFlyVectors()
    {
        float yawRad = _flyYaw * MathF.PI / 180f;
        float pitchRad = _flyPitch * MathF.PI / 180f;

        var forward = new Vector3(
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Cos(yawRad)
        );
        
        // Use Cross with UnitY but guard against looking straight up/down
        Vector3 worldUp = Vector3.UnitY;
        if (MathF.Abs(Vector3.Dot(forward, worldUp)) > 0.99f) worldUp = Vector3.UnitZ;

        var right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return (forward, right, up);
    }

    public bool ProcessInput(App.InputManager input, bool viewportHovered, float deltaTime)
    {
        if (!viewportHovered) 
        {
            if (_isFlyMode) ExitFlyMode();
            return false;
        }

        bool rightMouseDown = input.RightMouseDown;

        if (rightMouseDown && !_isFlyMode)
        {
            EnterFlyMode();
        }
        else if (!rightMouseDown && _isFlyMode)
        {
            ExitFlyMode();
        }

        if (_isFlyMode)
        {
            // Update rotation
            _flyYaw -= input.MouseDelta.X * 0.15f;
            _flyPitch -= input.MouseDelta.Y * 0.15f;
            _flyPitch = MathF.Max(-89f, MathF.Min(89f, _flyPitch));

            var (forward, right, _) = GetFlyVectors();
            float speed = 8f * deltaTime;
            if (input.ShiftHeld) speed *= 4f;

            if (input.IsKeyPressed(Key.W)) _flyPos += forward * speed;
            if (input.IsKeyPressed(Key.S)) _flyPos -= forward * speed;
            if (input.IsKeyPressed(Key.A)) _flyPos -= right * speed;
            if (input.IsKeyPressed(Key.D)) _flyPos += right * speed;
            if (input.IsKeyPressed(Key.E)) _flyPos += Vector3.UnitY * speed;
            if (input.IsKeyPressed(Key.Q)) _flyPos -= Vector3.UnitY * speed;

            // Maintain OrbitTarget in front of the camera so we can switch back smoothly
            OrbitTarget = _flyPos + forward * OrbitDistance;

            return true;
        }
        else
        {
            if (MathF.Abs(input.ScrollDelta) > 0.01f)
            {
                OrbitDistance -= input.ScrollDelta * OrbitDistance * 0.15f;
                OrbitDistance = MathF.Max(0.1f, MathF.Min(OrbitDistance, 2000f));
            }

            if (input.MiddleMouseDown && !input.ShiftHeld)
            {
                OrbitYaw -= input.MouseDelta.X * 0.3f;
                OrbitPitch += input.MouseDelta.Y * 0.3f;
                OrbitPitch = MathF.Max(-89f, MathF.Min(89f, OrbitPitch));
            }

            if (input.MiddleMouseDown && input.ShiftHeld)
            {
                float panSpeed = OrbitDistance * 0.003f;
                float yawRad = OrbitYaw * MathF.PI / 180f;
                var right = new Vector3(MathF.Cos(yawRad), 0, -MathF.Sin(yawRad));
                OrbitTarget -= right * input.MouseDelta.X * panSpeed;
                OrbitTarget += Vector3.UnitY * input.MouseDelta.Y * panSpeed;
            }

            return false;
        }
    }

    private void EnterFlyMode()
    {
        float yawRad = OrbitYaw * MathF.PI / 180f;
        float pitchRad = OrbitPitch * MathF.PI / 180f;
        
        _flyPos = new Vector3(
            OrbitTarget.X + OrbitDistance * MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            OrbitTarget.Y + OrbitDistance * MathF.Sin(pitchRad),
            OrbitTarget.Z + OrbitDistance * MathF.Cos(pitchRad) * MathF.Cos(yawRad)
        );

        // Calculate fly angles to match current view
        _flyYaw = OrbitYaw + 180f;
        _flyPitch = -OrbitPitch;
        _isFlyMode = true;
    }

    private void ExitFlyMode()
    {
        // Smooth transition back to orbit: 
        // We've been maintaining OrbitTarget and OrbitDistance,
        // now we just need to calculate the new OrbitYaw/OrbitPitch based on the current fly position.
        var dir = Vector3.Normalize(_flyPos - OrbitTarget);
        
        OrbitPitch = MathF.Asin(dir.Y) * 180f / MathF.PI;
        OrbitYaw = MathF.Atan2(dir.X, dir.Z) * 180f / MathF.PI;

        _isFlyMode = false;
    }
}
