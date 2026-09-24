import os
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import VecNormalize
from stable_baselines3.common.callbacks import BaseCallback
from godot_rl.wrappers.stable_baselines_wrapper import StableBaselinesGodotEnv
from typing import Callable

def linear_schedule(initial_value: float, final_value: float = 0.0) -> Callable[[float], float]:
    """
    Linear learning rate schedule.

    :param initial_value: The starting learning rate.
    :param final_value: The minimum learning rate at the end of training.
    :return: A schedule function that accepts remaining progress (1.0 to 0.0).
    """
    def func(progress_remaining: float) -> float:
        # progress_remaining starts at 1.0 (start) and decays to 0.0 (end)
        return final_value + (initial_value - final_value) * progress_remaining
    return func

# Custom Callback to trace episode terminations
class TelemetryCallback(BaseCallback):
    def _on_step(self) -> bool:
        # Check if the environment just returned a 'done' signal
        #if self.locals.get("dones") is not None and self.locals["dones"][0]:
        #    print(f"[PYTHON TELEMETRY] Received DONE=True at step {self.num_timesteps}. SB3 is issuing reset...")
        return True

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
    learning_rate=linear_schedule(3e-4, 5e-5),  # Linearly decays from 3e-4 to 5e-5
    n_steps=4096,
    batch_size=1024,
    use_sde=True, # Critical for smooth physics exploration
    sde_sample_freq=4,
    tensorboard_log="./logs/ppo_llc_tensorboard/"
)

# 4. Execute the Training Loop
print("Starting PPO training loop...")
model.learn(total_timesteps=1000000, callback=TelemetryCallback())

# 5. Save the policy and normalization statistics
model.save("ppo_locomotion_llc")
env.save("vec_normalize_llc.pkl")

env.close()
