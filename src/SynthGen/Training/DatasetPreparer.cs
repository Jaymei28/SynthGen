using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    public static string Prepare(string outputDir, float trainSplit, Action<string>? log = null)
    {
        string rgbDir = Path.Combine(outputDir, "rgb");
        string labelsDir = Path.Combine(outputDir, "labels");

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
            if (!File.Exists(destImg))
                File.Copy(imgFile, destImg, true);

            // Copy label if exists
            if (File.Exists(lblFile))
            {
                string destLbl = Path.Combine(destLblDir, $"{baseName}.txt");
                if (!File.Exists(destLbl))
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
            writer.WriteLine($"nc: {classNames.Count}");
            writer.Write("names: [");
            writer.Write(string.Join(", ", classNames.OrderBy(kv => kv.Key).Select(kv => $"'{kv.Value}'")));
            writer.WriteLine("]");
        }

        log?.Invoke($"[DataPrep] ✅ data.yaml written → {yamlPath}");
        return yamlPath;
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
