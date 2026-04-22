using System;
using System.Linq;
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
    private readonly SynthGen.App.Application _app;

    // All randomizers
    private readonly List<RandomizerBase> _allRandomizers;
    public IReadOnlyList<RandomizerBase> AllRandomizers => _allRandomizers;

    public void UpdateRandomizer(int index, RandomizerBase newRandomizer)
    {
        if (index >= 0 && index < _allRandomizers.Count)
        {
            _allRandomizers[index] = newRandomizer;
        }
    }

    private string _customObjName = "NewObject";
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
    private Vector3 _initialBoneOffset; // Backup for undoing keypoint moves

    // Gizmo state
    private char _hoveredAxis = '\0';
    private ImGuiKey _heldToolKey = ImGuiKey.None; // Tracks which key is holding a tool active

    // Settings
    private Annotation.AnnotationMode _annotationMode = Annotation.AnnotationMode.BoundingBox;

    private bool _uiLocked = true;
    private float _preFocusDistance = -1f; // -1 = not focused
    private const float ToolbarHeight = 48f;
    
    // Hierarchy feature state
    private SceneObject? _renamingObject = null;
    private byte[] _renameBuf = new byte[128];
    private List<SceneObject> _draggedObjects = new(); 
    private SceneObject? _dropTarget = null;

    // Group manipulation state
    private struct TransformState { public Vector3 Pos; public Vector3 Rot; public Vector3 Sca; }
    private Dictionary<SceneObject, TransformState> _initialSelectedState = new();
    private List<SceneObject> _topSelection = new();


    // Training panel state
    private bool _showTrainingPanel = false;
    private bool _showTrainingPrompt = false;
    private int _generatedImageCount = 0;
    private bool _suppressObjectDelete;

    public UIManager(SynthGen.App.Application app)
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
            new MaterialRandomizer { Enabled = true },
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
        RefreshTexturePools();

        // Wire capture manager
        _app.CaptureManager.ActiveRandomizers = _allRandomizers;
        _app.CaptureManager.HdriRandomizer = _allRandomizers.OfType<HDRIRandomizer>().FirstOrDefault();
        _app.CaptureManager.OnLog += msg => _logs.Add(msg);
        _app.TrainingManager.OnLog += msg => _logs.Add(msg);

        AddLog("[SynthGen] UI initialized. Ready.");
    }

    public void Render()
    {
        _suppressObjectDelete = false;



        SetupDockspace();

        RenderMenuBar();
        RenderToolbar();
        RenderSceneHierarchy();
        RenderViewport();
        RenderInspector();
        RenderRandomizerPanel();
        RenderOceanSettings();
        RenderCapturePanel();
        if (_showTrainingPanel) RenderTrainingPanel();
        RenderTrainingPrompt();
        RenderConsole();

        HandleGlobalShortcuts();
    }

    private void HandleGlobalShortcuts()
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

        // Global Keyboard Shortcuts (only if not typing in a UI field)
        if (!ImGui.GetIO().WantTextInput)
        {
            var sel = _app.Scene.SelectedObject;

            // DELETE
            if (_app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Delete) && sel != null && !_suppressObjectDelete)
            {
                var target = sel;
                var parent = target.Parent;
                _app.Scene.RemoveObject(target);
                _app.CommandHistory.Push(new Commands.ActionCommand(
                    undoAction: () => { 
                        if (parent != null) parent.AddChild(target); 
                        else _app.Scene.AddObject(target); 
                        _app.Scene.SelectedObject = target; 
                    },
                    redoAction: () => { 
                        _app.Scene.RemoveObject(target); 
                        _app.Scene.SelectedObject = null; 
                    },
                    onExecute: () => { } // Already executed
                ));
                AddLog($"[Scene] Deleted {sel.Name}. Press Ctrl+Z to undo.");
            }

            // DUPLICATE (Ctrl+D)
            if (_app.Input.CtrlHeld && _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.D) && sel != null)
            {
                var target = sel;
                var clone = target.Clone();
                
                // Offset slightly so user can see it
                clone.Transform.Position += new Vector3(0.5f, 0, 0.5f);

                if (target.Parent != null) target.Parent.AddChild(clone);
                
                // CRITICAL: Always tell the scene about the new object (and its branch) 
                // so it gets added to the flat rendering list.
                _app.Scene.AddObject(clone);    

                _app.Scene.SelectedObject = clone;
                _app.CommandHistory.Push(new Commands.ActionCommand(
                    undoAction: () => { 
                        if (clone.Parent != null) clone.Parent.Children.Remove(clone);
                        else _app.Scene.RemoveObject(clone); 
                        _app.Scene.SelectedObject = target; 
                    },
                    redoAction: () => { 
                        if (target.Parent != null) target.Parent.AddChild(clone);
                        _app.Scene.AddObject(clone); 
                        _app.Scene.SelectedObject = clone; 
                    },
                    onExecute: () => { }
                ));
                AddLog($"[Scene] Duplicated {target.Name} (offset slightly)");
            }

            // UNDO (Ctrl+Z)
            if (_app.Input.CtrlHeld && _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Z))
            {
                if (_app.CommandHistory.CanUndo)
                {
                    _app.CommandHistory.Undo();
                    AddLog($"[Scene] Undo");
                }
            }

            // REDO (Ctrl+Y)
            if (_app.Input.CtrlHeld && _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Y))
            {
                if (_app.CommandHistory.CanRedo)
                {
                    _app.CommandHistory.Redo();
                    AddLog($"[Scene] Redo");
                }
            }

            // DESELECT (Esc)
            if (_app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Escape))
            {
                _app.Scene.SelectedObject = null;
            }

            // RANDOMIZE (R) - Dedicated shortcut for fast iteration
            // (Only triggers if not currently manipulating an object with 'R')
            if (_app.Input.WasKeyJustPressed(Silk.NET.Input.Key.R) && !_isManipulating && !_app.Input.CtrlHeld)
            {
                var rng = new Random();
                foreach (var r in _allRandomizers)
                {
                    if (r.Enabled || r.Category == "Object") 
                        r.Randomize(_app.Scene, rng);
                }
                AddLog("[UI] Randomized scene.");
            }
        }
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
                if (ImGui.MenuItem("Open Scenario..."))
                {
                    string? path = PickFileWithDialog("SynthGen Scenarios (*.json)|*.json|All files (*.*)|*.*");
                    if (path != null) SceneSerializer.Load(_app.Scene, path, _app, this, AddLog);
                }
                if (ImGui.MenuItem("Save Scenario As..."))
                {
                    string? path = SaveFileWithDialog("SynthGen Scenarios (*.json)|*.json|All files (*.*)|*.*", "json");
                    if (path != null) SceneSerializer.Save(_app, this, path);
                }
                ImGui.Separator();
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
    private unsafe void RenderSceneHierarchy()
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
                if (sameParent && firstParent != null) firstParent.AddChild(groupObj);
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

        // Use a child window for the list so buttons stay at bottom
        ImGui.BeginChild("HierarchyList", new Vector2(0, -35), true);

        // Only render root-level objects (no parent), children are drawn recursively
        foreach (var obj in _app.Scene.Objects.ToList())
        {
            if (obj.Parent != null) continue; 
            RenderSceneNode(obj);
        }

        // Drop on empty space to unparent (move to root)
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 50));
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("SCENE_OBJ");
            if (payload.NativePtr != null && _draggedObjects.Count > 0)
            {
                foreach (var target in _draggedObjects.ToList())
                {
                    if (target.Parent != null)
                    {
                        target.Parent.Children.Remove(target);
                        target.Parent = null; 
                    }
                }
                AddLog($"[Hierarchy] Unparented {_draggedObjects.Count} objects (moved to root)");
                _draggedObjects.Clear();
            }
            ImGui.EndDragDropTarget();
        }
        ImGui.EndChild();

        if (ImGui.Button("Import FBX")) ImportModelWithDialog();
        ImGui.SameLine();
        if (ImGui.Button("+ Folder"))
        {
            var folder = new SceneObject("New Folder");
            _app.Scene.AddObject(folder);
            _app.Scene.SelectedObject = folder;
            _renamingObject = folder;
            Array.Clear(_renameBuf, 0, _renameBuf.Length);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(folder.Name);
            Array.Copy(nameBytes, _renameBuf, Math.Min(nameBytes.Length, _renameBuf.Length - 1));
        }

        ImGui.SameLine();
        if (ImGui.Button("+ Cube")) AddPrimitive("Cube");
        ImGui.SameLine();
        if (ImGui.Button("+ Sphere")) AddPrimitive("Sphere");

        ImGui.End();
    }

    private unsafe void RenderSceneNode(SceneObject obj)
    {
        // ── HIDDEN SYSTEM NODES ──
        if (obj.Name.Contains("$AssimpFbx$"))
        {
            foreach (var child in obj.Children.ToList())
                RenderSceneNode(child);
            return;
        }

        // Determine icon
        string icon = obj.Children.Count > 0 ? "[F]" : "[M]";
        if (obj.HasComponent<LightComponent>()) icon = "[L]";
        if (obj is Camera) icon = "[C]";
        if (obj.HasComponent<MeshRendererComponent>()) icon = "[M]";

        bool isSelected = _app.Scene.SelectedObjects.Contains(obj);
        bool hasChildren = obj.Children.Count > 0;
        
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;
        if (!hasChildren) flags |= ImGuiTreeNodeFlags.Leaf;
        if (obj.Parent == null) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        bool open;
        if (_renamingObject == obj)
        {
            ImGui.SetKeyboardFocusHere();
            if (ImGui.InputText($"##rename{obj.GetHashCode()}", _renameBuf, (uint)_renameBuf.Length, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                obj.Name = System.Text.Encoding.UTF8.GetString(_renameBuf).TrimEnd('\0');
                _renamingObject = null;
            }
            if (!ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _renamingObject = null;
            open = false; 
        }
        else
        {
            open = ImGui.TreeNodeEx($"{icon} {obj.Name}##{obj.GetHashCode()}", flags);
            
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                bool isShift = ImGui.GetIO().KeyShift || _app.Input.ShiftHeld || ImGui.IsKeyDown(ImGuiKey.ModShift);
                bool isCtrl = ImGui.GetIO().KeyCtrl || _app.Input.CtrlHeld || ImGui.IsKeyDown(ImGuiKey.ModCtrl);
                if (isShift || isCtrl)
                {
                    if (isSelected) _app.Scene.SelectedObjects.Remove(obj);
                    else _app.Scene.SelectedObjects.Add(obj);
                }
                else
                {
                    _app.Scene.SelectedObject = obj;
                }
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _renamingObject = obj;
                Array.Clear(_renameBuf, 0, _renameBuf.Length);
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(obj.Name);
                Array.Copy(nameBytes, _renameBuf, Math.Min(nameBytes.Length, _renameBuf.Length - 1));
            }

            if (ImGui.BeginDragDropSource())
            {
                if (isSelected) 
                    _draggedObjects = _app.Scene.SelectedObjects.ToList();
                else 
                    _draggedObjects = new List<SceneObject> { obj };

                ImGui.SetDragDropPayload("SCENE_OBJ", IntPtr.Zero, 0);
                ImGui.Text(_draggedObjects.Count > 1 ? $"Moving {_draggedObjects.Count} objects" : $"Moving {obj.Name}");
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("SCENE_OBJ");
                if (payload.NativePtr != null && _draggedObjects.Count > 0)
                {
                    foreach (var target in _draggedObjects.ToList())
                    {
                        if (target == obj) continue;
                        if (!IsDescendantOf(target, obj))
                        {
                            if (target.Parent != null) target.Parent.Children.Remove(target);
                            obj.AddChild(target);
                        }
                    }
                    AddLog($"[Hierarchy] Parented {_draggedObjects.Count} objects to {obj.Name}");
                    _draggedObjects.Clear();
                }
                ImGui.EndDragDropTarget();
            }
        }

        if (open)
        {
            foreach (var child in obj.Children.ToList())
                RenderSceneNode(child);
            ImGui.TreePop();
        }
    }

    // ═══ Viewport ════════════════════════════════════════════════════════════
    private void RenderViewport()
    {
        ImGui.Begin("Viewport", GetWindowFlags(ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar));
        
        var availableSize = ImGui.GetContentRegionAvail();
        int drawW = (int)availableSize.X;
        int drawH = (int)availableSize.Y;

        var cam = _app.Scene.ActiveCamera;
        bool isFisheye = cam != null && cam.FisheyeStrength > 0.01f;
        if (isFisheye)
        {
            // Force 1:1 Aspect Ratio for Fisheye lenses
            drawW = drawH = Math.Min(drawW, drawH);
        }

        // Ensure renderer matches exactly
        _app.Renderer.ResizeFBOs(drawW, drawH);
        _viewportSize = new Vector2(drawW, drawH);
        _viewportScreenPos = ImGui.GetCursorScreenPos();
        
        if (isFisheye)
        {
            // Center the square viewport in the available space
            Vector2 offset = (availableSize - _viewportSize) * 0.5f;
            _viewportScreenPos += offset;
            ImGui.SetCursorScreenPos(_viewportScreenPos);
        }
        
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

            ImGui.Image((IntPtr)texId, _viewportSize, new Vector2(0, 1), new Vector2(1, 0));

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
        // ── Keypoint Skeleton Overlay (When NOT generating, show for selection) ──
        if (!_app.CaptureManager.IsGenerating)
        {
            var selObj = _app.Scene.SelectedObject;
            if (selObj != null)
            {
                var kpRoot = selObj;
                while (kpRoot.Parent != null) kpRoot = kpRoot.Parent;
                if (CountKeypointChildren(kpRoot) > 0)
                {
                    DrawKeypointOverlay(kpRoot);
                }
            }
        }

        ImGui.End();
    }

    private void HandleManipulationInput()
    {
        var io = ImGui.GetIO();
        var sel = _app.Scene.SelectedObject;

        // Shortcuts for Mode (Tool Selection) — uses Silk.NET event-based input (bypasses ImGui focus)
        bool inFly = _app.Scene.ActiveCamera?.IsFlyMode ?? false;
        if (!inFly && !ImGui.IsAnyItemActive())
        {
            var input = _app.Input;
            if (input.WasKeyJustPressed(Silk.NET.Input.Key.W) && !_isManipulating) { _manipMode = ManipulationMode.Move; _isManipulating = false; }
            if (input.WasKeyJustPressed(Silk.NET.Input.Key.E) && !_isManipulating) { _manipMode = ManipulationMode.Rotate; _isManipulating = false; }
            if (input.WasKeyJustPressed(Silk.NET.Input.Key.R) && !_isManipulating) { _manipMode = ManipulationMode.Scale; _isManipulating = false; }
            if (input.WasKeyJustPressed(Silk.NET.Input.Key.Q)) { _manipMode = ManipulationMode.None; _isManipulating = false; _axisLock = '\0'; }
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
                    var cam = _app.Scene.ActiveCamera;
                    if (cam != null)
                    {
                        // Unwarp mouse position if fisheye is active (viewport is distorted but segmentation buffer is not)
                        var pickPos = localMouse;
                        if (MathF.Abs(cam.FisheyeStrength) > 0.001f)
                        {
                            pickPos = Annotation.KeypointAnnotator.UnwarpFisheye(
                                localMouse,
                                (int)_viewportSize.X, (int)_viewportSize.Y,
                                cam.FisheyeStrength, cam.FieldOfView);
                        }

                        var color = _app.Renderer.PickSegmentationColor(_app.Scene, cam, (int)pickPos.X, (int)pickPos.Y);
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

            // Group delta application
            var cam = _app.Scene.ActiveCamera;
            Vector3 camFront = Vector3.UnitZ;
            if (cam != null)
            {
                var dir = cam.OrbitTarget - cam.Transform.Position;
                if (dir.LengthSquared() > 0.0001f) camFront = Vector3.Normalize(dir);
            }
            Vector3 camRight = Vector3.Normalize(Vector3.Cross(camFront, Vector3.UnitY));
            Vector3 camUp = Vector3.Normalize(Vector3.Cross(camRight, camFront));

            float dist = Vector3.Distance(cam.Transform.Position, sel.Transform.Position);
            float moveSense = dist * 0.003f;

            // Using cached top-most selection to avoid double-moving children
            var topSelection = _topSelection;

            if (_manipMode == ManipulationMode.Move)
            {
                Vector3 move = (camRight * mouseDelta.X - camUp * mouseDelta.Y) * moveSense;
                
                if (_axisLock == 'X' || _axisLock == 'Y' || _axisLock == 'Z')
                {
                    Vector3 axis = _axisLock == 'X' ? Vector3.UnitX : (_axisLock == 'Y' ? Vector3.UnitY : Vector3.UnitZ);
                    
                    // Project the axis onto the screen to find the visual direction 
                    WorldToScreen(sel.Transform.Position, out Vector2 p0);
                    WorldToScreen(sel.Transform.Position + axis, out Vector2 p1);
                    
                    Vector2 screenDir = p1 - p0;
                    if (screenDir.LengthSquared() > 0.0001f)
                    {
                        screenDir = Vector2.Normalize(screenDir);
                        // The amount of drag along the axis's screen projection
                        float delta = Vector2.Dot(mouseDelta, screenDir);
                        move = axis * delta * moveSense;
                    }
                    else
                    {
                        // Fallback: Axis is perpendicular to screen, use vertical mouse movement
                        move = axis * (-mouseDelta.Y) * moveSense;
                    }
                }

                foreach (var target in topSelection)
                {
                    var startState = _initialSelectedState[target];
                    
                    var kp = target.GetComponent<KeypointComponent>();
                    if (kp != null && !string.IsNullOrEmpty(kp.BoundBoneName))
                    {
                        var kpRoot = target.Parent;
                        while (kpRoot != null && kpRoot.Parent != null) kpRoot = kpRoot.Parent;
                        if (kpRoot != null)
                        {
                            var (skinnedObj, mr) = FindSkinnedMeshInHierarchy(kpRoot);
                            if (mr?.Mesh?.Skeleton != null && mr.Mesh.Skeleton.BonesByName.TryGetValue(kp.BoundBoneName, out var bone))
                            {
                                var modelObj = skinnedObj ?? kpRoot;
                                var boneInModel = bone.GlobalTransform * mr.Mesh.Skeleton.GlobalInverseTransform;
                                var jointWorld = boneInModel * modelObj.GetWorldMatrix();
                                Matrix4x4.Invert(jointWorld, out var invJointWorld);
                                invJointWorld.M41 = invJointWorld.M42 = invJointWorld.M43 = 0;
                                Vector3 localMove = Vector3.Transform(move, invJointWorld);
                                kp.BoneOffset = _initialBoneOffset + localMove; // Note: _initialBoneOffset only works for single active selection right now
                            }
                        }
                    }
                    else
                    {
                        target.Transform.Position = startState.Pos + move;
                    }
                }
            }
            else if (_manipMode == ManipulationMode.Rotate)
            {
                WorldToScreen(sel.Transform.Position, out Vector2 center);
                Vector3 axis = _axisLock == 'X' ? Vector3.UnitX : (_axisLock == 'Z' ? Vector3.UnitZ : Vector3.UnitY);
                if (_axisLock == '\0') axis = Vector3.UnitY;

                // Determine rotation direction by projecting a 1-degree rotation into screen space
                // This ensures rotation always follows the mouse correctly regardless of camera angle.
                Vector3 orth = Vector3.Normalize(Vector3.Cross(axis, camFront));
                if (orth.LengthSquared() < 0.001f) orth = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
                if (orth.LengthSquared() < 0.001f) orth = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));

                Vector3 pStart = sel.Transform.Position + orth * 0.5f;
                var rotQ = Quaternion.CreateFromAxisAngle(axis, 5.0f * MathF.PI / 180f); // 5 degree test
                Vector3 pEnd = sel.Transform.Position + Vector3.Transform(orth * 0.5f, rotQ);

                WorldToScreen(pStart, out Vector2 s0);
                WorldToScreen(pEnd, out Vector2 s1);
                
                Vector2 screenRotDir = s1 - s0;
                float angle = 0f;
                if (screenRotDir.LengthSquared() > 0.01f)
                {
                    screenRotDir = Vector2.Normalize(screenRotDir);
                    // Match mouse drag to the projected rotation direction
                    angle = Vector2.Dot(mouseDelta, screenRotDir) * 1.5f; // sensitivity
                }
                else
                {
                    // Fallback to circular/horizontal if axis is pointing straight at camera
                    Vector2 dragVec = io.MousePos - center;
                    float a1 = MathF.Atan2(_initialMousePos.Y - center.Y, _initialMousePos.X - center.X);
                    float a2 = MathF.Atan2(io.MousePos.Y - center.Y, io.MousePos.X - center.X);
                    angle = (a2 - a1) * 180f / MathF.PI;
                    if (Vector3.Dot(camFront, axis) > 0) angle = -angle;
                }

                angle = -angle; // Flip for user preference / consistency

                Vector3 r = Vector3.Zero;
                if (_axisLock == 'X') r.X = angle;
                else if (_axisLock == 'Y') r.Y = angle; 
                else if (_axisLock == 'Z') r.Z = angle;
                else r.Y = angle;

                foreach (var target in topSelection)
                {
                    target.Transform.Rotation = _initialSelectedState[target].Rot + r;
                }
            }
            else if (_manipMode == ManipulationMode.Scale)
            {
                float delta = (_axisLock == 'U')
                    ? (mouseDelta.X - mouseDelta.Y) * sensitivity  // diagonal: right/up = bigger
                    : mouseDelta.X * sensitivity;
                float s = 1.0f + delta;
                if (s < 0.01f) s = 0.01f;
                Vector3 scale = new Vector3(s);
                if (_axisLock == 'X') scale = new Vector3(s, 1, 1);
                else if (_axisLock == 'Y') scale = new Vector3(1, s, 1);
                else if (_axisLock == 'Z') scale = new Vector3(1, 1, s);

                foreach (var target in topSelection)
                {
                    target.Transform.Scale = _initialSelectedState[target].Sca * scale;
                }
            }

            // Confirm / Cancel
            if (_startedByMouse)
            {
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) 
                { 
                    CommitManipulation();
                    _isManipulating = false; 
                    _axisLock = '\0'; 
                }
            }
            else
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsKeyPressed(ImGuiKey.Enter)) 
                { 
                    CommitManipulation();
                    _isManipulating = false; 
                    _axisLock = '\0'; 
                }
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsKeyPressed(ImGuiKey.Escape)) 
                { 
                    CancelManipulation(sel); 
                    _isManipulating = false; 
                    _axisLock = '\0'; 
                }
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
                _heldToolKey = ImGuiKey.None; // Toolbar click = persistent, not hold-to-use
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
    public bool IsUsingGizmo() => _isManipulating;

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
        
        float distToObj = Vector3.Distance(cam.Transform.Position, worldPos);
        float gizmoScale = distToObj * 0.12f;

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
                    // Don't let axis hover steal from center uniform handle
                    bool isHovered = canHover && _hoveredAxis != 'U' && (IsMouseNearLine(mousePos, screenOrigin, screenEnd, 12f) || IsMouseNearPoint(mousePos, screenEnd, 18f));
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

            /* 
            // Removed to avoid skipping boxes for nested labels
            bool ancestorLabeled = false;
            var p = obj.Parent;
            while (p != null) { if (p.HasComponent<LabelComponent>()) { ancestorLabeled = true; break; } p = p.Parent; }
            if (ancestorLabeled) continue;
            */

            // --- Find All Meshes ---
            var relevantMeshes = new List<(SceneObject o, Rendering.Mesh m)>();
            FindMeshesInGroup(obj, (o, m) => relevantMeshes.Add((o, m)));

            // --- Compute Collective Bounding Box in obj-local space ---
            Vector3 collectiveMin = new Vector3(float.MaxValue);
            Vector3 collectiveMax = new Vector3(float.MinValue);
            Matrix4x4 rootWorld = obj.GetWorldMatrix();
            Matrix4x4.Invert(rootWorld, out var invRoot);

            foreach (var (mObj, mesh) in relevantMeshes)
            {
                var mrComp = mObj.GetComponent<MeshRendererComponent>();
                if (mrComp != null && !mrComp.Visible) continue; // Skip hidden collision boxes
                
                var model = mObj.GetWorldMatrix();
                
                if (mesh.HasSkinning && mesh.Skeleton != null)
                {
                    // Use more precise joint-based bounds calculation
                    var skeleton = mesh.Skeleton;
                    foreach (var bone in skeleton.Bones)
                    {
                        // Filter out non-body helper bones that often stretch the box to the model origin
                        string name = bone.Name.ToLower();
                        if (name.Contains("root") || name.Contains("armature") || 
                            name.Contains("pivot") || name.Contains("helper") || 
                            name.Contains("attach") || name.Contains("target")) 
                            continue;

                        // Match the logic in Application.UpdateBoneKeypointPositions for consistency
                        var boneInModel = bone.GlobalTransform * skeleton.GlobalInverseTransform;
                        var jointWorld = boneInModel * model;
                        var pLocal = Vector3.Transform(Vector3.Zero, jointWorld * invRoot);
                        
                        collectiveMin = Vector3.Min(collectiveMin, pLocal);
                        collectiveMax = Vector3.Max(collectiveMax, pLocal);
                    }
                    
                    // Add 5cm padding (tighter than before)
                    collectiveMin -= new Vector3(0.05f);
                    collectiveMax += new Vector3(0.05f);
                }
                else
                {
                    var modelInRoot = model * invRoot;
                    Vector3 mMin = mesh.BoundingBoxMin;
                    Vector3 mMax = mesh.BoundingBoxMax;
                    Vector3[] corners = {
                        new(mMin.X, mMin.Y, mMin.Z), new(mMax.X, mMin.Y, mMin.Z),
                        new(mMax.X, mMax.Y, mMin.Z), new(mMin.X, mMax.Y, mMin.Z),
                        new(mMin.X, mMin.Y, mMax.Z), new(mMax.X, mMin.Y, mMax.Z),
                        new(mMax.X, mMax.Y, mMax.Z), new(mMin.X, mMax.Y, mMax.Z)
                    };
                    foreach(var c in corners) {
                        var pLocal = Vector3.Transform(c, modelInRoot);
                        collectiveMin = Vector3.Min(collectiveMin, pLocal);
                        collectiveMax = Vector3.Max(collectiveMax, pLocal);
                    }
                }
            }

            if (relevantMeshes.Count > 0)
            {
                // Draw single oriented box for the group
                DrawOriented3DBoxLowLevel(rootWorld, collectiveMin, collectiveMax, label.SegmentationColor);
                
                // Draw Keypoint Skeleton if present
                DrawKeypointOverlay(obj);
            }
            else
            {
                // FALLBACK: If no meshes found, try to use keypoints or a small default box at origin
                if (CountKeypointChildren(obj) > 0)
                {
                    DrawKeypointOverlay(obj);
                    // Draw a small 10cm cube as a fallback indicator
                    DrawOriented3DBoxLowLevel(rootWorld, new Vector3(-0.05f), new Vector3(0.05f), label.SegmentationColor);
                }
            }

            // --- Draw Text Badge ---
            Vector3 labelWorldPos = obj.GetWorldMatrix().Translation;
            if (relevantMeshes.Count > 0) labelWorldPos += new Vector3(0, collectiveMax.Y + 0.2f, 0);

            if (WorldToScreen(labelWorldPos, out Vector2 screenPos))
            {
                string text = $"[{label.ClassName}] #{label.ClassID}";
                Vector2 textSize = ImGui.CalcTextSize(text);
                Vector2 boxSize = textSize + new Vector2(12, 6);
                Vector2 pMin = screenPos - new Vector2(boxSize.X / 2, boxSize.Y); 
                Vector2 pMax = pMin + boxSize;
                
                drawList.AddRectFilled(pMin, pMax, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.7f)), 4f);
                drawList.AddRect(pMin, pMax, ImGui.GetColorU32(new Vector4(label.SegmentationColor, 1.0f)), 4f, 0, 2f);
                drawList.AddText(pMin + new Vector2(6, 3), ImGui.GetColorU32(Vector4.One), text);
            }
        }
    }

    private void DrawOriented3DBoxLowLevel(Matrix4x4 model, Vector3 min, Vector3 max, Vector3 baseColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        
        Vector3[] localCorners = 
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), 
            new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), 
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)
        };
 
        Vector2[] screenCorners = new Vector2[8];
        bool[] valid = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 worldP = Vector3.Transform(localCorners[i], model);
            valid[i] = WorldToScreen(worldP, out screenCorners[i]);
        }
 
        int[,] edges = {
            {0, 1}, {1, 2}, {2, 3}, {3, 0}, {4, 5}, {5, 6}, {6, 7}, {7, 4}, {0, 4}, {1, 5}, {2, 6}, {3, 7}
        };
 
        uint col = ImGui.GetColorU32(new Vector4(baseColor, 0.8f));
        for (int i = 0; i < 12; i++)
        {
            if (valid[edges[i,0]] && valid[edges[i,1]])
                drawList.AddLine(screenCorners[edges[i,0]], screenCorners[edges[i,1]], col, 2.0f);
        }
    }
 
    private void FindMeshesInGroup(SceneObject obj, Action<SceneObject, Rendering.Mesh> action)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr?.Mesh != null) action(obj, mr.Mesh);

        foreach (var child in obj.Children)
        {
            FindMeshesInGroup(child, action);
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
        float sx = (ndc.X + 1) * 0.5f * _viewportSize.X;
        float sy = (1 - ndc.Y) * 0.5f * _viewportSize.Y;

        // Apply Fisheye Warp for UI overlay
        if (MathF.Abs(cam.FisheyeStrength) > 0.001f)
        {
            var warped = Annotation.KeypointAnnotator.WarpFisheye(new Vector2(sx, sy), (int)_viewportSize.X, (int)_viewportSize.Y, cam.FisheyeStrength);
            sx = warped.X;
            sy = warped.Y;
        }

        screenPos = _viewportScreenPos + new Vector2(sx, sy);
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
            if (Vector3.Distance(obj.PickingColor, color) < 0.01f)
                return obj;
            
            // Also search children
            var found = FindInHierarchy(obj, color);
            if (found != null) return found;
        }
        return null;
    }

    private SceneObject? FindInHierarchy(SceneObject obj, Vector3 color)
    {
        foreach (var child in obj.Children)
        {
            if (Vector3.Distance(child.PickingColor, color) < 0.01f)
                return child;
            var found = FindInHierarchy(child, color);
            if (found != null) return found;
        }
        return null;
    }

    private void StartManipulation(SceneObject obj, ManipulationMode mode)
    {
        _isManipulating = true;
        _manipMode = mode;
        _initialMousePos = ImGui.GetIO().MousePos;
        
        _initialSelectedState.Clear();
        var selection = _app.Scene.SelectedObjects.ToList();
        if (!selection.Contains(obj)) selection.Add(obj);

        foreach (var selected in selection)
        {
            _initialSelectedState[selected] = new TransformState {
                Pos = selected.Transform.Position,
                Rot = selected.Transform.Rotation,
                Sca = selected.Transform.Scale
            };
        }

        // Cache top-level selection to avoid expensive per-frame recursion
        _topSelection = selection.Where(s => !IsAnyParentSelected(s, selection)).ToList();

        _initialTransformValue = obj.Transform.Position;
        if (mode == ManipulationMode.Rotate) _initialTransformValue = obj.Transform.Rotation;
        if (mode == ManipulationMode.Scale) _initialTransformValue = obj.Transform.Scale;
        
        var kp = obj.GetComponent<KeypointComponent>();
        if (kp != null) _initialBoneOffset = kp.BoneOffset;
    }

    private void CommitManipulation()
    {
        if (_initialSelectedState.Count == 0) return;

        // Capture end state
        var endState = new Dictionary<SceneObject, TransformState>();
        foreach (var kvp in _initialSelectedState)
        {
            var obj = kvp.Key;
            endState[obj] = new TransformState {
                Pos = obj.Transform.Position,
                Rot = obj.Transform.Rotation,
                Sca = obj.Transform.Scale
            };
        }

        // Capture initial state (snapshot for command)
        var startState = new Dictionary<SceneObject, TransformState>(_initialSelectedState);

        _app.CommandHistory.Push(new Commands.ActionCommand(
            undoAction: () => {
                foreach (var kvp in startState) {
                    var obj = kvp.Key;
                    obj.Transform.Position = kvp.Value.Pos;
                    obj.Transform.Rotation = kvp.Value.Rot;
                    obj.Transform.Scale = kvp.Value.Sca;
                }
            },
            redoAction: () => {
                foreach (var kvp in endState) {
                    var obj = kvp.Key;
                    obj.Transform.Position = kvp.Value.Pos;
                    obj.Transform.Rotation = kvp.Value.Rot;
                    obj.Transform.Scale = kvp.Value.Sca;
                }
            }
        ));
        
        _initialSelectedState.Clear();
    }

    private void CancelManipulation(SceneObject? obj)
    {
        foreach (var kvp in _initialSelectedState)
        {
            var target = kvp.Key;
            var state = kvp.Value;
            target.Transform.Position = state.Pos;
            target.Transform.Rotation = state.Rot;
            target.Transform.Scale = state.Sca;
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
            UndoableDragFloat3("Position", sel.Transform.Position, v => sel.Transform.Position = v, 0.1f);
            UndoableDragFloat3("Rotation", sel.Transform.Rotation, v => sel.Transform.Rotation = v, 1f);
            UndoableDragFloat3("Scale", sel.Transform.Scale, v => sel.Transform.Scale = v, 0.05f);
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

            UndoableColorEdit3("Seg Color", label.SegmentationColor, v => label.SegmentationColor = v);
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
                UndoableColorEdit4("Base Color", mr.Material.BaseColor, v => mr.Material.BaseColor = v);
                UndoableSliderFloat("Smoothness", mr.Material.Smoothness, v => mr.Material.Smoothness = v, 0f, 1f);
                UndoableSliderFloat("Metallic", mr.Material.Metallic, v => mr.Material.Metallic = v, 0f, 1f);
                UndoableDragFloat("Normal Scale", mr.Material.NormalScale, v => mr.Material.NormalScale = v, 0.01f, 0f, 8f);
                
                if (ImGui.CollapsingHeader("Emission"))
                {
                    UndoableColorEdit3("Emissive Color", mr.Material.EmissiveColor, v => mr.Material.EmissiveColor = v);
                    UndoableDragFloat("Emissive Intensity", mr.Material.EmissiveIntensity, v => mr.Material.EmissiveIntensity = v, 0.1f, 0f, 100f);
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
            
            // Allow clearing texture with Delete key when button is focused
            if (ImGui.IsItemFocused() && _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Delete))
            {
                ApplyTextureRecursive(sel, 0, null, isAlbedo: true);
                AddLog("[Material] Cleared Albedo texture");
                _suppressObjectDelete = true;
            }

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
                        // Apply ONLY to the selected object and its descendants
                        ApplyTextureRecursive(sel, texId, destPath, isAlbedo: true);
                        AddLog($"[Material] Applied Albedo to {sel.Name} + children: {fileName}");
                    }
                }
            }

            // --- Normal Slot ---
            ImGui.Text("Normal:");
            ImGui.SameLine();
            string normalName = firstMr?.Material.NormalTexturePath != null ? Path.GetFileName(firstMr.Material.NormalTexturePath) : "None";
            if (ImGui.Button($"{normalName}##Normal", new Vector2(ImGui.GetContentRegionAvail().X - 60, 0))) { }

            // Allow clearing texture with Delete key when button is focused
            if (ImGui.IsItemFocused() && _app.Input.WasKeyJustPressed(Silk.NET.Input.Key.Delete))
            {
                ApplyTextureRecursive(sel, 0, null, isAlbedo: false);
                AddLog("[Material] Cleared Normal texture");
                _suppressObjectDelete = true;
            }

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
                        // Apply ONLY to the selected object and its descendants
                        ApplyTextureRecursive(sel, texId, destPath, isAlbedo: false);
                        AddLog($"[Material] Applied Normal to {sel.Name} + children: {fileName}");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // --- Smoothness ---
            float smoothness = firstMr?.Material.Smoothness ?? 0.5f;
            UndoableSliderFloat("Smoothness", smoothness, v => ApplyMaterialPropertyRecursive(sel, m => m.Smoothness = v), 0f, 1f);

            // --- Metallic ---
            float metallic = firstMr?.Material.Metallic ?? 0.0f;
            UndoableSliderFloat("Metallic", metallic, v => ApplyMaterialPropertyRecursive(sel, m => m.Metallic = v), 0f, 1f);

            // --- Normal Scale ---
            float nScale = firstMr?.Material.NormalScale ?? 1.0f;
            UndoableSliderFloat("Normal Scale", nScale, v => ApplyMaterialPropertyRecursive(sel, m => m.NormalScale = v), 0f, 5f);

            // --- Color Intensity ---
            float cIntensity = firstMr?.Material.ColorIntensity ?? 1.0f;
            UndoableSliderFloat("Color Intensity", cIntensity, v => ApplyMaterialPropertyRecursive(sel, m => m.ColorIntensity = v), 0f, 5f);
        }

        // ── Light ──
        var light = sel.GetComponent<LightComponent>();
        if (light != null && ImGui.CollapsingHeader("Light", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int lt = (int)light.LightType;
            if (ImGui.Combo("Type", ref lt, "Directional\0Point\0Spot\0"))
            {
                light.LightType = (LightType)lt;
            }

            UndoableColorEdit3("Color", light.Color, v => light.Color = v);
            UndoableDragFloat("Intensity", light.Intensity, v => light.Intensity = v, 0.05f, 0f, 10f);
            ImGui.Checkbox("Cast Shadow", ref light.CastShadow);
        }

        // ── Buoyancy (The Push) ──
        var buoy = sel.GetComponent<BuoyantBodyComponent>();
        if (buoy != null && ImGui.CollapsingHeader("Buoyancy", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Enabled", ref buoy.Enabled);
            UndoableDragFloat("Waterline", buoy.Waterline, v => buoy.Waterline = v, 0.05f, -10f, 10f);
            UndoableSliderFloat("Bob Intensity", buoy.BobIntensity, v => buoy.BobIntensity = v, 0f, 5f);
            UndoableSliderFloat("Tilt Intensity", buoy.TiltIntensity, v => buoy.TiltIntensity = v, 0f, 5f);
            
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
            
            if (animMr != null && animMr.Mesh != null && animMr.Mesh.Clips.Count > current)
            {
                float duration = animMr.Mesh.Clips[current].Duration / animMr.Mesh.Clips[current].TicksPerSecond;
                float time = anim.PlaybackTime;
                ImGui.ProgressBar(time / Math.Max(duration, 0.001f), new Vector2(-1, 0), $"{time:F2}s / {duration:F2}s");
            }
            
            if (ImGui.DragFloat("Time", ref anim.PlaybackTime, 0.01f, 0, anim.ClipDurationSeconds))
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


        // ── Pose Keypoint Setup ──
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.6f, 1), "Bone Mapping");

        // Always resolve to root parent for keypoint checking
        var kpRoot = sel;
        while (kpRoot.Parent != null) kpRoot = kpRoot.Parent;

        // Standard Picker (Always use the root's standard)
        string[] standardNames = Enum.GetNames(typeof(Annotation.PoseStandardType));
        int currentStdIdx = (int)kpRoot.PoseStandard;
        if (ImGui.Combo("Standard", ref currentStdIdx, standardNames, standardNames.Length))
        {
            kpRoot.PoseStandard = (Annotation.PoseStandardType)currentStdIdx;
            AddLog($"[Pose] Switched '{kpRoot.Name}' to {kpRoot.PoseStandard} standard");
        }

        var std = Annotation.KeypointRegistry.GetStandard(kpRoot.PoseStandard);
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.6f, 1), $"{std.Name} Keypoints");

        int kpCount = CountKeypointChildren(kpRoot);

        // If the selected object IS a keypoint node, show its info + back button
        var selKp = sel.GetComponent<KeypointComponent>();
        if (selKp != null)
        {
            ImGui.TextColored(new Vector4(1f, 1f, 0.3f, 1), $"🎯 Editing: [{selKp.KeypointIndex}] {selKp.KeypointName}");
            if (!string.IsNullOrEmpty(selKp.BoundBoneName))
            {
                ImGui.TextColored(new Vector4(0.5f, 1f, 0.8f, 1), $"🔗 Bound to: {selKp.BoundBoneName}");
                if (ImGui.SmallButton("Unbind##unbindkp"))
                {
                    selKp.BoundBoneName = null;
                    AddLog($"[Pose] Unbound keypoint [{selKp.KeypointIndex}] {selKp.KeypointName}");
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1), "⚠ Unbound — pick a bone below");
            }

            // Bone picker dropdown — get all bones from the skeleton
            var rootMr = FindFirstSkinnedMeshRenderer(kpRoot);
            if (rootMr?.Mesh?.Skeleton != null)
            {
                var boneNames = new List<string> { "(none)" };
                boneNames.AddRange(rootMr.Mesh.Skeleton.BonesByName.Keys);

                int currentBoneIdx = 0;
                if (!string.IsNullOrEmpty(selKp.BoundBoneName))
                {
                    int found = boneNames.IndexOf(selKp.BoundBoneName);
                    if (found >= 0) currentBoneIdx = found;
                }

                var boneArray = boneNames.ToArray();
                if (ImGui.Combo("Bind to Bone", ref currentBoneIdx, boneArray, boneArray.Length))
                {
                    if (currentBoneIdx == 0)
                    {
                        selKp.BoundBoneName = null;
                        AddLog($"[Pose] Unbound [{selKp.KeypointIndex}] {selKp.KeypointName}");
                    }
                    else
                    {
                        selKp.BoundBoneName = boneArray[currentBoneIdx];
                        
                        // SNAP TO BONE: Clear any manual offset so it jumps exactly to the bone center.
                        // You can still fine-tune it with the move tool afterwards.
                        selKp.BoneOffset = Vector3.Zero;

                        AddLog($"[Pose] Bound [{selKp.KeypointIndex}] {selKp.KeypointName} → {selKp.BoundBoneName} (Snapped to bone origin)");
                    }
                }
            }

            if (ImGui.Button("⬆ Back to Parent", new Vector2(-1, 0)))
            {
                _app.Scene.SelectedObject = kpRoot;
            }
            ImGui.Spacing();
        }

        ImGui.Text($"Keypoints: {kpCount}/{std.Keypoints.Count}");

        if (kpCount == 0)
        {
            if (ImGui.Button($"Setup {std.Name}", new Vector2(-1, 0)))
            {
                SetupKeypointsForObject(sel, sel.PoseStandard);
                AddLog($"[Pose] Created {std.Keypoints.Count} {std.Name} keypoint nodes under {sel.Name}");
            }
            ImGui.TextWrapped($"Creates automated empty child nodes for the {std.Name} standard. Select each node to reposition it on the model.");
        }
        else
        {
            // Show keypoint status list based on the active standard
            var kpNames = std.Keypoints;
            foreach (var kvp in kpNames)
            {
                int i = kvp.Key;
                string name = kvp.Value;
                var kpNode = FindKeypointChild(kpRoot, i);
                if (kpNode != null)
                {
                    bool isCurrentlySelected = (kpNode == sel);
                    if (isCurrentlySelected)
                        ImGui.TextColored(new Vector4(1f, 1f, 0.3f, 1), "▶");
                    else
                        ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1), "✅");
                    ImGui.SameLine();
                    if (ImGui.Selectable($"[{i}] {name}##kp{i}", isCurrentlySelected))
                    {
                        _app.Scene.SelectedObject = kpNode;
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 0.3f), $"  [ ] [{i}] {name} (missing)");
                }
            }

            if (ImGui.Button("Remove All Keypoints", new Vector2(-1, 0)))
            {
                RemoveKeypointsFromObject(kpRoot);
                AddLog($"[Pose] Removed all keypoint nodes from {kpRoot.Name}");
            }
        }

        // ── Randomization ──
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1), "[ Individual Randomizers ]");
        
        bool exclude = sel.ExcludeFromRandomization;
        if (ImGui.Checkbox("EXCLUDE Entire Object (Ignore ALL)", ref exclude))
            sel.ExcludeFromRandomization = exclude;

        if (!exclude)
        {
            // --- Position ---
            bool hasPos = sel.HasComponent<PositionRandomizerComponent>();
            if (ImGui.Checkbox("Position Randomization", ref hasPos))
            {
                if (hasPos) sel.AddComponent(new PositionRandomizerComponent());
                else sel.RemoveComponent<PositionRandomizerComponent>();
            }
            var pComp = sel.GetComponent<PositionRandomizerComponent>();
            if (pComp != null)
            {
                ImGui.Columns(2, "pos_cols", false);
                ImGui.DragFloat3("Min##p", ref pComp.MinBounds, 0.1f); ImGui.NextColumn();
                ImGui.DragFloat3("Max##p", ref pComp.MaxBounds, 0.1f); ImGui.Columns(1);
            }

            // --- Rotation ---
            bool hasRot = sel.HasComponent<RotationRandomizerComponent>();
            if (ImGui.Checkbox("Rotation Randomization", ref hasRot))
            {
                if (hasRot) sel.AddComponent(new RotationRandomizerComponent());
                else sel.RemoveComponent<RotationRandomizerComponent>();
            }
            var rComp = sel.GetComponent<RotationRandomizerComponent>();
            if (rComp != null)
            {
                ImGui.Columns(2, "rot_cols", false);
                ImGui.DragFloat3("Min Angles##r", ref rComp.MinAngles, 1f); ImGui.NextColumn();
                ImGui.DragFloat3("Max Angles##r", ref rComp.MaxAngles, 1f); ImGui.Columns(1);
            }

            // --- Scale ---
            bool hasScl = sel.HasComponent<ScaleRandomizerComponent>();
            if (ImGui.Checkbox("Scale Randomization", ref hasScl))
            {
                if (hasScl) sel.AddComponent(new ScaleRandomizerComponent());
                else sel.RemoveComponent<ScaleRandomizerComponent>();
            }
            var sComp = sel.GetComponent<ScaleRandomizerComponent>();
            if (sComp != null)
            {
                ImGui.DragFloat("Min Scale##s", ref sComp.MinScale, 0.05f);
                ImGui.DragFloat("Max Scale##s", ref sComp.MaxScale, 0.05f);
                ImGui.Checkbox("Uniform Scale", ref sComp.UniformScale);
            }

            // --- Texture ---
            bool hasTex = sel.HasComponent<TextureRandomizerComponent>();
            if (ImGui.Checkbox("Simple Texture Random", ref hasTex))
            {
                if (hasTex) sel.AddComponent(new TextureRandomizerComponent());
                else sel.RemoveComponent<TextureRandomizerComponent>();
            }

            // --- Material (Advanced) ---
            bool hasMat = sel.HasComponent<MaterialRandomizerComponent>();
            if (ImGui.Checkbox("Advanced Material Random", ref hasMat))
            {
                if (hasMat) sel.AddComponent(new MaterialRandomizerComponent());
                else sel.RemoveComponent<MaterialRandomizerComponent>();
            }
            var mComp = sel.GetComponent<MaterialRandomizerComponent>();
            if (mComp != null)
            {
                ImGui.Indent();
                ImGui.Checkbox("Randomize Color", ref mComp.RandomizeColor);
                ImGui.Checkbox("Randomize Texture", ref mComp.RandomizeTexture);
                ImGui.Checkbox("Randomize Emissive", ref mComp.RandomizeEmissive);
                
                if (mComp.RandomizeColor)
                {
                    ImGui.Checkbox("Use Custom Palette", ref mComp.UsePalette);
                    if (mComp.UsePalette)
                    {
                        // Color Selector Dropdown
                        string[] colorNames = mComp.ColorPalette.Select((c, i) => $"Color {i}").ToArray();
                        int selIdx = mComp.SelectedColorIndex;
                        if (ImGui.Combo("Select Color##pal", ref selIdx, colorNames, colorNames.Length))
                        {
                            mComp.SelectedColorIndex = selIdx;
                            // Apply for preview
                            ApplyMaterialPropertyRecursive(sel, m => m.BaseColor = mComp.ColorPalette[selIdx]);
                        }

                        for (int i = 0; i < mComp.ColorPalette.Count; i++)
                        {
                            var c = mComp.ColorPalette[i];
                            if (ImGui.ColorEdit4($"Edit {i}##pal", ref c, ImGuiColorEditFlags.NoInputs))
                            {
                                mComp.ColorPalette[i] = c;
                                if (i == mComp.SelectedColorIndex) ApplyMaterialPropertyRecursive(sel, m => m.BaseColor = c);
                            }
                            if (mComp.ColorPalette.Count > 1)
                            {
                                ImGui.SameLine();
                                if (ImGui.Button($"X##rem{i}")) 
                                {
                                    mComp.ColorPalette.RemoveAt(i);
                                    mComp.SelectedColorIndex = Math.Clamp(mComp.SelectedColorIndex, 0, mComp.ColorPalette.Count - 1);
                                    break;
                                }
                            }
                        }
                        if (ImGui.Button("+ Add Color")) mComp.ColorPalette.Add(new Vector4(1,1,1,1));
                    }
                    else
                    {
                        ImGui.DragFloatRange2("Brightness Range", ref mComp.MinBrightness, ref mComp.MaxBrightness, 0.01f, 0f, 2f);
                    }
                }

                if (mComp.RandomizeTexture)
                {
                    // Texture Selector Dropdown with inline Delete (LOCAL POOL)
                    var pool = mComp.LocalTexturePool;
                    if (pool.Count > 0)
                    {
                        string preview = string.IsNullOrEmpty(mComp.SelectedTexturePath) ? "Select Texture..." : Path.GetFileName(mComp.SelectedTexturePath);
                        if (ImGui.BeginCombo("Texture Pool", preview))
                        {
                            for (int i = 0; i < pool.Count; i++)
                            {
                                string path = pool[i];
                                string name = Path.GetFileName(path);
                                bool selected = (path == mComp.SelectedTexturePath);

                                if (ImGui.Selectable($"{name}##tex{i}", selected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - 60, 0)))
                                {
                                    mComp.SelectedTexturePath = path;
                                    uint id = _app.AssetManager.LoadTexture(path);
                                    if (id > 0) ApplyTextureRecursive(sel, id, path, true);
                                }

                                ImGui.SameLine();
                                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                                if (ImGui.SmallButton($"Remove##{i}"))
                                {
                                    pool.RemoveAt(i);
                                    if (mComp.SelectedTexturePath == path) mComp.SelectedTexturePath = null;
                                    ImGui.PopStyleColor();
                                    ImGui.EndCombo();
                                    return; 
                                }
                                ImGui.PopStyleColor();

                                if (selected) ImGui.SetItemDefaultFocus();
                            }
                            ImGui.EndCombo();
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("(Texture Pool is Empty)");
                    }

                    if (ImGui.Button("🚀 Batch Import Textures..."))
                    {
                        var paths = PickMultipleFilesWithDialog("Images (*.png;*.jpg;*.jpeg;*.tga)|*.png;*.jpg;*.jpeg;*.tga|All files (*.*)|*.*");
                        foreach (var path in paths)
                        {
                            string fileName = Path.GetFileName(path);
                            string dest = Path.Combine(_app.AssetManager.TexturesPath, fileName);
                            try {
                                if (!File.Exists(dest)) File.Copy(path, dest, true);
                                if (!mComp.LocalTexturePool.Contains(dest)) mComp.LocalTexturePool.Add(dest);
                                mComp.SelectedTexturePath = dest; 
                                _app.AssetManager.LoadTexture(dest);
                            } catch { }
                        }
                    }
                    
                    if (pool.Count > 0)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("🗑 Clear List")) pool.Clear();
                    }

                }

                if (mComp.RandomizeEmissive)
                {
                    ImGui.DragFloatRange2("Emissive Range", ref mComp.MinEmissive, ref mComp.MaxEmissive, 0.1f, 0f, 10f);
                }
                ImGui.Unindent();
            }
        }

        ImGui.Separator();
        if (sel.GetComponent<Scene.Components.BuoyantBodyComponent>() == null)
        {
            if (ImGui.Button("Add Buoyancy", new Vector2(-1, 0)))
                sel.AddComponent(new Scene.Components.BuoyantBodyComponent());
        }

        ImGui.Separator();
        if (ImGui.Button("Delete Object", new Vector2(-1, 0)))
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
            if (r.Category == "Object") continue;

            if (r.Category != lastCategory)
            {
                if (lastCategory != null) ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), $"--- {r.Category} ---");
                lastCategory = r.Category;
            }

            var sel_obj = _app.Scene.SelectedObject;
            
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
                    if (hr.CurrentHDRI == null) RefreshHDRIs();
                    else ImportHdriWithDialog();
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

            // --- OceanWaves Header ---
            ImGui.TextUnformatted("— OceanWaves");
            ImGui.Text($"FPS: {(int)ImGui.GetIO().Framerate} ({1000f/ImGui.GetIO().Framerate:F2}ms)");
            
            ImGui.Checkbox("Enable Sea Spray:", ref cfg.EnableSeaSpray);
            
            string[] resNames = { "512x512", "1024x1024", "2048x2048" };
            ImGui.Combo("Wave Resolution:", ref cfg.WaveResolutionIndex, resNames, resNames.Length);
            
            string[] qualNames = { "Low", "High", "Ultra" };
            ImGui.Combo("Wave Mesh Quality:", ref cfg.WaveMeshQualityIndex, qualNames, qualNames.Length);
            
            ImGui.DragFloat("Updates per Second:", ref cfg.UpdatesPerSecond, 1f, 1, 120);
            
            ImGui.ColorEdit3("Water Color:", ref cfg.WaterColor, ImGuiColorEditFlags.NoInputs);
            ImGui.ColorEdit3("Foam Color:", ref cfg.FoamColor, ImGuiColorEditFlags.NoInputs);

            ImGui.Spacing();
            ImGui.TextUnformatted("— Wave Parameters");
            
            if (ImGui.BeginTabBar("WaveCascades"))
            {
                if (ImGui.BeginTabItem("Cascade 1"))
                {
                    ImGui.DragFloat2("Tile Length:", ref cfg.TileLength, 1f, 1f, 500f);
                    ImGui.DragFloat("Time Scale:", ref cfg.TimeScale, 0.05f, 0f, 10f);
                    
                    ImGui.Separator();
                    ImGui.DragFloat("Wind Speed:", ref cfg.WindSpeed, 0.5f, 0f, 60f);
                    ImGui.DragFloat("Wind Direction:", ref cfg.WindDirection, 1f, -360f, 360f, "%.0f deg");
                    ImGui.DragFloat("Fetch Length:", ref cfg.FetchLength, 1f, 0.1f, 1000f);
                    ImGui.DragFloat("Swell:", ref cfg.Swell, 0.01f, 0f, 1f);
                    ImGui.DragFloat("Detail:", ref cfg.Detail, 0.01f, 0f, 1f);
                    ImGui.DragFloat("Spread:", ref cfg.Spread, 0.01f, 0f, 1f);
                    
                    ImGui.Separator();
                    ImGui.DragFloat("Whitecap:", ref cfg.Whitecap, 0.01f, 0f, 2f);
                    ImGui.DragFloat("Foam Amount:", ref cfg.FoamAmount, 0.1f, 0f, 10f);
                    
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("— Camera");
            var cam = _app.Scene.ActiveCamera;
            if (cam != null)
            {
                var camPos = cam.Transform.Position;
                ImGui.Text($"Camera Position: ({camPos.X:F2}, {camPos.Y:F2}, {camPos.Z:F2})");
                
                float fov = cam.FieldOfView;
                if (ImGui.DragFloat("Camera FOV:", ref fov, 0.5f, 10, 170))
                    cam.FieldOfView = fov;
            }

            ImGui.Separator();
            ImGui.TextDisabled("Press Ctrl-H to toggle GUI visibility!");
            ImGui.TextDisabled("Press Ctrl-F to toggle fullscreen!");
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
            ImGui.DragFloat("Anim Duration (s)", ref animDur, 0.05f, 0.1f, 10f);
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

    // ═══ Training Prompt (Post-Generation) ═══════════════════════════════════
    private void RenderTrainingPrompt()
    {
        // Detect when generation just completed
        if (_app.CaptureManager.GenerationJustCompleted)
        {
            _app.CaptureManager.GenerationJustCompleted = false;
            _generatedImageCount = _app.CaptureManager.LastImageCount;
            _showTrainingPrompt = true;
            ImGui.OpenPopup("TrainingPrompt");
        }

        // Keep the popup open if we're showing the prompt
        if (_showTrainingPrompt && !ImGui.IsPopupOpen("TrainingPrompt"))
            ImGui.OpenPopup("TrainingPrompt");

        // Center the popup
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(420, 0));

        if (ImGui.BeginPopupModal("TrainingPrompt", ref _showTrainingPrompt, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), "✅ Dataset Generation Complete!");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text($"Generated {_generatedImageCount} images.");
            ImGui.Text($"Output: {Path.GetFullPath(_app.CaptureManager.OutputDirectory)}");
            ImGui.Spacing();

            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Proceed to training?");
            ImGui.Spacing();

            float buttonWidth = 180;
            float totalWidth = buttonWidth * 2 + ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - totalWidth) * 0.5f);

            if (ImGui.Button("🚀 Yes, Train Now", new Vector2(buttonWidth, 35)))
            {
                _showTrainingPanel = true;
                _showTrainingPrompt = false;
                ImGui.CloseCurrentPopup();
                AddLog("[UI] Training panel opened.");
            }

            ImGui.SameLine();

            if (ImGui.Button("❌ No, Later", new Vector2(buttonWidth, 35)))
            {
                _showTrainingPrompt = false;
                ImGui.CloseCurrentPopup();
                AddLog("[UI] Training skipped. Open from View menu anytime.");
            }

            ImGui.EndPopup();
        }
    }

    // ═══ Training Panel ══════════════════════════════════════════════════════
    private void RenderTrainingPanel()
    {
        ImGui.Begin("Training", GetWindowFlags());

        var tm = _app.TrainingManager;

        // ── Status Bar ──
        Vector4 statusColor = tm.Status switch
        {
            Training.TrainingManager.TrainStatus.Training => new Vector4(0.3f, 1f, 0.5f, 1f),
            Training.TrainingManager.TrainStatus.Preparing => new Vector4(1f, 0.8f, 0.3f, 1f),
            Training.TrainingManager.TrainStatus.Complete => new Vector4(0.3f, 0.8f, 1f, 1f),
            Training.TrainingManager.TrainStatus.Failed => new Vector4(1f, 0.3f, 0.3f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f)
        };
        ImGui.TextColored(statusColor, $"Status: {tm.Status}");

        if (!string.IsNullOrEmpty(tm.CurrentEpochInfo))
        {
            ImGui.TextWrapped(tm.CurrentEpochInfo);
        }

        // ── Environment Status ──
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Environment Readiness");
        
        ImGui.Text("Python:"); ImGui.SameLine(100);
        ImGui.TextColored(tm.IsPythonInstalled ? new Vector4(0.3f, 1f, 0.5f, 1f) : new Vector4(1, 0.3f, 0.3f, 1f), tm.IsPythonInstalled ? "OK" : "MISSING");

        ImGui.Text("YOLOv8:"); ImGui.SameLine(100);
        ImGui.TextColored(tm.IsYoloInstalled ? new Vector4(0.3f, 1f, 0.5f, 1f) : new Vector4(1, 0.3f, 0.3f, 1f), tm.IsYoloInstalled ? "OK" : "MISSING");

        ImGui.Text("CUDA:"); ImGui.SameLine(100);
        ImGui.TextColored(tm.GpuAvailable ? new Vector4(0.3f, 1f, 0.5f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f), tm.GpuAvailable ? "AVAILABLE" : "NOT FOUND (CPU ONLY)");

        if (!tm.IsPythonInstalled || !tm.IsYoloInstalled || !tm.GpuAvailable)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.4f, 0.1f, 1.0f));
            string btnText = (!tm.IsPythonInstalled || !tm.IsYoloInstalled) 
                ? "🔧 Setup Python Environment" 
                : "🔧 Upgrade Environment for GPU (CUDA)";
                
            if (ImGui.Button(btnText, new Vector2(-1, 0)))
            {
                tm.InstallDependencies();
            }
            ImGui.PopStyleColor();
            
            if (!tm.GpuAvailable && tm.IsPythonInstalled)
                ImGui.TextWrapped("Click above to install the CUDA-accelerated PyTorch. (Currently using CPU-only version)");
            else
                ImGui.TextWrapped("Click above to install required Python packages (Ultralytics & Torch).");
        }

        if (ImGui.Button("Re-check Environment", new Vector2(-1, 0))) 
            tm.CheckEnvironment();

        ImGui.Separator();

        // ── Model Configuration ──
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Model Configuration");

        int modelIdx = tm.ModelSizeIndex;
        if (ImGui.Combo("Model Size", ref modelIdx, "YOLOv8n (Nano)\0YOLOv8s (Small)\0YOLOv8m (Medium)\0YOLOv8l (Large)\0YOLOv8x (XLarge)\0"))
            tm.ModelSizeIndex = modelIdx;

        int taskIdx = tm.TaskIndex;
        if (ImGui.Combo("Task", ref taskIdx, "Detect\0Segment\0Pose\0"))
            tm.TaskIndex = taskIdx;

        int deviceIdx = tm.DeviceIndex;
        string[] items = tm.GpuAvailable ? new[] { "GPU (0)", "CPU" } : new[] { "(No GPU)", "CPU" };
        if (ImGui.Combo("Device", ref deviceIdx, string.Join("\0", items) + "\0"))
        {
            if (tm.GpuAvailable || deviceIdx == 1) // Only allow 0 if GPU is available
                tm.DeviceIndex = deviceIdx;
        }
        if (!tm.GpuAvailable && tm.DeviceIndex == 0) tm.DeviceIndex = 1;

        ImGui.Separator();

        // ── Hyperparameters ──
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Hyperparameters");

        int epochs = tm.Epochs;
        if (ImGui.DragInt("Epochs", ref epochs, 1, 1, 500))
            tm.Epochs = epochs;

        int batch = tm.BatchSize;
        if (ImGui.DragInt("Batch Size", ref batch, 1, 1, 128))
            tm.BatchSize = batch;

        int imgSize = tm.ImgSize;
        if (ImGui.DragInt("Image Size", ref imgSize, 16, 320, 1280))
            tm.ImgSize = imgSize;

        float lr = tm.LearningRate;
        if (ImGui.DragFloat("Learning Rate", ref lr, 0.0001f, 0.0001f, 0.1f, "%.6f"))
            tm.LearningRate = lr;

        int patience = tm.Patience;
        if (ImGui.DragInt("Patience", ref patience, 1, 0, 100))
            tm.Patience = patience;

        int workers = tm.Workers;
        if (ImGui.DragInt("Workers", ref workers, 1, 0, 16))
            tm.Workers = workers;

        ImGui.Separator();

        // ── Dataset Settings ──
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Dataset");

        float split = tm.TrainValSplit;
        if (ImGui.DragFloat("Train/Val Split", ref split, 0.01f, 0.5f, 0.95f, "%.2f"))
            tm.TrainValSplit = split;

        bool resume = tm.ResumeTraining;
        if (ImGui.Checkbox("Resume from last.pt", ref resume))
            tm.ResumeTraining = resume;

        ImGui.Separator();

        // ── Action Buttons ──
        bool canTrain = tm.IsPythonInstalled && tm.IsYoloInstalled;
        if (!canTrain) ImGui.BeginDisabled();
        
        if (!tm.IsTraining)
        {
            if (ImGui.Button("🚀 Start Training", new Vector2(-1, 35)))
            {
                string datasetDir = Path.GetFullPath(_app.CaptureManager.OutputDirectory);
                tm.StartTraining(datasetDir);
            }
        }
        else
        {
            if (ImGui.Button("⏹ Stop Training", new Vector2(-1, 35)))
            {
                tm.StopTraining();
            }
        }

        if (!canTrain)
        {
            ImGui.EndDisabled();
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Setup environment above to enable training.");
        }

        // ── Results ──
        if (tm.Status == Training.TrainingManager.TrainStatus.Complete && !string.IsNullOrEmpty(tm.BestWeightsPath))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), "✅ Training Complete!");
            ImGui.TextWrapped($"Weights: {tm.BestWeightsPath}");
            if (ImGui.Button("Open Results Folder"))
            {
                string? dir = Path.GetDirectoryName(tm.BestWeightsPath);
                if (dir != null && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
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

        // Take a snapshot to avoid "Collection was modified" during enumeration
        // (CaptureManager/TrainingManager callbacks add to _logs from Update thread)
        var logsSnapshot = _logs.ToArray();
        foreach (var log in logsSnapshot)
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
    obj.AssetPath = $"primitive:{type}";

        _app.Scene.AddObject(obj);
        _app.Scene.SelectedObject = obj;
        AddLog($"[Scene] Added {type} (unlabeled)");
    }

    private void AddModelFromFile(string path)
    {
        var root = _app.AssetManager.ImportModelHierarchical(path, AddLog);
        if (root == null) return;
    root.AssetPath = path;
 
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

    private void RefreshTexturePools()
    {
        var files = _app.AssetManager.GetAvailableTextures();
        
        var texRand = _allRandomizers.OfType<TextureRandomizer>().FirstOrDefault();
        if (texRand != null)
        {
            texRand.TexturePaths.Clear();
            texRand.TexturePaths.AddRange(files);
            texRand.LoadTextureFunc = _app.AssetManager.LoadTexture;
        }

        var matRand = _allRandomizers.OfType<MaterialRandomizer>().FirstOrDefault();
        if (matRand != null)
        {
            // We no longer auto-fill global PoolTexturePaths for MaterialRandomizer 
            // because the user wants per-object pools.
            matRand.LoadTextureFunc = _app.AssetManager.LoadTexture;
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

    private List<string> PickMultipleFilesWithDialog(string filter)
    {
        AddLog("[Assets] Opening multi-file dialog...");
        try
        {
            using var f = new System.Windows.Forms.OpenFileDialog();
            f.Filter = filter;
            f.Multiselect = true;
            f.Title = "Select Files to Import";
            if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return f.FileNames.ToList();
            }
            return new List<string>();
        }
        catch (Exception ex)
        {
            AddLog($"[Error] Native dialog failed: {ex.Message}");
            return new List<string>();
        }
    }

    private string? PickFileWithDialog(string filter)
    {
        AddLog("[Assets] Opening file dialog...");
        try
        {
            using var f = new System.Windows.Forms.OpenFileDialog();
            f.Filter = filter;
            f.Title = "Select File to Import";
            if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return f.FileName;
            }
            return null;
        }
        catch (Exception ex)
        {
            AddLog($"[Error] Native dialog failed: {ex.Message}");
            return null;
        }
    }

    private string? SaveFileWithDialog(string filter, string defaultExt)
    {
        AddLog("[Scenario] Opening save dialog...");
        try
        {
            using var f = new System.Windows.Forms.SaveFileDialog();
            f.Filter = filter;
            f.DefaultExt = defaultExt;
            f.Title = "Save Scenario";
            if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return f.FileName;
            }
            return null;
        }
        catch (Exception ex)
        {
            AddLog($"[Error] Native save dialog failed: {ex.Message}");
            return null;
        }
    }

    private void ApplyTextureRecursive(SceneObject obj, uint texId, string? texPath, bool isAlbedo)
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

    /// <summary>
    /// Finds the first MeshRendererComponent with a valid skeleton in the hierarchy.
    /// Used for bone binding dropdowns so all imported animated models show their bones.
    /// </summary>
    private MeshRendererComponent? FindFirstSkinnedMeshRenderer(SceneObject obj)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr?.Mesh != null && mr.Mesh.HasSkinning && mr.Mesh.Skeleton != null)
            return mr;

        foreach (var child in obj.Children)
        {
            var found = FindFirstSkinnedMeshRenderer(child);
            if (found != null) return found;
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

    private bool IsAnyParentSelected(SceneObject obj, List<SceneObject> selection)
    {
        var curr = obj.Parent;
        while (curr != null)
        {
            if (selection.Contains(curr)) return true;
            curr = curr.Parent;
        }
        return false;
    }

    private bool IsDescendantOf(SceneObject parent, SceneObject potentialDescendant)
    {
        foreach (var child in parent.Children)
        {
            if (child == potentialDescendant) return true;
            if (IsDescendantOf(child, potentialDescendant)) return true;
        }
        return false;
    }

    // ── Undoable UI Wrappers ──
    private object? _dragInitialValue;

    private bool UndoableDragFloat3(string label, Vector3 currentValue, Action<Vector3> setter, float speed = 1f)
    {
        Vector3 val = currentValue;
        bool changed = ImGui.DragFloat3(label, ref val, speed);
        
        if (ImGui.IsItemActivated()) _dragInitialValue = currentValue;
        if (changed) setter(val);
        
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragInitialValue is Vector3 oldVal)
        {
            Vector3 newVal = val;
            _app.CommandHistory.Push(new Commands.ActionCommand(
                () => setter(oldVal),
                () => setter(newVal)
            ));
            _dragInitialValue = null;
        }
        return changed;
    }

    private bool UndoableDragFloat(string label, float currentValue, Action<float> setter, float speed = 1f, float min = 0f, float max = 0f)
    {
        float val = currentValue;
        bool changed = ImGui.DragFloat(label, ref val, speed, min, max);
        
        if (ImGui.IsItemActivated()) _dragInitialValue = currentValue;
        if (changed) setter(val);
        
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragInitialValue is float oldVal)
        {
            float newVal = val;
            _app.CommandHistory.Push(new Commands.ActionCommand(
                () => setter(oldVal),
                () => setter(newVal)
            ));
            _dragInitialValue = null;
        }
        return changed;
    }

    private bool UndoableSliderFloat(string label, float currentValue, Action<float> setter, float min, float max)
    {
        float val = currentValue;
        float speed = (max - min) * 0.005f; // Auto speed: 0.5% of range per pixel
        bool changed = ImGui.DragFloat(label, ref val, speed, min, max);
        
        if (ImGui.IsItemActivated()) _dragInitialValue = currentValue;
        if (changed) setter(val);
        
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragInitialValue is float oldVal)
        {
            float newVal = val;
            _app.CommandHistory.Push(new Commands.ActionCommand(
                () => setter(oldVal),
                () => setter(newVal)
            ));
            _dragInitialValue = null;
        }
        return changed;
    }

    private bool UndoableColorEdit3(string label, Vector3 currentValue, Action<Vector3> setter)
    {
        Vector3 val = currentValue;
        bool changed = ImGui.ColorEdit3(label, ref val);
        
        if (ImGui.IsItemActivated()) _dragInitialValue = currentValue;
        if (changed) setter(val);
        
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragInitialValue is Vector3 oldVal)
        {
            Vector3 newVal = val;
            _app.CommandHistory.Push(new Commands.ActionCommand(
                () => setter(oldVal),
                () => setter(newVal)
            ));
            _dragInitialValue = null;
        }
        return changed;
    }

    private bool UndoableColorEdit4(string label, Vector4 currentValue, Action<Vector4> setter)
    {
        Vector4 val = currentValue;
        bool changed = ImGui.ColorEdit4(label, ref val);
        
        if (ImGui.IsItemActivated()) _dragInitialValue = currentValue;
        if (changed) setter(val);
        
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragInitialValue is Vector4 oldVal)
        {
            Vector4 newVal = val;
            _app.CommandHistory.Push(new Commands.ActionCommand(
                () => setter(oldVal),
                () => setter(newVal)
            ));
            _dragInitialValue = null;
        }
        return changed;
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

    // ── Keypoint Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates 17 empty child nodes under the given object, each tagged with a KeypointComponent.
    /// Auto-binds to skeleton bones if the model has a skeleton.
    /// </summary>
    private void SetupKeypointsForObject(SceneObject parent, Annotation.PoseStandardType type)
    {
        var std = Annotation.KeypointRegistry.GetStandard(type);
        var kpNames = std.Keypoints;
        var mr = FindFirstMeshRenderer(parent);
        float h = 1.7f;
        if (mr?.Mesh != null)
        {
            h = (mr.Mesh.BoundingBoxMax.Y - mr.Mesh.BoundingBoxMin.Y) * parent.Transform.Scale.Y;
            if (h < 0.1f) h = 1.8f;
        }

        // Try to auto-map bones for binding using the chosen standard
        // Use skinned mesh specifically so we find the skeleton on any model
        Dictionary<int, string>? boneMapping = null;
        var skinnedMr = FindFirstSkinnedMeshRenderer(parent);
        if (skinnedMr?.Mesh?.Skeleton != null)
        {
            boneMapping = Annotation.KeypointRegistry.AutoMapBones(std, skinnedMr.Mesh.Skeleton.BonesByName.Keys);
            if (boneMapping.Count > 0)
                AddLog($"[Pose] Auto-bound {boneMapping.Count}/{std.Keypoints.Count} keypoints to skeleton bones using {std.Name} standard");
        }

        foreach (var kvp in std.Keypoints)
        {
            int i = kvp.Key;
            string name = kvp.Value;
            Vector3 offset = Vector3.Zero;

            // Layout based on standard type
            if (type == Annotation.PoseStandardType.Fisheye)
            {
                offset = i switch {
                    0 => new(0, h * 0.93f, 0),        // Head
                    1 => new(0, h * 0.75f, 0),        // Chest
                    2 => new(0.18f, h * 0.82f, 0),    // L Shoulder
                    3 => new(-0.18f, h * 0.82f, 0),   // R Shoulder
                    4 => new(0.40f, h * 0.70f, 0),    // L Elbow
                    5 => new(-0.40f, h * 0.70f, 0),   // R Elbow
                    6 => new(0.55f, h * 0.60f, 0),    // L Hand
                    7 => new(-0.55f, h * 0.60f, 0),   // R Hand
                    _ => Vector3.Zero
                };
            }
            else // COCO 17
            {
                offset = i switch {
                    0 => new(0, h * 0.95f, 0.02f),
                    1 => new(0.03f, h * 0.97f, 0.02f),
                    2 => new(-0.03f, h * 0.97f, 0.02f),
                    3 => new(0.07f, h * 0.95f, 0f),
                    4 => new(-0.07f, h * 0.95f, 0f),
                    5 => new(0.18f, h * 0.82f, 0),
                    6 => new(-0.18f, h * 0.82f, 0),
                    7 => new(0.40f, h * 0.70f, 0),
                    8 => new(-0.40f, h * 0.70f, 0),
                    9 => new(0.55f, h * 0.60f, 0),
                    10 => new(-0.55f, h * 0.60f, 0),
                    11 => new(0.10f, h * 0.50f, 0),
                    12 => new(-0.10f, h * 0.50f, 0),
                    13 => new(0.10f, h * 0.28f, 0),
                    14 => new(-0.10f, h * 0.28f, 0),
                    15 => new(0.10f, h * 0.05f, 0),
                    16 => new(-0.10f, h * 0.05f, 0),
                    _ => Vector3.Zero
                };
            }

            var kpComp = new KeypointComponent(i, name);
            if (boneMapping != null && boneMapping.TryGetValue(i, out var boneName))
                kpComp.BoundBoneName = boneName;

            var node = new SceneObject($"KP_{i}_{name.Replace(" ", "")}");
            node.Transform.Position = parent.Transform.Position + offset;
            node.AddComponent(kpComp);
            parent.AddChild(node);
            _app.Scene.AddObject(node);
        }
    }

    private int CountKeypointChildren(SceneObject obj)
    {
        int count = 0;
        foreach (var child in obj.Children)
        {
            if (child.GetComponent<KeypointComponent>() != null) count++;
            count += CountKeypointChildren(child);
        }
        return count;
    }

    private SceneObject? FindKeypointChild(SceneObject obj, int keypointIndex)
    {
        foreach (var child in obj.Children)
        {
            var kp = child.GetComponent<KeypointComponent>();
            if (kp != null && kp.KeypointIndex == keypointIndex) return child;
            var found = FindKeypointChild(child, keypointIndex);
            if (found != null) return found;
        }
        return null;
    }

    private void RemoveKeypointsFromObject(SceneObject obj)
    {
        var toRemove = new List<SceneObject>();
        CollectKeypointNodes(obj, toRemove);
        foreach (var node in toRemove)
        {
            _app.Scene.RemoveObject(node);
        }
    }

    private void CollectKeypointNodes(SceneObject obj, List<SceneObject> list)
    {
        foreach (var child in obj.Children.ToList())
        {
            if (child.GetComponent<KeypointComponent>() != null)
                list.Add(child);
            else
                CollectKeypointNodes(child, list);
        }
    }

    /// <summary>
    /// Draw COCO skeleton overlay in the viewport for the selected object's keypoints.
    /// </summary>
    private void DrawKeypointOverlay(SceneObject root)
    {
        var cam = _app.Scene.ActiveCamera;
        if (cam == null) return;

        var view = cam.GetViewMatrix();
        float aspect = (float)_app.Renderer.Width / Math.Max(1, _app.Renderer.Height);
        var proj = cam.GetProjectionMatrix(aspect);



        // Project all keypoint world positions to screen
        var currentStd = Annotation.KeypointRegistry.GetStandard(root.PoseStandard);
        var kpScreenPos = new Dictionary<int, Vector2>();

        // Project all keypoint world positions to screen
        foreach (var kvp in currentStd.Keypoints)
        {
            int i = kvp.Key;
            var kpNode = FindKeypointChild(root, i);
            if (kpNode == null) continue;

            var worldPos = kpNode.Transform.Position;
            var clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
            if (clip.W <= 0.001f) continue;

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float sx = (ndcX + 1) * 0.5f * _viewportSize.X;
            float sy = (1 - ndcY) * 0.5f * _viewportSize.Y;

            // Apply Fisheye Warp for UI overlay
            if (MathF.Abs(cam.FisheyeStrength) > 0.001f)
            {
                var warped = Annotation.KeypointAnnotator.WarpFisheye(new Vector2(sx, sy), (int)_viewportSize.X, (int)_viewportSize.Y, cam.FisheyeStrength);
                sx = warped.X;
                sy = warped.Y;
            }

            if (sx >= -50 && sx < _viewportSize.X + 50 && sy >= -50 && sy < _viewportSize.Y + 50)
                kpScreenPos[i] = _viewportScreenPos + new Vector2(sx, sy);
        }

        var drawList = ImGui.GetWindowDrawList();

        var std = Annotation.KeypointRegistry.GetStandard(root.PoseStandard);
        var edges = std.Edges;
        foreach (var (a, b) in edges)
        {
            if (kpScreenPos.TryGetValue(a, out var posA) && kpScreenPos.TryGetValue(b, out var posB))
            {
                uint lineColor;
                if (root.PoseStandard == Annotation.PoseStandardType.Fisheye)
                {
                    lineColor = (a == 0 || b == 0) ? ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.2f, 0.8f)) : ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.8f, 0.8f));
                }
                else
                {
                    if (a <= 4 || b <= 4) lineColor = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.2f, 0.8f));
                    else if (a >= 11 || b >= 11) lineColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.8f, 0.8f));
                    else lineColor = ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1f, 0.8f));
                }
                drawList.AddLine(posA, posB, lineColor, 2f);
            }
        }

        // Project all keypoint circles
        foreach (var kvp in std.Keypoints)
        {
            int i = kvp.Key;
            string name = kvp.Value;
            if (!kpScreenPos.TryGetValue(i, out var pos)) continue;
            
            uint circleColor;
            if (root.PoseStandard == Annotation.PoseStandardType.Fisheye)
            {
                circleColor = (i == 0) ? ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.2f, 1f)) : ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.8f, 1f));
            }
            else
            {
                if (i <= 4) circleColor = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.2f, 1f)); // Face
                else if (i >= 11) circleColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.8f, 1f)); // Legs
                else circleColor = ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1f, 1f)); // Arms
            }

            drawList.AddCircleFilled(pos, 5f, circleColor);
            drawList.AddCircle(pos, 5f, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.8f)), 12, 1.5f);
            drawList.AddText(pos + new Vector2(8, -8), circleColor, $"{i}: {name}");
        }
    }
    private static (SceneObject?, MeshRendererComponent?) FindSkinnedMeshInHierarchy(SceneObject obj)
    {
        var mr = obj.GetComponent<MeshRendererComponent>();
        if (mr != null && mr.Mesh != null && mr.Mesh.Skeleton != null) return (obj, mr);
        foreach (var child in obj.Children)
        {
            var result = FindSkinnedMeshInHierarchy(child);
            if (result.Item1 != null) return result;
        }
        return (null, null);
    }
}
