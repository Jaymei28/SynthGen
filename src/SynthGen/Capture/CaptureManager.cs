using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SynthGen.Rendering;
using SynthGen.Scene;
using SynthGen.Scene.Components;

namespace SynthGen.Capture;

/// <summary>
/// Orchestrates dataset capture: iterates frames, applies randomizers, captures images + annotations.
/// </summary>
public class CaptureManager
{
    private readonly GL _gl;
    private readonly Renderer _renderer;
    private readonly SceneGraph _scene;

    public bool IsGenerating { get; private set; }
    public int TotalFrames { get; set; } = 100;
    public int CompletedFrames { get; private set; }
    public float Progress => TotalFrames > 0 ? (float)CompletedFrames / TotalFrames : 0;

    public string OutputDirectory { get; set; } = "output";
    public Annotation.AnnotationMode Mode { get; set; } = Annotation.AnnotationMode.BoundingBox;
    public bool ExportYOLO { get; set; } = true;
    public bool ExportCOCO { get; set; } = true;
    public bool CaptureRGB { get; set; } = true;
    public bool CaptureSeg { get; set; } = true;
    public bool CaptureDepth { get; set; } = true;

    // Animation capture settings
    public bool AnimatedCapture { get; set; } = true;
    public int SubFramesPerIteration { get; set; } = 10;
    public float AnimationDuration { get; set; } = 2.0f;

    // Event for logging
    public event Action<string>? OnLog;

    private Annotation.COCOExporter? _cocoExporter;
    private bool _pendingCapture;
    private int _totalCapturedImages;

    // Randomizer list — set externally by UI
    public List<Randomizers.RandomizerBase> ActiveRandomizers { get; set; } = new();
    public int RandomSeed { get; set; } = 42;

    public CaptureManager(GL gl, Renderer renderer, SceneGraph scene)
    {
        _gl = gl;
        _renderer = renderer;
        _scene = scene;
    }

    public void StartGeneration(int numFrames)
    {
        TotalFrames = numFrames;
        CompletedFrames = 0;
        IsGenerating = true;
        _pendingCapture = true;
        _totalCapturedImages = 0;

        // Create output directories
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "rgb"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "seg"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "depth"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "labels"));

        _cocoExporter = new Annotation.COCOExporter();

        // Register categories from scene labels
        foreach (var obj in _scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label != null)
                _cocoExporter.AddCategory(label.ClassID, label.ClassName);
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
        IsGenerating = false;
    }

    public void Update(float dt)
    {
        if (!IsGenerating || !_pendingCapture) return;

        if (CompletedFrames >= TotalFrames)
        {
            FinalizeGeneration();
            IsGenerating = false;
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
                CaptureFrame(_totalCapturedImages);
                _totalCapturedImages++;
            }
        }
        else
        {
            // Standard mode: single snapshot per iteration
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

        OnLog?.Invoke($"[Frame {frameIndex}] {bboxes.Count} objects annotated");
    }

    private void FinalizeGeneration()
    {
        if (ExportCOCO && _cocoExporter != null)
        {
            _cocoExporter.Save(Path.Combine(OutputDirectory, "annotations.json"));
            OnLog?.Invoke("[Capture] COCO annotations saved.");
        }
    }

    private static void SaveImage(byte[] pixels, int width, int height, string path)
    {
        // Flip vertically (OpenGL reads bottom-up)
        var flipped = new byte[pixels.Length];
        int rowSize = width * 4;
        for (int y = 0; y < height; y++)
        {
            Array.Copy(pixels, (height - 1 - y) * rowSize, flipped, y * rowSize, rowSize);
        }

        using var img = Image.LoadPixelData<Rgba32>(flipped, width, height);
        img.SaveAsPng(path);
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
