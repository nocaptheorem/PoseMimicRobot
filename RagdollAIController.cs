using Godot;
using Godot.Collections;

namespace ActiveRagdollModules
{
    public partial class RagdollAIController : AIController3D
    {
        [Export] public NoCAPTheorem.Virtual.ActiveRagdoll Ragdoll = null!;
        private int _stepCounter = 0;

        public override void _Ready()
        {
            base._Ready();
            AddToGroup("AGENT");
            if (Ragdoll == null) GD.PrintErr("[RagdollAIController] Ragdoll reference missing!");
            GD.Print("[TELEMETRY] AI Controller Initialized and Ready.");
        }

        public override Dictionary get_obs()
        {
          // Remove the needs_reset interception from here
          _stepCounter++;
          if (_stepCounter % 60 == 0)
          {
            GD.Print($"[TELEMETRY] get_obs() called. Step: {_stepCounter}");
          }

          return new Dictionary { { "obs", Ragdoll.CollectObservations() } };
        }

        public override void set_done_false()
        {
          // Intercept the reset exactly when Python clears the done state
          if (needs_reset)
          {
            GD.Print("[TELEMETRY] Python issued reset (set_done_false). Forcing Phase 1 Reset.");
            reset(); // This handles Phase 1 and sets needs_reset = false
          }
          needs_reset = false;
        }

        public override void reset()
        {
            Ragdoll.ResetSimulation();
            needs_reset = false;
        }

        public override bool get_done()
        {
            bool isDone = Ragdoll.CheckIfDone();
            if (isDone)
            {
                GD.Print("[TELEMETRY] CheckIfDone() evaluated to TRUE! Agent has fallen.");
                needs_reset = true;
            }
            return isDone;
        }

        public override void set_action(Dictionary action)
        {
            var actionVariant = action["action"].AsFloat32Array();
            Ragdoll.ApplyActions(actionVariant, (float)GetPhysicsProcessDeltaTime());
        }

        public override Dictionary get_obs_space()
        {
            return new Dictionary {
                { "obs", new Dictionary { { "size", new int[] { Ragdoll.CollectObservations().Length } }, { "space", "box" } } }
            };
        }

        public override Dictionary get_action_space()
        {
            return new Dictionary {
                { "action", new Dictionary { { "size", 72 }, { "action_type", "continuous" } } }
            };
        }

        public override float get_reward() => Ragdoll.CalculateReward();
        public override void zero_reward() { }
        public override Dictionary get_info() => new Dictionary();
    }
}
