using Godot;
using Godot.Collections;

namespace ActiveRagdollModules
{
    public abstract partial class AIController3D : Node3D
    {
        [Export]
        public Dictionary ControlModes { get; set; } = new Dictionary
        {
            { "HUMAN", 0 },
            { "ONNX", 1 },
            { "TRAINING", 2 },
            { "INHERIT_FROM_SYNC", 3 } // ADDED: New sync state
        };

        [Export] public int control_mode { get; set; } = 2;
        [Export] public string policy_name { get; set; } = "default";

        // FIXED: Expose the reset flag required by sync.gd during episode restarts
        [Export] public bool needs_reset { get; set; } = false;

        public virtual void set_heuristic(string heuristic) {}

        public abstract Dictionary get_obs();
        public abstract float get_reward();
        public abstract void set_action(Dictionary action);
        public abstract bool get_done();
        public abstract void reset();
        public abstract Dictionary get_obs_space();
        public abstract Dictionary get_action_space();
        public abstract Dictionary get_info();
        public abstract void zero_reward();
        public abstract void set_done_false();
    }
}
