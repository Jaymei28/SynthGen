using Silk.NET.OpenGL;
using System.Numerics;
using System.Collections.Generic;

namespace SynthGen.Rendering;

/// <summary>
/// GPU mesh: VAO + VBO + EBO. Vertex layout: pos(3), normal(3), uv(2).
/// </summary>
public class Mesh : IDisposable
{
    private readonly GL _gl;
    public uint VAO { get; private set; }
    public uint VBO { get; private set; }
    public uint EBO { get; private set; }
    public uint BoneIDVBO { get; private set; }
    public uint WeightVBO { get; private set; }
    public bool HasSkinning { get; private set; }
    public uint IndexCount { get; private set; }
    public uint VertexCount { get; private set; }
    public string SourcePath { get; set; } = "";
    public Vector3 BoundingBoxMin { get; set; }
    public Vector3 BoundingBoxMax { get; set; }
    public string? SuggestedTexturePath { get; set; }
    public List<Matrix4x4> BoneOffsets { get; private set; } = new();
    public Skeleton? Skeleton { get; set; }
    public List<SkeletalAnimationClip> Clips { get; set; } = new();

    public Mesh(GL gl) { _gl = gl; }

    public unsafe void Upload(float[] vertices, uint[] indices, int[]? boneIDs = null, float[]? weights = null, List<Matrix4x4>? offsets = null, Skeleton? skeleton = null, List<SkeletalAnimationClip>? clips = null)
    {
        if (offsets != null) BoneOffsets = offsets;
        if (skeleton != null) Skeleton = skeleton;
        if (clips != null) Clips = clips;
        VAO = _gl.GenVertexArray();
        VBO = _gl.GenBuffer();
        EBO = _gl.GenBuffer();

        _gl.BindVertexArray(VAO);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
        fixed (float* ptr = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
        fixed (uint* ptr = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ptr, BufferUsageARB.StaticDraw);

        uint stride = 11 * sizeof(float); // pos(3) + normal(3) + tangent(3) + uv(2)

        // Position (0)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

        // Normal (1)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));

        // UV (2)
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));

        // Tangent (3)
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // --- Skeletal Animation Data ---
        if (boneIDs != null && weights != null)
        {
            HasSkinning = true;
            BoneIDVBO = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, BoneIDVBO);
            fixed (int* p = boneIDs)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(boneIDs.Length * sizeof(int)), p, BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribIPointer(4, 4, VertexAttribIType.Int, 4 * sizeof(int), (void*)0);

            WeightVBO = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, WeightVBO);
            fixed (float* p = weights)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(weights.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        }

        _gl.BindVertexArray(0);
        IndexCount = (uint)indices.Length;
        VertexCount = (uint)vertices.Length / 11;
 
        // --- Calculate Bounding Box ---
        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        for (int i = 0; i < vertices.Length; i += 11)
        {
            min.X = MathF.Min(min.X, vertices[i]);
            min.Y = MathF.Min(min.Y, vertices[i+1]);
            min.Z = MathF.Min(min.Z, vertices[i+2]);
            max.X = MathF.Max(max.X, vertices[i]);
            max.Y = MathF.Max(max.Y, vertices[i+1]);
            max.Z = MathF.Max(max.Z, vertices[i+2]);
        }
        BoundingBoxMin = min;
        BoundingBoxMax = max;
    }

    public void Draw()
    {
        _gl.BindVertexArray(VAO);
        unsafe { _gl.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, null); }
        _gl.BindVertexArray(0);
    }

    public unsafe void Normalize()
    {
        if (VertexCount == 0) return;

        float[] verts = new float[VertexCount * 11];
        fixed(float* p = verts)
        {
             _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
             _gl.GetBufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(verts.Length * sizeof(float)), p);
        }

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        for (int i = 0; i < verts.Length; i += 11)
        {
            min.X = MathF.Min(min.X, verts[i]);
            min.Y = MathF.Min(min.Y, verts[i+1]);
            min.Z = MathF.Min(min.Z, verts[i+2]);
            max.X = MathF.Max(max.X, verts[i]);
            max.Y = MathF.Max(max.Y, verts[i+1]);
            max.Z = MathF.Max(max.Z, verts[i+2]);
        }

        Vector3 center = (min + max) / 2f;
        Vector3 size = max - min;
        float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (maxDim < 0.0001f) maxDim = 1.0f;
        float scale = 1.0f / maxDim;

        for (int i = 0; i < verts.Length; i += 11)
        {
            verts[i] = (verts[i] - center.X) * scale;
            verts[i + 1] = (verts[i + 1] - min.Y) * scale; // Origin at bottom
            verts[i + 2] = (verts[i + 2] - center.Z) * scale;
        }
        BoundingBoxMin = new Vector3((min.X - center.X) * scale, 0, (min.Z - center.Z) * scale);
        BoundingBoxMax = new Vector3((max.X - center.X) * scale, (max.Y - min.Y) * scale, (max.Z - center.Z) * scale);

        // Re-upload
        fixed (float* p = verts)
        {
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(verts.Length * sizeof(float)), p);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(VAO);
        _gl.DeleteBuffer(VBO);
        _gl.DeleteBuffer(EBO);
        if (BoneIDVBO != 0) _gl.DeleteBuffer(BoneIDVBO);
        if (WeightVBO != 0) _gl.DeleteBuffer(WeightVBO);
    }

    // ── Procedural primitives ──
    public static Mesh CreateCube(GL gl)
    {
        var mesh = new Mesh(gl);
        // pos(3), normal(3), tangent(3), uv(2) = 11 floats
        float[] v = {
            // Front (N: 0,0,1  T: 1,0,0)
            -0.5f,-0.5f, 0.5f,  0,0,1,  1,0,0,  0,0,
             0.5f,-0.5f, 0.5f,  0,0,1,  1,0,0,  1,0,
             0.5f, 0.5f, 0.5f,  0,0,1,  1,0,0,  1,1,
            -0.5f, 0.5f, 0.5f,  0,0,1,  1,0,0,  0,1,
            // Back (N: 0,0,-1 T: -1,0,0)
             0.5f,-0.5f,-0.5f,  0,0,-1, -1,0,0, 0,0,
            -0.5f,-0.5f,-0.5f,  0,0,-1, -1,0,0, 1,0,
            -0.5f, 0.5f,-0.5f,  0,0,-1, -1,0,0, 1,1,
             0.5f, 0.5f,-0.5f,  0,0,-1, -1,0,0, 0,1,
            // Top (N: 0,1,0  T: 1,0,0)
            -0.5f, 0.5f, 0.5f,  0,1,0,  1,0,0,  0,0,
             0.5f, 0.5f, 0.5f,  0,1,0,  1,0,0,  1,0,
             0.5f, 0.5f,-0.5f,  0,1,0,  1,0,0,  1,1,
            -0.5f, 0.5f,-0.5f,  0,1,0,  1,0,0,  0,1,
            // Bottom (N: 0,-1,0 T: -1,0,0)
            -0.5f,-0.5f,-0.5f,  0,-1,0, -1,0,0, 0,0,
             0.5f,-0.5f,-0.5f,  0,-1,0, -1,0,0, 1,0,
             0.5f,-0.5f, 0.5f,  0,-1,0, -1,0,0, 1,1,
            -0.5f,-0.5f, 0.5f,  0,-1,0, -1,0,0, 0,1,
            // Right (N: 1,0,0  T: 0,0,-1)
             0.5f,-0.5f, 0.5f,  1,0,0,  0,0,-1, 0,0,
             0.5f,-0.5f,-0.5f,  1,0,0,  0,0,-1, 1,0,
             0.5f, 0.5f,-0.5f,  1,0,0,  0,0,-1, 1,1,
             0.5f, 0.5f, 0.5f,  1,0,0,  0,0,-1, 0,1,
            // Left (N: -1,0,0 T: 0,0,1)
            -0.5f,-0.5f,-0.5f, -1,0,0,  0,0,1,  0,0,
            -0.5f,-0.5f, 0.5f, -1,0,0,  0,0,1,  1,0,
            -0.5f, 0.5f, 0.5f, -1,0,0,  0,0,1,  1,1,
            -0.5f, 0.5f,-0.5f, -1,0,0,  0,0,1,  0,1,
        };
        uint[] idx = {
            0,1,2, 2,3,0,       4,5,6, 6,7,4,
            8,9,10, 10,11,8,    12,13,14, 14,15,12,
            16,17,18, 18,19,16, 20,21,22, 22,23,20,
        };
        mesh.Upload(v, idx);
        mesh.SourcePath = "cube";
        return mesh;
    }

    public static Mesh CreateSphere(GL gl, int stacks = 20, int slices = 20)
    {
        var mesh = new Mesh(gl);
        var verts = new List<float>();
        var inds = new List<uint>();

        for (int i = 0; i <= stacks; i++)
        {
            float phi = MathF.PI * i / stacks;
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2 * MathF.PI * j / slices;
                float x = MathF.Sin(phi) * MathF.Cos(theta);
                float y = MathF.Cos(phi);
                float z = MathF.Sin(phi) * MathF.Sin(theta);
                float u = (float)j / slices;
                float v2 = (float)i / stacks;

                // Tangent is perpendicular to Normal and Up? No, along theta.
                Vector3 tangent = new Vector3(-MathF.Sin(theta), 0, MathF.Cos(theta));
                if (tangent.LengthSquared() < 0.001f) tangent = Vector3.UnitX;

                verts.AddRange(new[] { x * 0.5f, y * 0.5f, z * 0.5f, x, y, z, tangent.X, tangent.Y, tangent.Z, u, v2 });
            }
        }
        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                uint a = (uint)(i * (slices + 1) + j);
                uint b = a + (uint)slices + 1;
                inds.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
        }
        mesh.Upload(verts.ToArray(), inds.ToArray());
        mesh.SourcePath = "sphere";
        return mesh;
    }

    public static Mesh CreatePlane(GL gl, int subdivisions = 50, float size = 100f)
    {
        var mesh = new Mesh(gl);
        var verts = new List<float>();
        var inds = new List<uint>();
        float half = size / 2f;
        float step = size / subdivisions;

        for (int z = 0; z <= subdivisions; z++)
        {
            for (int x = 0; x <= subdivisions; x++)
            {
                float px = -half + x * step;
                float pz = -half + z * step;
                float u = (float)x / subdivisions;
                float v = (float)z / subdivisions;
                // Pos(3), Norm(0,1,0), Tang(1,0,0), UV(2)
                verts.AddRange(new[] { px, 0f, pz, 0f, 1f, 0f, 1f, 0f, 0f, u, v });
            }
        }
        for (int z = 0; z < subdivisions; z++)
        {
            for (int x = 0; x < subdivisions; x++)
            {
                uint a = (uint)(z * (subdivisions + 1) + x);
                uint b = a + (uint)subdivisions + 1;
                inds.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
        }
        mesh.Upload(verts.ToArray(), inds.ToArray());
        mesh.SourcePath = "plane";
        return mesh;
    }
}
