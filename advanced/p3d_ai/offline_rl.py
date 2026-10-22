from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd

from .observation import feature_names

OBSERVATION_PREFIX = "obs_"
ACTION_COLUMNS = ["action_aileron", "action_elevator", "action_rudder", "action_throttle"]


def load_transition_table(path: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    frame = pd.read_parquet(path) if path.suffix.lower() == ".parquet" else pd.read_csv(path)
    observation_columns = [f"{OBSERVATION_PREFIX}{name}" for name in feature_names()]
    actual_observations = {column for column in frame if column.startswith(OBSERVATION_PREFIX)}
    required = set(ACTION_COLUMNS + ["reward", "terminal"])
    missing = required.difference(frame.columns)
    missing_observations = set(observation_columns).difference(actual_observations)
    extra_observations = actual_observations.difference(observation_columns)
    if missing or missing_observations or extra_observations:
        raise ValueError(
            "offline dataset schema mismatch: "
            f"missing={sorted(missing)} missing_observations={sorted(missing_observations)} "
            f"extra_observations={sorted(extra_observations)}"
        )
    observations = frame[observation_columns].to_numpy(np.float32)
    actions = frame[ACTION_COLUMNS].to_numpy(np.float32)
    rewards = frame["reward"].to_numpy(np.float32)
    terminals = frame["terminal"].to_numpy(np.float32)
    if not all(np.isfinite(value).all() for value in (observations, actions, rewards, terminals)):
        raise ValueError("offline dataset contains non-finite values")
    if np.any(np.abs(actions) > 1.0 + 1e-6):
        raise ValueError("offline actions must be in [-1, 1]")
    if np.any((terminals != 0.0) & (terminals != 1.0)):
        raise ValueError("terminal values must be binary")
    return observations, actions, rewards, terminals


def train(path: Path, output: Path, algorithm: str, steps: int, seed: int) -> None:
    if algorithm not in {"iql", "cql"}:
        raise ValueError("algorithm must be 'iql' or 'cql'")
    if steps < 1:
        raise ValueError("steps must be positive")
    try:
        import torch
        import d3rlpy
        from d3rlpy.algos import CQLConfig, IQLConfig
        from d3rlpy.dataset import MDPDataset
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt to train IQL/CQL") from exc
    observations, actions, rewards, terminals = load_transition_table(path)
    frame=pd.read_parquet(path) if path.suffix.lower()==".parquet" else pd.read_csv(path)
    required={"episode_id","split","timeout","action_semantics"}
    if not required.issubset(frame):raise ValueError("offline training requires episode boundaries, timeouts, splits and action semantics")
    if set(frame.action_semantics)!={"normalized_residual"}:raise ValueError("offline live residual policy requires normalized residual actions")
    if (frame.groupby("episode_id")["split"].nunique()>1).any():raise ValueError("episode crosses splits")
    mask=frame["split"].eq("train").to_numpy()
    if not mask.any():raise ValueError("no training split")
    timeouts=frame["timeout"].to_numpy(np.float32)
    if not np.isfinite(timeouts).all() or np.any((timeouts!=0)&(timeouts!=1)):raise ValueError("invalid timeouts")
    for _,episode in frame[mask].groupby("episode_id",sort=False):
        if not (episode.iloc[-1]["terminal"] or episode.iloc[-1]["timeout"]):raise ValueError("unterminated episode")
    d3rlpy.seed(seed)
    dataset = MDPDataset(observations[mask], actions[mask], rewards[mask], terminals[mask],timeouts=timeouts[mask])
    config = IQLConfig() if algorithm == "iql" else CQLConfig()
    learner = config.create(device=0 if torch.cuda.is_available() else False)
    learner.fit(dataset, n_steps=steps, experiment_name=f"p3d_{algorithm}_{seed}")
    output.parent.mkdir(parents=True, exist_ok=True)
    learner.save(str(output))
    output.with_suffix(".json").write_text(json.dumps({"algorithm": algorithm, "steps": steps, "seed": seed,"action_semantics":"normalized_residual","flight_validated":False}, indent=2), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("dataset", type=Path)
    parser.add_argument("--algorithm", choices=("iql", "cql"), default="iql")
    parser.add_argument("--steps", type=int, default=100_000)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--output", type=Path, default=Path("artifacts/offline_policy.d3"))
    args = parser.parse_args()
    if args.steps < 1:
        parser.error("--steps must be positive")
    train(args.dataset, args.output, args.algorithm, args.steps, args.seed)


if __name__ == "__main__":
    main()
