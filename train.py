import os
import torch
import torch.nn as nn
import torch.nn.functional as F
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import VecNormalize
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.torch_layers import BaseFeaturesExtractor
from godot_rl.wrappers.stable_baselines_wrapper import StableBaselinesGodotEnv
from typing import Callable, Dict

def linear_schedule(initial_value: float, final_value: float = 0.0) -> Callable[[float], float]:
    def func(progress_remaining: float) -> float:
        return final_value + (initial_value - final_value) * progress_remaining
    return func

class TelemetryCallback(BaseCallback):
    def _on_step(self) -> bool:
        return True

# --- DEEPLOCO BILINEAR PHASE TRANSFORM ---
class BilinearPhaseExtractor(BaseFeaturesExtractor):
    def __init__(self, observation_space, phase_dim=4):
        # Explicitly target the "obs" key within the Gymnasium Dict space
        original_dim = observation_space.spaces["obs"].shape[0]
        self.base_dim = original_dim - phase_dim
        self.phase_dim = phase_dim

        # The resulting dimension after the outer product
        features_dim = self.base_dim * self.phase_dim
        super().__init__(observation_space, features_dim=features_dim)

    def forward(self, observations: Dict[str, torch.Tensor]) -> torch.Tensor:
        # Extract the flat tensor payload from the observation dictionary
        obs_tensor = observations["obs"]

        # 1. Split base features and normalized one-hot phase
        base_obs = obs_tensor[:, :self.base_dim]
        norm_phi = obs_tensor[:, -self.phase_dim:]

        # 2. Recover the absolute one-hot tensor destroyed by VecNormalize
        active_indices = torch.argmax(norm_phi, dim=1)
        clean_phi = F.one_hot(active_indices, num_classes=self.phase_dim).float()

        # 3. Outer Product (Bilinear Transform)
        expanded = clean_phi.unsqueeze(2) * base_obs.unsqueeze(1)

        # 4. Flatten to (batch, base_dim * phase_dim)
        return expanded.view(-1, self.features_dim)

env = StableBaselinesGodotEnv(env_path=None, port=11008, show_window=True)
env = VecNormalize(env, norm_obs=True, norm_reward=True, clip_obs=10.)

policy_kwargs = dict(
    features_extractor_class=BilinearPhaseExtractor,
    features_extractor_kwargs=dict(phase_dim=4),
    net_arch=dict(pi=[512, 256], vf=[512, 256])
)

model = PPO(
    "MultiInputPolicy",
    env,
    verbose=1,
    learning_rate=linear_schedule(3e-4, 5e-5),
    n_steps=4096,
    batch_size=1024,
    use_sde=True,
    sde_sample_freq=4,
    policy_kwargs=policy_kwargs,
    tensorboard_log="./logs/ppo_llc_tensorboard/"
)

print("Starting PPO training loop...")
model.learn(total_timesteps=1000000, callback=TelemetryCallback())

model.save("ppo_locomotion_llc")
env.save("vec_normalize_llc.pkl")
env.close()
