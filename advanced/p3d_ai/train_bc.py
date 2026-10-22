from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

import numpy as np
import pandas as pd

from .models import build_gru_policy, require_torch
from .observation import feature_names

ACTION_COLUMNS = ["action_aileron", "action_elevator", "action_rudder", "action_throttle"]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("dataset", type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/bc/pilot_gru.pt"))
    parser.add_argument("--sequence", type=int, default=25)
    parser.add_argument("--epochs", type=int, default=50)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    if args.sequence < 1 or args.epochs < 1 or args.batch_size < 1:
        parser.error("--sequence, --epochs, and --batch-size must be positive")
    torch, nn = require_torch()
    from torch.utils.data import DataLoader, TensorDataset

    random.seed(args.seed); np.random.seed(args.seed); torch.manual_seed(args.seed)
    frame = pd.read_parquet(args.dataset) if args.dataset.suffix.lower() == ".parquet" else pd.read_csv(args.dataset)
    observation_columns = [f"obs_{name}" for name in feature_names()]
    required = set(observation_columns + ACTION_COLUMNS + ["episode_id", "phase", "action_semantics"])
    missing = required.difference(frame.columns)
    extra_observations = {column for column in frame if column.startswith("obs_")}.difference(observation_columns)
    if missing or extra_observations:
        raise ValueError(f"dataset schema mismatch: missing={sorted(missing)} extra_observations={sorted(extra_observations)}")
    numeric = frame[observation_columns + ACTION_COLUMNS].to_numpy(np.float32)
    if not np.isfinite(numeric).all():
        raise ValueError("training dataset contains non-finite observations or actions")
    if np.any(np.abs(frame[ACTION_COLUMNS].to_numpy(np.float32)) > 1.0 + 1e-6):
        raise ValueError("training actions must be in [-1, 1]")
    if frame["episode_id"].isna().any() or frame["phase"].isna().any():
        raise ValueError("episode_id and phase must not be missing")
    semantics=set(frame["action_semantics"].astype(str))
    if len(semantics)!=1 or not semantics.issubset({"full_pilot_actuator","normalized_residual"}):raise ValueError("mixed or missing action semantics")
    action_semantics=next(iter(semantics))
    if "split" not in frame: raise ValueError("independent train/validation/test episode splits are required")
    if not {"train","validation","test"}.issubset(set(frame["split"])): raise ValueError("all three splits required")
    if (frame.groupby("episode_id")["split"].nunique()>1).any(): raise ValueError("episode crosses splits")
    train_mask=frame["split"].eq("train").to_numpy()
    validation_mask=frame["split"].eq("validation").to_numpy()

    def sequences(mask: np.ndarray):
        xs, ys, weights = [], [], []
        for _, episode in frame[mask].groupby("episode_id", sort=False):
            values = episode[observation_columns].to_numpy(np.float32)
            targets = episode[ACTION_COLUMNS].to_numpy(np.float32)
            phases = episode["phase"].astype(str).tolist()
            for end in range(args.sequence - 1, len(episode)):
                xs.append(values[end - args.sequence + 1:end + 1])
                ys.append(targets[end])
                weights.append(2.0 if phases[end] in {"Final", "Flare"} else 1.0)
        return (
            np.asarray(xs, dtype=np.float32).reshape(-1, args.sequence, len(observation_columns)),
            np.asarray(ys, dtype=np.float32).reshape(-1, len(ACTION_COLUMNS)),
            np.asarray(weights, dtype=np.float32),
        )

    train_x, train_y, train_w = sequences(train_mask)
    val_x, val_y, _ = sequences(validation_mask)
    if not len(train_x):
        raise ValueError("no training sequences")
    if not len(val_x):
        raise ValueError("no validation sequences")
    loader = DataLoader(TensorDataset(torch.from_numpy(train_x), torch.from_numpy(train_y), torch.from_numpy(train_w)),
                        batch_size=args.batch_size, shuffle=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = build_gru_policy(len(observation_columns)).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=2e-3, weight_decay=1e-4)
    best_loss = float("inf"); best_state = None
    for epoch in range(1, args.epochs + 1):
        model.train()
        for x, y, weight in loader:
            optimizer.zero_grad(set_to_none=True)
            prediction = model(x.to(device))
            loss = (nn.functional.smooth_l1_loss(prediction, y.to(device), reduction="none").mean(1) * weight.to(device)).mean()
            loss.backward(); nn.utils.clip_grad_norm_(model.parameters(), 2.0); optimizer.step()
        model.eval()
        with torch.no_grad():
            val_prediction = model(torch.from_numpy(val_x).to(device))
            val_loss = float(nn.functional.smooth_l1_loss(val_prediction, torch.from_numpy(val_y).to(device)).item())
        print(f"epoch={epoch:03d} validation_loss={val_loss:.6f}")
        if val_loss < best_loss:
            best_loss = val_loss
            best_state = {name: value.detach().cpu().clone() for name, value in model.state_dict().items()}
    if best_state is None or not np.isfinite(best_loss):
        raise RuntimeError("training produced no finite checkpoint")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.save({"model_state": best_state, "input_size": len(observation_columns), "features": observation_columns,
                "sequence_length": args.sequence, "validation_loss": best_loss, "seed": args.seed,"action_semantics":action_semantics}, args.output)
    args.output.with_suffix(".json").write_text(json.dumps({"validation_loss": best_loss, "features": observation_columns,
                                                             "sequence_length": args.sequence, "seed": args.seed,"action_semantics":action_semantics}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
