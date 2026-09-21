using Godot;
using Godot.Collections;

namespace ActiveRagdollModules
{
    /// <summary>
    /// The Godot RL Agents interface for the DeepLoco Low-Level Controller (LLC).
    /// </summary>
    public partial class RagdollAIController : AIController3D
    {
        [Export] public NoCAPTheorem.Virtual.ActiveRagdoll Ragdoll = null!;

        public override void _Ready()
        {
            base._Ready();
            if (Ragdoll == null) GD.PrintErr("[RagdollAIController] Ragdoll reference missing! Assign it in the inspector.");
        }

        public override Dictionary GetObs()
        {
            // Gathers the 110D state space containing phase, contacts, and proprioception
            float[] obs = Ragdoll.CollectObservations();
            var dict = new Dictionary();
            dict["obs"] = obs;
            return dict;
        }

        public override float GetReward()
        {
            // Calculates the multi-objective reward (r_step, r_root, r_pose)
            return Ragdoll.CalculateReward();
        }

        public override void SetAction(Dictionary action)
        {
          // Converts the incoming Python tensor into a C# float array and applies physical torques
          var actionVariant = action["action"].AsFloat32Array();
          float dt = (float)GetPhysicsProcessDeltaTime();
          Ragdoll.ApplyActions(actionVariant, dt);
        }

        public override bool GetDone()
        {
            // Terminates the episode if the torso hits the floor
            return Ragdoll.CheckIfDone();
        }

        public override void Reset()
        {
            // Resets the physical joints to the default upright stance upon failure
            Ragdoll.Call("ResetSimulation");
        }
    }
}
