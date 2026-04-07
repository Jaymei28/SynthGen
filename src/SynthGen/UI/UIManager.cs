using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SynthGen.App;
using SynthGen.Randomizers;
using SynthGen.Randomizers.ObjectRandomizers;
using SynthGen.Randomizers.CameraRandomizers;
using SynthGen.Randomizers.GlobalRandomizers;
using SynthGen.Scene;
using SynthGen.Scene.Components;
using SynthGen.Physics;
using SynthGen.Rendering;

namespace SynthGen.UI;

/// <summary>
/// Top-level ImGui manager: sets up docking, renders all panels.
/// </summary>
public class UIManager
{
    private readonly Application _app;

    // All randomizers
    private readonly List<RandomizerBase> _allRandomizers;
    public IReadOnlyList<RandomizerBase> AllRandomizers => _allRandomizers;

    // Console log
    private readonly List<string> _logs = new();

    // Viewport state
    private bool _viewportHovered;
    private Vector2 _viewportSize;
    private Vector2 _viewportScreenPos;

    // Manipulation state (Blender-style G, R, S)
    private enum ManipulationMode { None, Move, Rotate, Scale }
    private ManipulationMode _manipMode = ManipulationMode.None;
    private bool _isManipulating = false;
    private char _axisLock = '\0'; // 'X', 'Y', 'Z' or '\0'
    private Vector3 _initialTransformValue; // Initial Pos, Rot, or Scale
    private Vector2 _initialMousePos;
    private bool _startedByMouse = false;

    // Gizmo state
    private char _hoveredAxis = '\0';

    // Settings
    private Annotation.AnnotationMode _annotationMode = Annotation.AnnotationMode.BoundingBox;

    private bool _uiLocked = true;
    private float _preFocusDistance = -1f; // -1 = not focused
    private const float ToolbarHeight = 48f;

    public UIManager(Application app)
    {
        _app = app;

        // Create all randomizers
        _allRandomizers = new List<RandomizerBase>
        {
            // Object
            new PositionRandomizer { Enabled = true },
            new RotationRandomizer { Enabled = true },
            new ScaleRandomizer { Enabled = true },
            new TextureRandomizer(),
            new DepthScaleMapper(),
            // Camera
            new CameraPositionRandomizer(),
            new FisheyeRandomizer(),
            new FogRandomizer(),
            new BloomRandomizer(),
            new ExposureRandomizer(),
            new NoiseRandomizer(),
            new AmbientOcclusionRandomizer(),
            new WhiteBalanceRandomizer(),
            new BlurRandomizer(),
            // Global
            new WeatherRandomizer(),
            new LightingRandomizer { Enabled = true },
            // HDRI
            new HDRIRandomizer { Enabled = true },
        };

        RefreshHDRIs();

        // Wire capture manager
        _app.CaptureManager.ActiveRandomizers = _allRandomizers;
        _app.CaptureManager.OnLog += msg => _logs.Add(msg);

        AddLog("[SynthGen] UI initialized. Ready.");
    }

    public void Render()
    {
        // Global Shortcuts
        if (_app.Input.CtrlHeld && ImGui.IsKeyPressed(ImGuiKey.L))
        {
            _uiLocked = !_uiLocked;
            AddLog($"[UI] Layout {(_uiLocked ? "LOCKED" : "UNLOCKED")}");
        }

        // F = Focus on selected object (raw input, works from anywhere)
        if (_app.Input.WasKeyJustPressed(Silk.NET.Input.Key.F))
        {
            var sel = _app.Scene.SelectedObject;
            var cam = _app.Scene.ActiveCamera;
            if (sel != null && cam != null && !cam.IsFlyMode)
            {
                if (_preFocusDistance > 0)
                {
                    // Already focused — zoom back out
                    cam.OrbitDistance = _preFocusDistance;
                    _preFocusDistance = -1f;
                }
                else
                {
                    // Zoom in — save current distance first
                    _preFocusDistance = cam.OrbitDistance;
                    cam.OrbitTarget = sel.Transform.Position;
                    cam.OrbitDistance = MathF.Max(cam.OrbitDistance * 0.3f, 3f);
                }
            }
        }

        SetupDockspace();

        RenderMenuBar();
        RenderToolbar();
        RenderSceneHierarchy();
        RenderViewport();
        RenderInspector();
        RenderRandomizerPanel();
        RenderOceanSettings();
        RenderCapturePanel();
        RenderConsole();
    }

    // ═══ Dockspace ═══════════════════════════════════════════════════════════
    private void SetupDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        // Leave space at the top for the fixed toolbar
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(0, ToolbarHeight));
        ImGui.SetNextWindowSize(viewport.WorkSize - new Vector2(0, ToolbarHeight));
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGui.Begin("DockSpace", ImGuiWindowFlags.MenuBar
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground);

        ImGui.PopStyleVar(3);

        uint dockId = ImGui.GetID("MainDock");
        var dockFlags = ImGuiDockNodeFlags.PassthruCentralNode;
        if (_uiLocked) 
        {
            dockFlags |= ImGuiDockNodeFlags.NoResize | ImGuiDockNodeFlags.NoSplit;
        }

        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        ImGui.End();
    }

    // ═══ Menu Bar ════════════════════════════════════════════════════════════
    private void RenderMenuBar()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Exit")) Environment.Exit(0);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Lock Layout", "", ref _uiLocked);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Scene"))
            {
                if (ImGui.MenuItem("Add Cube")) AddPrimitive("Cube");
                if (ImGui.MenuItem("Add Sphere")) AddPrimitive("Sphere");
                if (ImGui.MenuItem("Add Light")) AddLight();
                ImGui.Separator();
                if (ImGui.MenuItem("Import FBX Model...")) ImportModelWithDialog();
                if (ImGui.MenuItem("Import HDRI Skybox...")) ImportHdriWithDialog();
                if (ImGui.MenuItem("Open HDRI Folder"))
                {
                    string path = Path.GetFullPath(_app.AssetManager.HDRIPath);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    System.Diagnostics.Process.Start("explorer.exe", path);
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Clear Scene")) _app.Scene.Clear();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Help"))
            {
                ImGui.MenuItem("About SynthGen");
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
        }
    }
    
    private ImGuiWindowFlags GetWindowFlags(ImGuiWindowFlags extra = ImGuiWindowFlags.None)
    {
        var flags = extra;
        if (_uiLocked) 
        {
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
        }
        return flags;
    }

    // ═══ Toolbar ═════════════════════════════════════════════════════════════
    private void RenderToolbar()
    {
        var viewport = ImGui.GetMainViewport();
        
        // Full width bar at the very top
        ImGui.SetNextWindowPos(viewport.WorkPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(viewport.WorkSize.X, ToolbarHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);

        // Toolbar is strictly fixed at the top
        ImGui.Begin("Toolbar", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse 
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus);
        
        ImGui.PopStyleVar();

        ImGui.SetCursorPosX((viewport.WorkSize.X - 350f) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5);

        bool generating = _app.CaptureManager.IsGenerating;

        if (!generating)
        {
            if (ImGui.Button("[>] Generate")) _app.CaptureManager.StartGeneration(_app.CaptureManager.TotalFrames);
        }
        else
        {
            if (ImGui.Button("[#] Stop")) _app.CaptureManager.StopGeneration();
        }

        ImGui.SameLine();
        if (ImGui.Button("[C] Capture 1")) _app.CaptureManager.CaptureSingleFrame();

        ImGui.SameLine();
        if (ImGui.Button("[R] Randomize"))
        {
            var rng = new Random();
            foreach (var r in _allRandomizers)
            {
                if (r.Enabled) r.Randomize(_app.Scene, rng);
            }
            AddLog("[UI] Randomized scene.");
        }

        if (_uiLocked)
        {
            ImGui.SameLine(viewport.WorkSize.X - 120);
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "  [ LOCKED ]");
        }

        if (generating)
        {
            ImGui.SameLine();
            float prog = _app.CaptureManager.Progress;
            ImGui.ProgressBar(prog, new Vector2(200, 0),
                $"{_app.CaptureManager.CompletedFrames}/{_app.CaptureManager.TotalFrames}");
        }

        ImGui.End();
    }

    // ═══ Scene Hierarchy ═════════════════════════════════════════════════════
    private void RenderSceneHierarchy()
    {
        ImGui.Begin("Scene Hierarchy", GetWindowFlags());

        // Ctrl+G Shortcut: Group selected objects under a new parent node
        bool isCtrl = ImGui.GetIO().KeyCtrl || _app.Input.CtrlHeld || ImGui.IsKeyDown(ImGuiKey.ModCtrl);
        bool isG = ImGui.IsKeyPressed(ImGuiKey.G, false) || _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.G);
        
        if (!ImGui.GetIO().WantTextInput && isCtrl && isG)
        {
            if (_app.Scene.SelectedObjects.Count > 1)
            {
                var groupObj = new SceneObject { Name = "Group" };
                
                var firstParent = _app.Scene.SelectedObjects[0].Parent;
                bool sameParent = _app.Scene.SelectedObjects.All(o => o.Parent == firstParent);

                if (sameParent && firstParent != null)
                {
                    firstParent.AddChild(groupObj);
                }
                
                _app.Scene.AddObject(groupObj);

                var toMove = _app.Scene.SelectedObjects.ToList();
                foreach (var obj in toMove)
                {
                    if (obj.Parent != null) obj.Parent.Children.Remove(obj);
                    groupObj.AddChild(obj);
                }

                _app.Scene.SelectedObject = groupObj;
                AddLog($"[Scene] Grouped {toMove.Count} objects under new parent '{groupObj.Name}'");
            }
        }

        // Only render root-level objects (no parent), children are drawn recursively
        foreach (var obj in _app.Scene.Objects)
        {
            if (obj.Parent != null) continue; // Skip children, they're drawn under parents
            RenderSceneNode(obj);
        }

        ImGui.Separator();

        if (ImGui.Button("Import FBX"))
        {
            ImportModelWithDialog();
        }

        ImGui.SameLine();
        if (ImGui.Button("+ Cube")) AddPrimitive("Cube");
        ImGui.SameLine();
        if (ImGui.Button("+ Sphere")) AddPrimitive("Sphere");

        ImGui.End();
    }

    private void RenderSceneNode(SceneObject obj)
    {
        // ── HIDDEN SYSTEM NODES ──
        if (obj.Name.Contains("$AssimpFbx$"))
        {
            foreach (var child in obj.Children)
                RenderSceneNode(child);
            return;
        }

        // Determine icon
        string icon = obj.Children.Count > 0 ? "[P]" : "[M]";
        if (obj.HasComponent<LightComponent>()) icon = "[L]";
        if (obj is Camera) icon = "[C]";
        if (obj.HasComponent<MeshRendererComponent>() && obj.Children.Count > 0) icon = "[P]";

        bool isSelected = _app.Scene.SelectedObjects.Contains(obj);
        bool hasChildren = obj.Children.Count > 0;

        if (hasChildren)
        {
            // Collapsible tree node for parents
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;
            // Default open for first level
            if (obj.Parent == null) flags |= ImGuiTreeNodeFlags.DefaultOpen;

            bool open = ImGui.TreeNodeEx($"{icon} {obj.Name}##{obj.GetHashCode()}", flags);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Accept ImGui's internal modifier state OR Silk.NET's direct hardware state
                bool isShift = ImGui.GetIO().KeyShift || _app.Input.ShiftHeld || ImGui.IsKeyDown(ImGuiKey.ModShift);
                bool isCtrl = ImGui.GetIO().KeyCtrl || _app.Input.CtrlHeld || ImGui.IsKeyDown(ImGuiKey.ModCtrl);
                
                if (isShift || isCtrl)
                {
                    if (isSelected) _app.Scene.SelectedObjects.Remove(obj);
                    else _app.Scene.SelectedObjects.Add(obj);
                    AddLog($"[Hierarchy] Multi-Select: {obj.Name}. Total selected: {_app.Scene.SelectedObjects.Count}");
                }
                else
                {
                    _app.Scene.SelectedObject = obj;
                    AddLog($"[Hierarchy] Single-Select: {obj.Name}");
                }
            }

            if (open)
            {
                foreach (var child in obj.Children)
                    RenderSceneNode(child);
                ImGui.TreePop();
            }
        }
        else
        {
            // Leaf node (no children) - simple selectable
            var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;

            ImGui.TreeNodeEx($"{icon} {obj.Name}##{obj.GetHashCode()}", flags);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                bool isShift = ImGui.GetIO().KeyShift || _app.Input.ShiftHeld || ImGui.IsKeyDown(ImGuiKey.ModShift);
                bool isCtrl = ImGui.GetIO().KeyCtrl || _app.Input.CtrlHeld || ImGui.IsKeyDown(ImGuiKey.ModCtrl);
                
                if (isShift || isCtrl)
                {
                    if (isSelected) _app.Scene.SelectedObjects.Remove(obj);
                    else _app.Scene.SelectedObjects.Add(obj);
                    AddLog($"[Hierarchy] Multi-Select: {obj.Name}. Total selected: {_app.Scene.SelectedObjects.Count}");
                }
                else
                {
                    _app.Scene.SelectedObject = obj;
                    AddLog($"[Hierarchy] Single-Select: {obj.Name}");
                }
            }
        }
    }

    // ═══ Viewport ════════════════════════════════════════════════════════════
    private void RenderViewport()
    {
        ImGui.Begin("Viewport", GetWindowFlags(ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar));
        
        _viewportScreenPos = ImGui.GetCursorScreenPos();
        var viewportSize = ImGui.GetContentRegionAvail();
        
        // Ensure renderer matches exactly
        _app.Renderer.ResizeFBOs((int)viewportSize.X, (int)viewportSize.Y);
        _viewportSize = new Vector2(_app.Renderer.Width, _app.Renderer.Height); // Use int-snapped size for consistency
        _viewportHovered = ImGui.IsWindowHovered();
        
        if (_viewportSize.X > 0 && _viewportSize.Y > 0)
        {
            // Camera controls (returns true if in fly mode)
            bool flyMode = _app.Scene.ActiveCamera?.ProcessInput(_app.Input, _viewportHovered, _app.DeltaTime) ?? false;
            
            if (!flyMode)
            {
                HandleManipulationInput();
            }
            else
            {
                // In fly mode: cancel any active manipulation
                if (_isManipulating) { CancelManipulation(_app.Scene.SelectedObject); _isManipulating = false; }
            }
            
            // Display the rendered texture
            var texId = _app.Renderer.CurrentViewMode switch
            {
                Rendering.ViewMode.Segmentation => _app.Renderer.SegTexture,
                Rendering.ViewMode.Depth => _app.Renderer.DepthTexture,
                _ => _app.Renderer.RGBTexture,
            };

            ImGui.Image((IntPtr)texId, viewportSize, new Vector2(0, 1), new Vector2(1, 0));

            // Overlay tools (Draw ABOVE image)
            DrawObjectGizmo();
            if (_manipMode != ManipulationMode.None) DrawManipulationOverlay();
            
            // Fly mode overlay
            if (_app.Scene.ActiveCamera?.IsFlyMode == true)
            {
                var oList = ImGui.GetWindowDrawList();
                var oPos = _viewportScreenPos + new Vector2(8, 8);
                string flyMsg = "  ✈ FLY MODE  |  WASD: Move   Q/E: Up/Down   Shift: Sprint   RMB: Exit";
                var flySize = ImGui.CalcTextSize(flyMsg);
                oList.AddRectFilled(oPos, oPos + new Vector2(flySize.X + 16, 26), ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.05f, 0.85f)), 4f);
                oList.AddRect(oPos, oPos + new Vector2(flySize.X + 16, 26), ImGui.GetColorU32(new Vector4(0f, 0.8f, 1f, 0.9f)), 4f, 0, 1.5f);
                oList.AddText(oPos + new Vector2(8, 5), ImGui.GetColorU32(new Vector4(0f, 0.9f, 1f, 1f)), flyMsg);
            }
            
            // ── Dataset Preview Overlays (Only during iteration) ──
            if (_app.CaptureManager.IsGenerating)
            {
                DrawLabelOverlays();
            }
 
            RenderViewportTools();
            DrawViewGizmo();
        }

        // View mode buttons overlay
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos() + new Vector2(10, 30);
        ImGui.SetCursorPos(new Vector2(10, 30));
        if (ImGui.SmallButton("RGB")) _app.Renderer.CurrentViewMode = Rendering.ViewMode.RGB;
        ImGui.SameLine();
        if (ImGui.SmallButton("SEG")) _app.Renderer.CurrentViewMode = Rendering.ViewMode.Segmentation;
        ImGui.SameLine();
        if (ImGui.SmallButton("DEPTH")) _app.Renderer.CurrentViewMode = Rendering.ViewMode.Depth;

        // Manipulation Overlay
        if (_manipMode != ManipulationMode.None)
        {
            string axis = _axisLock == '\0' ? "View" : _axisLock.ToString();
            string msg = $"{_manipMode} [{axis}] | Enter/Click: Confirm | Esc/R-Click: Cancel";
            var size = ImGui.CalcTextSize(msg);
            var winSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(pos + new Vector2(0, 30), pos + new Vector2(size.X + 20, 60), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.7f)));
            drawList.AddText(pos + new Vector2(10, 35), ImGui.GetColorU32(new Vector4(1, 1, 0, 1)), msg);
        }

        ImGui.End();
    }

    private void HandleManipulationInput()
    {
        var io = ImGui.GetIO();
        var sel = _app.Scene.SelectedObject;

        // Shortcuts for Mode (Tool Selection) — suppressed during fly mode
        bool inFly = _app.Scene.ActiveCamera?.IsFlyMode ?? false;
        if (!inFly)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) { _manipMode = ManipulationMode.Move; _isManipulating = false; }
            if (ImGui.IsKeyPressed(ImGuiKey.E)) { _manipMode = ManipulationMode.Rotate; _isManipulating = false; }
            if (ImGui.IsKeyPressed(ImGuiKey.R)) { _manipMode = ManipulationMode.Scale; _isManipulating = false; }
            if (ImGui.IsKeyPressed(ImGuiKey.Q)) { _manipMode = ManipulationMode.None; _isManipulating = false; _axisLock = '\0'; }
        }

        // Start / Stop Manipulation
        if (!_isManipulating)
        {
            // 1. Gizmo interaction (High Priority)
            if (sel != null && _hoveredAxis != '\0' && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _axisLock = _hoveredAxis;
                _startedByMouse = true;
                StartManipulation(sel, _manipMode == ManipulationMode.None ? ManipulationMode.Move : _manipMode);
                return;
            }

            // 2. Click Picking & Tool Activation
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _viewportHovered)
            {
                var localMouse = ImGui.GetMousePos() - _viewportScreenPos;
                
                // Skip picking if mouse is over viewport tools (top-left or top-right)
                bool overTools = localMouse.X < 50 && localMouse.Y < 200;
                bool overGizmo = localMouse.X > _viewportSize.X - 100 && localMouse.Y < 100;
                
                if (!overTools && !overGizmo)
                {
                    var color = _app.Renderer.PickSegmentationColor((int)localMouse.X, (int)localMouse.Y);
                    var found = FindObjectBySegColor(color);
                    
                    // Group picking logic: Select the topmost manually-created group parent
                    // This makes grouped items "stick together" when clicked in the 3D viewport
                    while (found != null && found.Parent != null && found.Parent.Name.StartsWith("Group"))
                    {
                        found = found.Parent;
                    }
                    
                    bool isShift = ImGui.GetIO().KeyShift || _app.Input.ShiftHeld || ImGui.IsKeyDown(ImGuiKey.ModShift);
                    bool isCtrl = ImGui.GetIO().KeyCtrl || _app.Input.CtrlHeld || ImGui.IsKeyDown(ImGuiKey.ModCtrl);

                    if (found != null)
                    {
                        if (isShift || isCtrl)
                        {
                            if (_app.Scene.SelectedObjects.Contains(found))
                                _app.Scene.SelectedObjects.Remove(found);
                            else
                                _app.Scene.SelectedObjects.Add(found);
                            AddLog($"[Scene] Multi-Selected: {found.Name}. Total selected: {_app.Scene.SelectedObjects.Count}");
                        }
                        else
                        {
                            _app.Scene.SelectedObject = found;
                            AddLog($"[Scene] Selected: {found.Name}");
                        }
                        
                        sel = _app.Scene.SelectedObject; // Update local var for manipulation
                        if (_manipMode != ManipulationMode.None && sel != null)
                        {
                            StartManipulation(sel, _manipMode);
                            _startedByMouse = true;
                        }
                    }
                    else
                    {
                        if (!isShift && !isCtrl)
                        {
                            // Deselect if clicking background without modifiers
                            _app.Scene.SelectedObject = null;
                            sel = null;
                        }
                    }
                }
            }

            // 3. Hotkeys (G/R/S)
            if (sel != null && _viewportHovered)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.G)) StartManipulation(sel, ManipulationMode.Move);
                if (ImGui.IsKeyPressed(ImGuiKey.R)) StartManipulation(sel, ManipulationMode.Rotate);
                if (ImGui.IsKeyPressed(ImGuiKey.S)) StartManipulation(sel, ManipulationMode.Scale);
            }
        }

        // Processing active manipulation
        if (_isManipulating && sel != null)
        {
            // Axis Lock mid-manipulation
            if (ImGui.IsKeyPressed(ImGuiKey.X)) _axisLock = 'X';
            if (ImGui.IsKeyPressed(ImGuiKey.Y)) _axisLock = 'Y';
            if (ImGui.IsKeyPressed(ImGuiKey.Z)) _axisLock = 'Z';
            if (ImGui.IsKeyPressed(ImGuiKey.C)) _axisLock = '\0';

            Vector2 mouseDelta = io.MousePos - _initialMousePos;
            float sensitivity = 0.01f;
            if (_manipMode == ManipulationMode.Rotate) sensitivity = 0.5f;
            if (_manipMode == ManipulationMode.Scale) sensitivity = 0.005f;

            var cam = _app.Scene.ActiveCamera;
            Vector3 camFront = Vector3.UnitZ;
            if (cam != null)
            {
                var dir = cam.OrbitTarget - cam.Transform.Position;
                if (dir.LengthSquared() > 0.0001f) camFront = Vector3.Normalize(dir);
            }
            Vector3 camRight = Vector3.Normalize(Vector3.Cross(camFront, Vector3.UnitY));
            Vector3 camUp = Vector3.Normalize(Vector3.Cross(camRight, camFront));

            float dist = cam != null ? cam.OrbitDistance : 10f;
            float moveSense = dist * 0.001f;

            if (_manipMode == ManipulationMode.Move)
            {
                Vector3 move = (camRight * mouseDelta.X - camUp * mouseDelta.Y) * moveSense;
                if (_axisLock == 'X') move = new Vector3(mouseDelta.X * moveSense, 0, 0);
                if (_axisLock == 'Y') move = new Vector3(0, -mouseDelta.Y * moveSense, 0);
                if (_axisLock == 'Z') move = new Vector3(0, 0, mouseDelta.Y * moveSense);
                sel.Transform.Position = _initialTransformValue + move;
            }
            else if (_manipMode == ManipulationMode.Rotate)
            {
                float rotSense = 0.5f;
                float rot = mouseDelta.X * rotSense;
                Vector3 r = Vector3.Zero;
                if (_axisLock == 'X') r.X = rot;
                else if (_axisLock == 'Y') r.Y = rot;
                else if (_axisLock == 'Z') r.Z = rot;
                else r.Y = rot;
                sel.Transform.Rotation = _initialTransformValue + r;
            }
            else if (_manipMode == ManipulationMode.Scale)
            {
                float s = 1.0f + mouseDelta.X * sensitivity;
                if (s < 0.01f) s = 0.01f;
                Vector3 scale = new Vector3(s);
                if (_axisLock == 'X') scale = new Vector3(s, 1, 1);
                else if (_axisLock == 'Y') scale = new Vector3(1, s, 1);
                else if (_axisLock == 'Z') scale = new Vector3(1, 1, s);
                // 'U' or '\0' results in uniform scale
                sel.Transform.Scale = _initialTransformValue * scale;
            }

            // Confirm / Cancel
            if (_startedByMouse)
            {
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { _isManipulating = false; _axisLock = '\0'; }
            }
            else
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsKeyPressed(ImGuiKey.Enter)) { _isManipulating = false; _axisLock = '\0'; }
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsKeyPressed(ImGuiKey.Escape)) { CancelManipulation(sel); _isManipulating = false; _axisLock = '\0'; }
            }
        }
    }

    private void RenderViewportTools()
    {
        var drawList = ImGui.GetWindowDrawList();
        var startPos = _viewportScreenPos + new Vector2(10, 10);
        
        // Background for vertical bar
        drawList.AddRectFilled(startPos, startPos + new Vector2(40, 160), ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.8f)), 4f);

        string[] toolIcons = { "S", "M", "R", "Sc" };
        ManipulationMode[] toolModes = { ManipulationMode.None, ManipulationMode.Move, ManipulationMode.Rotate, ManipulationMode.Scale };
        
        for (int i = 0; i < toolIcons.Length; i++)
        {
            var btnPos = startPos + new Vector2(5, 5 + i * 38);
            bool isActive = _manipMode == toolModes[i];
            
            if (isActive)
                drawList.AddRectFilled(btnPos, btnPos + new Vector2(30, 30), ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 1.0f)), 2f);
            
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton($"##Tool{i}", new Vector2(30, 30)))
            {
                _manipMode = toolModes[i];
                _isManipulating = false;
            }
            
            var text = toolIcons[i];
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(btnPos + (new Vector2(30,30) - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), text);
            
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{toolModes[i]} ({(i==0?'Q':i==1?'W':i==2?'E':'R')})");
            }
        }
    }

    private void DrawViewGizmo()
    {
        var cam = _app.Scene.ActiveCamera;
        if (cam == null) return;

        var drawList = ImGui.GetWindowDrawList();
        var center = _viewportScreenPos + new Vector2(_viewportSize.X - 60, 60);
        
        // Axis lines
        float length = 30f;
        var view = cam.GetViewMatrix();
        
        Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        Vector4[] colors = { new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1) };
        string[] labels = { "X", "Y", "Z" };
        
        // View presets: (yaw, pitch) - matching how OrbitYaw/OrbitPitch work
        // X → Front view, Y → Top view, Z → Right view
        (float yaw, float pitch)[] viewPresets = {
            (90f, 0f),    // X: Front view (looking from +X)
            (0f, 89f),    // Y: Top view (looking down)
            (0f, 0f),     // Z: Right view (looking from +Z)
        };

        Vector2[] endPositions = new Vector2[3];

        for (int i = 0; i < 3; i++)
        {
            Vector3 worldAxis = axes[i];
            Vector3 viewAxis = Vector3.TransformNormal(worldAxis, view);
            
            Vector2 endPos = center + new Vector2(viewAxis.X, -viewAxis.Y) * length;
            endPositions[i] = endPos;
            
            drawList.AddLine(center, endPos, ImGui.GetColorU32(colors[i]), 2f);
            
            // Draw label as clickable circle + text
            var labelPos = endPos + new Vector2(-6, -8);
            var textSize = ImGui.CalcTextSize(labels[i]);
            var labelCenter = endPos + new Vector2(textSize.X / 2, textSize.Y / 2 - 4);

            // Check if mouse is near this label (click detection)
            var mousePos = ImGui.GetMousePos();
            float dist = Vector2.Distance(mousePos, labelCenter);
            bool hovered = dist < 14f;

            if (hovered)
            {
                // Highlight on hover
                drawList.AddCircleFilled(labelCenter, 12f, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.6f)));
                
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    cam.OrbitYaw = viewPresets[i].yaw;
                    cam.OrbitPitch = viewPresets[i].pitch;
                    // Focus on selected object if any
                    var sel = _app.Scene.SelectedObject;
                    if (sel != null) cam.OrbitTarget = sel.Transform.Position;
                }
            }
            
            drawList.AddText(labelPos, ImGui.GetColorU32(colors[i]), labels[i]);
        }
        
        // Draw center dot (click to reset to default perspective)
        var centerMouseDist = Vector2.Distance(ImGui.GetMousePos(), center);
        if (centerMouseDist < 8f)
        {
            drawList.AddCircleFilled(center, 6f, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.6f)));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                cam.OrbitYaw = 30f;
                cam.OrbitPitch = 25f;
            }
        }
        drawList.AddCircleFilled(center, 3f, ImGui.GetColorU32(Vector4.One));
        
        // Bottom view label (-Y) below the gizmo
        Vector3 negYView = Vector3.TransformNormal(-Vector3.UnitY, view);
        Vector2 negYEnd = center + new Vector2(negYView.X, -negYView.Y) * length;
        
        var negYLabelPos = negYEnd + new Vector2(-8, -4);
        var negYMouseDist = Vector2.Distance(ImGui.GetMousePos(), negYEnd);
        
        if (negYMouseDist < 14f)
        {
            drawList.AddCircleFilled(negYEnd, 12f, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.6f)));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                cam.OrbitYaw = 0f;
                cam.OrbitPitch = -89f; // Bottom view (looking up)
                var sel = _app.Scene.SelectedObject;
                if (sel != null) cam.OrbitTarget = sel.Transform.Position;
            }
        }
        drawList.AddLine(center, negYEnd, ImGui.GetColorU32(new Vector4(0, 0.5f, 0, 0.5f)), 1.5f);
        drawList.AddText(negYLabelPos, ImGui.GetColorU32(new Vector4(0, 0.5f, 0, 0.8f)), "-Y");
    }

    private void DrawObjectGizmo()
    {
        var sel = _app.Scene.SelectedObject;
        var cam = _app.Scene.ActiveCamera;
        if (sel == null || cam == null) return;

        var drawList = ImGui.GetWindowDrawList();
        Vector3 worldPos = sel.Transform.Position;
        
        if (!WorldToScreen(worldPos, out Vector2 screenOrigin)) return;

        if (_manipMode == ManipulationMode.None) return;

        bool canHover = !_isManipulating && !cam.IsFlyMode;
        _hoveredAxis = '\0';
        Vector2 mousePos = ImGui.GetMousePos();
        
        float gizmoScale = cam.OrbitDistance * 0.12f;

        // Uniform scale handle (center box)
        if (_manipMode == ManipulationMode.Scale)
        {
            bool centerHovered = canHover && IsMouseNearPoint(mousePos, screenOrigin, 15f);
            if (centerHovered) _hoveredAxis = 'U';
            
            var centerColor = (_hoveredAxis == 'U' || _axisLock == 'U') ? new Vector4(1, 1, 0, 1) : new Vector4(1, 1, 1, 0.7f);
            drawList.AddRectFilled(screenOrigin - new Vector2(6, 6), screenOrigin + new Vector2(6, 6), ImGui.GetColorU32(centerColor), 1f);
        }

        Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        Vector4[] colors = { new(1, 0.1f, 0.1f, 1), new(0.1f, 1, 0.1f, 1), new(0.1f, 0.1f, 1, 1) };
        char[] axisChars = { 'X', 'Y', 'Z' };

        for (int i = 0; i < 3; i++)
        {
            Vector3 axis = axes[i];
            Vector4 color = colors[i];
            char axisChar = axisChars[i];

            if (_manipMode == ManipulationMode.Rotate)
            {
                // Draw Rotation Rings
                int segments = 32;
                Vector2 lastP = Vector2.Zero;
                bool lastValid = false;
                
                // Vectors to define the circle plane
                Vector3 p1 = (i == 0) ? Vector3.UnitY : Vector3.UnitX;
                Vector3 v1 = Vector3.Normalize(Vector3.Cross(axis, p1));
                Vector3 v2 = Vector3.Normalize(Vector3.Cross(axis, v1));
                
                float radius = gizmoScale;

                for (int s = 0; s <= segments; s++)
                {
                    float angle = (s / (float)segments) * MathF.PI * 2f;
                    Vector3 worldP = worldPos + (v1 * MathF.Cos(angle) + v2 * MathF.Sin(angle)) * radius;
                    
                    if (WorldToScreen(worldP, out Vector2 screenP))
                    {
                        if (lastValid)
                        {
                            bool isRingHovered = canHover && IsMouseNearLine(mousePos, lastP, screenP, 8f);
                            if (isRingHovered) _hoveredAxis = axisChar;

                            Vector4 drawColor = color;
                            if (_hoveredAxis == axisChar) drawColor = Vector4.Lerp(color, Vector4.One, 0.5f);
                            if (_axisLock == axisChar) drawColor = Vector4.One;

                            drawList.AddLine(lastP, screenP, ImGui.GetColorU32(drawColor), 4f);
                        }
                        lastP = screenP;
                        lastValid = true;
                    }
                    else lastValid = false;
                }
                
                // Also draw a label at the "top" of the ring
                if (WorldToScreen(worldPos + v2 * radius, out Vector2 labelPos))
                {
                    drawList.AddText(labelPos + new Vector2(5, 5), ImGui.GetColorU32(color), axisChar.ToString());
                }
            }
            else
            {
                // Move or Scale (Standard axis lines)
                if (WorldToScreen(worldPos + axis * gizmoScale, out Vector2 screenEnd))
                {
                    bool isHovered = canHover && (IsMouseNearLine(mousePos, screenOrigin, screenEnd, 12f) || IsMouseNearPoint(mousePos, screenEnd, 18f));
                    if (isHovered) _hoveredAxis = axisChar;

                    Vector4 drawColor = color;
                    if (isHovered) drawColor = Vector4.Lerp(color, Vector4.One, 0.5f);
                    if (_axisLock == axisChar) drawColor = Vector4.One;

                    // Axis Line
                    drawList.AddLine(screenOrigin, screenEnd, ImGui.GetColorU32(drawColor), 3f);
                    
                    // End Handle
                    if (_manipMode == ManipulationMode.Scale)
                    {
                        drawList.AddRectFilled(screenEnd - new Vector2(7, 7), screenEnd + new Vector2(7, 7), ImGui.GetColorU32(drawColor), 1f);
                    }
                    else // Move
                    {
                        drawList.AddCircleFilled(screenEnd, 7f, ImGui.GetColorU32(drawColor));
                    }
                    
                    drawList.AddCircle(screenEnd, 10f, ImGui.GetColorU32(new Vector4(0,0,0,0.5f)), 16, 1f);
                    drawList.AddText(screenEnd + new Vector2(8, 8), ImGui.GetColorU32(drawColor), axisChar.ToString());
                }
            }
        }

        // Outer Screen-Space Guide Ring (subtle white circle)
        if (_manipMode == ManipulationMode.Rotate || _manipMode == ManipulationMode.Move)
        {
            drawList.AddCircle(screenOrigin, 120f, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.2f)), 64, 1.5f);
        }
    }

    private void DrawLabelOverlays()
    {
        var drawList = ImGui.GetWindowDrawList();
        foreach (var obj in _app.Scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label == null) continue;

            // --- Skeleton Bone Filtering ---
            // If an ancestor already has a label, skip it to avoid clutter.
            bool ancestorLabeled = false;
            var p = obj.Parent;
            while (p != null) { if (p.HasComponent<LabelComponent>()) { ancestorLabeled = true; break; } p = p.Parent; }
            if (ancestorLabeled) continue;

            // --- Find All Meshes ---
            var relevantMeshes = new List<(SceneObject o, Rendering.Mesh m)>();
            FindMeshesInGroup(obj, (o, m) => relevantMeshes.Add((o, m)));

            // --- Compute Target Position (Top-Center of group) ---
            Vector3 centerSum = Vector3.Zero;
            float highestY = -1000f;
            foreach (var (mObj, mesh) in relevantMeshes)
            {
                var model = mObj.GetWorldMatrix();
                if (mesh.HasSkinning && mesh.Skeleton != null && mesh.PrimaryBoneIndex >= 0)
                {
                    var boneMats = mesh.Skeleton.GetFinalMatrices();
                    if (mesh.PrimaryBoneIndex < boneMats.Length)
                        model = boneMats[mesh.PrimaryBoneIndex] * model;
                }
                centerSum += model.Translation;
                highestY = MathF.Max(highestY, model.Translation.Y + 1.2f); // Offset to head height
                DrawOriented3DBox(mObj, mesh, label.SegmentationColor);
            }

            Vector3 labelWorldPos = relevantMeshes.Count > 0 
                ? new Vector3(centerSum.X / relevantMeshes.Count, highestY, centerSum.Z / relevantMeshes.Count)
                : obj.GetWorldMatrix().Translation;

            if (!WorldToScreen(labelWorldPos, out Vector2 screenPos)) continue;
 
            // Simple 2D Label Badge
            string text = $"[{label.ClassName}] #{label.ClassID}";
            Vector2 textSize = ImGui.CalcTextSize(text);
            Vector2 boxSize = textSize + new Vector2(12, 6);
            Vector2 pMin = screenPos - new Vector2(boxSize.X / 2, 0); // At the point
            Vector2 pMax = pMin + boxSize;
            
            Vector4 col = new Vector4(label.SegmentationColor, 1.0f);
            drawList.AddRectFilled(pMin, pMax, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.7f)), 4f);
            drawList.AddRect(pMin, pMax, ImGui.GetColorU32(col), 4f, 0, 2f);
            drawList.AddText(pMin + new Vector2(6, 3), ImGui.GetColorU32(Vector4.One), text);
        }
    }

    private void FindMeshesInGroup(SceneObject obj, Action<SceneObject, Rendering.Mesh> action)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr?.Mesh != null) action(obj, mr.Mesh);

        foreach (var child in obj.Children)
        {
            if (child.HasComponent<LabelComponent>()) continue;
            FindMeshesInGroup(child, action);
        }
    }
 
    private void DrawOriented3DBox(SceneObject obj, Rendering.Mesh mesh, Vector3 baseColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var model = obj.GetWorldMatrix();
        
        // Apply primary bone transform so the UI bounding box tracks animated meshes
        if (mesh.HasSkinning && mesh.Skeleton != null && mesh.PrimaryBoneIndex >= 0)
        {
            var boneMatrices = mesh.Skeleton.GetFinalMatrices();
            if (mesh.PrimaryBoneIndex < boneMatrices.Length)
            {
                model = boneMatrices[mesh.PrimaryBoneIndex] * model;
            }
        }
        
        Vector3 min = mesh.BoundingBoxMin;
        Vector3 max = mesh.BoundingBoxMax;
 
        // 8 Corners of the AABB in mesh-space
        Vector3[] localCorners = 
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), 
            new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), 
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)
        };
 
        // Project to Screen
        Vector2[] screenCorners = new Vector2[8];
        bool[] valid = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 worldP = Vector3.Transform(localCorners[i], model);
            valid[i] = WorldToScreen(worldP, out screenCorners[i]);
        }
 
        // Draw 12 Edges
        int[,] edges = {
            {0, 1}, {1, 2}, {2, 3}, {3, 0}, // Bottom
            {4, 5}, {5, 6}, {6, 7}, {7, 4}, // Top
            {0, 4}, {1, 5}, {2, 6}, {3, 7}  // Pillars
        };
 
        uint col = ImGui.GetColorU32(new Vector4(baseColor, 0.8f));
        for (int i = 0; i < 12; i++)
        {
            if (valid[edges[i,0]] && valid[edges[i,1]])
            {
                drawList.AddLine(screenCorners[edges[i,0]], screenCorners[edges[i,1]], col, 1.5f);
            }
        }
    }
 
    private bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
    {
        screenPos = Vector2.Zero;
        var cam = _app.Scene.ActiveCamera;
        if (cam == null) return false;

        var view = cam.GetViewMatrix();
        float aspect = (float)_app.Renderer.Width / Math.Max(1, _app.Renderer.Height);
        var proj = cam.GetProjectionMatrix(aspect);
        var vp = view * proj;

        var clip = Vector4.Transform(new Vector4(worldPos, 1.0f), vp);
        if (clip.W <= 0) return false;

        Vector2 ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        screenPos = _viewportScreenPos + new Vector2(
            (ndc.X + 1) * 0.5f * _viewportSize.X,
            (1 - ndc.Y) * 0.5f * _viewportSize.Y
        );
        return true;
    }

    private bool IsMouseNearLine(Vector2 mouse, Vector2 a, Vector2 b, float threshold)
    {
        float l2 = Vector2.DistanceSquared(a, b);
        if (l2 == 0.0) return Vector2.Distance(mouse, a) < threshold;
        float t = MathF.Max(0, MathF.Min(1, Vector2.Dot(mouse - a, b - a) / l2));
        Vector2 projection = a + t * (b - a);
        return Vector2.Distance(mouse, projection) < threshold;
    }

    private bool IsMouseNearPoint(Vector2 mouse, Vector2 p, float threshold)
    {
        return Vector2.Distance(mouse, p) < threshold;
    }

    private SceneObject? FindObjectBySegColor(Vector3 color)
    {
        if (color == Vector3.Zero) return null;
        foreach (var obj in _app.Scene.Objects)
        {
            var label = obj.GetComponent<LabelComponent>();
            if (label != null)
            {
                if (Vector3.Distance(label.SegmentationColor, color) < 0.02f)
                    return obj;
            }
        }
        return null;
    }

    private void StartManipulation(SceneObject obj, ManipulationMode mode)
    {
        _isManipulating = true;
        _manipMode = mode;
        _initialMousePos = ImGui.GetIO().MousePos;
        // Keep _axisLock if set by gizmo
        if (mode == ManipulationMode.Move) _initialTransformValue = obj.Transform.Position;
        if (mode == ManipulationMode.Rotate) _initialTransformValue = obj.Transform.Rotation;
        if (mode == ManipulationMode.Scale) _initialTransformValue = obj.Transform.Scale;
    }

    private void CancelManipulation(SceneObject? obj)
    {
        if (obj != null)
        {
            if (_manipMode == ManipulationMode.Move) obj.Transform.Position = _initialTransformValue;
            if (_manipMode == ManipulationMode.Rotate) obj.Transform.Rotation = _initialTransformValue;
            if (_manipMode == ManipulationMode.Scale) obj.Transform.Scale = _initialTransformValue;
        }
    }

    // ═══ Inspector ═══════════════════════════════════════════════════════════
    private void RenderInspector()
    {
        ImGui.Begin("Inspector", GetWindowFlags());

        var sel = _app.Scene.SelectedObject;
        if (sel == null)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "Select an object in the hierarchy.");
            ImGui.End();
            return;
        }

        ImGui.Text(sel.Name);
        ImGui.Separator();

        // ── Transform ──
        if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.DragFloat3("Position", ref sel.Transform.Position, 0.1f);
            ImGui.DragFloat3("Rotation", ref sel.Transform.Rotation, 1f);
            ImGui.DragFloat3("Scale", ref sel.Transform.Scale, 0.05f);
        }

        // ── Label ──
        var label = sel.GetComponent<LabelComponent>();
        if (label != null && ImGui.CollapsingHeader("Label", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.InputInt("Class ID", ref label.ClassID);

            // Class name as byte array for ImGui
            byte[] nameBuffer = new byte[128];
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(label.ClassName);
            Array.Copy(nameBytes, nameBuffer, Math.Min(nameBytes.Length, 127));
            if (ImGui.InputText("Class Name", nameBuffer, (uint)nameBuffer.Length))
            {
                label.ClassName = System.Text.Encoding.UTF8.GetString(nameBuffer).TrimEnd('\0');
            }

            ImGui.ColorEdit3("Seg Color", ref label.SegmentationColor);
            ImGui.Text($"Instance ID: {label.InstanceID}");
        }

        // ── Material & Textures ──
        var mr = sel.GetComponent<MeshRendererComponent>();
        // Find first mesh renderer in hierarchy to show current texture names
        var firstMr = mr ?? FindFirstMeshRenderer(sel);
        
        bool hasMaterialOrChildren = firstMr != null; // Show if we or any child has a mesh
        
        if (hasMaterialOrChildren && ImGui.CollapsingHeader("Material", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (mr != null && mr.Mesh != null)
            {
                ImGui.Text($"Mesh: {Path.GetFileName(mr.Mesh.SourcePath)}");
                ImGui.Text($"Tris: {mr.Mesh.IndexCount / 3}");
                
                Vector3 size = mr.Mesh.BoundingBoxMax - mr.Mesh.BoundingBoxMin;
                Vector3 center = (mr.Mesh.BoundingBoxMax + mr.Mesh.BoundingBoxMin) / 2f;
                ImGui.Text($"Size: {size.X:F1}, {size.Y:F1}, {size.Z:F1}");
                ImGui.Text($"Center: {center.X:F1}, {center.Y:F1}, {center.Z:F1}");
                
                if (ImGui.Button("Normalize Model Bounds"))
                {
                    mr.Mesh.Normalize();
                    AddLog($"[Mesh] Normalized: {Path.GetFileName(mr.Mesh.SourcePath)}");
                }
                ImGui.Separator();
            }

            if (mr != null)
            {
                ImGui.ColorEdit4("Base Color", ref mr.Material.BaseColor);
                ImGui.SliderFloat("Smoothness", ref mr.Material.Smoothness, 0, 1);
                ImGui.SliderFloat("Metallic", ref mr.Material.Metallic, 0, 1);
                ImGui.DragFloat("Normal Scale", ref mr.Material.NormalScale, 0.01f, 0, 8);
                
                if (ImGui.CollapsingHeader("Emission"))
                {
                    ImGui.ColorEdit3("Emissive Color", ref mr.Material.EmissiveColor);
                    ImGui.DragFloat("Emissive Intensity", ref mr.Material.EmissiveIntensity, 0.1f, 0, 100);
                }

                ImGui.Checkbox("Visible", ref mr.Visible);
                ImGui.Separator();
            }

            ImGui.Text("Texture Maps (Applies Recursively)");

            // --- Albedo Slot ---
            ImGui.Text("Albedo:");
            ImGui.SameLine();
            string albedoName = firstMr?.Material.AlbedoTexturePath != null ? Path.GetFileName(firstMr.Material.AlbedoTexturePath) : "None";
            if (ImGui.Button($"{albedoName}##Albedo", new Vector2(ImGui.GetContentRegionAvail().X - 60, 0))) { }
            ImGui.SameLine();
            if (ImGui.Button("Browse##Albedo"))
            {
                string? path = PickFileWithDialog("Images (*.png;*.jpg;*.jpeg;*.tga)|*.png;*.jpg;*.jpeg;*.tga|All files (*.*)|*.*");
                if (path != null)
                {
                    string fileName = Path.GetFileName(path);
                    string destPath = Path.Combine(_app.AssetManager.TexturesPath, fileName);
                    if (!File.Exists(destPath)) File.Copy(path, destPath);
                    uint texId = _app.AssetManager.LoadTexture(destPath);
                    if (texId > 0)
                    {
                        // Find the topmost root of this object's hierarchy
                        var rootObj = sel;
                        while (rootObj.Parent != null) rootObj = rootObj.Parent;

                        // Apply to the root and all its descendants
                        ApplyTextureRecursive(rootObj, texId, destPath, isAlbedo: true);
                        AddLog($"[Material] Applied Albedo to {rootObj.Name} (Root) + all children: {fileName}");
                    }
                }
            }

            // --- Normal Slot ---
            ImGui.Text("Normal:");
            ImGui.SameLine();
            string normalName = firstMr?.Material.NormalTexturePath != null ? Path.GetFileName(firstMr.Material.NormalTexturePath) : "None";
            if (ImGui.Button($"{normalName}##Normal", new Vector2(ImGui.GetContentRegionAvail().X - 60, 0))) { }
            ImGui.SameLine();
            if (ImGui.Button("Browse##Normal"))
            {
                string? path = PickFileWithDialog("Images (*.png;*.jpg;*.jpeg;*.tga)|*.png;*.jpg;*.jpeg;*.tga|All files (*.*)|*.*");
                if (path != null)
                {
                    string fileName = Path.GetFileName(path);
                    string destPath = Path.Combine(_app.AssetManager.TexturesPath, fileName);
                    if (!File.Exists(destPath)) File.Copy(path, destPath);
                    uint texId = _app.AssetManager.LoadTexture(destPath);
                    if (texId > 0)
                    {
                        // Find the topmost root of this object's hierarchy
                        var rootObj = sel;
                        while (rootObj.Parent != null) rootObj = rootObj.Parent;

                        // Apply to the root and all its descendants
                        ApplyTextureRecursive(rootObj, texId, destPath, isAlbedo: false);
                        AddLog($"[Material] Applied Normal to {rootObj.Name} (Root) + all children: {fileName}");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // --- Smoothness ---
            float smoothness = firstMr?.Material.Smoothness ?? 0.5f;
            if (ImGui.SliderFloat("Smoothness", ref smoothness, 0f, 1f))
            {
                ApplyMaterialPropertyRecursive(sel, m => m.Smoothness = smoothness);
            }

            // --- Metallic ---
            float metallic = firstMr?.Material.Metallic ?? 0.0f;
            if (ImGui.SliderFloat("Metallic", ref metallic, 0f, 1f))
            {
                ApplyMaterialPropertyRecursive(sel, m => m.Metallic = metallic);
            }

            // --- Normal Scale ---
            float nScale = firstMr?.Material.NormalScale ?? 1.0f;
            if (ImGui.SliderFloat("Normal Scale", ref nScale, 0f, 5f))
            {
                ApplyMaterialPropertyRecursive(sel, m => m.NormalScale = nScale);
            }

            // --- Color Intensity ---
            float cIntensity = firstMr?.Material.ColorIntensity ?? 1.0f;
            if (ImGui.SliderFloat("Color Intensity", ref cIntensity, 0f, 5f))
            {
                ApplyMaterialPropertyRecursive(sel, m => m.ColorIntensity = cIntensity);
            }
        }

        // ── Light ──
        var light = sel.GetComponent<LightComponent>();
        if (light != null && ImGui.CollapsingHeader("Light", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int lt = (int)light.LightType;
            ImGui.Combo("Type", ref lt, "Directional\0Point\0Spot\0");
            light.LightType = (LightType)lt;

            ImGui.ColorEdit3("Color", ref light.Color);
            ImGui.DragFloat("Intensity", ref light.Intensity, 0.05f, 0, 10);
            ImGui.Checkbox("Cast Shadow", ref light.CastShadow);
        }

        // ── Buoyancy (The Push) ──
        var buoy = sel.GetComponent<BuoyantBodyComponent>();
        if (buoy != null && ImGui.CollapsingHeader("Buoyancy", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Enabled", ref buoy.Enabled);
            ImGui.DragFloat("Waterline", ref buoy.Waterline, 0.05f, -10, 10);
            ImGui.SliderFloat("Bob Intensity", ref buoy.BobIntensity, 0, 5);
            ImGui.SliderFloat("Tilt Intensity", ref buoy.TiltIntensity, 0, 5);
            
            if (ImGui.Button("Snap to Surface"))
            {
                var mrComp = sel.GetComponent<MeshRendererComponent>();
                var mesh = mrComp?.Mesh;
                float meshHeight = (mesh != null) ? (mesh.BoundingBoxMax.Y - mesh.BoundingBoxMin.Y) : 1.0f;
                float defaultSubmerge = -meshHeight * 0.4f; // 40% submerge
                
                float level = _app.Ocean.Config.Level;
                buoy.Waterline = defaultSubmerge;
                buoy.AnchorPosition = new Vector3(sel.Transform.Position.X, level, sel.Transform.Position.Z);
                sel.Transform.Position = new Vector3(sel.Transform.Position.X, level + defaultSubmerge, sel.Transform.Position.Z);
                buoy.Velocity = 0;
                AddLog($"[Physics] Balanced {sel.Name}. Waterline set to {defaultSubmerge:F2}");
            }
        }

        // ── Animations ──
        var animObj = FindFirstAnimatedObject(sel);
        if (animObj != null && ImGui.CollapsingHeader("Animations", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var anim = animObj.GetComponent<AnimationPlayerComponent>()!;
            var animMr = animObj.GetComponent<MeshRendererComponent>()!;
            
            int clipCount = animMr.Mesh?.Clips.Count ?? 0;
            ImGui.Text($"Available Tracks: {clipCount}");
            
            string[] clipNames = new string[clipCount];
            for (int i = 0; i < clipCount; i++) clipNames[i] = string.IsNullOrEmpty(animMr.Mesh!.Clips[i].Name) ? $"Track {i}" : animMr.Mesh.Clips[i].Name;
            
            int current = anim.CurrentClipIndex;
            if (ImGui.Combo("Clip", ref current, clipNames, clipNames.Length))
            {
                anim.CurrentClipIndex = current;
                anim.PlaybackTime = 0;
                SyncAnimationState(animObj, anim);
            }

            bool wasPlaying = anim.IsPlaying;
            ImGui.Checkbox("Play", ref anim.IsPlaying);
            ImGui.SameLine();
            bool wasLoop = anim.Loop;
            ImGui.Checkbox("Loop", ref anim.Loop);
            if (anim.IsPlaying != wasPlaying || anim.Loop != wasLoop)
                SyncAnimationState(animObj, anim);
            
            float duration = animMr.Mesh.Clips[current].Duration / animMr.Mesh.Clips[current].TicksPerSecond;
            float time = anim.PlaybackTime;
            ImGui.ProgressBar(time / Math.Max(duration, 0.001f), new Vector2(-1, 0), $"{time:F2}s / {duration:F2}s");
            
            if (ImGui.SliderFloat("Time", ref anim.PlaybackTime, 0, anim.ClipDurationSeconds))
            {
                anim.IsPlaying = false;
                SyncAnimationState(animObj, anim);
            }
        }

        ImGui.Separator();
        if (label == null)
        {
            if (ImGui.Button("Add Label", new Vector2(-1, 0)))
            {
                sel.AddComponent(new LabelComponent());
                AddLog($"[Scene] Added label to {sel.Name}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1), "[ Label Active ]");
        }

        // ── Body Part Group ──
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1), "Body Part Group");
        
        var groupNames = SynthGen.Scene.Components.BodyPartGroups.GetDropdownNames();
        int currentGroupIdx = SynthGen.Scene.Components.BodyPartGroups.GetIndex(sel.BodyPartGroup);
        if (ImGui.Combo("Group", ref currentGroupIdx, groupNames, groupNames.Length))
        {
            sel.BodyPartGroup = SynthGen.Scene.Components.BodyPartGroups.GetName(currentGroupIdx);
            AddLog($"[Scene] Set '{sel.Name}' group = '{sel.BodyPartGroup}'");
        }

        // Show color preview
        var groupColor = SynthGen.Scene.Components.BodyPartGroups.GetColor(sel.BodyPartGroup);
        if (groupColor.HasValue)
        {
            var c = groupColor.Value;
            ImGui.SameLine();
            ImGui.ColorButton("##grpclr", new Vector4(c.X, c.Y, c.Z, 1));
        }

        if (ImGui.Button("Apply Group to Children", new Vector2(-1, 0)))
        {
            ApplyGroupToChildren(sel, sel.BodyPartGroup);
            AddLog($"[Scene] Applied group '{sel.BodyPartGroup}' to all children of '{sel.Name}'");
        }

        // ── Randomization ──
        ImGui.Separator();
        bool exclude = sel.ExcludeFromRandomization;
        if (ImGui.Checkbox("Exclude from Randomization", ref exclude))
        {
            sel.ExcludeFromRandomization = exclude;
        }
        ImGui.SameLine();
        HelpMarker("When enabled, position/rotation/scale/texture randomizers will skip this object and all its children.");

        // ── Custom Randomizer Overrides ──
        var posRand = sel.GetComponent<PositionRandomizerComponent>();
        if (posRand != null)
        {
            if (ImGui.CollapsingHeader("Position Randomizer (Override)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("Enabled##pos_rand", ref posRand.Enabled);
                ImGui.DragFloat3("Min Bounds##pos_rand", ref posRand.MinBounds, 0.1f);
                ImGui.DragFloat3("Max Bounds##pos_rand", ref posRand.MaxBounds, 0.1f);
                if (ImGui.Button("Remove Position Randomizer", new Vector2(-1, 0))) sel.RemoveComponent<PositionRandomizerComponent>();
            }
        }
        else
        {
            if (ImGui.Button("Add Position Randomizer", new Vector2(-1, 0))) sel.AddComponent(new PositionRandomizerComponent());
        }

        var rotRand = sel.GetComponent<RotationRandomizerComponent>();
        if (rotRand != null)
        {
            if (ImGui.CollapsingHeader("Rotation Randomizer (Override)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("Enabled##rot_rand", ref rotRand.Enabled);
                ImGui.DragFloat3("Min Angles##rot_rand", ref rotRand.MinAngles, 1f);
                ImGui.DragFloat3("Max Angles##rot_rand", ref rotRand.MaxAngles, 1f);
                if (ImGui.Button("Remove Rotation Randomizer", new Vector2(-1, 0))) sel.RemoveComponent<RotationRandomizerComponent>();
            }
        }
        else
        {
            if (ImGui.Button("Add Rotation Randomizer", new Vector2(-1, 0))) sel.AddComponent(new RotationRandomizerComponent());
        }

        var sclRand = sel.GetComponent<ScaleRandomizerComponent>();
        if (sclRand != null)
        {
            if (ImGui.CollapsingHeader("Scale Randomizer (Override)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("Enabled##scl_rand", ref sclRand.Enabled);
                ImGui.DragFloat("Min Scale##scl_rand", ref sclRand.MinScale, 0.05f, 0.01f, 10f);
                ImGui.DragFloat("Max Scale##scl_rand", ref sclRand.MaxScale, 0.05f, 0.01f, 10f);
                ImGui.Checkbox("Uniform Scale##scl_rand", ref sclRand.UniformScale);
                if (ImGui.Button("Remove Scale Randomizer", new Vector2(-1, 0))) sel.RemoveComponent<ScaleRandomizerComponent>();
            }
        }
        else
        {
            if (ImGui.Button("Add Scale Randomizer", new Vector2(-1, 0))) sel.AddComponent(new ScaleRandomizerComponent());
        }

        var depthRand = sel.GetComponent<DepthScaleComponent>();
        if (depthRand != null)
        {
            if (ImGui.CollapsingHeader("Depth Scale (Override)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("Enabled##depth_rand", ref depthRand.Enabled);
                ImGui.DragFloat("Near Scale##depth_rand", ref depthRand.NearScale, 0.05f, 0.1f, 5f);
                ImGui.DragFloat("Far Scale##depth_rand", ref depthRand.FarScale, 0.05f, 0.1f, 5f);
                ImGui.DragFloat("Reference Dist##depth_rand", ref depthRand.ReferenceDistance, 0.5f, 1f, 100f);
                if (ImGui.Button("Remove Depth Scale", new Vector2(-1, 0))) sel.RemoveComponent<DepthScaleComponent>();
            }
        }
        else
        {
            if (ImGui.Button("Add Depth Scale Randomizer", new Vector2(-1, 0))) sel.AddComponent(new DepthScaleComponent());
        }

        var texRand = sel.GetComponent<TextureRandomizerComponent>();
        if (texRand != null)
        {
            if (ImGui.CollapsingHeader("Texture Randomizer (Override)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("Enabled##tex_rand", ref texRand.Enabled);
                if (ImGui.Button("Remove Texture Randomizer", new Vector2(-1, 0))) sel.RemoveComponent<TextureRandomizerComponent>();
            }
        }
        else
        {
            if (ImGui.Button("Add Texture Randomizer", new Vector2(-1, 0))) sel.AddComponent(new TextureRandomizerComponent());
        }

        ImGui.Separator();
        if (ImGui.Button("Add Buoyancy") && buoy == null)
        {
            sel.AddComponent(new BuoyantBodyComponent());
        }

        ImGui.Separator();
        if (ImGui.Button("🗑 Delete Object"))
        {
            _app.Scene.RemoveObject(sel);
        }

        ImGui.End();
    }

    // ═══ Randomizer Panel ════════════════════════════════════════════════════
    private void RenderRandomizerPanel()
    {
        ImGui.Begin("Randomizers", GetWindowFlags());

        string? lastCategory = null;
        foreach (var r in _allRandomizers)
        {
            if (r.Category != lastCategory)
            {
                if (lastCategory != null) ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), $"--- {r.Category} ---");
                lastCategory = r.Category;
            }

            bool enabled = r.Enabled;
            if (ImGui.Checkbox($"{r.Name}##{r.GetHashCode()}", ref enabled))
            {
                r.Enabled = enabled;
                r.OnToggle(_app.Scene, enabled);
            }

            if (r.Enabled)
            {
                ImGui.SameLine();
                if (ImGui.TreeNode($"[*]##{r.GetHashCode()}"))
                {
                    ImGui.Indent(10);
                    r.DrawConfigUI(_app.Scene);
                    ImGui.Unindent(10);
                    ImGui.TreePop();
                }

                if (r is HDRIRandomizer hr && hr.NeedsRefresh)
                {
                    ImportHdriWithDialog();
                    hr.NeedsRefresh = false;
                }
            }
        }

        ImGui.Separator();
        int seed = _app.CaptureManager.RandomSeed;
        ImGui.InputInt("Random Seed", ref seed);
        _app.CaptureManager.RandomSeed = seed;

        ImGui.End();
    }

    // ═══ Ocean Panel ═════════════════════════════════════════════════════════
    private void RenderOceanSettings()
    {
        var ocean = _app.Ocean;
        var cfg = ocean.Config;

        if (ImGui.Begin("Ocean Editor (Sync Mode)", GetWindowFlags()))
        {
            ImGui.Checkbox("Ocean Enabled", ref cfg.Enabled);
            ImGui.Separator();

            // 1. THE FORMULA (Wave Shapes & Motion)
            ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1), "[ The Formula ]");
            ImGui.SliderFloat("Time Speed", ref cfg.TimeMultiplier, 0, 5);
            ImGui.SliderFloat("Wind Speed", ref cfg.LargeWindSpeed, 0, 100);
            ImGui.SliderFloat("Wind Direction", ref cfg.WindDirection, 0, 360);
            ImGui.SliderFloat("Storm Intensity", ref cfg.StormIntensity, 0, 1);
            ImGui.SliderFloat("Steepness", ref cfg.LargeSteepness, 0, 1);
            ImGui.SliderFloat("Chaos", ref cfg.LargeChaos, 0, 1);
            ImGui.SliderFloat("Water Level", ref cfg.Level, -10, 10);
            
            ImGui.Separator();

            // 2. VISUAL LOOK (Tropical Styling)
            ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1), "[ Visual Look ]");
            ImGui.ColorEdit3("Shallow (Teal)", ref cfg.RefractionColor);
            ImGui.ColorEdit3("Deep (Base)", ref cfg.ScatteringColor);
            ImGui.SliderFloat("Foam Amount", ref cfg.FoamAmount, 0, 1);
            ImGui.SliderFloat("Sparkle/Glitter", ref cfg.SparkleIntensity, 0, 10);
            ImGui.SliderFloat("Micro-Ripples", ref cfg.MicroRippleStrength, 0, 1);
            ImGui.SliderFloat("Refl Saturation", ref cfg.ReflectionSaturation, 0, 1);

            ImGui.Separator();

            // 3. THE PUSH (Buoyancy Interaction)
            ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1), "[ The Push ]");
            ImGui.SliderFloat("Buoyancy Force", ref cfg.BuoyancyForce, 0, 100);
            ImGui.SliderFloat("Physics Damping", ref cfg.BuoyancyDamping, 0, 2);
        }

        ImGui.End();
    }

    // ═══ Capture Panel ═══════════════════════════════════════════════════════
    private void RenderCapturePanel()
    {
        ImGui.Begin("Dataset Capture", GetWindowFlags());

        var cap = _app.CaptureManager;

        // Output directory
        byte[] dirBuf = new byte[256];
        var dirBytes = System.Text.Encoding.UTF8.GetBytes(cap.OutputDirectory);
        Array.Copy(dirBytes, dirBuf, Math.Min(dirBytes.Length, 255));
        if (ImGui.InputText("Output Dir", dirBuf, (uint)dirBuf.Length))
            cap.OutputDirectory = System.Text.Encoding.UTF8.GetString(dirBuf).TrimEnd('\0');

        int frames = cap.TotalFrames;
        ImGui.InputInt("Frame Count", ref frames);
        cap.TotalFrames = Math.Max(1, frames);

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Capture Channels");
        bool rgb = cap.CaptureRGB, seg = cap.CaptureSeg, dep = cap.CaptureDepth;
        ImGui.Checkbox("RGB", ref rgb); cap.CaptureRGB = rgb;
        ImGui.SameLine();
        ImGui.Checkbox("Segmentation", ref seg); cap.CaptureSeg = seg;
        ImGui.SameLine();
        ImGui.Checkbox("Depth", ref dep); cap.CaptureDepth = dep;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Annotation Mode");

        int mode = (int)_annotationMode;
        ImGui.Combo("Mode", ref mode,
            "Bounding Box\0Instance Segmentation\0Semantic Segmentation\0Keypoints\0Panoptic\0");
        _annotationMode = (Annotation.AnnotationMode)mode;
        cap.Mode = _annotationMode;

        bool yolo = cap.ExportYOLO, coco = cap.ExportCOCO;
        if (ImGui.Checkbox("YOLO", ref yolo)) cap.ExportYOLO = yolo;
        ImGui.SameLine();
        if (ImGui.Checkbox("COCO", ref coco)) cap.ExportCOCO = coco;
        ImGui.SameLine();
        bool exportKp = cap.ExportKeypointPose;
        if (ImGui.Checkbox("Keypoints", ref exportKp)) cap.ExportKeypointPose = exportKp;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Animation Capture");
        bool animCapture = cap.AnimatedCapture;
        ImGui.Checkbox("Animated Capture", ref animCapture); cap.AnimatedCapture = animCapture;
        if (animCapture)
        {
            int subFrames = cap.SubFramesPerIteration;
            ImGui.SliderInt("Sub-frames / Iteration", ref subFrames, 1, 60);
            cap.SubFramesPerIteration = subFrames;

            float animDur = cap.AnimationDuration;
            ImGui.SliderFloat("Anim Duration (s)", ref animDur, 0.1f, 10f);
            cap.AnimationDuration = animDur;

            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1), 
                $"Total images: {cap.TotalFrames * cap.SubFramesPerIteration}");
        }

        ImGui.Separator();
        if (cap.IsGenerating)
        {
            ImGui.ProgressBar(cap.Progress, new Vector2(-1, 0),
                $"{cap.CompletedFrames}/{cap.TotalFrames}");
        }

        ImGui.End();
    }

    // ═══ Console ═════════════════════════════════════════════════════════════
    private void RenderConsole()
    {
        ImGui.Begin("Console", GetWindowFlags());

        if (ImGui.Button("Clear")) _logs.Clear();

        ImGui.Separator();
        ImGui.BeginChild("LogScroll");

        foreach (var log in _logs)
        {
            Vector4 color = new(0.8f, 0.8f, 0.8f, 1);
            if (log.Contains("Error") || log.Contains("❌")) color = new(1, 0.3f, 0.3f, 1);
            else if (log.Contains("Warning") || log.Contains("⚠")) color = new(1, 0.8f, 0.3f, 1);
            else if (log.Contains("✓") || log.Contains("Capture") || log.Contains("saved")) color = new(0.3f, 1, 0.5f, 1);
            else if (log.Contains("Randomize")) color = new(0.4f, 0.7f, 1, 1);

            ImGui.TextColored(color, log);
        }

        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
        ImGui.End();
    }

    // ═══ Helpers ══════════════════════════════════════════════════════════════
    private void AddPrimitive(string type)
    {
        var obj = new SceneObject(type);
        var mr = new MeshRendererComponent();
        mr.Mesh = type switch
        {
            "Cube" => Rendering.Mesh.CreateCube(_app.GL),
            "Sphere" => Rendering.Mesh.CreateSphere(_app.GL),
            _ => Rendering.Mesh.CreateCube(_app.GL)
        };
        obj.AddComponent(mr);

        _app.Scene.AddObject(obj);
        _app.Scene.SelectedObject = obj;
        AddLog($"[Scene] Added {type} (unlabeled)");
    }

    private void AddModelFromFile(string path)
    {
        var root = _app.AssetManager.ImportModelHierarchical(path, AddLog);
        if (root == null) return;
 
        // Auto-labeling DISABLED. Users must manually add labels via the Inspector.
        // This prevents character models from exploding with 100+ unwanted labels on bones.

        _app.Scene.AddObject(root);
        _app.Scene.SelectedObject = root;
        AddLog($"[Assets] Hierarchically imported {Path.GetFileName(path)}. Use 'Add Label' in Inspector to annotate.");
    }
 
    private void Legacy_Loader_Removed(string path)
    {
        return;
    }
 
    private void AddLight()
    {
        var obj = new SceneObject("Light");
        obj.AddComponent(new LightComponent());
        _app.Scene.AddObject(obj);
        _app.Scene.SelectedObject = obj;
        AddLog("[Scene] Added Light");
    }

    private void AddLog(string msg)
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }

    private static System.Numerics.Vector3 HsvToRgb(float h, float s, float v)
    {
        int hi = (int)(h * 6) % 6;
        float f = h * 6 - (int)(h * 6);
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);
        return hi switch
        {
            0 => new(v, t, p),
            1 => new(q, v, p),
            2 => new(p, v, t),
            3 => new(p, q, v),
            4 => new(t, p, v),
            _ => new(v, p, q),
        };
    }

    public void RefreshHDRIs()
    {
        var hdriRandomizer = _allRandomizers.OfType<HDRIRandomizer>().FirstOrDefault();
        if (hdriRandomizer != null)
        {
            hdriRandomizer.HDRIPaths.Clear();
            var files = _app.AssetManager.GetAvailableHDRIs();
            foreach (var f in files)
            {
                hdriRandomizer.HDRIPaths.Add(f);
            }
            AddLog($"[Assets] Discovered {files.Length} HDRIs.");
        }
    }

    private void ImportModelWithDialog()
    {
        string? path = PickFileWithDialog("3D Models (*.fbx;*.obj;*.gltf;*.glb)|*.fbx;*.obj;*.gltf;*.glb|All files (*.*)|*.*");
        if (path != null)
        {
            string fileName = Path.GetFileName(path);
            string destPath = Path.Combine(_app.AssetManager.ModelsPath, fileName);
            try {
                if (!string.Equals(path, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(path, destPath, overwrite: true);
                }

                // Copy nearby textures too
                string srcDir = Path.GetDirectoryName(path) ?? "";
                string destDir = Path.GetDirectoryName(destPath) ?? "";
                
                string[] possibleTexDirs = { srcDir, Path.Combine(srcDir, "textures"), Path.Combine(srcDir, "Textures") };
                foreach (var texDir in possibleTexDirs)
                {
                    if (Directory.Exists(texDir) && !string.Equals(texDir, destDir, StringComparison.OrdinalIgnoreCase))
                    {
                        var files = Directory.GetFiles(texDir, "*.*")
                            .Where(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg") || f.EndsWith(".tga"));
                        foreach (var f in files)
                        {
                            string tName = Path.GetFileName(f);
                            string tDest = Path.Combine(destDir, tName); // Try to put them alongside the model
                            if (!string.Equals(f, tDest, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(f, tDest, overwrite: true);
                            }
                        }
                    }
                }

                AddModelFromFile(destPath);
            } catch (Exception ex) {
                AddLog($"[Error] Import failed: {ex.Message}");
            }
        }
    }

    private void ImportHdriWithDialog()
    {
        string? path = PickFileWithDialog("HDRI Skybox (*.exr;*.hdr)|*.exr;*.hdr|EXR files (*.exr)|*.exr|HDR files (*.hdr)|*.hdr|All files (*.*)|*.*");
        if (path != null)
        {
            string fileName = Path.GetFileName(path);
            string destPath = Path.Combine(_app.AssetManager.HDRIPath, fileName);
            try {
                if (!File.Exists(destPath)) File.Copy(path, destPath);
                RefreshHDRIs();
                
                // Auto-enable and select for immediate feedback
                var hr = _allRandomizers.OfType<HDRIRandomizer>().FirstOrDefault();
                if (hr != null)
                {
                    hr.Enabled = true;
                    hr.SelectHDRI(destPath);
                }
                
                AddLog($"[Assets] Imported and activated HDRI: {fileName}");
            } catch (Exception ex) {
                AddLog($"[Error] Import failed: {ex.Message}");
            }
        }
    }

    private string? PickFileWithDialog(string filter)
    {
        AddLog("[Assets] Opening file dialog...");
        try
        {
            // Use a clean single-line command for reliability and proper quoting
            string command = $"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Filter = '{filter}'; $f.Title = 'Select File to Import'; if($f.ShowDialog() -eq 'OK') {{ $f.FileName }}";

            var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            
            if (string.IsNullOrEmpty(output)) return null;
            
            AddLog($"[Assets] Selected: {Path.GetFileName(output)}");
            return output;
        }
        catch (Exception ex)
        {
            AddLog($"[Error] Failed to open file dialog: {ex.Message}");
            return null;
        }
    }

    private void ApplyTextureRecursive(SceneObject obj, uint texId, string texPath, bool isAlbedo)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr != null)
        {
            if (isAlbedo)
            {
                mr.Material.AlbedoTextureID = texId;
                mr.Material.AlbedoTexturePath = texPath;
            }
            else
            {
                mr.Material.NormalTextureID = texId;
                mr.Material.NormalTexturePath = texPath;
            }
        }

        foreach (var child in obj.Children)
        {
            ApplyTextureRecursive(child, texId, texPath, isAlbedo);
        }
    }

    private void ApplyMaterialPropertyRecursive(SceneObject obj, Action<Rendering.Material> action)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr != null) action(mr.Material);
        foreach (var child in obj.Children) ApplyMaterialPropertyRecursive(child, action);
    }

    private MeshRendererComponent? FindFirstMeshRenderer(SceneObject obj)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr != null) return mr;

        foreach (var child in obj.Children)
        {
            var childMr = FindFirstMeshRenderer(child);
            if (childMr != null) return childMr;
        }
        return null;
    }

    private SceneObject? FindFirstAnimatedObject(SceneObject obj)
    {
        var anim = obj.GetComponent<AnimationPlayerComponent>();
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (anim != null && mr?.Mesh != null && mr.Mesh.Clips.Count > 0) return obj;

        foreach (var child in obj.Children)
        {
            var found = FindFirstAnimatedObject(child);
            if (found != null) return found;
        }
        return null;
    }

    private void DrawManipulationOverlay()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = _viewportScreenPos + new Vector2(10, 30);
        string axis = _axisLock == '\0' ? "View" : _axisLock.ToString();
        string msg = $"{_manipMode} [{axis}] | Enter/Click: Confirm | Esc/R-Click: Cancel";
        var size = ImGui.CalcTextSize(msg);
        drawList.AddRectFilled(pos, pos + new Vector2(size.X + 20, 30), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.7f)));
        drawList.AddText(pos + new Vector2(10, 5), ImGui.GetColorU32(new Vector4(1, 1, 0, 1)), msg);
    }

    private void ApplyGroupToChildren(SceneObject obj, string groupName)
    {
        obj.BodyPartGroup = groupName;
        foreach (var child in obj.Children)
            ApplyGroupToChildren(child, groupName);
    }

    /// <summary>
    /// Syncs animation state (play/pause/loop/time/clip) from the source to all
    /// AnimationPlayerComponents in the parent hierarchy (siblings + descendants).
    /// </summary>
    private void SyncAnimationState(SceneObject source, AnimationPlayerComponent sourceAnim)
    {
        // Find the top-level parent that owns the model
        var root = source;
        while (root.Parent != null && root.Parent.Parent != null) root = root.Parent;
        SyncAnimRecursive(root, sourceAnim);
    }

    private void SyncAnimRecursive(SceneObject obj, AnimationPlayerComponent source)
    {
        var anim = obj.GetComponent<AnimationPlayerComponent>();
        if (anim != null && anim != source)
        {
            anim.IsPlaying = source.IsPlaying;
            anim.Loop = source.Loop;
            anim.PlaybackTime = source.PlaybackTime;
            anim.CurrentClipIndex = source.CurrentClipIndex;
            anim.ClipDurationSeconds = source.ClipDurationSeconds;
        }
        foreach (var child in obj.Children)
            SyncAnimRecursive(child, source);
    }

    private static void HelpMarker(string desc)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
