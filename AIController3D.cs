using Godot;
using Godot.Collections;

namespace ActiveRagdollModules
{
    /// <summary>
    /// Base class for Godot RL Agents C# integration.
    /// The external GDScript Sync node will duck-type call these methods using Godot's reflection.
    /// </summary>
    public abstract partial class AIController3D : Node3D
    {
        /// <summary>
        /// Returns the continuous state space array (s_L) to the Python environment.
        /// </summary>
        public abstract Dictionary GetObs();

        /// <summary>
        /// Returns the scalar reward (r_L) for the current step.
        /// </summary>
        public abstract float GetReward();

        /// <summary>
        /// Receives the continuous action tensor (a_L) from the Python policy and applies it.
        /// </summary>
        public abstract void SetAction(Dictionary action);

        /// <summary>
        /// Evaluates if the episode has reached a terminal state (e.g., falling over).
        /// </summary>
        public abstract bool GetDone();

        /// <summary>
        /// Resets the environment and agent state when an episode terminates.
        /// </summary>
        public abstract void Reset();
    }
}
