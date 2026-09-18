using Godot;

namespace ActiveRagdollModules
{
    public partial class Main : Node3D
    {
        private const string BotScenePath = "res://x_bot.tscn";

        // No camera needed – the ragdoll provides its own FPS camera.
        private Node3D _botInstance = null!;

        public override void _Ready()
        {
            BuildFloor();
            BuildLighting();
            SpawnBot();
        }

        // No _Process needed – the ragdoll handles its own camera movement.
        // (Removed the A/D orbit logic.)

        private void BuildFloor()
        {
            var staticBody = new StaticBody3D();
            AddChild(staticBody);

            var col = new CollisionShape3D();
            col.Shape = new BoxShape3D { Size = new Vector3(20, 1, 20) };
            staticBody.AddChild(col);

            var mesh = new MeshInstance3D();
            mesh.Mesh = new BoxMesh { Size = new Vector3(20, 1, 20) };
            mesh.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.2f, 0.2f, 0.2f),
                Metallic = 0.5f,
                Roughness = 0.2f
            };
            staticBody.AddChild(mesh);
            staticBody.Position = new Vector3(0, -0.5f, 0);
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
