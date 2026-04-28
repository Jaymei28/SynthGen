using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SynthGen.Scene.Components;

namespace SynthGen.App;

/// <summary>
/// Main application class. Owns the window, OpenGL context, ImGui, and the main loop.
/// </summary>
public class Application
{
    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imguiController = null!;

    // Core systems
    private Scene.SceneGraph _scene = null!;
    private Rendering.Renderer _renderer = null!;
    private Assets.AssetManager _assetManager = null!;
    private Physics.OceanSimulation _ocean = null!;
    private Physics.BuoyancySystem _buoyancy = null!;
    private Capture.CaptureManager _captureManager = null!;
    private Training.TrainingManager _trainingManager = null!;
    private UI.UIManager _uiManager = null!;
    private InputManager _inputManager = null!;

    private float _deltaTime;
    private float _totalTime;
    private DateTime _lastFrame;
    private float _autosaveTimer = 0f;
    private const float AutosaveInterval = 60f; // Save every 60 seconds
    private string _autosavePath = "";

    public GL GL => _gl;
    public Scene.SceneGraph Scene => _scene;
    public Rendering.Renderer Renderer => _renderer;
    public Assets.AssetManager AssetManager => _assetManager;
    public Physics.OceanSimulation Ocean => _ocean;
    public Physics.BuoyancySystem Buoyancy => _buoyancy;
    public Capture.CaptureManager CaptureManager => _captureManager;
    public Training.TrainingManager TrainingManager => _trainingManager;
    public InputManager Input => _inputManager;
    public Commands.CommandHistory CommandHistory { get; } = new();

    public float DeltaTime => _deltaTime;
    public float TotalTime => _totalTime;

    public void Run()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Vector2D<int>(1920, 1080);
        opts.Title = "SynthGen — Synthetic Data Generator";
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 5));
        opts.VSync = true;
        opts.PreferredDepthBufferBits = 24;
        opts.PreferredStencilBufferBits = 8;

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Resize += OnResize;

        _window.Run();
    }

    private void OnLoad()
    {
        // Set window icon
        SetWindowIcon();

        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _imguiController = new ImGuiController(_gl, _window, _input);

        // Enable OpenGL features
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(CullFaceMode.Back);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Initialize systems
        _scene = new Scene.SceneGraph();
        _assetManager = new Assets.AssetManager(_gl);
        _renderer = new Rendering.Renderer(_gl, 1920, 1080);
        _ocean = new Physics.OceanSimulation();
        _ocean.WaveGen = _renderer.WaveGen;
        _buoyancy = new Physics.BuoyancySystem(_ocean);
        _captureManager = new Capture.CaptureManager(_gl, _renderer, _scene, _ocean);
        _trainingManager = new Training.TrainingManager();
        _inputManager = new InputManager(_input);
        _uiManager = new UI.UIManager(this);

        // Set up default scene
        SetupDefaultScene();

        _autosavePath = Path.Combine(AppContext.BaseDirectory, "autosave.json");
        CheckForRecovery();

        _lastFrame = DateTime.UtcNow;

        Console.WriteLine("[SynthGen] Application initialized.");
    }

    private void SetupDefaultScene()
    {
        // Add a default directional light
        var light = new SynthGen.Scene.SceneObject("Directional Light");
        light.Transform.Rotation = new Vector3(-45f, -30f, 0f);
        light.AddComponent(new SynthGen.Scene.Components.LightComponent
        {
            LightType = SynthGen.Scene.Components.LightType.Directional,
            Color = new Vector3(1.0f, 0.98f, 0.95f),
            Intensity = 1.2f
        });
        _scene.AddObject(light);

        // Add a default camera
        var cam = new SynthGen.Scene.Camera();
        cam.Name = "Main Camera";
        cam.Transform.Position = new Vector3(0, 2, 8);
        cam.WeatherType = 0;      // Sun
        cam.WeatherIntensity = 0f;
        cam.FogDensity = 0f;
        cam.FogColor = new Vector3(0.7f, 0.75f, 0.8f);
        _scene.ActiveCamera = cam;
        _scene.AddObject(cam);

        // Add a default cube so the user sees something
        var cube = new SynthGen.Scene.SceneObject("Default Cube");
        var mr = new SynthGen.Scene.Components.MeshRendererComponent();
        mr.Mesh = SynthGen.Rendering.Mesh.CreateCube(_gl);
        cube.AddComponent(mr);
        
        // ADDED: Add label for picking support
        var label = new SynthGen.Scene.Components.LabelComponent
        {
            ClassName = "cube",
            ClassID = 1,
            SegmentationColor = new Vector3(1, 0.5f, 0) // Distinct orange for the default cube
        };
        cube.AddComponent(label);
        
        _scene.AddObject(cube);
    }

    private void OnUpdate(double dt)
    {
        var now = DateTime.UtcNow;
        _deltaTime = (float)(now - _lastFrame).TotalSeconds;
        _totalTime += _deltaTime;
        _lastFrame = now;

        // Auto-save logic
        if (!_captureManager.IsGenerating)
        {
            _autosaveTimer += _deltaTime;
            if (_autosaveTimer >= AutosaveInterval)
            {
                _autosaveTimer = 0f;
                PerformAutosave();
            }
        }

        _inputManager.Update();


        // Update ocean
        _ocean.Update(_totalTime);

        // Update buoyancy
        _buoyancy.Update(_scene, _deltaTime);

        // Update capture pipeline if generating
        _captureManager.Update(_deltaTime, _totalTime);

        // Poll training process output
        _trainingManager.Update();

        // Update all randomizers
        foreach (var r in _uiManager.AllRandomizers)
        {
            r.OnUpdate(_scene, _deltaTime);
        }

        // Update Animations
        foreach (var obj in _scene.Objects)
        {
            var anim = obj.GetComponent<AnimationPlayerComponent>();
            if (anim != null) anim.Update(_deltaTime);
        }

        // Update bone-bound keypoints to follow skeleton animation
        UpdateBoneKeypointPositions();
    }

    private void OnRender(double dt)
    {
        _imguiController.Update((float)dt);

        // Update renderer with HDRI settings from randomizer
        var hdriRandomizer = _uiManager.AllRandomizers.OfType<Randomizers.HDRIRandomizer>().FirstOrDefault();
        if (hdriRandomizer != null && hdriRandomizer.Enabled && !string.IsNullOrEmpty(hdriRandomizer.CurrentHDRI))
        {
            _renderer.HdriTextureID = _assetManager.LoadTexture(hdriRandomizer.CurrentHDRI);
            _renderer.HdriStrength = hdriRandomizer.CurrentStrength;
        }
        else
        {
            _renderer.HdriTextureID = 0;
        }

        // Pass selection to renderer for highlight
        _renderer.SelectedObjects = _scene.SelectedObjects;

        // Render 3D scene to framebuffers
        _renderer.RenderScene(_scene, _ocean, _totalTime);

        // Render UI
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
        _gl.ClearColor(0.1f, 0.1f, 0.12f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _uiManager.Render();

        _imguiController.Render();
        
        // Clear per-frame input state
        _inputManager.EndFrame();
    }

    private void OnResize(Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
    }

    private void SetWindowIcon()
    {
        try
        {
            // Look for icon next to the executable
            var exeDir = AppContext.BaseDirectory;
            var iconPath = Path.Combine(exeDir, "SyntGen.png");
            
            // Fallback: look in working directory or project root
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(Directory.GetCurrentDirectory(), "SyntGen.png");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(exeDir, "..", "..", "..", "..", "..", "SyntGen.png");
            
            if (File.Exists(iconPath))
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(iconPath);
                
                // Resize to 32x32 for window icon
                image.Mutate(x => x.Resize(32, 32));
                
                var pixels = new byte[32 * 32 * 4];
                image.CopyPixelDataTo(pixels);
                
                var rawImage = new RawImage(32, 32, pixels);
                _window.SetWindowIcon(ref rawImage);
                Console.WriteLine($"[SynthGen] Window icon set from: {iconPath}");
            }
            else
            {
                Console.WriteLine("[SynthGen] SyntGen.png not found, using default icon.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynthGen] Failed to set window icon: {ex.Message}");
        }
    }

    private void OnClosing()
    {
        _renderer?.Dispose();
        _imguiController?.Dispose();
        _input?.Dispose();
        _gl?.Dispose();
        Console.WriteLine("[SynthGen] Shutdown complete.");
    }

    private void CheckForRecovery()
    {
        if (File.Exists(_autosavePath))
        {
            _uiManager.ShowRecoveryPrompt(_autosavePath);
        }
    }

    public void PerformAutosave()
    {
        try
        {
            SynthGen.Scene.SceneSerializer.Save(this, _uiManager, _autosavePath);
            _uiManager.AddLog("[System] Periodic autosave complete.");
        }
        catch (Exception ex)
        {
            _uiManager.AddLog($"[Error] Autosave failed: {ex.Message}");
        }
    }

    public void ClearAutosave()
    {
        if (File.Exists(_autosavePath))
            File.Delete(_autosavePath);
    }

    /// <summary>
    /// Updates all bone-bound keypoint nodes to follow their skeleton bones each frame.
    /// Must apply the animation clip to the skeleton FIRST to get up-to-date bone transforms.
    /// </summary>
    private void UpdateBoneKeypointPositions()
    {
        // Group keypoints by root parent for efficiency
        var rootsProcessed = new HashSet<Scene.SceneObject>();

        foreach (var obj in _scene.Objects)
        {
            var kp = obj.GetComponent<KeypointComponent>();
            if (kp == null || string.IsNullOrEmpty(kp.BoundBoneName)) continue;

            // Walk up to find the root parent
            var root = obj;
            while (root.Parent != null) root = root.Parent;

            // Find the skinned mesh + skeleton
            var (skinnedObj, mr) = FindFirstSkinnedMeshInHierarchy(root);
            if (skinnedObj == null || mr?.Mesh?.Skeleton == null) continue;

            var skeleton = mr.Mesh.Skeleton;

            // Apply animation to skeleton ONCE per root (ensures bone transforms are current)
            if (rootsProcessed.Add(root))
            {
                var anim = skinnedObj.GetComponent<AnimationPlayerComponent>();
                if (anim != null && mr.Mesh.Clips.Count > 0)
                {
                    int clipIdx = anim.CurrentClipIndex % mr.Mesh.Clips.Count;
                    mr.Mesh.Clips[clipIdx].Apply(skeleton, anim.PlaybackTime);
                }
            }

            // Now read the bone's updated transform
            if (!skeleton.BonesByName.TryGetValue(kp.BoundBoneName, out var bone)) continue;

            // bone.GlobalTransform = animated joint position in skeleton root space
            // GlobalInverseTransform = undo root node transform → model space
            // objectWorldMatrix = model space → world space
            var boneInModel = bone.GlobalTransform * skeleton.GlobalInverseTransform;
            var objectWorldMatrix = skinnedObj.GetWorldMatrix();
            var jointWorld = boneInModel * objectWorldMatrix;

            // Always keep the keypoint locked to the bone using its local offset.
            // (We handle manual movement by updating the BoneOffset itself in UIManager)
            obj.Transform.Position = Vector3.Transform(kp.BoneOffset, jointWorld);
        }
    }

    private static (Scene.SceneObject?, MeshRendererComponent?) FindFirstSkinnedMeshInHierarchy(Scene.SceneObject obj)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr?.Mesh != null && mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null)
            return (obj, mr);
        foreach (var child in obj.Children)
        {
            var result = FindFirstSkinnedMeshInHierarchy(child);
            if (result.Item1 != null) return result;
        }
        return (null, null);
    }
}
