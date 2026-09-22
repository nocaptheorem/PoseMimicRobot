using Godot;
using Godot.Collections;

namespace ActiveRagdollModules
{
    public partial class RagdollAIController : AIController3D
    {
        [Export] public NoCAPTheorem.Virtual.ActiveRagdoll Ragdoll = null!;

        public override void _Ready()
        {
            base._Ready();
            AddToGroup("AGENT");
            if (Ragdoll == null) GD.PrintErr("[RagdollAIController] Ragdoll reference missing!");
        }

        public override Dictionary get_obs_space()
        {
            var dict = new Dictionary();
            int obsSize = Ragdoll.CollectObservations().Length;

            // godot_rl expects OBSERVATION size to be an array
            dict["obs"] = new Dictionary { { "size", new int[] { obsSize } }, { "space", "box" } };
            return dict;
        }

        public override Dictionary get_action_space()
        {
            var dict = new Dictionary();

            // godot_rl expects ACTION size to be a raw integer
            dict["action"] = new Dictionary { { "size", 72 }, { "action_type", "continuous" } };
            return dict;
        }

        public override Dictionary get_info()
        {
            return new Dictionary();
        }

        public override void zero_reward()
        {
        }

        public override Dictionary get_obs()
        {
            float[] obs = Ragdoll.CollectObservations();
            var dict = new Dictionary();
            dict["obs"] = obs;
            return dict;
        }

        public override float get_reward()
        {
            return Ragdoll.CalculateReward();
        }

        public override void set_action(Dictionary action)
        {
            var actionVariant = action["action"].AsFloat32Array();
            float dt = (float)GetPhysicsProcessDeltaTime();
            Ragdoll.ApplyActions(actionVariant, dt);
        }

        public override bool get_done()
        {
            return Ragdoll.CheckIfDone();
        }

        public override void reset()
        {
            Ragdoll.Call("ResetSimulation");
        }

        public override void set_done_false()
        {
        }
    }
}
