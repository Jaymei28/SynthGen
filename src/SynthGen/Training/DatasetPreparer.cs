using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SynthGen.Annotation;

namespace SynthGen.Training;

/// <summary>
/// Restructures the SynthGen output directory into the YOLO-expected train/val format
/// and generates the data.yaml configuration file.
/// </summary>
public static class DatasetPreparer
{
    /// <summary>
    /// Prepares the dataset for YOLO training:
    /// 1. Discovers class names from label files.
    /// 2. Splits images+labels into train/val sets.
    /// 3. Generates data.yaml.
    /// Returns the path to data.yaml, or null on failure.
    /// </summary>
    public static string Prepare(string outputDir, float trainSplit, string task = "detect", Action<string>? log = null)
    {
        string rgbDir = Path.Combine(outputDir, "rgb");
        // For pose tasks, use the specialized keypoint labels folder
        string labelsSubDir = task == "pose" ? "keypoint_labels" : "labels";
        string labelsDir = Path.Combine(outputDir, labelsSubDir);

        if (!Directory.Exists(rgbDir))
        {
            log?.Invoke($"[DataPrep] ❌ RGB directory not found: {rgbDir}");
            return "";
        }
        if (!Directory.Exists(labelsDir))
        {
            log?.Invoke($"[DataPrep] ❌ Labels directory not found: {labelsDir}");
            return "";
        }

        // Get matched image+label pairs
        var imageFiles = Directory.GetFiles(rgbDir, "*.png")
            .OrderBy(f => f)
            .ToList();

        if (imageFiles.Count == 0)
        {
            log?.Invoke("[DataPrep] ❌ No images found in rgb/");
            return "";
        }

        // Discover class names from COCO annotations or label files
        var classNames = DiscoverClassNames(outputDir, labelsDir);
        log?.Invoke($"[DataPrep] Found {imageFiles.Count} images, {classNames.Count} classes: [{string.Join(", ", classNames.Values)}]");

        // Create train/val directory structure
        string trainImgDir = Path.Combine(outputDir, "train", "images");
        string trainLblDir = Path.Combine(outputDir, "train", "labels");
        string valImgDir = Path.Combine(outputDir, "val", "images");
        string valLblDir = Path.Combine(outputDir, "val", "labels");

        Directory.CreateDirectory(trainImgDir);
        Directory.CreateDirectory(trainLblDir);
        Directory.CreateDirectory(valImgDir);
        Directory.CreateDirectory(valLblDir);

        // Split
        int trainCount = (int)(imageFiles.Count * trainSplit);
        trainCount = Math.Max(1, Math.Min(trainCount, imageFiles.Count - 1));

        int trainCopied = 0, valCopied = 0;
        for (int i = 0; i < imageFiles.Count; i++)
        {
            string imgFile = imageFiles[i];
            string baseName = Path.GetFileNameWithoutExtension(imgFile);
            string lblFile = Path.Combine(labelsDir, $"{baseName}.txt");

            bool isTrain = i < trainCount;
            string destImgDir = isTrain ? trainImgDir : valImgDir;
            string destLblDir = isTrain ? trainLblDir : valLblDir;

            // Copy image
            string destImg = Path.Combine(destImgDir, Path.GetFileName(imgFile));
            File.Copy(imgFile, destImg, true);

            // Copy label if exists
            if (File.Exists(lblFile))
            {
                string destLbl = Path.Combine(destLblDir, $"{baseName}.txt");
                File.Copy(lblFile, destLbl, true);
            }

            if (isTrain) trainCopied++; else valCopied++;
        }

        log?.Invoke($"[DataPrep] Split: {trainCopied} train / {valCopied} val");

        // Generate data.yaml
        string yamlPath = Path.Combine(outputDir, "data.yaml");
        using (var writer = new StreamWriter(yamlPath))
        {
            writer.WriteLine($"path: {Path.GetFullPath(outputDir)}");
            writer.WriteLine($"train: train/images");
            writer.WriteLine($"val: val/images");
            writer.WriteLine();
            int maxId = 0;
            if (classNames.Count > 0)
                maxId = classNames.Keys.Max();
            
            writer.WriteLine($"nc: {maxId + 1}");
            
            var paddedNames = new List<string>();
            for (int i = 0; i <= maxId; i++)
            {
                if (classNames.TryGetValue(i, out string? name))
                    paddedNames.Add($"'{name}'");
                else
                    paddedNames.Add($"'unknown_{i}'");
            }
            
            writer.Write("names: [");
            writer.Write(string.Join(", ", paddedNames));
            writer.WriteLine("]");

            // YOLOv8-Pose requires kpt_shape: [num_keypoints, dim]
            if (task == "pose")
            {
                int kptCount = DiscoverKeypointCount(outputDir);
                writer.WriteLine($"kpt_shape: [{kptCount}, 3]");

                // Attempt to match a registered standard to provide flip_idx and kpt_names
                var standards = new[] { KeypointRegistry.COCO, KeypointRegistry.Fisheye, KeypointRegistry.Halpe26 };
                var match = standards.FirstOrDefault(s => s.Keypoints.Count == kptCount);
                if (match != null)
                {
                    if (match.FlipIndices.Length > 0)
                        writer.WriteLine($"flip_idx: [{string.Join(", ", match.FlipIndices)}]");
                    
                    writer.WriteLine();
                    writer.WriteLine("kpt_names:");
                    writer.WriteLine("  0:"); // Writing for the first class (person)
                    foreach (var kvp in match.Keypoints.OrderBy(k => k.Key))
                    {
                        // Convert "Left Eye" to "left_eye"
                        string formattedName = kvp.Value.ToLower().Replace(" ", "_");
                        writer.WriteLine($"    - {formattedName}");
                    }

                    log?.Invoke($"[DataPrep] Pose task: Matched {match.Name} standard. Added flip_idx and kpt_names to data.yaml");
                }

                log?.Invoke($"[DataPrep] Pose task detected. Added kpt_shape: [{kptCount}, 3] to data.yaml");
            }
        }

        log?.Invoke($"[DataPrep] ✅ data.yaml written → {yamlPath}");
        return yamlPath;
    }

    private static int DiscoverKeypointCount(string outputDir)
    {
        // Try to get from COCO annotations if they were exported
        string cocoPath = Path.Combine(outputDir, "annotations.json");
        if (File.Exists(cocoPath))
        {
            try
            {
                string json = File.ReadAllText(cocoPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("categories", out var cats) && cats.GetArrayLength() > 0)
                {
                    var cat = cats[0];
                    if (cat.TryGetProperty("keypoints", out var kpts))
                        return kpts.GetArrayLength();
                }
            }
            catch { }
        }

        // Fallback: Check if we have keypoint labels and try to count parts
        string kpLabels = Path.Combine(outputDir, "keypoint_labels");
        if (Directory.Exists(kpLabels))
        {
            string[] files = Directory.GetFiles(kpLabels, "*.txt");
            if (files.Length > 0)
            {
                string line = File.ReadLines(files[0]).FirstOrDefault("");
                if (!string.IsNullOrEmpty(line))
                {
                    // YOLO Pose line: class cx cy w h [x y v] * N
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 5)
                    {
                        return (parts.Length - 5) / 3;
                    }
                }
            }
        }

        return 17; // Final fallback to COCO standard
    }

    /// <summary>
    /// Discovers class names from COCO annotations.json or by scanning label files.
    /// </summary>
    private static SortedDictionary<int, string> DiscoverClassNames(string outputDir, string labelsDir)
    {
        var classes = new SortedDictionary<int, string>();

        // Try COCO annotations first
        string cocoPath = Path.Combine(outputDir, "annotations.json");
        if (File.Exists(cocoPath))
        {
            try
            {
                string json = File.ReadAllText(cocoPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("categories", out var cats))
                {
                    foreach (var cat in cats.EnumerateArray())
                    {
                        int id = cat.GetProperty("id").GetInt32();
                        string name = cat.GetProperty("name").GetString() ?? $"class_{id}";
                        classes[id] = name;
                    }
                }
            }
            catch { }
        }

        // Fallback: scan label files for unique class IDs
        if (classes.Count == 0)
        {
            var classIds = new HashSet<int>();
            foreach (var lblFile in Directory.GetFiles(labelsDir, "*.txt"))
            {
                foreach (var line in File.ReadAllLines(lblFile))
                {
                    var parts = line.Trim().Split(' ');
                    if (parts.Length >= 5 && int.TryParse(parts[0], out int classId))
                        classIds.Add(classId);
                }
            }
            foreach (var id in classIds.OrderBy(x => x))
                classes[id] = $"class_{id}";
        }

        // Ensure at least one class
        if (classes.Count == 0)
            classes[0] = "object";

        return classes;
    }
}
