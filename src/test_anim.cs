using System;
using Assimp;

namespace TestAnim {
    class Program {
        static void Main(string[] args) {
            string path = @"c:\Users\Administrator\.gemini\antigravity\scratch\SynthGen\src\SynthGen\Assets\models\boat.fbx";
            try {
                using var importer = new AssimpContext();
                var scene = importer.ImportFile(path);
                if (scene != null) {
                    Console.WriteLine($"Model: {path}");
                    Console.WriteLine($"Has Animations: {scene.HasAnimations}");
                    if (scene.HasAnimations) {
                        for (int i = 0; i < scene.AnimationCount; i++) {
                            Console.WriteLine($"Anim {i}: {scene.Animations[i].Name} Duration: {scene.Animations[i].DurationInTicks}");
                        }
                    }
                    Console.WriteLine($"Has Meshes: {scene.HasMeshes}");
                    for(int i=0; i<scene.MeshCount; i++) {
                        Console.WriteLine($"Mesh {i}: {scene.Meshes[i].Name} Has Bones: {scene.Meshes[i].HasBones}");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
