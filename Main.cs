using Godot;

namespace ActiveRagdollModules
{
  public partial class Main : Node3D
  {
    private const string BotScenePath = "res://x_bot.tscn";
    private Node3D _botInstance = null!;

    public override void _Ready()
    {
      BuildFloor();
      BuildLighting();
      SpawnBot();
      SpawnSyncNode();
    }

    private void SpawnSyncNode()
    {
      // Blocks Godot's physics tick execution until the external Python
      // script returns the next action tensor.
      var syncNode = new Godot.Node();
      syncNode.SetScript(ResourceLoader.Load("res://addons/godot_rl_agents/sync.gd"));
      syncNode.Name = "Sync";
      AddChild(syncNode);
    }

    private void BuildFloor()
    {
      var staticBody = new StaticBody3D();
      AddChild(staticBody);

      // 1. Infinite physics plane facing upward (+Y)
      var col = new CollisionShape3D();
      col.Shape = new WorldBoundaryShape3D
      {
        Plane = new Plane(Vector3.Up, 0f) // Normal: (0, 1, 0), Distance: 0
      };
      staticBody.AddChild(col);

      // 2. Large visual plane with repeated texture mapping
      var mesh = new MeshInstance3D();
      mesh.Mesh = new PlaneMesh
      {
        Size = new Vector2(4000, 4000)
      };

      mesh.MaterialOverride = new StandardMaterial3D
      {
        AlbedoColor = new Color(0.2f, 0.2f, 0.2f),
        Metallic = 0.5f,
        Roughness = 0.2f,
        Uv1Scale = new Vector3(1000, 1000, 1) // Keeps texture grid scale intact if a texture is added
      };
      staticBody.AddChild(mesh);

      // Position at y = 0
      staticBody.Position = Vector3.Zero;
    }

    private void BuildLighting()
    {
      var sun = new DirectionalLight3D();
      sun.ShadowEnabled = true;
      sun.Position = new Vector3(5, 10, 5);
      AddChild(sun);
      sun.LookAt(Vector3.Zero);

      var env = new WorldEnvironment();
      env.Environment = new Godot.Environment
      {
        BackgroundMode = Godot.Environment.BGMode.Sky,
        Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
        AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        TonemapMode = Godot.Environment.ToneMapper.Filmic
      };
      AddChild(env);
    }

    private void SpawnBot()
    {
      if (!ResourceLoader.Exists(BotScenePath))
      {
        GD.PrintErr($"CRASH: Could not find scene at {BotScenePath}");
        return;
      }

      var scene = GD.Load<PackedScene>(BotScenePath);
      _botInstance = scene.Instantiate<Node3D>();
      AddChild(_botInstance);
      _botInstance.GlobalPosition = new Vector3(0, 0.2f, 0);
    }
  }
}
