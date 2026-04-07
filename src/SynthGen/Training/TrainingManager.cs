using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SynthGen.Training;

/// <summary>
/// Manages YOLOv8 model training by launching and monitoring a Python process.
/// </summary>
public class TrainingManager
{
    // ── Training Status ──
    public enum TrainStatus { Idle, Preparing, Training, Complete, Failed }
    public TrainStatus Status { get; private set; } = TrainStatus.Idle;

    // ── Hyperparameters ──
    public int ModelSizeIndex { get; set; } = 0; // 0=nano, 1=small, 2=medium, 3=large, 4=xlarge
    public int TaskIndex { get; set; } = 0; // 0=detect, 1=segment, 2=pose
    public int Epochs { get; set; } = 50;
    public int BatchSize { get; set; } = 16;
    public int ImgSize { get; set; } = 640;
    public float LearningRate { get; set; } = 0.01f;
    public int Workers { get; set; } = 4;
    public int DeviceIndex { get; set; } = 0; // 0=GPU(0), 1=CPU
    public int Patience { get; set; } = 50;
    public float TrainValSplit { get; set; } = 0.8f;
    public bool ResumeTraining { get; set; } = false;
    public string ProjectDir { get; set; } = "runs";

    // ── Read-only State ──
    public string CurrentEpochInfo { get; private set; } = "";
    public string BestWeightsPath { get; private set; } = "";
    public bool IsTraining => Status == TrainStatus.Training || Status == TrainStatus.Preparing;

    // ── Internal ──
    private Process? _trainProcess;
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _lock = new();

    public event Action<string>? OnLog;

    public static readonly string[] ModelSizes = { "yolov8n", "yolov8s", "yolov8m", "yolov8l", "yolov8x" };
    public static readonly string[] TaskTypes = { "detect", "segment", "pose" };
    public static readonly string[] DeviceOptions = { "0", "cpu" };

    public string SelectedModelName => ModelSizes[ModelSizeIndex];
    public string SelectedTask => TaskTypes[TaskIndex];
    public string SelectedDevice => DeviceOptions[DeviceIndex];

    /// <summary>
    /// Start the training pipeline: prepare dataset, then launch YOLO.
    /// </summary>
    public void StartTraining(string datasetDir)
    {
        if (IsTraining) return;

        Status = TrainStatus.Preparing;
        _outputBuffer.Clear();
        CurrentEpochInfo = "";
        BestWeightsPath = "";

        try
        {
            // 1. Prepare dataset (train/val split + data.yaml)
            string dataYamlPath = DatasetPreparer.Prepare(datasetDir, TrainValSplit, OnLog);
            if (string.IsNullOrEmpty(dataYamlPath))
            {
                Status = TrainStatus.Failed;
                OnLog?.Invoke("[Training] ❌ Dataset preparation failed.");
                return;
            }

            OnLog?.Invoke($"[Training] Dataset prepared → {dataYamlPath}");

            // 2. Build the YOLO CLI command
            string model = $"{SelectedModelName}.pt";
            if (SelectedTask == "segment") model = $"{SelectedModelName}-seg.pt";
            else if (SelectedTask == "pose") model = $"{SelectedModelName}-pose.pt";

            string resumeArg = ResumeTraining ? "resume=True" : "";
            string projectPath = Path.GetFullPath(Path.Combine(datasetDir, ProjectDir));

            string args = $"yolo {SelectedTask} train " +
                         $"data=\"{Path.GetFullPath(dataYamlPath)}\" " +
                         $"model={model} " +
                         $"epochs={Epochs} " +
                         $"batch={BatchSize} " +
                         $"imgsz={ImgSize} " +
                         $"lr0={LearningRate:F6} " +
                         $"workers={Workers} " +
                         $"device={SelectedDevice} " +
                         $"patience={Patience} " +
                         $"project=\"{projectPath}\" " +
                         $"exist_ok=True " +
                         (ResumeTraining ? "resume=True " : "") +
                         $"verbose=True";

            OnLog?.Invoke($"[Training] 🚀 Launching: {args}");

            // 3. Launch the process
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {args}",
                WorkingDirectory = Path.GetFullPath(datasetDir),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _trainProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _trainProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_lock) { _outputBuffer.AppendLine(e.Data); }
                ParseTrainingOutput(e.Data);
            };

            _trainProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_lock) { _outputBuffer.AppendLine(e.Data); }
                // YOLO often prints progress to stderr
                ParseTrainingOutput(e.Data);
            };

            _trainProcess.Exited += (_, _) =>
            {
                int exitCode = _trainProcess?.ExitCode ?? -1;
                if (exitCode == 0)
                {
                    Status = TrainStatus.Complete;
                    // Try to find best.pt
                    string bestPath = Path.Combine(projectPath, "train", "weights", "best.pt");
                    if (File.Exists(bestPath)) BestWeightsPath = bestPath;
                    OnLog?.Invoke($"[Training] ✅ Training complete! Weights: {BestWeightsPath}");
                }
                else
                {
                    Status = TrainStatus.Failed;
                    OnLog?.Invoke($"[Training] ❌ Training failed with exit code {exitCode}");
                }
            };

            _trainProcess.Start();
            _trainProcess.BeginOutputReadLine();
            _trainProcess.BeginErrorReadLine();

            Status = TrainStatus.Training;
        }
        catch (Exception ex)
        {
            Status = TrainStatus.Failed;
            OnLog?.Invoke($"[Training] ❌ Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Kill the running training process.
    /// </summary>
    public void StopTraining()
    {
        if (_trainProcess != null && !_trainProcess.HasExited)
        {
            try
            {
                _trainProcess.Kill(true);
                OnLog?.Invoke("[Training] ⏹ Training stopped by user.");
            }
            catch { }
        }
        Status = TrainStatus.Idle;
    }

    /// <summary>
    /// Call every frame to flush buffered output to the console.
    /// </summary>
    public void Update()
    {
        string? lines = null;
        lock (_lock)
        {
            if (_outputBuffer.Length > 0)
            {
                lines = _outputBuffer.ToString();
                _outputBuffer.Clear();
            }
        }

        if (lines != null)
        {
            foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    OnLog?.Invoke($"[YOLO] {trimmed}");
            }
        }
    }

    private void ParseTrainingOutput(string line)
    {
        // Try to extract epoch progress like "  Epoch    GPU_mem   box_loss..."
        if (line.Contains("Epoch") || line.Contains("epoch"))
        {
            CurrentEpochInfo = line.Trim();
        }
        // Detect completion markers
        else if (line.Contains("Results saved to") || line.Contains("best.pt"))
        {
            CurrentEpochInfo = line.Trim();
        }
    }
}
