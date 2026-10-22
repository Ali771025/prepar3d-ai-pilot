from __future__ import annotations

import argparse
from pathlib import Path

from .contracts import ExperimentManifest
from .train_online import seed_everything


def main() -> None:
    parser = argparse.ArgumentParser(description="Train SAC/TQC over all curriculum stages and independent seeds")
    parser.add_argument("--algorithm", choices=("sac", "tqc"), default="tqc")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--stages", default="airwork,pattern,approach,full")
    parser.add_argument("--seeds", default="42,1337,9001")
    parser.add_argument("--steps-per-stage", type=int, default=100_000)
    parser.add_argument("--frames", type=int, default=5)
    parser.add_argument("--agent-hz", type=float, default=5.0)
    parser.add_argument("--output", type=Path, default=Path("artifacts/curriculum"))
    args = parser.parse_args()
    import torch
    from sb3_contrib import TQC
    from stable_baselines3 import SAC
    from stable_baselines3.common.callbacks import CheckpointCallback

    from .live_env import AdvancedP3DEnv

    stages = [item.strip() for item in args.stages.split(",") if item.strip()]
    seeds = [int(item.strip()) for item in args.seeds.split(",") if item.strip()]
    if not stages or not seeds:
        raise ValueError("stages and seeds are required")
    invalid_stages = set(stages).difference({"takeoff", "airwork", "pattern", "approach", "full"})
    if invalid_stages:
        raise ValueError(f"invalid curriculum stages: {sorted(invalid_stages)}")
    if len(set(stages)) != len(stages):
        raise ValueError("curriculum stages must not be duplicated")
    if args.steps_per_stage < 1:
        raise ValueError("steps-per-stage must be positive")
    algorithm_type = SAC if args.algorithm == "sac" else TQC
    for seed in seeds:
        seed_everything(seed)
        seed_root = args.output / f"{args.algorithm}-seed{seed}"
        seed_root.mkdir(parents=True, exist_ok=True)
        model = None
        for level, stage in enumerate(stages):
            env = AdvancedP3DEnv(args.bridge, stage, args.agent_hz, args.frames, min(level, 5))
            if model is None:
                model = algorithm_type(
                    "MlpPolicy", env, learning_rate=3e-4, buffer_size=500_000,
                    learning_starts=5_000, batch_size=256, tau=0.005, gamma=0.995,
                    ent_coef="auto_0.1", policy_kwargs={"net_arch": [256, 256, 128], "activation_fn": torch.nn.SiLU},
                    tensorboard_log=str(seed_root / "tensorboard"), seed=seed, device="auto", verbose=1,
                )
            else:
                model.set_env(env, force_reset=True)
            callback = CheckpointCallback(save_freq=max(1000, args.steps_per_stage // 20),
                                          save_path=str(seed_root / "checkpoints"), name_prefix=f"{stage}",
                                          save_replay_buffer=True)
            model.learn(args.steps_per_stage, callback=callback, reset_num_timesteps=False, progress_bar=True)
            model.save(seed_root / f"{stage}.zip")
            model.save_replay_buffer(seed_root / f"{stage}.replay.pkl")
            env.close()
        assert model is not None
        model.save(seed_root / "candidate.zip")
        ExperimentManifest(f"p3d-v4-{args.algorithm}-seed{seed}", args.algorithm, seed,
                           scenario_set="curriculum-v2", config={"stages": stages, "steps_per_stage": args.steps_per_stage,
                                                                  "frames": args.frames, "agent_hz": args.agent_hz}).write(seed_root / "experiment.json")


if __name__ == "__main__":
    main()
