import numpy as np
from godot_rl.wrappers.stable_baselines_wrapper import StableBaselinesGodotEnv

def main():
    print("Starting Python TCP Server on port 11008...")
    env = StableBaselinesGodotEnv(env_path=None, port=11008)

    print("TCP Handshake successful! C# and Python are connected.")

    obs = env.reset()
    print(f"Observation space acquired. Shape: {obs['obs'].shape}")

    print("Sending random neural actions for 10 physical frames...")
    for i in range(10):
        # FIXED: Wrap the action in a NumPy array instead of a native Python list
        action = np.array([env.action_space.sample()])

        obs, reward, done, info = env.step(action)
        print(f"Step {i} | Multi-Objective Reward: {reward[0]:.4f}")

        if done[0]:
            print("Agent fell! Godot reset triggered.")
            env.reset()

    env.close()
    print("Bridge test complete. Terminating.")

if __name__ == "__main__":
    main()
