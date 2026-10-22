from __future__ import annotations

import argparse
from pathlib import Path

import torch
from p3d_env import P3DResidualEnv
from stable_baselines3 import SAC
from stable_baselines3.common.callbacks import (
    BaseCallback,
    CallbackList,
    CheckpointCallback,
)
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor


class RewardComponentsCallback(BaseCallback):
    def _on_step(self) -> bool:
        infos = self.locals.get("infos", [])
        values: dict[str, list[float]] = {}
        for info in infos:
            for name, value in info.get("reward_components", {}).items():
                values.setdefault(name, []).append(float(value))
        for name, samples in values.items():
            self.logger.record(f"reward/{name}", sum(samples) / len(samples))
        return True


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train residual SAC against the live P3D v4 bridge")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--curriculum", default="airwork,pattern,approach,full")
    parser.add_argument("--steps-per-stage", type=int, default=50_000)
    parser.add_argument("--agent-hz", type=float, default=5.0)
    parser.add_argument("--output", type=Path, default=Path("artifacts/residual_sac"))
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--check-env", action="store_true")
    return parser.parse_args()


def build_model(env: Monitor, output: Path, seed: int) -> SAC:
    return SAC(
        "MlpPolicy",
        env,
        learning_rate=3e-4,
        buffer_size=250_000,
        learning_starts=2_000,
        batch_size=256,
        tau=0.005,
        gamma=0.995,
        train_freq=(1, "step"),
        gradient_steps=1,
        ent_coef="auto_0.1",
        target_entropy="auto",
        use_sde=True,
        sde_sample_freq=4,
        policy_kwargs={"net_arch": [256, 256], "activation_fn": torch.nn.SiLU},
        tensorboard_log=str(output / "tensorboard"),
        seed=seed,
        device="auto",
        verbose=1,
    )


def main() -> None:
    args = parse_args()
    stages = [value.strip() for value in args.curriculum.split(",") if value.strip()]
    if not stages:
        raise ValueError("Curriculum is empty")
    invalid_stages = set(stages).difference({"takeoff", "airwork", "pattern", "approach", "full"})
    if invalid_stages:
        raise ValueError(f"Unknown curriculum stages: {sorted(invalid_stages)}")
    if len(set(stages)) != len(stages):
        raise ValueError("Curriculum stages must not be duplicated")
    if args.steps_per_stage < 1:
        raise ValueError("steps-per-stage must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    model: SAC | None = None

    for index, stage in enumerate(stages):
        raw_env = P3DResidualEnv(bridge_url=args.bridge, stage=stage, agent_hz=args.agent_hz)
        if args.check_env and index == 0:
            check_env(raw_env, warn=True)
        env = Monitor(raw_env, filename=str(args.output / f"monitor_{stage}"), info_keywords=("is_success", "stage"))

        if model is None:
            if args.resume:
                model = SAC.load(args.resume, env=env, device="auto")
                replay_path = args.resume.with_suffix(".replay.pkl")
                if replay_path.exists():
                    model.load_replay_buffer(replay_path)
            else:
                model = build_model(env, args.output, args.seed)
        else:
            model.set_env(env, force_reset=True)

        checkpoint = CheckpointCallback(
            save_freq=max(1000, args.steps_per_stage // 10),
            save_path=str(args.output / "checkpoints"),
            name_prefix=f"sac_{stage}",
            save_replay_buffer=True,
            save_vecnormalize=False,
        )
        model.learn(
            total_timesteps=args.steps_per_stage,
            callback=CallbackList([checkpoint, RewardComponentsCallback()]),
            tb_log_name=f"residual_{stage}",
            reset_num_timesteps=False,
            progress_bar=True,
        )
        stage_path = args.output / f"residual_sac_{stage}.zip"
        model.save(stage_path)
        model.save_replay_buffer(stage_path.with_suffix(".replay.pkl"))
        env.close()

    assert model is not None
    final_path = args.output / "residual_sac_final.zip"
    model.save(final_path)
    model.save_replay_buffer(final_path.with_suffix(".replay.pkl"))
    print(f"saved={final_path}")


if __name__ == "__main__":
    main()
