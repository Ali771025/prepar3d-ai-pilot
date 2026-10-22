from __future__ import annotations

import argparse
import random
from pathlib import Path

import numpy as np

from .contracts import ExperimentManifest


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    try:
        import torch
        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)
    except ImportError:
        pass


def train(args: argparse.Namespace) -> None:
    if args.steps < 1:
        raise ValueError("steps must be positive")
    try:
        import torch
        from sb3_contrib import TQC
        from stable_baselines3 import SAC
        from stable_baselines3.common.callbacks import CheckpointCallback
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt before online training") from exc
    from .live_env import AdvancedP3DEnv

    seed_everything(args.seed)
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = ExperimentManifest(
        experiment_id=args.experiment_id, algorithm=args.algorithm, seed=args.seed,
        dataset_version=args.dataset_version, scenario_set=args.scenario_set,
        config={"steps": args.steps, "stage": args.stage, "agent_hz": args.agent_hz},
    )
    manifest.write(args.output / "experiment.json")
    env = AdvancedP3DEnv(args.bridge, args.stage, args.agent_hz, frames=args.frames,
                         curriculum_level=args.curriculum_level)
    algorithm_type = SAC if args.algorithm == "sac" else TQC
    model = algorithm_type(
        "MlpPolicy", env, learning_rate=3e-4, buffer_size=500_000,
        learning_starts=5_000, batch_size=256, tau=0.005, gamma=0.995,
        train_freq=(1, "step"), gradient_steps=1, ent_coef="auto_0.1",
        policy_kwargs={"net_arch": [256, 256, 128], "activation_fn": torch.nn.SiLU},
        tensorboard_log=str(args.output / "tensorboard"), seed=args.seed, device="auto", verbose=1,
    )
    checkpoint = CheckpointCallback(save_freq=max(1000, args.steps // 20), save_path=str(args.output / "checkpoints"),
                                    name_prefix=f"{args.algorithm}_{args.stage}", save_replay_buffer=True)
    model.learn(args.steps, callback=checkpoint, progress_bar=True)
    model.save(args.output / "candidate.zip")
    model.save_replay_buffer(args.output / "candidate.replay.pkl")
    env.close()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--algorithm", choices=("sac", "tqc"), default="tqc")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--stage", choices=("takeoff", "airwork", "pattern", "approach", "full"), default="airwork")
    parser.add_argument("--steps", type=int, default=100_000)
    parser.add_argument("--agent-hz", type=float, default=5.0)
    parser.add_argument("--frames", type=int, default=5)
    parser.add_argument("--curriculum-level", type=int, choices=range(6), default=0)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--experiment-id", default="p3d-v4-tqc-seed42")
    parser.add_argument("--dataset-version", default="unassigned")
    parser.add_argument("--scenario-set", default="curriculum-v2")
    parser.add_argument("--output", type=Path, default=Path("artifacts/online"))
    args = parser.parse_args()
    if args.steps < 1:
        parser.error("--steps must be positive")
    return args


if __name__ == "__main__":
    train(parse_args())
