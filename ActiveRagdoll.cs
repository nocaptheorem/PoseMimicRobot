using Godot;

namespace NoCAPTheorem.Virtual
{
  /// <summary>
  /// A Physics-based Active Ragdoll Controller with integrated FPS Debug Camera and Ballistics.
  /// This system drives a physical skeleton to match an animated target pose using a combination
  /// of techniques:
  /// - PD Controllers: Act as "muscles" on each bone to match orientation.
  /// - Gravity Compensation: Counteracts gravity's torque, making the bones feel weightless.
  /// - Core Stabilization: A "gyro" on the hips to keep the torso upright.
  /// - Virtual Model Control (VMC): Applies forces to the feet to move the Center of Mass (COM)
  ///   to a stable position, enabling dynamic balancing.
  /// - Recovery Strategies: Special controllers to help the ragdoll get up after falling.
  /// </summary>
  [GlobalClass]
  public partial class ActiveRagdoll : Skeleton3D
  {
    #region 1. Debug Configuration
    [ExportGroup("DEBUG")]
    [Export] public bool EnableDebugLogs = true;
    [Export] public bool DrawDebugGizmos = true;
    #endregion

    #region 2. System Links
    [ExportGroup("1. System Links")]
    [Export] public NodePath TargetSimulatorPath = null!;
    [Export] public Skeleton3D AnimationShadow = null!;

    private PhysicalBoneSimulator3D? _sim;
    private PhysicalBone3D? _hips;
    private MuscleGroup? _hipMuscle;
    private PhysicalBone3D? _chest;
    #endregion

    #region 3. Bio-Mechanics (PD Controller Gains)
    [ExportGroup("2. Bio-Mechanics (Structure)")]
    [Export] public bool RelaxArms = false;
    [Export] public float MuscleStiffness = 6000.0f;
    [Export] public float MuscleDamping = 2000.0f;
    [Export] public float MaxMuscleTorque = 15000.0f;
    [Export(PropertyHint.Range, "0, 2.0")] public float GravityComp = 0.5f;
    #endregion

    #region 4. Balance & VMC Settings
    [ExportGroup("3. Core Stabilization (The Gyro)")]
    [Export] public float HipGyroStiffness = 2000.0f;
    [Export] public float HipGyroDamping = 1000.0f;

    [ExportGroup("4. VMC Balance (The Legs)")]
    [Export] public float TargetHeight = 0.7f;
    [Export] public float CenterOfPressureOffset = 0.2f;
    [Export] public float SupportSpring = 1000.0f;
    [Export] public float SupportDamp = 500.0f;
    [Export] public float BalanceStiffness = 3000.0f;
    [Export] public float BalanceDamping = 1000.0f;
    [Export] public float MaxForce = 4000.0f;
    #endregion

    #region 5. Recovery & Strategy
    [ExportGroup("5. Recovery")]
    [Export] public float FallenHeight = 0.45f;
    [Export] public float RecoveryStiffness = 10000.0f;
    [Export] public float RecoveryDamping = 6000.0f;

    [ExportGroup("6. Ankle Strategy")]
    [Export] public float AnkleStiffness = 9000.0f;
    [Export] public float AnkleDamping = 4000.0f;
    [Export(PropertyHint.Layers3DPhysics)] public uint GroundMask = 1;
    #endregion

    #region State Evaluation Tuning
    [ExportGroup("7. State Thresholds")]
    [Export] public float AirborneHysteresisTime = 0.15f; // Seconds without foot contact before declaring airborne
    [Export] public float RestingVelocityThreshold = 0.5f; // Max vertical velocity to be considered "resting"
    #endregion

    // --- INTERNAL STATE ---
    private float _airborneTimer = 0.0f;
    private float _gaitPhase = 0.0f; // Tracks the 0.0 to 1.0 gait cycle
    private bool _isActive = true;
    private bool _wasFallen = false;
    private int _frameCounter = 0;
    private bool _isShadowVisible = true;

    // --- LLC INTERMEDIATE GOALS (g_L) ---
    public Vector3 TargetFootstep0 = Vector3.Forward * 0.4f; // p_0 target
    public float TargetRootHeading = 0.0f; // theta_root target

    // --- DATA STRUCTURES ---
    private List<MuscleGroup> _muscles = new List<MuscleGroup>();
    private List<LimbChain> _legs = new List<LimbChain>();
    private List<LimbChain> _groundedLegsCache = new List<LimbChain>(2);

    // --- DEBUG RENDERERS ---
    private ImmediateMesh _gizmoMesh = new ImmediateMesh();
    private MeshInstance3D _gizmoInstance = new MeshInstance3D();

    // --- CAMERA STATE ---
    private Camera3D? _camera;
    private Node3D? _cameraPivot;
    private float _pitch = 0.0f;
    private float _yaw = 0.0f;
    private Control? _crosshairUI;
    private float MouseSensitivity = 0.005f;

    // --- PROJECTILE SETTINGS ---
    private float AimedShoveForce = 500.0f;
    private float ProjectileForce = 50.0f;
    private float ProjectileMass = 2.0f;
    private float AdogenBlastForce = 20000.0f;

    // --- RANDOM NUMBER GENERATOR ---
    private RandomNumberGenerator _rng = new RandomNumberGenerator();

    // --- GRAB & PULL STATE ---
    private bool _isGrabbing = false;
    private PhysicalBone3D? _grabbedBone = null;
    private float _grabDistance = 0.0f;
    private RigidBody3D? _grabHandle;
    private Generic6DofJoint3D? _grabJoint;

    // --- GRAB & PULL TUNING ---
    private float _grabHandleSpeed = 5.0f;
    private float _grabStiffness = 5000.0f;
    private float _grabDamping = 2000.0f;
    private HashSet<int> _grabbedLimbBoneIds = new HashSet<int>();

    // --- IMPACT EFFECT SYSTEM STATE ---
    private readonly List<Label3D> _effectPool = new();
    private Node? _effectRoot;
    private readonly string[] _defaultImpactText = { "THWACK!", "BONK!", "KAPOW!", "WHAM!", "ZAP!", "BAM!" };
    private readonly Color _defaultImpactColor = Colors.White;
    private FontFile? _defaultFont;

    // --- SHADER ---
    private const string ROCK_SHADER = @"
      shader_type spatial;
      uniform float displacement_strength : hint_range(0, 0.5) = 0.1;
      uniform sampler2D noise_tex;

      void vertex() {
        float noise = texture(noise_tex, UV).r;
        VERTEX += NORMAL * (noise - 0.5) * displacement_strength;
      }

      void fragment() {
        float noise = texture(noise_tex, UV).r;
        vec3 rock_color_dark = vec3(0.2, 0.18, 0.15);
        vec3 rock_color_light = vec3(0.4, 0.38, 0.35);

        vec3 color = mix(rock_color_dark, rock_color_light, noise);
        ALBEDO = color;
        ROUGHNESS = 0.9;
        METALLIC = 0.1;
      }
    ";

    private class MuscleGroup
    {
      public PhysicalBone3D Bone = null!;
      public PhysicalBone3D? ParentBone = null;
      public int BoneId;
      public bool IsSpine;
      public bool IsLeg;
      public bool IsHead;
      public bool IsArm;
      public bool IsFinger;
      public List<MuscleGroup> ChildMuscles = new List<MuscleGroup>();
    }

    private class LimbChain
    {
      public string Name = "Leg";
      public PhysicalBone3D Foot = null!;
      public PhysicalBone3D LowerLeg = null!;
      public PhysicalBone3D UpperLeg = null!;
      public ShapeCast3D GroundSensor = null!;
      public List<int> ChainBoneIds = new List<int>();
      public float GroundedConfidence = 0.0f;
    }

    private void MakeAnimationShadowTransparent(Color tint, float alpha = 0.35f)
    {
      if (AnimationShadow == null) return;

      var ghostMat = new StandardMaterial3D
      {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(tint.R, tint.G, tint.B, alpha),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Back,
        NoDepthTest = false
      };

      // OPTIMIZATION: Climb to the local root of this specific character prefab
      // (stops when it hits the main scene root)
      Node searchRoot = AnimationShadow;
      while (searchRoot.GetParent() != null && searchRoot.GetParent() != GetTree().CurrentScene)
      {
          searchRoot = searchRoot.GetParent();
      }

      ApplyGhostMaterialRecursive(searchRoot, ghostMat);
    }

    private void ApplyGhostMaterialRecursive(Node node, Material ghostMat)
    {
      if (node == null) return;

      if (node is MeshInstance3D meshInstance)
      {
        if (meshInstance.Skeleton != null && !meshInstance.Skeleton.IsEmpty)
        {
          var targetSkeleton = meshInstance.GetNodeOrNull(meshInstance.Skeleton);
          if (targetSkeleton != null && targetSkeleton.GetInstanceId() == AnimationShadow.GetInstanceId())
          {
            meshInstance.MaterialOverride = ghostMat;
            meshInstance.Visible = true;
            meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            // Force all parent nodes in the hierarchy to be visible
            Node current = meshInstance.GetParent();
            while (current is Node3D parent3D)
            {
                parent3D.Visible = true;
                current = current.GetParent();
            }
          }
        }
      }

      foreach (Node child in node.GetChildren())
      {
        ApplyGhostMaterialRecursive(child, ghostMat);
      }
    }

    public override void _Ready()
    {
      _rng.Randomize();
      SetupDebugGizmos();
      SetupFPSCamera();

      if (TargetSimulatorPath != null && !TargetSimulatorPath.IsEmpty)
        _sim = GetNodeOrNull<PhysicalBoneSimulator3D>(TargetSimulatorPath);
      if (_sim == null) _sim = GetChildren().OfType<PhysicalBoneSimulator3D>().FirstOrDefault();

      if (_sim == null || AnimationShadow == null) {
        GD.PrintErr($"[CRITICAL] Missing Simulator or Shadow.");
        SetPhysicsProcess(false);
        return;
      }

      // Make the target animation skeleton visible as a translucent cyan shadow
      MakeAnimationShadowTransparent(new Color(0.2f, 0.8f, 1.0f));

      foreach (var pb in _sim.GetChildren().OfType<PhysicalBone3D>()) {
        string boneName = pb.Get("bone_name").AsString();
        int bid = FindBoneIndex(boneName);
        if (bid == -1) continue;

        string nameLower = boneName.ToLower();
        bool isFinger = nameLower.Contains("thumb") || nameLower.Contains("index") ||
          nameLower.Contains("middle") || nameLower.Contains("ring") ||
          nameLower.Contains("pinky");

        if (isFinger)
        {
          pb.QueueFree();
          continue;
        }

        pb.GravityScale = 1.0f;
        pb.CanSleep = false;
        pb.LinearDamp = 0.5f;
        pb.AngularDamp = 1.0f;
        pb.CollisionLayer = 1u << 0;
        pb.CollisionMask = 1u << 0;

        MuscleGroup m = new MuscleGroup {
          Bone = pb,
          BoneId = bid,
          IsSpine = nameLower.Contains("spine") || nameLower.Contains("chest") || nameLower.Contains("torso") || nameLower.Contains("pelvis"),
          IsLeg = nameLower.Contains("leg") || nameLower.Contains("calf") || nameLower.Contains("foot") || nameLower.Contains("toe"),
          IsHead = nameLower.Contains("head") || nameLower.Contains("neck"),
          IsArm = nameLower.Contains("arm") || nameLower.Contains("hand") || nameLower.Contains("shoulder") || nameLower.Contains("elbow"),
          IsFinger = false
        };

        _muscles.Add(m);

        if (pb.Name.ToString().Contains("Hips") || pb.Name.ToString().Contains("Pelvis"))
          _hips = pb;

        if (nameLower.Contains("spine2") || nameLower.Contains("chest") || nameLower.Contains("torso"))
          _chest = pb;
      }

      foreach (var m in _muscles)
      {
        int parentId = AnimationShadow.GetBoneParent(m.BoneId);
        if (parentId >= 0)
        {
          var parentMuscle = _muscles.FirstOrDefault(pm => pm.BoneId == parentId);
          if (parentMuscle != null)
          {
            m.ParentBone = parentMuscle.Bone;
            parentMuscle.ChildMuscles.Add(m);
          }
        }
      }

      if (_hips == null) { GD.PrintErr("[CRITICAL] Hips not found!"); return; }
      _hipMuscle = _muscles.FirstOrDefault(m => m.Bone == _hips);
      if (_chest == null) _chest = _muscles.LastOrDefault(m => m.IsSpine && m.Bone != _hips)?.Bone;

      AutoConfigureMasses();
      ConfigureJoints();

      BuildLeg("Left", "mixamorig_LeftFoot", "mixamorig_LeftLeg", "mixamorig_LeftUpLeg");
      BuildLeg("Right", "mixamorig_RightFoot", "mixamorig_RightLeg", "mixamorig_RightUpLeg");

      CallDeferred(nameof(ResetSimulation));
      CallDeferred(nameof(InitializeEffectSystem));
      if (EnableDebugLogs)
      {
          GD.Print("--- SKELETON BONE DUMP ---");
          for (int i = 0; i < AnimationShadow.GetBoneCount(); i++)
          {
              GD.Print($"Bone Index {i}: {AnimationShadow.GetBoneName(i)}");
          }
          GD.Print("--------------------------");
      }
    }

    public override void _ExitTree()
    {
      if (IsInstanceValid(_cameraPivot)) _cameraPivot.QueueFree();
      if (IsInstanceValid(_crosshairUI?.GetParent())) _crosshairUI.GetParent().QueueFree();
    }

    private void SetupFPSCamera()
    {
      _cameraPivot = new Node3D();
      _cameraPivot.Name = "FPS_Cam_Pivot";
      GetTree().Root.CallDeferred("add_child", _cameraPivot);

      _camera = new Camera3D();
      _camera.Name = "FPS_Camera";
      _cameraPivot.AddChild(_camera);

      CallDeferred(nameof(FinalizeCameraSetup));
    }

    private void FinalizeCameraSetup()
    {
      if (_cameraPivot == null || _camera == null) return;

      // 1. Move the pivot to your desired starting location in the world
      _cameraPivot.Position = new Vector3(0, 1.5f, 4.5f);

      // 2. Remove the local offset so the camera rotates directly on its own axis
      _camera.Position = Vector3.Zero;

      _camera.Current = true;
      Input.MouseMode = Input.MouseModeEnum.Captured;
      CreateCrosshair();
    }

    private void CreateCrosshair()
    {
      _crosshairUI = new Control();
      _crosshairUI.SetAnchorsPreset(Control.LayoutPreset.FullRect);
      _crosshairUI.MouseFilter = Control.MouseFilterEnum.Ignore;

      var centerPoint = new ColorRect();
      centerPoint.Color = Colors.White;
      centerPoint.CustomMinimumSize = new Vector2(4, 4);
      centerPoint.SetAnchorsPreset(Control.LayoutPreset.Center);
      _crosshairUI.AddChild(centerPoint);

      CanvasLayer canvas = new CanvasLayer();
      canvas.AddChild(_crosshairUI);
      GetTree().Root.CallDeferred("add_child", canvas);
    }

    private void ConfigureJoints()
    {
      foreach (var m in _muscles)
      {
        if (m.Bone == _hips) continue;

        m.Bone.JointType = PhysicalBone3D.JointTypeEnum.Type6Dof;
        float limitX = 45.0f; float limitY = 45.0f; float limitZ = 45.0f;

        if (m.IsSpine) { limitX = 30.0f; limitY = 2.0f; limitZ = 10.0f; }
        else if (m.IsHead) { limitX = 30.0f; limitY = 30.0f; limitZ = 30.0f; }
        else if (m.IsLeg) { limitX = 90.0f; limitY = 5.0f; limitZ = 10.0f; }
        else if (m.IsArm) {
          string n = m.Bone.Name.ToString().ToLower();
          if (n.Contains("forearm") || n.Contains("lower")) { limitX = 130.0f; limitY = 5.0f; limitZ = 5.0f; }
          else { limitX = 175.0f; limitY = 175.0f; limitZ = 175.0f; } // Opened up to prevent limit deflection
        }

        m.Bone.Set("joint_constraints/angular_limit_x/enabled", true);
        m.Bone.Set("joint_constraints/angular_limit_x/upper_angle", Mathf.DegToRad(limitX));
        m.Bone.Set("joint_constraints/angular_limit_x/lower_angle", Mathf.DegToRad(-limitX));
        m.Bone.Set("joint_constraints/angular_limit_y/enabled", true);
        m.Bone.Set("joint_constraints/angular_limit_y/upper_angle", Mathf.DegToRad(limitY));
        m.Bone.Set("joint_constraints/angular_limit_y/lower_angle", Mathf.DegToRad(-limitY));
        m.Bone.Set("joint_constraints/angular_limit_z/enabled", true);
        m.Bone.Set("joint_constraints/angular_limit_z/upper_angle", Mathf.DegToRad(limitZ));
        m.Bone.Set("joint_constraints/angular_limit_z/lower_angle", Mathf.DegToRad(-limitZ));
      }
    }

    private void AutoConfigureMasses()
    {
      foreach (var m in _muscles) {
        if (m.IsHead) m.Bone.Mass = 3.0f;
        else if (m.IsArm) m.Bone.Mass = 1.5f;
        else if (m.IsSpine) m.Bone.Mass = 4.0f;
        else if (m.Bone == _hips) m.Bone.Mass = 12.0f;
        else if (m.IsLeg) {
          if (m.Bone.Name.ToString().ToLower().Contains("up")) m.Bone.Mass = 6.0f;
          else m.Bone.Mass = 4.0f;
        }
        else m.Bone.Mass = 1.0f;
      }
    }

    private void BuildLeg(string prefix, string footName, string lowName, string upName)
    {
      var foot = FindBonePhys(footName);
      var low = FindBonePhys(lowName);
      var up = FindBonePhys(upName);

      if (foot != null && low != null && up != null) {
        PhysicsServer3D.BodySetParam(foot.GetRid(), PhysicsServer3D.BodyParameter.Friction, 1.0f);

        var sensor = new ShapeCast3D();
        var sphere = new SphereShape3D { Radius = 0.15f };
        sensor.Shape = sphere;
        sensor.TargetPosition = Vector3.Down * 0.5f;
        sensor.CollisionMask = GroundMask;
        sensor.ExcludeParent = true;
        foreach(var muscle in _muscles) sensor.AddException(muscle.Bone);
        foot.AddChild(sensor);

        var chain = new LimbChain {
          Name = prefix,
          Foot = foot,
          LowerLeg = low,
          UpperLeg = up,
          GroundSensor = sensor
        };
        chain.ChainBoneIds.Add(FindBoneIndex(upName));
        chain.ChainBoneIds.Add(FindBoneIndex(lowName));
        chain.ChainBoneIds.Add(FindBoneIndex(footName));

        _legs.Add(chain);
      }
    }

    private void StartPhysics() { if (_sim != null) { _sim.Active = true; _sim.PhysicalBonesStartSimulation(); } }

    private void InitializeEffectSystem()
    {
      _effectRoot = GetTree().CurrentScene;
      _effectPool.Clear();
    }

    public override void _Input(InputEvent @event) {
      if (@event.IsActionPressed("ui_cancel"))
        Input.MouseMode = (Input.MouseMode == Input.MouseModeEnum.Captured) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;

      if (Input.MouseMode != Input.MouseModeEnum.Captured) return;

      if (@event is InputEventMouseMotion m) {
        _yaw -= m.Relative.X * MouseSensitivity;
        _pitch -= m.Relative.Y * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch, -Mathf.Pi / 2.0f + 0.1f, Mathf.Pi / 2.0f - 0.1f);

        if (_cameraPivot != null) {
          _cameraPivot.Rotation = new Vector3(0, _yaw, 0);
          if (_camera != null) _camera.Rotation = new Vector3(_pitch, 0, 0);
        }
      }

      if (@event is InputEventKey key && key.Pressed)
      {
        // R: Reset simulation
        if (key.Keycode == Key.R) ResetSimulation();

        // E: Alternative key for Aimed Shove
        if (key.Keycode == Key.E) AimedShove();

        // Q: Alternative key for Adogen Projectile
        if (key.Keycode == Key.Q) FireAdogenProjectile();

        // V: Toggle Animation Shadow Visibility
        if (key.Keycode == Key.V) ToggleShadowVisibility();
      }

      if (@event is InputEventKey skey && skey.Pressed && skey.Keycode == Key.Space)
      {
        // Ensure we actually have hips and are currently standing
        if (_hips != null && _airborneTimer < AirborneHysteresisTime)
        {
          // Apply a massive sudden force to simulate a jump
          float jumpForce = 200.0f;
          PhysicsServer3D.BodyApplyCentralImpulse(_hips.GetRid(), Vector3.Up * jumpForce);
        }
      }

      if (@event is InputEventMouseButton mb && mb.Pressed)
      {
        // Left Click options
        if (mb.ButtonIndex == MouseButton.Left)
        {
          // Shift + Left Click -> Aimed Shove
          if (Input.IsKeyPressed(Key.Shift))
          {
            AimedShove();
          }
          // Ctrl + Left Click -> Fire Adogen Projectile
          else if (Input.IsKeyPressed(Key.Ctrl))
          {
            FireAdogenProjectile();
          }
          // Standard Left Click -> Fire Projectile
          else
          {
            FireProjectile();
          }
        }
      }

      if (@event is InputEventMouseButton grabMb && grabMb.ButtonIndex == MouseButton.Right)
      {
        // Trackpad Right-Click (or two-finger tap) -> Grab / Release Bone
        if (grabMb.Pressed) TryGrabBone();
        else ReleaseBone();
      }
    }

private void ToggleShadowVisibility()
    {
      if (AnimationShadow == null) return;

      _isShadowVisible = !_isShadowVisible;

      // Climb to the local root of this specific character prefab (same as initialization)
      Node searchRoot = AnimationShadow;
      while (searchRoot.GetParent() != null && searchRoot.GetParent() != GetTree().CurrentScene)
      {
          searchRoot = searchRoot.GetParent();
      }

      SetShadowVisibilityRecursive(searchRoot, _isShadowVisible);
    }

    private void SetShadowVisibilityRecursive(Node node, bool isVisible)
    {
      if (node == null) return;

      if (node is MeshInstance3D meshInstance)
      {
        if (meshInstance.Skeleton != null && !meshInstance.Skeleton.IsEmpty)
        {
          var targetSkeleton = meshInstance.GetNodeOrNull(meshInstance.Skeleton);

          // Only toggle the mesh if it's tied to our animation shadow
          if (targetSkeleton != null && targetSkeleton.GetInstanceId() == AnimationShadow.GetInstanceId())
          {
            meshInstance.Visible = isVisible;
          }
        }
      }

      foreach (Node child in node.GetChildren())
      {
        SetShadowVisibilityRecursive(child, isVisible);
      }
    }

    public override void _Process(double delta)
    {
      var _cameraMoveSpeed = 5.0f;
      if (_cameraPivot == null || _camera == null || Input.MouseMode != Input.MouseModeEnum.Captured) return;

      // 2. ADD WASD Free-Moving Logic:
      Vector3 moveDirection = Vector3.Zero;

      // Use the camera pivot's local axes to move relative to where you are looking
      if (Input.IsKeyPressed(Key.W)) moveDirection += -_camera.GlobalTransform.Basis.Z;
      if (Input.IsKeyPressed(Key.S)) moveDirection += _camera.GlobalTransform.Basis.Z;
      if (Input.IsKeyPressed(Key.A)) moveDirection += -_camera.GlobalTransform.Basis.X;
      if (Input.IsKeyPressed(Key.D)) moveDirection += _camera.GlobalTransform.Basis.X;
      if (Input.IsKeyPressed(Key.Shift)) moveDirection += _camera.GlobalTransform.Basis.Y;
      if (Input.IsKeyPressed(Key.Ctrl)) moveDirection += -_camera.GlobalTransform.Basis.Y;

      // Normalize to prevent faster diagonal movement and apply speed
      if (moveDirection != Vector3.Zero)
      {
        moveDirection = moveDirection.Normalized();
        _cameraPivot.GlobalPosition += moveDirection * _cameraMoveSpeed * (float)delta;
      }

      // 3. KEEP your existing crosshair / grab-raycast logic below:
      if (!_isGrabbing && _crosshairUI != null)
      {
        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(_camera.GlobalPosition, _camera.GlobalPosition - _camera.GlobalBasis.Z * 100.0f);
        var result = spaceState.IntersectRay(query);

        if (result.Count > 0 && result["collider"].As<Node3D>() is PhysicalBone3D hitBody && hitBody != _hips)
        {
          _crosshairUI.RotationDegrees = 45;
        }
        else
        {
          _crosshairUI.RotationDegrees = 0;
        }
      }
    }

    private void AimedShove()
    {
      if (_camera == null) return;

      var spaceState = GetWorld3D().DirectSpaceState;
      var query = PhysicsRayQueryParameters3D.Create(_camera.GlobalPosition, _camera.GlobalPosition - _camera.GlobalBasis.Z * 1000.0f);
      var result = spaceState.IntersectRay(query);

      if (result.Count > 0 && result["collider"].As<Node3D>() is PhysicalBone3D hitBody && _muscles.Any(m => m.Bone == hitBody))
      {
        Vector3 impactPosition = result["position"].AsVector3();
        Vector3 shoveDir = (impactPosition - _camera.GlobalPosition).Normalized();
        ApplyForce(hitBody, shoveDir * AimedShoveForce, 1.0f);
        SpawnImpactText(impactPosition, new string[] { "SHOVE!" }, Colors.Orange);
      }
    }

    private void FireProjectile()
    {
      if (_camera == null) return;

      SpinningProjectile rb = new SpinningProjectile();
      rb.Mass = ProjectileMass;
      rb.ContinuousCd = true;

      var colShape = new SphereShape3D { Radius = 0.05f };
      CollisionShape3D col = new CollisionShape3D { Shape = colShape };

      var rockNoise = new FastNoiseLite();
      rockNoise.Seed = (int)GD.RandRange(0, int.MaxValue);
      var noiseTexture = new NoiseTexture2D { Noise = rockNoise };

      var rockMaterial = new ShaderMaterial { Shader = new Shader { Code = ROCK_SHADER } };
      rockMaterial.SetShaderParameter("noise_tex", noiseTexture);
      rockMaterial.SetShaderParameter("displacement_strength", 0.08f);

      MeshInstance3D mesh = new MeshInstance3D {
        Mesh = new SphereMesh { Height = 0.05f, Radius = 0.05f },
        MaterialOverride = rockMaterial
      };

      rb.AddChild(col);
      rb.AddChild(mesh);
      GetTree().Root.AddChild(rb);

      rb.Rotation = new Vector3(_rng.Randf() * Mathf.Tau, _rng.Randf() * Mathf.Tau, _rng.Randf() * Mathf.Tau);

      Vector3 forward = -_camera.GlobalTransform.Basis.Z;
      rb.GlobalPosition = _camera.GlobalPosition + (forward * 0.8f);

      rb.Connect(SpinningProjectile.SignalName.Impact, Callable.From((Vector3 impactPos, Node hitBody) => OnProjectileImpact(impactPos, hitBody)));
      rb.ApplyCentralImpulse(forward * ProjectileForce);
    }

    private void FireAdogenProjectile()
    {
      if (_camera == null) return;

      var projectile = new AdogenProjectile();
      projectile.Mass = 5.0f;
      projectile.ContinuousCd = true;
      projectile.CollisionLayer = 1u << 0;
      projectile.CollisionMask = 1u << 0;

      var colShape = new SphereShape3D { Radius = 0.4f };
      var col = new CollisionShape3D { Shape = colShape };
      projectile.AddChild(col);

      var meshInstance = new MeshInstance3D();
      meshInstance.Mesh = new SphereMesh { Radius = 0.4f, Height = 0.8f };
      var shaderMat = new ShaderMaterial { Shader = new Shader { Code = AdogenProjectile.SHADER_CODE } };
      var noise = new FastNoiseLite { Frequency = 0.02f, FractalType = FastNoiseLite.FractalTypeEnum.Fbm };
      var noiseImage = noise.GetImage(64, 64);
      var noiseTex = ImageTexture.CreateFromImage(noiseImage);
      shaderMat.SetShaderParameter("noise_tex_a", noiseTex);
      shaderMat.SetShaderParameter("noise_tex_b", noiseTex);
      meshInstance.MaterialOverride = shaderMat;
      projectile.AddChild(meshInstance);

      var light = new OmniLight3D { LightColor = new Color(0.3f, 0.1f, 0.8f), LightEnergy = 5.0f, OmniRange = 5.0f };
      projectile.AddChild(light);
      GetTree().Root.AddChild(projectile);

      Vector3 forward = -_camera.GlobalTransform.Basis.Z;
      projectile.GlobalPosition = _camera.GlobalPosition + (forward * 0.8f);

      projectile.Initialize(forward, 50.0f);
      projectile.Connect(AdogenProjectile.SignalName.Impact, Callable.From((Vector3 impactPos, Node hitBody) => OnAdogenImpact(impactPos, hitBody)));
    }

    private void OnAdogenImpact(Vector3 impactPos, Node hitBody)
    {
      if (_muscles.Any(m => m.Bone == hitBody))
      {
        if (_hips == null) return;
        Vector3 blastDir = (_hips.GlobalPosition - impactPos).Normalized();
        blastDir += Vector3.Up * 0.5f;
        blastDir = blastDir.Normalized();

        ApplyForce(_hips, blastDir * AdogenBlastForce, 1.0f);
        SpawnImpactText(impactPos, new string[] { "ADOGEN!" }, Colors.Cyan);
      }
      else
      {
        SpawnImpactText(impactPos, new string[] { "ADOGEN!" }, Colors.Cyan);
      }
    }

    private void TryGrabBone()
    {
      if (_camera == null || _isGrabbing) return;

      var spaceState = GetWorld3D().DirectSpaceState;
      var query = PhysicsRayQueryParameters3D.Create(_camera.GlobalPosition, _camera.GlobalPosition - _camera.GlobalBasis.Z * 100.0f);
      var result = spaceState.IntersectRay(query);

      if (result.Count > 0 && result["collider"].As<Node3D>() is PhysicalBone3D hitBody && hitBody != _hips)
      {
        _isGrabbing = true;
        _grabbedBone = hitBody;
        _grabDistance = _camera.GlobalPosition.DistanceTo(hitBody.GlobalPosition);

        PopulateGrabbedLimbSet(hitBody);

        _grabHandle = new RigidBody3D { Freeze = true, CollisionLayer = 0, CollisionMask = 0 };
        AddChild(_grabHandle);
        _grabHandle.GlobalPosition = hitBody.GlobalPosition;

        _grabJoint = new Generic6DofJoint3D();
        AddChild(_grabJoint);
        _grabJoint.NodeA = _grabHandle.GetPath();
        _grabJoint.NodeB = hitBody.GetPath();

        _grabJoint.Set("linear_limit_x/enabled", false);
        _grabJoint.Set("linear_limit_y/enabled", false);
        _grabJoint.Set("linear_limit_z/enabled", false);

        _grabJoint.Set("linear_spring_x/enabled", true);
        _grabJoint.Set("linear_spring_y/enabled", true);
        _grabJoint.Set("linear_spring_z/enabled", true);

        _grabJoint.Set("linear_spring_x/stiffness", _grabStiffness);
        _grabJoint.Set("linear_spring_y/stiffness", _grabStiffness);
        _grabJoint.Set("linear_spring_z/stiffness", _grabStiffness);

        _grabJoint.Set("linear_spring_x/damping", _grabDamping);
        _grabJoint.Set("linear_spring_y/damping", _grabDamping);
        _grabJoint.Set("linear_spring_z/damping", _grabDamping);

        _grabJoint.Set("angular_limit_x/enabled", true); _grabJoint.Set("angular_limit_x/upper_angle", 0); _grabJoint.Set("angular_limit_x/lower_angle", 0);
        _grabJoint.Set("angular_limit_y/enabled", true); _grabJoint.Set("angular_limit_y/upper_angle", 0); _grabJoint.Set("angular_limit_y/lower_angle", 0);
        _grabJoint.Set("angular_limit_z/enabled", true); _grabJoint.Set("angular_limit_z/upper_angle", 0); _grabJoint.Set("angular_limit_z/lower_angle", 0);

        if (_crosshairUI != null) _crosshairUI.RotationDegrees = 90;
      }
    }

    private void PopulateGrabbedLimbSet(PhysicalBone3D grabbedBone)
    {
      _grabbedLimbBoneIds.Clear();
      var currentMuscle = _muscles.FirstOrDefault(m => m.Bone == grabbedBone);
      if (currentMuscle == null) return;

      while (currentMuscle != null)
      {
        _grabbedLimbBoneIds.Add(currentMuscle.BoneId);
        if (currentMuscle.IsSpine || currentMuscle.Bone == _hips) break;
        currentMuscle = _muscles.FirstOrDefault(m => m.Bone == currentMuscle.ParentBone);
      }
    }

    private void ReleaseBone()
    {
      if (!_isGrabbing) return;

      if (_grabJoint != null) _grabJoint.QueueFree();
      if (_grabHandle != null) _grabHandle.QueueFree();

      _isGrabbing = false;
      _grabbedBone = null;
      _grabJoint = null;
      _grabHandle = null;

      _grabbedLimbBoneIds.Clear();
    }

    private void OnProjectileImpact(Vector3 impactPosition, Node hitBody)
    {
      if (EnableDebugLogs)
      {
        GD.Print($"[ImpactEffect] Impact on {hitBody.Name} at {impactPosition}");
      }
      SpawnImpactText(impactPosition);
    }

    private void ResetSimulation()
    {
      if (_sim == null) return;
      _sim.Active = false;
      foreach(var m in _muscles) {
        Transform3D t = AnimationShadow.GetBoneGlobalPose(m.BoneId);
        PhysicsServer3D.BodySetState(m.Bone.GetRid(), PhysicsServer3D.BodyState.Transform, AnimationShadow.GlobalTransform * t);
        PhysicsServer3D.BodySetState(m.Bone.GetRid(), PhysicsServer3D.BodyState.LinearVelocity, Vector3.Zero);
        PhysicsServer3D.BodySetState(m.Bone.GetRid(), PhysicsServer3D.BodyState.AngularVelocity, Vector3.Zero);
      }
      CallDeferred(nameof(StartPhysics));
    }

    /// <summary>
    /// Evaluates if the agent has fallen.
    /// An episode is terminated early if the torso contacts the ground.
    /// </summary>
    public bool CheckIfDone()
    {
        if (_hips == null) return false;

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(_hips.GlobalPosition, _hips.GlobalPosition + (Vector3.Down * 10.0f));
        query.CollisionMask = GroundMask;
        var result = spaceState.IntersectRay(query);

        float trueHeightAboveGround = TargetHeight;
        if (result.Count > 0)
        {
            trueHeightAboveGround = _hips.GlobalPosition.Y - result["position"].AsVector3().Y;
        }

        // 0.45f represents the FallenHeight threshold
        return trueHeightAboveGround < FallenHeight;
    }

    /// <summary>
    /// Computes the multi-objective reward (r_L) per 30 Hz step.
    /// </summary>
    public float CalculateReward()
    {
        if (_hips == null || AnimationShadow == null) return 0f;

        // 1. Style Similarity (r_pose)
        // Penalizes deviation from the reference motion capture pose
        float poseError = 0f;
        foreach (var m in _muscles)
        {
            Quaternion simQ = m.Bone.GlobalBasis.GetRotationQuaternion();
            Quaternion animQ = (AnimationShadow.GlobalTransform * AnimationShadow.GetBoneGlobalPose(m.BoneId)).Basis.GetRotationQuaternion();
            poseError += Mathf.Abs(simQ.AngleTo(animQ));
        }
        float r_pose = Mathf.Exp(-2.0f * (poseError / _muscles.Count));

        // 2. Root Heading Accuracy (r_root)
        // Penalizes deviation from the target walking direction
        Vector3 hipForward = -_hips.GlobalBasis.Z;
        float currentHeading = Mathf.Atan2(hipForward.X, hipForward.Z);
        float headingError = Mathf.Abs(currentHeading - TargetRootHeading);
        float r_root = Mathf.Exp(-5.0f * headingError);

        // 3. Footstep Placement Accuracy (r_step)
        // Evaluates the distance of the active swing foot to the target placement
        Vector3 swingFootPos = _legs[0].GroundedConfidence < _legs[1].GroundedConfidence ?
                               _legs[0].Foot.GlobalPosition : _legs[1].Foot.GlobalPosition;
        float stepError = new Vector2(swingFootPos.X - TargetFootstep0.X, swingFootPos.Z - TargetFootstep0.Z).Length();
        float r_step = Mathf.Exp(-stepError);

        // Weighted summation of the multi-objective reward
        return (0.5f * r_pose) + (0.3f * r_root) + (0.2f * r_step);
    }

    /// <summary>
    /// Constructs the state space (s_L) for the DeepLoco LLC neural network.
    /// Returns a flattened array of Phase (1), Foot Contacts (2), and Bone Kinematics (N * 13).
    /// </summary>
    public float[] CollectObservations()
    {
        List<float> obs = new List<float>();

        // 1. Phase Variable (1D)
        // Keeps the LLC in sync with the 1-second reference motion cycle.
        obs.Add(_gaitPhase);

        // 2. Contact Sensors (2D)
        // Binary indicators (1.0 or 0.0) for foot ground contact.
        foreach (var leg in _legs)
        {
            obs.Add(leg.GroundSensor.IsColliding() ? 1.0f : 0.0f);
        }

        // 3. Proprioception (Bone Kinematics)
        if (_hips == null) return obs.ToArray();

        Transform3D rootTransform = _hips.GlobalTransform;
        Basis rootBasisInv = rootTransform.Basis.Inverse();

        foreach (var m in _muscles)
        {
            // Center of Mass Position (3D): Relative to the root/pelvis
            Vector3 relPos = rootBasisInv * (m.Bone.GlobalPosition - rootTransform.Origin);
            obs.Add(relPos.X);
            obs.Add(relPos.Y);
            obs.Add(relPos.Z);

            // Relative Rotation (4D): Quaternions relative to the root orientation
            Quaternion relRot = (rootBasisInv * m.Bone.GlobalBasis).GetRotationQuaternion();
            obs.Add(relRot.X);
            obs.Add(relRot.Y);
            obs.Add(relRot.Z);
            obs.Add(relRot.W);

            // Linear Velocity (3D): Transformed to the root's local coordinate frame
            Vector3 locLinVel = rootBasisInv * m.Bone.LinearVelocity;
            obs.Add(locLinVel.X);
            obs.Add(locLinVel.Y);
            obs.Add(locLinVel.Z);

            // Angular Velocity (3D): Transformed to the root's local coordinate frame
            Vector3 locAngVel = rootBasisInv * m.Bone.AngularVelocity;
            obs.Add(locAngVel.X);
            obs.Add(locAngVel.Y);
            obs.Add(locAngVel.Z);
        }

        return obs.ToArray();
    }

    /// <summary>
    /// Binds the neural network's action space (a_L) to the physical joint PD controllers.
    /// Maps normalized continuous actions [-1, 1] to target orientations bounded by physical limits.
    /// </summary>
    public void ApplyActions(float[] actions, float dt)
    {
        if (_hips == null) return;

        int actionIndex = 0;

        foreach (var m in _muscles)
        {
            // The root bone (hips) is driven by the environment, not a parent joint constraint.
            if (m.Bone == _hips || m.ParentBone == null) continue;

            // Ensure we do not overflow the action array
            if (actionIndex + 2 >= actions.Length) break;

            // 1. Read normalized actions [-1, 1] from the DeepLoco LLC
            float actX = actions[actionIndex++];
            float actY = actions[actionIndex++];
            float actZ = actions[actionIndex++];

            // 2. Map normalized actions to physical joint limits
            // Action parameters are clamped to stay within permissible ranges of motion.
            float limitX = (float)m.Bone.Get("joint_constraints/angular_limit_x/upper_angle");
            float limitY = (float)m.Bone.Get("joint_constraints/angular_limit_y/upper_angle");
            float limitZ = (float)m.Bone.Get("joint_constraints/angular_limit_z/upper_angle");

            Vector3 targetEuler = new Vector3(
                Mathf.Clamp(actX, -1f, 1f) * limitX,
                Mathf.Clamp(actY, -1f, 1f) * limitY,
                Mathf.Clamp(actZ, -1f, 1f) * limitZ
            );

            // Construct the target local orientation requested by the LLC
            Quaternion targetLocalQ = Quaternion.FromEuler(targetEuler);

            // 3. PD Control Law Execution
            // Calculate current local rotation relative to the parent bone
            Quaternion parentWorldQ = m.ParentBone.GlobalBasis.GetRotationQuaternion();
            Quaternion currentWorldQ = m.Bone.GlobalBasis.GetRotationQuaternion();
            Quaternion currentLocalQ = parentWorldQ.Inverse() * currentWorldQ;

            // Calculate rotational difference
            Quaternion diff = targetLocalQ * currentLocalQ.Inverse();
            if (diff.W < 0f) diff = new Quaternion(-diff.X, -diff.Y, -diff.Z, -diff.W);

            Vector3 axis = diff.GetAxis().Normalized();
            float angle = diff.GetAngle();
            if (float.IsNaN(angle)) continue;

            // P-Term: Proportional stiffness pushing toward the target angle
            Vector3 pTerm = (axis * angle) * MuscleStiffness;

            // D-Term: Derivative damping relative to the parent bone's velocity
            Vector3 relativeVel = m.Bone.AngularVelocity - m.ParentBone.AngularVelocity;
            Vector3 dTerm = relativeVel * MuscleDamping;

            Vector3 rawTorque = pTerm - dTerm;

            // 4. Physical Safeguards
            float limit = MaxMuscleTorque;
            if (m.IsSpine) limit *= 5.0f;
            if (m.IsArm) limit *= 0.2f;

            // Apply the final clamped torque to the bone and equal opposite torque to the parent
            ApplyTorque(m.Bone, rawTorque.LimitLength(limit), dt, m.ParentBone);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
      if (!_isActive || _sim == null || !_sim.Active || _hips == null) return;

      if (_isGrabbing) UpdateGrabHandlePosition((float)delta);

      // We only track X and Z so the animation's vertical bounce (Y axis) remains pure.
      if (AnimationShadow != null)
      {
        Vector3 shadowPos = AnimationShadow.GlobalPosition;
        shadowPos.X = Mathf.Lerp(shadowPos.X, _hips.GlobalPosition.X, 5.0f * (float)delta);
        shadowPos.Z = Mathf.Lerp(shadowPos.Z, _hips.GlobalPosition.Z, 5.0f * (float)delta);
        shadowPos.Y = 0;
        AnimationShadow.GlobalPosition = shadowPos;

        // walking pace
        // AnimationShadow.RotateY(0.02f);
      }

      float dt = (float)delta;
      if (dt <= 0f) return;
      _gaitPhase += dt;
      if (_gaitPhase >= 1.0f) _gaitPhase -= 1.0f;

      _frameCounter++;
      bool isDebugFrame = EnableDebugLogs && (_frameCounter % 60 == 0);

      // --- 1. SENSOR EVALUATION ---
      HashSet<int> stanceBoneIds = new HashSet<int>();
      bool hasFootContact = false;
      float confidenceBlendSpeed = 15.0f;

      foreach (var leg in _legs)
      {
        if (leg.GroundSensor.IsColliding())
        {
          hasFootContact = true;
          foreach (int id in leg.ChainBoneIds) stanceBoneIds.Add(id);
          leg.GroundedConfidence = Mathf.Lerp(leg.GroundedConfidence, 1.0f, confidenceBlendSpeed * dt);
        } else {
          leg.GroundedConfidence = Mathf.Lerp(leg.GroundedConfidence, 0.0f, confidenceBlendSpeed * dt);
        }
      }

      // --- 2. HYSTERESIS (DEBOUNCING) ---
      if (!hasFootContact)
      {
        _airborneTimer += dt;
      }
      else
      {
        _airborneTimer = 0.0f;
      }

      // --- 3. BROADENED SENSORS & VELOCITY CHECK ---
      float trueHeightAboveGround = TargetHeight; // Default to safe height
      var spaceState = GetWorld3D().DirectSpaceState;
      var query = PhysicsRayQueryParameters3D.Create(_hips.GlobalPosition,
          _hips.GlobalPosition + (Vector3.Down * 10.0f));
      query.CollisionMask = GroundMask; // Only detect physical floors
      var result = spaceState.IntersectRay(query);

      if (result.Count > 0)
      {
          float groundY = result["position"].AsVector3().Y;
          trueHeightAboveGround = _hips.GlobalPosition.Y - groundY;
      }
      bool isFallen = trueHeightAboveGround < FallenHeight;

      // If we lack foot contact but our vertical velocity is nearly zero,
      // we are resting on a surface (e.g., draped over a table), not falling.
      bool isResting = Mathf.Abs(_hips.LinearVelocity.Y) < RestingVelocityThreshold;

      // Rigorous Airborne Definition:
      // - Sensors disconnected longer than the hysteresis threshold.
      // - Torso is NOT on the ground (!isFallen).
      // - We are actually falling (!isResting).
      bool isAirborne = (_airborneTimer >= AirborneHysteresisTime) && !isFallen && !isResting;

      // --- STATE MACHINE EXECUTION (NEURAL NETWORK READY) ---
      // The analytical heuristic controllers (VMC, Gyro, PD Shadow Tracking)
      // have been removed. The DeepLoco LLC will dictate joint targets.
      CompensateForGravity(dt);

      // TODO (Phase 4): Godot RL Agents will inject the actual action tensor here.
      // For now, we simulate a neutral zero-action array to test the PD limits.
      int requiredActionSize = (_muscles.Count - 1) * 3;
      float[] currentActions = new float[requiredActionSize];

      ApplyActions(currentActions, dt);

      if (DrawDebugGizmos) DrawGizmos();
    }

    private void UpdateGrabHandlePosition(float delta)
    {
      if (_grabHandle == null || _camera == null) return;

      Vector3 targetPos = _camera.GlobalPosition - _camera.GlobalBasis.Z * _grabDistance;
      _grabHandle.GlobalPosition = _grabHandle.GlobalPosition.Lerp(targetPos, delta * _grabHandleSpeed);
    }

    private void CompensateForGravity(float dt)
    {
      foreach (var m in _muscles)
      {
        if (m.IsFinger) continue;
        if (RelaxArms && m.IsArm) continue;

        Vector3 pivot = m.Bone.GlobalPosition;
        var (compositeMass, weightedPos) = GetSubtreeMassProperties(m);
        if (compositeMass <= 0.001f) continue;

        Vector3 compositeCOM = weightedPos / compositeMass;
        Vector3 leverArm = compositeCOM - pivot;
        Vector3 gravityForce = Vector3.Down * 9.8f * compositeMass;
        Vector3 counterTorque = leverArm.Cross(gravityForce);

        ApplyTorque(m.Bone, -counterTorque * GravityComp, dt, m.ParentBone);
      }
    }

    private (float mass, Vector3 weightedPos) GetSubtreeMassProperties(MuscleGroup m)
    {
      float totalMass = m.Bone.Mass;
      Vector3 totalWeightedPos = m.Bone.GlobalPosition * m.Bone.Mass;
      foreach (var child in m.ChildMuscles)
      {
        var childProps = GetSubtreeMassProperties(child);
        totalMass += childProps.mass;
        totalWeightedPos += childProps.weightedPos;
      }
      return (totalMass, totalWeightedPos);
    }

    private void ApplyTorque(PhysicalBone3D bone, Vector3 torque, float dt, PhysicalBone3D? reactionBody = null)
    {
      if (bone == null) return;
      PhysicsServer3D.BodyApplyTorqueImpulse(bone.GetRid(), torque * dt);
      if (reactionBody != null) PhysicsServer3D.BodyApplyTorqueImpulse(reactionBody.GetRid(), -torque * dt);
    }

    private void ApplyForce(PhysicalBone3D bone, Vector3 force, float dt) {
      if (bone == null) return;
      PhysicsServer3D.BodyApplyCentralImpulse(bone.GetRid(), force * dt);
    }

    private Vector3 GetTrueCenterOfMass()
    {
      Vector3 weightedPositionSum = Vector3.Zero;
      float totalMass = 0f;
      foreach (var muscle in _muscles) {
        weightedPositionSum += muscle.Bone.GlobalPosition * muscle.Bone.Mass;
        totalMass += muscle.Bone.Mass;
      }
      return totalMass > 0f ? weightedPositionSum / totalMass : (_hips?.GlobalPosition ?? Vector3.Zero);
    }

    private PhysicalBone3D? FindBonePhys(string namePart) {
      return _muscles.FirstOrDefault(m => m.Bone.Get("bone_name").AsString().Contains(namePart))?.Bone;
    }

    private int FindBoneIndex(string boneName) => AnimationShadow.FindBone(boneName);

    private void SetupDebugGizmos() {
      if (_gizmoInstance.GetParent() == null) AddChild(_gizmoInstance);
      _gizmoInstance.Mesh = _gizmoMesh;
      _gizmoInstance.MaterialOverride = new StandardMaterial3D { ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true };
    }

    private void DrawGizmos() {
      if (_hips == null) return;
      Vector3 com = GetTrueCenterOfMass();
      _gizmoMesh.ClearSurfaces();
      _gizmoMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

      _gizmoMesh.SurfaceSetColor(Colors.Magenta);
      _gizmoMesh.SurfaceAddVertex(com);
      _gizmoMesh.SurfaceAddVertex(com + Vector3.Down * TargetHeight);

      Vector3 predictedCoM = com + (_hips.LinearVelocity * 0.2f);
      _gizmoMesh.SurfaceSetColor(Colors.Yellow);
      _gizmoMesh.SurfaceAddVertex(com);
      _gizmoMesh.SurfaceAddVertex(predictedCoM);

      foreach(var leg in _legs) {
        Vector3 start = leg.Foot.GlobalPosition;
        Vector3 end = leg.GroundSensor.IsColliding() ? leg.GroundSensor.GetCollisionPoint(0) : start + (Vector3.Down * 0.5f);
        _gizmoMesh.SurfaceSetColor(leg.GroundSensor.IsColliding() ? Colors.Green : Colors.Red);
        _gizmoMesh.SurfaceAddVertex(start);
        _gizmoMesh.SurfaceAddVertex(end);

        if (leg.GroundSensor.IsColliding()) {
          Vector3 footForward = -leg.Foot.GlobalBasis.Z;
          footForward.Y = 0;
          footForward = footForward.Normalized();
          Vector3 midFoot = start + (footForward * CenterOfPressureOffset);
          _gizmoMesh.SurfaceSetColor(Colors.Blue);
          _gizmoMesh.SurfaceAddVertex(start);
          _gizmoMesh.SurfaceAddVertex(midFoot);
        }
      }
      _gizmoMesh.SurfaceEnd();
    }

    private void SpawnImpactText(Vector3 worldPosition, string[]? textOptions = null, Color? color = null)
    {
      if (_effectRoot == null || !GodotObject.IsInstanceValid(_effectRoot))
      {
        GD.PrintErr("ImpactEffectSystem not initialized or root is invalid.");
        return;
      }

      var label = GetPooledLabel();

      if (label == null) return;

      label.GlobalPosition = worldPosition + Vector3.Up * 2.0f;

      var chosenText = (textOptions ?? _defaultImpactText)[GD.Randi() % (textOptions ?? _defaultImpactText).Length];
      label.Text = chosenText;
      label.Modulate = color ?? _defaultImpactColor;
      label.Visible = true;

      var tween = label.CreateTween();
      tween.SetParallel(true);

      tween.TweenProperty(label, "position", label.Position + Vector3.Up, 0.8f)
        .SetTrans(Tween.TransitionType.Expo)
        .SetEase(Tween.EaseType.Out);

      tween.TweenProperty(label, "modulate:a", 0.0f, 0.8f);

      tween.TweenCallback(Callable.From(() => label.Visible = false));
    }

    private Label3D? GetPooledLabel()
    {
      var pooledLabel = _effectPool.FirstOrDefault(lbl => GodotObject.IsInstanceValid(lbl) && !lbl.Visible);

      if (pooledLabel != null)
      {
        return pooledLabel;
      }

      if (_defaultFont == null)
      {
        _defaultFont = (FontFile)ThemeDB.FallbackFont;
      }

      var newLabel = new Label3D
      {
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        PixelSize = 0.005f
      };
      _effectRoot?.AddChild(newLabel);
      _effectPool.Add(newLabel);
      return newLabel;
    }

    private partial class SpinningProjectile : RigidBody3D
    {
      [Signal]
      public delegate void ImpactEventHandler(Vector3 worldPosition, Node collidedBody);

      [Export] public float SpinTorque = 10.0f;
      private Vector3 _spinAxis;

      public override void _Ready()
      {
        _spinAxis = new Vector3(GD.Randf(), GD.Randf(), GD.Randf()).Normalized();
        BodyEntered += OnBodyEntered;
      }

      private void OnBodyEntered(Node body)
      {
        if (body is PhysicalBone3D || body is StaticBody3D)
        {
          EmitSignal(SignalName.Impact, GlobalPosition, body);
        }

        Vector3 velocity = LinearVelocity;
        LinearVelocity = new Vector3(velocity.X, -velocity.Y * 0.8f, velocity.Z);

        GetTree().CreateTimer(2.0f).Connect("timeout", Callable.From(DestroyProjectile));
      }

      public void DestroyProjectile()
      {
        BodyEntered -= OnBodyEntered;
        QueueFree();
      }
    }

    private partial class AdogenProjectile : RigidBody3D
    {
      [Signal]
      public delegate void ImpactEventHandler(Vector3 worldPosition, Node collidedBody);

      public const string SHADER_CODE = @"
        shader_type spatial;
        render_mode unshaded;
        uniform sampler2D noise_tex_a;
        uniform sampler2D noise_tex_b;
        void fragment() {
          float n1 = texture(noise_tex_a, UV + TIME * 0.03).r;
          float n2 = texture(noise_tex_b, UV - TIME * 0.02).r;
          float combined_noise = (n1 + n2) * 0.5;
          vec3 dark_base = vec3(0.05, 0.0, 0.15);
          vec3 mid_glow = vec3(0.3, 0.1, 0.8);
          vec3 bright_scales = vec3(0.5, 0.9, 1.0);
          vec3 color = mix(dark_base, mid_glow, smoothstep(0.2, 0.5, combined_noise));
          color = mix(color, bright_scales, smoothstep(0.6, 0.9, combined_noise));
          float fresnel = pow(1.0 - dot(NORMAL, VIEW), 2.5);
          ALBEDO = color + (bright_scales * fresnel * 0.5);
        }
        ";

      public override void _Ready()
      {
        var lifetimeTimer = GetTree().CreateTimer(10.0f);
        lifetimeTimer.Connect("timeout", Callable.From(DestroyProjectile));
        BodyEntered += OnBodyEntered;
      }

      public void Initialize(Vector3 direction, float speed)
      {
        LinearVelocity = direction * speed;
      }

      private void OnBodyEntered(Node body)
      {
        GD.Print($"AdogenProjectile HIT: {body.Name}");
        EmitSignal(SignalName.Impact, GlobalPosition, body);
        DestroyProjectile();
      }

      public void DestroyProjectile()
      {
        BodyEntered -= OnBodyEntered;
        QueueFree();
      }
    }
  }}
