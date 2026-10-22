from __future__ import annotations

import json
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np


@dataclass(frozen=True)
class CorrectionSegment:
    episode_id: str
    start_index: int
    end_index: int
    reason: str
    pilot_id: str
    phase: str
    quality_score: float


def detect_override_segments(
    pilot_actions: np.ndarray,
    autopilot_actions: np.ndarray,
    phases: Sequence[str],
    episode_id: str,
    pilot_id: str,
    threshold: float = 0.18,
    minimum_samples: int = 5,
) -> list[CorrectionSegment]:
    pilot = np.asarray(pilot_actions, dtype=np.float64)
    auto = np.asarray(autopilot_actions, dtype=np.float64)
    if pilot.shape != auto.shape or pilot.ndim != 2 or pilot.shape[1] != 4:
        raise ValueError("actions must have shape [N, 4]")
    if len(phases) != len(pilot):
        raise ValueError("phases length mismatch")
    if not np.isfinite(pilot).all() or not np.isfinite(auto).all():
        raise ValueError("actions must be finite")
    if not np.isfinite(threshold) or threshold < 0.0:
        raise ValueError("threshold must be finite and non-negative")
    if isinstance(minimum_samples, bool) or not isinstance(minimum_samples, int) or minimum_samples < 1:
        raise ValueError("minimum_samples must be a positive integer")
    active = np.linalg.norm(pilot - auto, axis=1) >= threshold
    segments: list[CorrectionSegment] = []
    start: int | None = None
    for index, value in enumerate(np.r_[active, False]):
        if value and start is None:
            start = index
        elif not value and start is not None:
            if index - start >= minimum_samples:
                disagreement = np.linalg.norm(pilot[start:index] - auto[start:index], axis=1)
                segments.append(CorrectionSegment(
                    episode_id, start, index - 1, "pilot-override", pilot_id,
                    max(set(phases[start:index]), key=list(phases[start:index]).count),
                    float(np.clip(disagreement.mean(), 0.0, 1.0)),
                ))
            start = None
    return segments


def append_manifest(path: Path, segments: Sequence[CorrectionSegment]) -> None:
    existing = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"segments": []}
    if not isinstance(existing, dict) or not isinstance(existing.get("segments"), list):
        raise TypeError("correction manifest is malformed")
    existing["segments"].extend(asdict(item) for item in segments)
    temporary = path.with_suffix(".tmp")
    temporary.parent.mkdir(parents=True, exist_ok=True)
    temporary.write_text(json.dumps(existing, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def style_vector(actions: np.ndarray, phases: Sequence[str]) -> np.ndarray:
    values = np.asarray(actions, dtype=np.float64)
    if values.ndim != 2 or values.shape[1] != 4 or len(phases) != len(values) or len(values) == 0:
        raise ValueError("invalid style data")
    if not np.isfinite(values).all():
        raise ValueError("style actions must be finite")
    delta = np.diff(values, axis=0, prepend=values[:1])
    final_mask = np.asarray([phase in {"Final", "Flare"} for phase in phases])
    turn_mask = np.asarray([phase in {"Crosswind", "Base"} for phase in phases])
    features = np.r_[
        np.mean(np.abs(values), axis=0),
        np.mean(np.abs(delta), axis=0),
        np.mean(values[final_mask], axis=0) if final_mask.any() else np.zeros(4),
        np.mean(values[turn_mask], axis=0) if turn_mask.any() else np.zeros(4),
    ]
    norm = np.linalg.norm(features)
    return (features / norm if norm > 1e-9 else features).astype(np.float32)
