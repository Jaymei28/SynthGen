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
    private SceneSnapshot? _snapshot;

    // Randomizer list — set externally by UI
    public List<Randomizers.RandomizerBase> ActiveRandomizers { get; set; } = new();
    public int RandomSeed { get; set; } = 42;

    /// <summary>Set by UIManager so the snapshot can save/restore HDRI state.</summary>
    public Randomizers.HDRIRandomizer? HdriRandomizer { get; set; }

    public CaptureManager(GL gl, Renderer renderer, SceneGraph scene, Physics.OceanSimulation ocean)
    {
        _gl = gl;
        _renderer = renderer;
        _scene = scene;
        _ocean = ocean;
    }

    public void StartGeneration(int numFrames)
    {
        // Snapshot scene state BEFORE any randomizers run so we can restore later
        _snapshot = SceneSnapshot.Capture(_scene, HdriRandomizer);

        TotalFrames = numFrames;
        CompletedFrames = 0;
        IsGenerating = true;
        _pendingCapture = true;
        _totalCapturedImages = 0;
        _renderer.ShowSceneUI = false;
        _renderer.IsDatasetGenerationMode = true;

        // Create output directories
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "rgb"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "seg"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "depth"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "labels"));
        if (ExportKeypointPose)
            Directory.CreateDirectory(Path.Combine(OutputDirectory, "keypoint_labels"));

        _cocoExporter = new Annotation.COCOExporter();

        // Ensure unique segmentation colors for all labeled objects 
        var usedColors = new HashSet<uint>();
        var colorRng = new Random(RandomSeed);
        int fixedColors = 0;
        int labeledFound = 0;
        
        foreach (var obj in _scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label == null) continue;
            labeledFound++;

            uint colorKey = ((uint)(label.SegmentationColor.X * 255) << 16) |
                             ((uint)(label.SegmentationColor.Y * 255) << 8) |
                              (uint)(label.SegmentationColor.Z * 255);

            if (usedColors.Contains(colorKey) || colorKey == 0)
            {
                Vector3 newColor;
                do {
                    newColor = new Vector3(colorRng.Next(5, 250) / 255f, colorRng.Next(5, 250) / 255f, colorRng.Next(5, 250) / 255f);
                    colorKey = ((uint)(newColor.X * 255) << 16) | ((uint)(newColor.Y * 255) << 8) | (uint)(newColor.Z * 255);
                } while (usedColors.Contains(colorKey));
                
                label.SegmentationColor = newColor;
                fixedColors++;
            }
            usedColors.Add(colorKey);
        }
        OnLog?.Invoke($"[Capture] Found {labeledFound} labeled objects. Assigned {usedColors.Count} unique tracking colors.");

        // Register categories from scene labels
        foreach (var obj in _scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label != null)
            {
                var std = ExportKeypointPose ? Annotation.KeypointRegistry.GetStandard(obj.PoseStandard) : null;
                _cocoExporter.AddCategory(label.ClassID, label.ClassName, std);
            }
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
            _snapshot?.Restore(_scene, HdriRandomizer);
            _snapshot = null;
            OnLog?.Invoke("[Capture] Generation stopped by user. Scene restored.");
        }
        _renderer.ShowSceneUI = true;
        _renderer.IsDatasetGenerationMode = false;
        IsGenerating = false;
    }

    public void Update(float dt, float totalTime)
    {
        if (!IsGenerating || !_pendingCapture) return;

        if (CompletedFrames >= TotalFrames)
        {
            FinalizeGeneration();
            _snapshot?.Restore(_scene, HdriRandomizer);
            _snapshot = null;
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

        float timeStep = 0.1f;
        if (AnimatedCapture)
        {
            // Animated mode: capture multiple sub-frames per iteration
            int subFrames = Math.Max(1, SubFramesPerIteration);
            float stepTime = AnimationDuration / subFrames;

            for (int sf = 0; sf < subFrames; sf++)
            {
                // Cumulative time across iterations + sub-frames
                float animTime = (CompletedFrames * timeStep) + (sf * stepTime);

                // Advance all animation players to this time
                foreach (var obj in _scene.Objects)
                {
                    var anim = obj.GetComponent<Scene.Components.AnimationPlayerComponent>();
                    if (anim != null && anim.IsPlaying)
                    {
                        anim.PlaybackTime = animTime % Math.Max(0.1f, anim.ClipDurationSeconds);
                    }
                }

                // Update bone-bound keypoint positions BEFORE capture
                // so they reflect the current animation frame
                if (ExportKeypointPose)
                    UpdateBoneKeypointPositions();

                // Re-render the scene at this animation pose
                _renderer.RenderScene(_scene, _ocean, totalTime + animTime);
                
                CaptureFrame(_totalCapturedImages);
                _totalCapturedImages++;
            }
        }
        else
        {
            foreach (var obj in _scene.Objects)
            {
                var anim = obj.GetComponent<Scene.Components.AnimationPlayerComponent>();
                if (anim != null && anim.IsPlaying)
                {
                    anim.PlaybackTime = (CompletedFrames * timeStep) % Math.Max(0.1f, anim.ClipDurationSeconds);
                }
            }

            // Update bone-bound keypoint positions BEFORE capture
            // so they reflect the current frame's bone transforms
            if (ExportKeypointPose)
                UpdateBoneKeypointPositions();

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

        // Generate keypoint annotations whenever keypoint export is enabled
        if (ExportKeypointPose)
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

        if (ExportKeypointPose)
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
            (KeypointBoneMap.Count > 0 ? KeypointBoneMap : null),
            cam.FisheyeStrength, cam.FieldOfView);

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
        else
        {
            // Write empty file so user knows the frame was processed
            File.WriteAllText(
                Path.Combine(OutputDirectory, "keypoint_labels", $"{frameName}.txt"), "");
            OnLog?.Invoke($"[Frame {frameIndex}] WARNING: No keypoints detected — check bone bindings and camera view");
        }
    }

    /// <summary>
    /// Auto-discovers skeleton bones in the scene and maps them to the appropriate keypoint standard.
    /// </summary>
    private void AutoMapKeypointsFromScene()
    {
        foreach (var obj in _scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr?.Mesh == null || !mr.Mesh.HasSkinning || mr.Mesh.Skeleton == null) continue;

            var std = Annotation.KeypointRegistry.GetStandard(obj.PoseStandard);
            KeypointBoneMap = Annotation.KeypointRegistry.AutoMapBones(std, mr.Mesh.Skeleton.BonesByName.Keys);
            
            if (KeypointBoneMap.Count > 0)
            {
                OnLog?.Invoke($"[Keypoints] Auto-mapped {KeypointBoneMap.Count}/{std.Keypoints.Count} bones to {std.Name} keypoints");
                foreach (var kvp in KeypointBoneMap)
                {
                    if (std.Keypoints.TryGetValue(kvp.Key, out var name))
                        OnLog?.Invoke($"  [{kvp.Key}] {name} → {kvp.Value}");
                }
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

                using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(flipped, width, height);
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

    /// <summary>
    /// Updates all bone-bound keypoint node positions to follow their skeleton bones.
    /// This mirrors the logic in Application.UpdateBoneKeypointPositions() but is called
    /// explicitly during generation to ensure keypoint positions are up-to-date BEFORE capture.
    /// </summary>
    private void UpdateBoneKeypointPositions()
    {
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

            var boneInModel = bone.GlobalTransform * skeleton.GlobalInverseTransform;
            var objectWorldMatrix = skinnedObj.GetWorldMatrix();
            var jointWorld = boneInModel * objectWorldMatrix;

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
