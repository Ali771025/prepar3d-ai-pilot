from __future__ import annotations

import hashlib
import json
import math
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

SCHEMA_VERSION = "observation-v3.0"
ACTION_VERSION = "residual-action-v2.0"
EPISODE_VERSION = "episode-v4.2"
SUPPORTED_EPISODE_VERSIONS = {"episode-v4.1", EPISODE_VERSION}
FULL_ACTION_VERSION = "pilot-actuator-v1.0"


@dataclass(frozen=True)
class SafetyLimits:
    max_bank_deg: float = 22.0
    max_final_bank_deg: float = 12.0
    min_pitch_deg: float = -10.0
    max_pitch_deg: float = 15.0
    min_speed_margin_kts: float = 8.0
    max_speed_kts: float = 145.0
    max_sink_rate_fpm: float = 900.0
    max_cross_track_final_m: float = 45.0
    max_action_rate_per_second: float = 0.60
    max_uncertainty: float = 0.35

    def validate(self) -> None:
        if not all(math.isfinite(float(value)) for value in asdict(self).values()):
            raise ValueError("safety limits must be finite")
        if not 5.0 <= self.max_bank_deg <= 35.0:
            raise ValueError("max_bank_deg must be in [5, 35]")
        if not 3.0 <= self.max_final_bank_deg <= self.max_bank_deg:
            raise ValueError("max_final_bank_deg is invalid")
        if self.min_pitch_deg >= self.max_pitch_deg:
            raise ValueError("pitch envelope is invalid")
        if self.max_action_rate_per_second <= 0.0:
            raise ValueError("max_action_rate_per_second must be positive")
        if self.min_speed_margin_kts <= 0.0 or self.max_speed_kts <= self.min_speed_margin_kts:
            raise ValueError("speed envelope is invalid")
        if self.max_sink_rate_fpm <= 0.0 or self.max_cross_track_final_m <= 0.0:
            raise ValueError("approach limits must be positive")
        if not 0.0 < self.max_uncertainty <= 1.0:
            raise ValueError("max_uncertainty must be in (0, 1]")


@dataclass(frozen=True)
class ExperimentManifest:
    experiment_id: str
    algorithm: str
    seed: int
    observation_schema: str = SCHEMA_VERSION
    action_schema: str = ACTION_VERSION
    dataset_version: str = "unassigned"
    scenario_set: str = "baseline-v1"
    config: dict[str, Any] = field(default_factory=dict)

    def canonical_json(self) -> str:
        return json.dumps(asdict(self), sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)

    @property
    def fingerprint(self) -> str:
        return hashlib.sha256(self.canonical_json().encode("utf-8")).hexdigest()

    def write(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = asdict(self)
        payload["fingerprint"] = self.fingerprint
        path.write_text(json.dumps(payload, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()
