using Silk.NET.OpenGL;
using System.Numerics;
using SynthGen.Scene;
using SynthGen.Scene.Components;
using SynthGen.Physics;

namespace SynthGen.Rendering;

public enum ViewMode { RGB, Segmentation, Depth, All }

/// <summary>
/// Multi-pass renderer: RGB, Segmentation, Depth, Ocean, Grid, and Post-Processing.
/// </summary>
public class Renderer : IDisposable
{
    private readonly GL _gl;

    // Shaders
    private Shader _pbrShader = null!;
    private Shader _segShader = null!;
    private Shader _depthShader = null!;
    private Shader _gridShader = null!;
    private Shader _oceanShader = null!;
    private Shader _oceanDepthShader = null!;
    private Shader _skyboxShader = null!;

    // Post-process shaders
    private Shader _bloomShader = null!;
    private Shader _fogShader = null!;
    private Shader _fisheyeShader = null!;
    private Shader _blurShader = null!;
    private Shader _noiseShader = null!;
    private Shader _exposureShader = null!;
    private Shader _whiteBalShader = null!;
    private Shader _ssaoShader = null!;
    private Shader _weatherShader = null!;
    private Shader _outlineMaskShader = null!;
    private Shader _outlineCompositeShader = null!;

    // FBOs
    private Framebuffer _rgbFbo = null!;
    private Framebuffer _segFbo = null!;
    private Framebuffer _depthFbo = null!;
    private Framebuffer _postFboA = null!;  // ping-pong for post
    private Framebuffer _postFboB = null!;
    private Framebuffer _selectionMaskFbo = null!; // Selection outline mask

    // Grid mesh
    private Mesh? _gridMesh;
    private Mesh? _oceanMesh;
    private Mesh? _skyboxMesh;

    // Screen quad for post-processing
    private uint _quadVAO, _quadVBO;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ViewMode CurrentViewMode { get; set; } = ViewMode.RGB;

    public uint RGBTexture => _rgbFbo.ColorTexture;
    public uint SegTexture => _segFbo.ColorTexture;
    public uint DepthTexture => _depthFbo.ColorTexture;
    public Framebuffer RGBFramebuffer => _rgbFbo;
    public Framebuffer SegFramebuffer => _segFbo;
    public Framebuffer DepthFramebuffer => _depthFbo;

    // HDRI support
    public uint HdriTextureID { get; set; } = 0;
    public float HdriStrength { get; set; } = 1.0f;
    public IEnumerable<SceneObject> SelectedObjects { get; set; } = Array.Empty<SceneObject>();

    public Renderer(GL gl, int width, int height)
    {
        _gl = gl;
        Width = width;
        Height = height;
        Initialize();
    }

    private void Initialize()
    {
        // Compile all shaders
        _pbrShader = new Shader(_gl, ShaderSources.PBR_VERT, ShaderSources.PBR_FRAG);
        _segShader = new Shader(_gl, ShaderSources.SEG_VERT, ShaderSources.SEG_FRAG);
        _depthShader = new Shader(_gl, ShaderSources.DEPTH_VERT, ShaderSources.DEPTH_FRAG);
        _gridShader = new Shader(_gl, ShaderSources.GRID_VERT, ShaderSources.GRID_FRAG);
        _oceanShader = new Shader(_gl, ShaderSources.OCEAN_VERT, ShaderSources.OCEAN_FRAG);
        _oceanDepthShader = new Shader(_gl, ShaderSources.OCEAN_DEPTH_VERT, ShaderSources.OCEAN_DEPTH_FRAG);
        _skyboxShader = new Shader(_gl, ShaderSources.SKYBOX_VERT, ShaderSources.SKYBOX_FRAG);

        _bloomShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.BLOOM_FRAG);
        _fogShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.FOG_FRAG);
        _fisheyeShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.FISHEYE_FRAG);
        _blurShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.BLUR_FRAG);
        _noiseShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.NOISE_FRAG);
        _exposureShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.EXPOSURE_FRAG);
        _whiteBalShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.WHITEBALANCE_FRAG);
        _ssaoShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.SSAO_FRAG);
        _weatherShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.WEATHER_FRAG);
        _outlineMaskShader = new Shader(_gl, ShaderSources.OUTLINE_MASK_VERT, ShaderSources.OUTLINE_MASK_FRAG);
        _outlineCompositeShader = new Shader(_gl, ShaderSources.SCREEN_VERT, ShaderSources.OUTLINE_COMPOSITE_FRAG);

        // Create FBOs
        _rgbFbo = new Framebuffer(_gl, Width, Height);
        _segFbo = new Framebuffer(_gl, Width, Height);
        _depthFbo = new Framebuffer(_gl, Width, Height);
        _postFboA = new Framebuffer(_gl, Width, Height);
        _postFboB = new Framebuffer(_gl, Width, Height);
        _selectionMaskFbo = new Framebuffer(_gl, Width, Height);

        // Build grid
        BuildGridMesh();
        _skyboxMesh = Mesh.CreateCube(_gl);
        BuildScreenQuad();

        Console.WriteLine("[Renderer] Initialized with all shaders and FBOs.");
    }

    public void ResizeFBOs(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == Width && h == Height)) return;
        Width = w; Height = h;
        _rgbFbo.Resize(w, h);
        _segFbo.Resize(w, h);
        _depthFbo.Resize(w, h);
        _postFboA.Resize(w, h);
        _postFboB.Resize(w, h);
        _selectionMaskFbo.Resize(w, h);
    }

    public void RenderScene(SceneGraph scene, OceanSimulation ocean, float time)
    {
        var cam = scene.ActiveCamera;
        if (cam == null) return;

        float aspect = Width / (float)Math.Max(Height, 1);
        var view = cam.GetViewMatrix();
        var proj = cam.GetProjectionMatrix(aspect);

        _gl.Viewport(0, 0, (uint)Width, (uint)Height);

        // ── RGB Pass ──────────────────────────────────────────────────────
        _rgbFbo.Bind();
        _gl.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);

        // Draw objects (opaque first)
        DrawObjectsPBR(scene, view, proj, cam, ocean);

        // ── Skybox Pass ───────────────────────────────────────────────────
        if (HdriTextureID != 0 && _skyboxMesh != null)
        {
            _gl.DepthMask(false);
            _gl.DepthFunc(DepthFunction.Lequal); 
            _gl.Disable(EnableCap.CullFace); 
            
            _skyboxShader.Use();
            _skyboxShader.SetMat4("uView", view);
            _skyboxShader.SetMat4("uProjection", proj);
            _skyboxShader.SetFloat("uStrength", HdriStrength);
            _skyboxShader.SetFloat("uLightning", cam.LightningIntensity);
            
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, HdriTextureID);
            _skyboxShader.SetInt("uHdri", 0);
            
            _skyboxMesh.Draw();
            
            _gl.Enable(EnableCap.CullFace);
            _gl.DepthFunc(DepthFunction.Less); 
            _gl.DepthMask(true);
        }

        // ── Transparent Pass ──────────────────────────────────────────────
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false); // Disable depth writing for transparents
        _gl.Disable(EnableCap.CullFace); // See both sides of waves
        
        // Draw ocean
        if (ocean.Config.Enabled && _oceanMesh != null)
        {
            DrawOcean(view, proj, cam, scene, ocean, time);
        }

        // Draw grid
        DrawGrid(view, proj);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);

        _rgbFbo.Unbind();

        // ── Selection Outline (screen-space, after RGB is complete) ───────
        if (SelectedObjects.Any())
            DrawSelectionHighlight(SelectedObjects, view, proj);

        // ── Segmentation Pass ─────────────────────────────────────────────
        _segFbo.Bind();
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        DrawObjectsSeg(scene, view, proj);
        _segFbo.Unbind();

        // ── Depth Pass ────────────────────────────────────────────────────
        _depthFbo.Bind();
        _gl.ClearColor(1.0f, 1.0f, 1.0f, 1.0f); // Clear to white (Far)
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        DrawObjectsDepth(scene, view, proj, cam);
        
        // ADDED: Draw ocean to depth buffer
        if (ocean.Config.Enabled && _oceanMesh != null)
        {
            DrawOceanDepth(view, proj, cam, ocean, time);
        }
        
        _depthFbo.Unbind();

        // ── Post-Processing ───────────────────────────────────────────────
        ApplyPostProcessing(cam, ocean, time);
    }

    public unsafe Vector3 PickSegmentationColor(int x, int y)
    {
        _segFbo.Bind();
        byte[] pixels = new byte[4];
        fixed (byte* ptr = pixels)
        {
            int px = Math.Clamp(x, 0, Width - 1);
            int py = Math.Clamp(Height - 1 - y, 0, Height - 1);
            _gl.ReadPixels(px, py, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
        _segFbo.Unbind();
        return new Vector3(pixels[0] / 255f, pixels[1] / 255f, pixels[2] / 255f);
    }

    private void DrawObjectsPBR(SceneGraph scene, Matrix4x4 view, Matrix4x4 proj, Camera cam, OceanSimulation ocean)
    {
        _pbrShader.Use();
        _pbrShader.SetMat4("uView", view);
        _pbrShader.SetMat4("uProjection", proj);
        _pbrShader.SetVec3("uCameraPos", cam.Transform.Position);
        
        // Link ambient light to ocean color for better blending
        Vector3 oceanTint = ocean.Config.ScatteringColor * 0.4f;
        _pbrShader.SetVec3("uAmbient", new Vector3(0.08f, 0.08f, 0.1f) + oceanTint);

        // Find directional light
        Vector3 lightDir = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.5f));
        Vector3 lightColor = Vector3.One;
        float lightIntensity = 1.0f;

        foreach (var obj in scene.Objects)
        {
            var light = obj.GetComponent<LightComponent>();
            if (light != null && light.LightType == LightType.Directional)
            {
                float yawRad = obj.Transform.Rotation.Y * MathF.PI / 180f;
                float pitchRad = obj.Transform.Rotation.X * MathF.PI / 180f;
                lightDir = new Vector3(
                    MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                    MathF.Sin(pitchRad),
                    MathF.Cos(pitchRad) * MathF.Cos(yawRad)
                );
                lightColor = light.Color;
                lightIntensity = light.Intensity;
                break;
            }
        }

        _pbrShader.SetVec3("uLightDir", lightDir);
        _pbrShader.SetVec3("uLightColor", lightColor);
        _pbrShader.SetFloat("uLightIntensity", lightIntensity);

        foreach (var obj in scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr?.Mesh == null || !mr.Visible) continue;

            var model = obj.GetWorldMatrix();
            _pbrShader.SetMat4("uModel", model);

            // Skeletal Animation
            bool useSkinning = mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null && mr.Mesh.Skeleton.Bones.Count > 0;
            _pbrShader.SetInt("uHasSkinning", useSkinning ? 1 : 0);
            if (useSkinning)
            {
                var anim = obj.GetComponent<AnimationPlayerComponent>();
                if (anim != null && mr.Mesh.Clips.Count > 0)
                {
                    int clipIdx = anim.CurrentClipIndex % mr.Mesh.Clips.Count;
                    mr.Mesh.Clips[clipIdx].Apply(mr.Mesh.Skeleton!, anim.PlaybackTime);
                }
                var matrices = mr.Mesh.Skeleton!.GetFinalMatrices();
                for (int m = 0; m < matrices.Length && m < 100; m++)
                    _pbrShader.SetMat4($"uBones[{m}]", matrices[m]);
            }

            // Normal matrix
            Matrix4x4.Invert(model, out var inv);
            var normMat = Matrix4x4.Transpose(inv);
            _pbrShader.SetMat3("uNormalMatrix", normMat); 

            _pbrShader.SetVec4("uBaseColor", mr.Material.BaseColor);
            _pbrShader.SetFloat("uSmoothness", mr.Material.Smoothness);
            _pbrShader.SetFloat("uMetallic", mr.Material.Metallic);
            _pbrShader.SetFloat("uNormalScale", mr.Material.NormalScale);
            _pbrShader.SetFloat("uColorIntensity", mr.Material.ColorIntensity);
            _pbrShader.SetVec3("uEmissiveColor", mr.Material.EmissiveColor);
            _pbrShader.SetFloat("uEmissiveIntensity", mr.Material.EmissiveIntensity);

            if (mr.Material.AlbedoTextureID > 0)
            {
                _pbrShader.SetInt("uHasTexture", 1);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, mr.Material.AlbedoTextureID);
                _pbrShader.SetInt("uAlbedoTex", 0);
            }
            else
            {
                _pbrShader.SetInt("uHasTexture", 0);
            }

            if (mr.Material.NormalTextureID > 0)
            {
                _pbrShader.SetInt("uHasNormalMap", 1);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, mr.Material.NormalTextureID);
                _pbrShader.SetInt("uNormalTex", 1);
            }
            else
            {
                _pbrShader.SetInt("uHasNormalMap", 0);
            }

            mr.Mesh.Draw();
        }
    }

    private void DrawObjectsSeg(SceneGraph scene, Matrix4x4 view, Matrix4x4 proj)
    {
        _segShader.Use();
        _segShader.SetMat4("uView", view);
        _segShader.SetMat4("uProjection", proj);

        foreach (var obj in scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            var label = obj.GetComponent<LabelComponent>();
            if (mr?.Mesh == null || !mr.Visible || label == null) continue;

            // Use body part group color if assigned, otherwise fallback to label color
            Vector3 segColor = label.SegmentationColor;
            var groupColor = SynthGen.Scene.Components.BodyPartGroups.GetColor(obj.BodyPartGroup);
            if (groupColor.HasValue)
                segColor = groupColor.Value;

            _segShader.SetMat4("uModel", obj.GetWorldMatrix());
            _segShader.SetVec3("uSegColor", segColor);

            bool useSkinning = mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null;
            _segShader.SetInt("uHasSkinning", useSkinning ? 1 : 0);
            if (useSkinning)
            {
                var matrices = mr.Mesh.Skeleton!.GetFinalMatrices();
                for (int m = 0; m < matrices.Length && m < 100; m++)
                    _segShader.SetMat4($"uBones[{m}]", matrices[m]);
            }
            mr.Mesh.Draw();
        }
    }

    private void DrawObjectsDepth(SceneGraph scene, Matrix4x4 view, Matrix4x4 proj, Camera cam)
    {
        _depthShader.Use();
        _depthShader.SetMat4("uView", view);
        _depthShader.SetMat4("uProjection", proj);
        _depthShader.SetFloat("uNear", cam.NearPlane);
        _depthShader.SetFloat("uFar", cam.FarPlane);

        foreach (var obj in scene.Objects)
        {
            var mr = obj.GetComponent<MeshRendererComponent>();
            if (mr?.Mesh == null || !mr.Visible) continue;

            _depthShader.SetMat4("uModel", obj.GetWorldMatrix());

            bool useSkinning = mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null;
            _depthShader.SetInt("uHasSkinning", useSkinning ? 1 : 0);
            if (useSkinning)
            {
                var matrices = mr.Mesh.Skeleton!.GetFinalMatrices();
                for (int m = 0; m < matrices.Length && m < 100; m++)
                    _depthShader.SetMat4($"uBones[{m}]", matrices[m]);
            }
            mr.Mesh.Draw();
        }
    }

    private void DrawSelectionHighlight(IEnumerable<SceneObject> selectedObjects, Matrix4x4 view, Matrix4x4 proj)
    {
        // Collect all meshes to highlight (selected + all its children)
        var meshObjects = new List<(SceneObject obj, MeshRendererComponent mr)>();
        foreach (var selected in selectedObjects)
            CollectMeshObjects(selected, meshObjects);
        
        if (meshObjects.Count == 0) return;

        // === STEP 1: Render selection mask (white on black) ===
        _selectionMaskFbo.Bind();
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);

        _outlineMaskShader.Use();
        _outlineMaskShader.SetMat4("uView", view);
        _outlineMaskShader.SetMat4("uProjection", proj);

        foreach (var (obj, mr) in meshObjects)
        {
            if (mr.Mesh == null || !mr.Visible) continue;
            _outlineMaskShader.SetMat4("uModel", obj.GetWorldMatrix());

            // Apply skinning so outline follows animated pose
            bool useSkinning = mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null;
            _outlineMaskShader.SetInt("uHasSkinning", useSkinning ? 1 : 0);
            if (useSkinning)
            {
                var matrices = mr.Mesh.Skeleton!.GetFinalMatrices();
                for (int m = 0; m < matrices.Length && m < 100; m++)
                    _outlineMaskShader.SetMat4($"uBones[{m}]", matrices[m]);
            }
            mr.Mesh.Draw();
        }
        _selectionMaskFbo.Unbind();

        // === STEP 2: Edge detection composite onto RGB ===
        _postFboA.Bind();
        _gl.Disable(EnableCap.DepthTest);

        _outlineCompositeShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _rgbFbo.ColorTexture);
        _outlineCompositeShader.SetInt("uScene", 0);

        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _selectionMaskFbo.ColorTexture);
        _outlineCompositeShader.SetInt("uMask", 1);

        _outlineCompositeShader.SetVec2("uTexelSize", new Vector2(1.0f / Width, 1.0f / Height));
        _outlineCompositeShader.SetVec4("uOutlineColor", new Vector4(1.0f, 0.55f, 0.0f, 1.0f)); // Blender orange
        _outlineCompositeShader.SetFloat("uOutlineWidth", 2.0f); // 2 pixel wide outline

        DrawScreenQuadGeometry();
        _postFboA.Unbind();

        // Copy composited result back to RGB FBO
        _rgbFbo.Bind();
        _gl.Disable(EnableCap.DepthTest);
        DrawScreenQuad(_postFboA.ColorTexture, null);
        _rgbFbo.Unbind();

        _gl.Enable(EnableCap.DepthTest);
    }

    private void CollectMeshObjects(SceneObject obj, List<(SceneObject, MeshRendererComponent)> list)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr != null) list.Add((obj, mr));
        foreach (var child in obj.Children)
            CollectMeshObjects(child, list);
    }

    private void DrawGrid(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_gridMesh == null) return;
        _gl.Enable(EnableCap.Blend);
        _gridShader.Use();
        _gridShader.SetMat4("uView", view);
        _gridShader.SetMat4("uProjection", proj);
        _gridMesh.Draw();
    }

    private void DrawOcean(Matrix4x4 view, Matrix4x4 proj, Camera cam, SceneGraph scene, OceanSimulation ocean, float time)
    {
        if (_oceanMesh == null) return;
        _oceanShader.Use();
        _oceanShader.SetMat4("uView", view);
        _oceanShader.SetMat4("uProjection", proj);
        _oceanShader.SetFloat("uTime", time);
        _oceanShader.SetFloat("uLevel", ocean.Config.Level);
        _oceanShader.SetFloat("uWindSpeed", ocean.Config.LargeWindSpeed);
        _oceanShader.SetFloat("uWindDirection", ocean.Config.WindDirection);
        _oceanShader.SetFloat("uStormIntensity", ocean.Config.StormIntensity);
        _oceanShader.SetFloat("uSteepness", ocean.Config.LargeSteepness);
        _oceanShader.SetFloat("uChaos", ocean.Config.LargeChaos);
        _oceanShader.SetFloat("uTimeMultiplier", ocean.Config.TimeMultiplier);
        
        _oceanShader.SetVec3("uCameraPos", cam.Transform.Position);
        _oceanShader.SetVec3("uRefractionColor", ocean.Config.RefractionColor);
        _oceanShader.SetVec3("uScatteringColor", ocean.Config.ScatteringColor);
        _oceanShader.SetFloat("uFoamAmount", ocean.Config.FoamAmount);
        _oceanShader.SetFloat("uSparkleIntensity", ocean.Config.SparkleIntensity);
        float microMult = (cam.WeatherType == 1) ? 2.5f : 1.0f;
        _oceanShader.SetFloat("uMicroRipple", ocean.Config.MicroRippleStrength * microMult);
        _oceanShader.SetFloat("uReflectionSaturation", ocean.Config.ReflectionSaturation);
        
        _oceanShader.SetInt("uWeatherType", cam.WeatherType);
        _oceanShader.SetFloat("uWeatherIntensity", cam.WeatherIntensity);
        _oceanShader.SetFloat("uLightning", cam.LightningIntensity);

        // Get light from scene
        Vector3 ld = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.5f));
        Vector3 lc = Vector3.One; float li = 1f;
        foreach (var obj in scene.Objects)
        {
            var l = obj.GetComponent<LightComponent>();
            if (l != null && l.LightType == LightType.Directional) { lc = l.Color; li = l.Intensity; break; }
        }
        _oceanShader.SetVec3("uLightDir", ld);
        _oceanShader.SetVec3("uLightColor", lc);
        _oceanShader.SetFloat("uLightIntensity", li);

        // HDRI Reflection
        if (HdriTextureID != 0)
        {
            _oceanShader.SetInt("uHasHdri", 1);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, HdriTextureID);
            _oceanShader.SetInt("uHdri", 0);
            _oceanShader.SetFloat("uHdriStrength", HdriStrength);
        }
        else
        {
            _oceanShader.SetInt("uHasHdri", 0);
        }

        _oceanMesh.Draw();
    }

    private void DrawOceanDepth(Matrix4x4 view, Matrix4x4 proj, Camera cam, OceanSimulation ocean, float time)
    {
        if (_oceanMesh == null) return;
        _oceanDepthShader.Use();
        _oceanDepthShader.SetMat4("uView", view);
        _oceanDepthShader.SetMat4("uProjection", proj);
        _oceanDepthShader.SetFloat("uTime", time);
        _oceanDepthShader.SetFloat("uLevel", ocean.Config.Level);
        _oceanDepthShader.SetFloat("uWindSpeed", ocean.Config.LargeWindSpeed);
        _oceanDepthShader.SetFloat("uWindDirection", ocean.Config.WindDirection);
        _oceanDepthShader.SetFloat("uStormIntensity", ocean.Config.StormIntensity);
        _oceanDepthShader.SetFloat("uSteepness", ocean.Config.LargeSteepness);
        _oceanDepthShader.SetFloat("uChaos", ocean.Config.LargeChaos);
        _oceanDepthShader.SetFloat("uTimeMultiplier", ocean.Config.TimeMultiplier);
        _oceanDepthShader.SetFloat("uNear", cam.NearPlane);
        _oceanDepthShader.SetFloat("uFar", cam.FarPlane);
        _oceanMesh.Draw();
    }

    private void ApplyPostProcessing(Camera cam, OceanSimulation ocean, float time)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend); 
        _gl.Disable(EnableCap.CullFace); 
        _gl.ClearColor(0, 0, 0, 1);

        float aspect = Width / (float)Math.Max(Height, 1);
        var view = cam.GetViewMatrix();
        var proj = cam.GetProjectionMatrix(aspect);

        uint currentTex = _rgbFbo.ColorTexture;
        bool useA = true;

        // Chain post-process effects based on camera settings
        if (cam.SSAOIntensity > 0.01f)
            currentTex = ApplyPostEffect(_ssaoShader, currentTex, ref useA, s => {
                s.SetFloat("uRadius", cam.SSAORadius);
                s.SetFloat("uIntensity", cam.SSAOIntensity);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _depthFbo.ColorTexture);
                s.SetInt("uDepth", 1);
            });

        if (cam.FogDensity > 0.01f)
            currentTex = ApplyPostEffect(_fogShader, currentTex, ref useA, s => {
                s.SetFloat("uDensity", cam.FogDensity);
                s.SetVec3("uFogColor", cam.FogColor);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _depthFbo.ColorTexture);
                s.SetInt("uDepth", 1);
            });

        if (cam.WeatherIntensity > 0.01f)
            currentTex = ApplyPostEffect(_weatherShader, currentTex, ref useA, s => {
                s.SetFloat("uIntensity", cam.WeatherIntensity);
                s.SetFloat("uTime", time);
                s.SetInt("uType", cam.WeatherType);
                s.SetFloat("uLightning", cam.LightningIntensity);
                s.SetVec3("uCameraPos", cam.Transform.Position);
                
                // Pass wind parameters for tilt and movement
                s.SetFloat("uWindSpeed", ocean.Config.LargeWindSpeed);
                s.SetFloat("uWindDirection", ocean.Config.WindDirection);

                System.Numerics.Matrix4x4.Invert(view, out var invView);
                System.Numerics.Matrix4x4.Invert(proj, out var invProj);
                s.SetMat4("uInvView", invView);
                s.SetMat4("uInvProj", invProj);

                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _depthFbo.ColorTexture);
                s.SetInt("uDepth", 1);
            });

        if (cam.BloomIntensity > 0.01f)
            currentTex = ApplyPostEffect(_bloomShader, currentTex, ref useA, s => {
                s.SetFloat("uThreshold", cam.BloomThreshold);
                s.SetFloat("uIntensity", cam.BloomIntensity);
            });

        if (MathF.Abs(cam.Exposure - 1.0f) > 0.01f)
            currentTex = ApplyPostEffect(_exposureShader, currentTex, ref useA, s => {
                s.SetFloat("uExposure", cam.Exposure);
            });

        if (cam.FisheyeStrength > 0.01f)
            currentTex = ApplyPostEffect(_fisheyeShader, currentTex, ref useA, s => {
                s.SetFloat("uStrength", cam.FisheyeStrength);
            });

        if (cam.BlurRadius > 0.1f)
            currentTex = ApplyPostEffect(_blurShader, currentTex, ref useA, s => {
                s.SetFloat("uRadius", cam.BlurRadius);
            });

        if (cam.NoiseIntensity > 0.001f)
            currentTex = ApplyPostEffect(_noiseShader, currentTex, ref useA, s => {
                s.SetFloat("uIntensity", cam.NoiseIntensity);
                s.SetFloat("uTime", time);
                s.SetInt("uLarge", cam.NoiseLarge ? 1 : 0);
            });

        if (MathF.Abs(cam.WhiteBalanceTemperature - 6500f) > 100f || MathF.Abs(cam.WhiteBalanceTint) > 0.01f)
            currentTex = ApplyPostEffect(_whiteBalShader, currentTex, ref useA, s => {
                s.SetFloat("uTemperature", (cam.WhiteBalanceTemperature - 6500f) / 3000f);
                s.SetFloat("uTint", cam.WhiteBalanceTint);
            });

        // Copy final result back to _rgbFbo if post was applied
        if (currentTex != _rgbFbo.ColorTexture)
        {
            _rgbFbo.Bind();
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            DrawScreenQuad(currentTex, null);
            _rgbFbo.Unbind();
        }

        _gl.Enable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
    }

    private uint ApplyPostEffect(Shader shader, uint inputTex, ref bool useA, Action<Shader> setup)
    {
        var target = useA ? _postFboA : _postFboB;
        target.Bind();
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        shader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, inputTex);
        shader.SetInt("uScene", 0);
        setup(shader);

        DrawScreenQuadGeometry();

        target.Unbind();
        useA = !useA;
        return target.ColorTexture;
    }

    private void DrawScreenQuad(uint texture, Shader? shader)
    {
        shader?.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);

        // Use a simple passthrough if no shader
        if (shader == null)
        {
            _exposureShader.Use();
            _exposureShader.SetInt("uScene", 0);
            _exposureShader.SetFloat("uExposure", 1.0f);
        }

        DrawScreenQuadGeometry();
    }

    private unsafe void DrawScreenQuadGeometry()
    {
        _gl.BindVertexArray(_quadVAO);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.BindVertexArray(0);
    }

    private void BuildGridMesh()
    {
        _gridMesh = Mesh.CreatePlane(_gl, 100, 100f);
        _oceanMesh = Mesh.CreatePlane(_gl, 256, 1000f); // Higher res for smoother waves
    }

    private unsafe void BuildScreenQuad()
    {
        float[] quadVerts = {
            -1f, -1f,  1f, -1f,  1f, 1f,   // T1: Bottom-Left, Bottom-Right, Top-Right (CCW)
            -1f, -1f,  1f,  1f, -1f, 1f    // T2: Bottom-Left, Top-Right, Top-Left (CCW)
        };

        _quadVAO = _gl.GenVertexArray();
        _quadVBO = _gl.GenBuffer();
        _gl.BindVertexArray(_quadVAO);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVBO);
        fixed (float* ptr = quadVerts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), null);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _pbrShader?.Dispose();
        _segShader?.Dispose();
        _depthShader?.Dispose();
        _gridShader?.Dispose();
        _oceanShader?.Dispose();
        _oceanDepthShader?.Dispose();

        _bloomShader?.Dispose();
        _fogShader?.Dispose();
        _fisheyeShader?.Dispose();
        _blurShader?.Dispose();
        _noiseShader?.Dispose();
        _exposureShader?.Dispose();
        _whiteBalShader?.Dispose();
        _ssaoShader?.Dispose();
        _weatherShader?.Dispose();

        _rgbFbo?.Dispose();
        _segFbo?.Dispose();
        _depthFbo?.Dispose();
        _postFboA?.Dispose();
        _postFboB?.Dispose();

        _gridMesh?.Dispose();
        _oceanMesh?.Dispose();

        _gl.DeleteVertexArray(_quadVAO);
        _gl.DeleteBuffer(_quadVBO);
    }
}
