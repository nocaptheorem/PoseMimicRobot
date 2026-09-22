import os
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import VecNormalize
from godot_rl.wrappers.stable_baselines_wrapper import StableBaselinesGodotEnv

# 1. Initialize the Godot Environment via TCP
# Note: env_path=None allows it to connect to the open editor instance
env = StableBaselinesGodotEnv(env_path=None, port=11008, show_window=True)

# 2. Apply VecNormalize to standardize the 289D observation space
env = VecNormalize(env, norm_obs=True, norm_reward=True, clip_obs=10.)

# 3. Initialize the PPO Policy
model = PPO(
    "MultiInputPolicy",
    env,
    verbose=1,
    learning_rate=3e-4,
    n_steps=2048,
    batch_size=64,
    tensorboard_log="./logs/ppo_llc_tensorboard/"
)

# 4. Execute the Training Loop
print("Starting PPO training loop...")
model.learn(total_timesteps=1000000)

# 5. Save the policy and normalization statistics
model.save("ppo_locomotion_llc")
env.save("vec_normalize_llc.pkl")

env.close()
