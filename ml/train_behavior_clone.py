"""Train a GRU policy on manually flown P3D AI Pilot CSV logs.

Use only reviewed manual-flight logs. Autopilot-generated rows must not be mixed
into the human demonstration dataset unless that choice is deliberate.
"""

from __future__ import annotations

import argparse
import json
import math
import random
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
import pandas as pd
import torch
from torch import nn
from torch.utils.data import DataLoader, Dataset

FEATURES = [
    "alt_agl_ft",
    "ias_kts",
    "gs_kts",
    "vs_fpm",
    "pitch_deg",
    "bank_deg",
    "heading_deg",
    "aoa_deg",
    "beta_deg",
    "pilot_throttle",
    "pilot_flaps",
    "along_m",
    "cross_m",
]
TARGETS = ["pilot_yoke_x", "pilot_yoke_y", "pilot_rudder", "pilot_throttle"]


@dataclass
class Normalization:
    feature_mean: list[float]
    feature_std: list[float]
    target_mean: list[float]
    target_std: list[float]


class SequenceDataset(Dataset):
    def __init__(self, frames: list[pd.DataFrame], sequence_length: int, normalization: Normalization):
        if sequence_length < 1:
            raise ValueError("sequence_length must be positive")
        self.sequence_length = sequence_length
        self.items: list[tuple[np.ndarray, np.ndarray]] = []
        f_mean = np.asarray(normalization.feature_mean, dtype=np.float32)
        f_std = np.asarray(normalization.feature_std, dtype=np.float32)
        t_mean = np.asarray(normalization.target_mean, dtype=np.float32)
        t_std = np.asarray(normalization.target_std, dtype=np.float32)

        for frame in frames:
            features = (frame[FEATURES].to_numpy(np.float32) - f_mean) / f_std
            targets = (frame[TARGETS].to_numpy(np.float32) - t_mean) / t_std
            for end in range(sequence_length - 1, len(frame)):
                start = end - sequence_length + 1
                self.items.append((features[start : end + 1], targets[end]))

    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor]:
        features, target = self.items[index]
        return torch.from_numpy(features), torch.from_numpy(target)


class PilotGru(nn.Module):
    def __init__(self, feature_count: int, hidden_size: int = 128, layers: int = 2):
        super().__init__()
        self.gru = nn.GRU(feature_count, hidden_size, layers, batch_first=True, dropout=0.15 if layers > 1 else 0.0)
        self.head = nn.Sequential(
            nn.Linear(hidden_size, hidden_size),
            nn.SiLU(),
            nn.Dropout(0.10),
            nn.Linear(hidden_size, len(TARGETS)),
        )

    def forward(self, sequence: torch.Tensor) -> torch.Tensor:
        encoded, _ = self.gru(sequence)
        return self.head(encoded[:, -1, :])


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("logs", nargs="+", type=Path, help="Reviewed manual-flight CSV files")
    parser.add_argument("--output", type=Path, default=Path("artifacts/pilot_gru.pt"))
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--sequence", type=int, default=25)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--learning-rate", type=float, default=2e-3)
    parser.add_argument("--seed", type=int, default=42)
    return parser.parse_args()


def load_frames(paths: list[Path]) -> list[pd.DataFrame]:
    frames: list[pd.DataFrame] = []
    required = set(FEATURES + TARGETS + ["phase", "safety"])
    for path in paths:
        frame = pd.read_csv(path)
        missing = required.difference(frame.columns)
        if missing:
            raise ValueError(f"{path}: missing columns: {sorted(missing)}")
        frame = frame.replace([np.inf, -np.inf], np.nan).dropna(subset=FEATURES + TARGETS)
        safety = frame["safety"].fillna("").astype(str).str.strip().str.upper()
        frame = frame[safety.isin(["", "OK"])]
        if len(frame) < 100:
            raise ValueError(f"{path}: fewer than 100 usable rows")
        actions = frame[TARGETS].to_numpy(np.float64)
        if np.any(np.abs(actions[:, :3]) > 1.0 + 1e-6) or np.any((actions[:, 3] < 0.0) | (actions[:, 3] > 1.0)):
            raise ValueError(f"{path}: pilot controls are outside their valid range")
        frames.append(frame.reset_index(drop=True))
    return frames


def make_normalization(frames: list[pd.DataFrame]) -> Normalization:
    combined = pd.concat(frames, ignore_index=True)
    f_mean = combined[FEATURES].mean().to_numpy(np.float64)
    f_std = combined[FEATURES].std().to_numpy(np.float64)
    t_mean = combined[TARGETS].mean().to_numpy(np.float64)
    t_std = combined[TARGETS].std().to_numpy(np.float64)
    f_std[f_std < 1e-6] = 1.0
    t_std[t_std < 1e-6] = 1.0
    return Normalization(f_mean.tolist(), f_std.tolist(), t_mean.tolist(), t_std.tolist())


def evaluate(model: nn.Module, loader: DataLoader, device: torch.device) -> float:
    model.eval()
    total = 0.0
    count = 0
    with torch.no_grad():
        for features, target in loader:
            prediction = model(features.to(device))
            loss = nn.functional.smooth_l1_loss(prediction, target.to(device), reduction="sum")
            total += float(loss.item())
            count += int(target.numel())
    return total / max(1, count)


def main() -> None:
    args = parse_args()
    if (args.epochs < 1 or args.sequence < 1 or args.batch_size < 1
            or not math.isfinite(args.learning_rate) or args.learning_rate <= 0.0):
        raise ValueError("epochs, sequence, batch size, and learning rate must be positive")
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)

    frames = load_frames(args.logs)
    random.shuffle(frames)
    validation_count = max(1, round(len(frames) * 0.2)) if len(frames) > 1 else 0
    validation_frames = frames[:validation_count]
    training_frames = frames[validation_count:]
    if not training_frames:
        training_frames = frames
        validation_frames = frames

    normalization = make_normalization(training_frames)
    train_set = SequenceDataset(training_frames, args.sequence, normalization)
    validation_set = SequenceDataset(validation_frames or training_frames, args.sequence, normalization)
    if not train_set:
        raise ValueError("No training sequences were created")

    train_loader = DataLoader(train_set, batch_size=args.batch_size, shuffle=True, num_workers=0)
    validation_loader = DataLoader(validation_set, batch_size=args.batch_size, shuffle=False, num_workers=0)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = PilotGru(len(FEATURES)).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate, weight_decay=1e-4)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=max(1, args.epochs))
    best_loss = float("inf")
    best_state: dict[str, torch.Tensor] | None = None

    for epoch in range(1, args.epochs + 1):
        model.train()
        for features, target in train_loader:
            optimizer.zero_grad(set_to_none=True)
            prediction = model(features.to(device))
            loss = nn.functional.smooth_l1_loss(prediction, target.to(device))
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), max_norm=2.0)
            optimizer.step()
        scheduler.step()
        validation_loss = evaluate(model, validation_loader, device)
        print(f"epoch={epoch:03d} validation_loss={validation_loss:.6f}")
        if validation_loss < best_loss:
            best_loss = validation_loss
            best_state = {name: value.detach().cpu().clone() for name, value in model.state_dict().items()}

    if best_state is None:
        raise RuntimeError("Training produced no checkpoint")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.save(
        {
            "model_state": best_state,
            "features": FEATURES,
            "targets": TARGETS,
            "sequence_length": args.sequence,
            "normalization": asdict(normalization),
            "validation_loss": best_loss,
        },
        args.output,
    )
    args.output.with_suffix(".json").write_text(
        json.dumps({"features": FEATURES, "targets": TARGETS, "sequence_length": args.sequence,
                    "normalization": asdict(normalization), "validation_loss": best_loss}, indent=2),
        encoding="utf-8",
    )
    print(f"saved={args.output} device={device} sequences={len(train_set)}")


if __name__ == "__main__":
    main()
