using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SynthGen.Rendering;
using SynthGen.Scene;
using SynthGen.Scene.Components;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;

namespace SynthGen.Capture;

/// <summary>
/// Orchestrates dataset capture: iterates frames, applies randomizers, captures images + annotations.
/// </summary>
public class CaptureManager
{
    private readonly GL _gl;
    private readonly Renderer _renderer;
    private readonly SceneGraph _scene;
    private readonly Physics.OceanSimulation _ocean;

    public bool IsGenerating { get; private set; }
    public int TotalFrames { get; set; } = 100;
    public int CompletedFrames { get; private set; }
    public float Progress => TotalFrames > 0 ? (float)CompletedFrames / TotalFrames : 0;

    /// <summary>Set to true for one frame when generation completes. UI reads and clears it.</summary>
    public bool GenerationJustCompleted { get; set; }
    public int LastImageCount { get; private set; }

    public string OutputDirectory { get; set; } = "output";
    public Annotation.AnnotationMode Mode { get; set; } = Annotation.AnnotationMode.BoundingBox;
    public bool ExportYOLO { get; set; } = true;
    public bool ExportCOCO { get; set; } = true;
    public bool CaptureRGB { get; set; } = true;
    public bool CaptureSeg { get; set; } = true;
    public bool CaptureDepth { get; set; } = true;

    // Animation capture settings
    public bool AnimatedCapture { get; set; } = false;
    public int SubFramesPerIteration { get; set; } = 10;
    public float AnimationDuration { get; set; } = 2.0f;

    // Keypoint pose estimation settings
    public bool ExportKeypointPose { get; set; } = false; // Disabled by default
    /// <summary>Bone-to-keypoint mapping. Key = COCO keypoint index (0-16), Value = skeleton bone name.</summary>
    public Dictionary<int, string> KeypointBoneMap { get; set; } = new();

    // Event for logging
    public event Action<string>? OnLog;

    private Annotation.COCOExporter? _cocoExporter;
    private bool _pendingCapture;
    private int _totalCapturedImages;

    // Randomizer list — set externally by UI
    public List<Randomizers.RandomizerBase> ActiveRandomizers { get; set; } = new();
    public int RandomSeed { get; set; } = 42;

    public CaptureManager(GL gl, Renderer renderer, SceneGraph scene, Physics.OceanSimulation ocean)
    {
        _gl = gl;
        _renderer = renderer;
        _scene = scene;
        _ocean = ocean;
    }

    public void StartGeneration(int numFrames)
    {
        TotalFrames = numFrames;
        CompletedFrames = 0;
        IsGenerating = true;
        _pendingCapture = true;
        _totalCapturedImages = 0;
        _renderer.ShowSceneUI = false;

        // Create output directories
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "rgb"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "seg"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "depth"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "labels"));
        if (ExportKeypointPose)
            Directory.CreateDirectory(Path.Combine(OutputDirectory, "keypoint_labels"));

        _cocoExporter = new Annotation.COCOExporter();

        // Register categories from scene labels
        foreach (var obj in _scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label != null)
                _cocoExporter.AddCategory(label.ClassID, label.ClassName, ExportKeypointPose);
        }

        // Auto-map bones to keypoints if no mapping exists yet
        if (ExportKeypointPose && KeypointBoneMap.Count == 0)
        {
            AutoMapKeypointsFromScene();
        }

        OnLog?.Invoke($"[Capture] Starting generation: {numFrames} frames → {OutputDirectory}");
    }

    public void StopGeneration()
    {
        if (IsGenerating)
        {
            FinalizeGeneration();
            OnLog?.Invoke("[Capture] Generation stopped by user.");
        }
        _renderer.ShowSceneUI = true;
        IsGenerating = false;
    }

    public void Update(float dt, float totalTime)
    {
        if (!IsGenerating || !_pendingCapture) return;

        if (CompletedFrames >= TotalFrames)
        {
            FinalizeGeneration();
            _renderer.ShowSceneUI = true;
            IsGenerating = false;
            LastImageCount = _totalCapturedImages;
            GenerationJustCompleted = true;
            OnLog?.Invoke($"[Capture] Generation complete! {TotalFrames} iterations, {_totalCapturedImages} images saved.");
            return;
        }

        // Apply randomizers for this frame
        var rng = new Random(RandomSeed + CompletedFrames);
        foreach (var randomizer in ActiveRandomizers)
        {
            if (randomizer.Enabled)
                randomizer.Randomize(_scene, rng);
        }

        if (AnimatedCapture)
        {
            // Animated mode: capture multiple sub-frames per iteration
            int subFrames = Math.Max(1, SubFramesPerIteration);
            float stepTime = AnimationDuration / subFrames;

            for (int sf = 0; sf < subFrames; sf++)
            {
                float animTime = sf * stepTime;

                // Advance all animation players to this time
                foreach (var obj in _scene.Objects)
                {
                    var anim = obj.GetComponent<Scene.Components.AnimationPlayerComponent>();
                    if (anim != null)
                    {
                        anim.PlaybackTime = animTime;
                    }
                }

                // Re-render the scene at this animation pose
                // (The renderer will pick up updated bone poses from the animation player)
                _renderer.RenderScene(_scene, _ocean, totalTime + animTime);
                
                CaptureFrame(_totalCapturedImages);
                _totalCapturedImages++;
            }
        }
        else
        {
            // Standard mode: single snapshot per iteration
            _renderer.RenderScene(_scene, _ocean, totalTime);
            
            CaptureFrame(_totalCapturedImages);
            _totalCapturedImages++;
        }

        CompletedFrames++;
    }

    private void CaptureFrame(int frameIndex)
    {
        string frameName = $"frame_{frameIndex:D5}";

        // Save RGB
        if (CaptureRGB)
        {
            var pixels = _renderer.RGBFramebuffer.ReadPixels();
            SaveImage(pixels, _renderer.Width, _renderer.Height,
                Path.Combine(OutputDirectory, "rgb", $"{frameName}.png"));
        }

        // Save Segmentation
        if (CaptureSeg)
        {
            var pixels = _renderer.SegFramebuffer.ReadPixels();
            SaveImage(pixels, _renderer.Width, _renderer.Height,
                Path.Combine(OutputDirectory, "seg", $"{frameName}.png"));

            // Generate annotations from seg mask
            GenerateAnnotations(pixels, frameIndex, frameName);
        }

        // Save Depth
        if (CaptureDepth)
        {
            var pixels = _renderer.DepthFramebuffer.ReadPixels();
            SaveImage(pixels, _renderer.Width, _renderer.Height,
                Path.Combine(OutputDirectory, "depth", $"{frameName}.png"));
        }

        OnLog?.Invoke($"[Frame {frameIndex}] Captured RGB/Seg/Depth");

        // Generate keypoint annotations (Only if mode is set to Keypoints)
        if (Mode == Annotation.AnnotationMode.Keypoints && ExportKeypointPose)
        {
            GenerateKeypointAnnotations(frameIndex, frameName);
        }
    }

    private void GenerateAnnotations(byte[] segPixels, int frameIndex, string frameName)
    {
        // Build color map from scene labels
        var colorMap = new Dictionary<uint, (int classId, int instanceId, string className)>();
        foreach (var obj in _scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label == null) continue;

            var c = label.SegmentationColor;
            uint key = ((uint)(c.X * 255) << 16) | ((uint)(c.Y * 255) << 8) | (uint)(c.Z * 255);
            colorMap[key] = (label.ClassID, label.InstanceID, label.ClassName);
        }

        var bboxes = Annotation.BoundingBoxAnnotator.GenerateFromMask(
            segPixels, _renderer.Width, _renderer.Height, colorMap);

        // YOLO export
        if (ExportYOLO)
        {
            Annotation.YOLOExporter.ExportFrame(
                Path.Combine(OutputDirectory, "labels", $"{frameName}.txt"),
                bboxes, _renderer.Width, _renderer.Height);
        }

        // COCO export
        if (ExportCOCO && _cocoExporter != null)
        {
            _cocoExporter.AddFrame(frameIndex, $"rgb/{frameName}.png",
                _renderer.Width, _renderer.Height, bboxes);
        }

        if (bboxes.Count > 0)
            OnLog?.Invoke($"[Frame {frameIndex}] {bboxes.Count} objects annotated");
    }

    private void FinalizeGeneration()
    {
        if (ExportCOCO && _cocoExporter != null)
        {
            _cocoExporter.Save(Path.Combine(OutputDirectory, "annotations.json"));
            OnLog?.Invoke("[Capture] COCO annotations saved.");
        }

        if (ExportKeypointPose && Mode == Annotation.AnnotationMode.Keypoints)
        {
            OnLog?.Invoke($"[Capture] Keypoint pose labels saved to keypoint_labels/");
        }
    }

    /// <summary>
    /// Generates 2D keypoint annotations by projecting 3D bone positions through the camera.
    /// </summary>
    private void GenerateKeypointAnnotations(int frameIndex, string frameName)
    {
        var cam = _scene.ActiveCamera;
        if (cam == null) return;

        var view = cam.GetViewMatrix();
        float aspect = (float)_renderer.Width / Math.Max(1, _renderer.Height);
        var proj = cam.GetProjectionMatrix(aspect);

        var annotations = Annotation.KeypointAnnotator.GenerateKeypoints(
            _scene, view, proj, _renderer.Width, _renderer.Height, 
            KeypointBoneMap.Count > 0 ? KeypointBoneMap : null);

        if (annotations.Count > 0)
        {
            // YOLOv8-Pose format export
            Annotation.YOLOPoseExporter.ExportFrame(
                Path.Combine(OutputDirectory, "keypoint_labels", $"{frameName}.txt"),
                annotations, _renderer.Width, _renderer.Height);

            // COCO keypoint JSON
            if (ExportCOCO && _cocoExporter != null)
            {
                _cocoExporter.AddKeypointFrame(frameIndex, $"rgb/{frameName}.png",
                    _renderer.Width, _renderer.Height, annotations);
            }

            OnLog?.Invoke($"[Frame {frameIndex}] {annotations.Count} person(s), keypoints exported");
        }
    }

    /// <summary>
    /// Auto-discovers skeleton bones in the scene and maps them to COCO keypoints.
    /// </summary>
    private void AutoMapKeypointsFromScene()
    {
        foreach (var obj in _scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr?.Mesh == null || !mr.Mesh.HasSkinning || mr.Mesh.Skeleton == null) continue;

            KeypointBoneMap = Annotation.KeypointRegistry.AutoMapBones(
                mr.Mesh.Skeleton.BonesByName.Keys);
            
            if (KeypointBoneMap.Count > 0)
            {
                OnLog?.Invoke($"[Keypoints] Auto-mapped {KeypointBoneMap.Count}/17 bones to COCO keypoints");
                foreach (var kvp in KeypointBoneMap)
                    OnLog?.Invoke($"  [{kvp.Key}] {Annotation.KeypointRegistry.KeypointNames[kvp.Key]} → {kvp.Value}");
                return;
            }
        }
    }

    private void SaveImage(byte[] pixels, int width, int height, string path)
    {
        // Clone pixels so the caller can reuse the buffer or move on
        byte[] pixelsCopy = (byte[])pixels.Clone();

        Task.Run(() =>
        {
            try
            {
                // Flip vertically (OpenGL reads bottom-up)
                var flipped = new byte[pixelsCopy.Length];
                int rowSize = width * 4;
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(pixelsCopy, (height - 1 - y) * rowSize, flipped, y * rowSize, rowSize);
                }

                using var img = Image.LoadPixelData<Rgba32>(flipped, width, height);
                img.SaveAsPng(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Capture] Error saving image to {path}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Capture a single frame for preview without full generation.
    /// </summary>
    public void CaptureSingleFrame()
    {
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "rgb"));
        var pixels = _renderer.RGBFramebuffer.ReadPixels();
        string path = Path.Combine(OutputDirectory, $"preview_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        SaveImage(pixels, _renderer.Width, _renderer.Height, path);
        OnLog?.Invoke($"[Capture] Preview saved: {path}");
    }
}
