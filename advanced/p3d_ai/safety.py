from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any

import numpy as np

from .contracts import SafetyLimits


@dataclass(frozen=True)
class ProjectionResult:
    action: np.ndarray
    scale: float
    intervened: bool
    reason: str


class RuntimeAssurance:
    """Independent action projection and uncertainty gate.

    This is deliberately deterministic. It does not depend on the learned model.
    """

    def __init__(self, limits: SafetyLimits, authority: Sequence[float] = (0.15, 0.12, 0.10, 0.10)):
        limits.validate()
        self.limits = limits
        self.authority = np.asarray(authority, dtype=np.float64)
        if self.authority.shape != (4,) or not np.isfinite(self.authority).all() or np.any(self.authority <= 0.0):
            raise ValueError("authority must contain four positive values")
        self._previous = np.zeros(4, dtype=np.float64)

    def reset(self) -> None:
        self._previous.fill(0.0)

    def project(self, requested: Sequence[float], state: Mapping[str, Any], dt_seconds: float) -> ProjectionResult:
        action = np.asarray(requested, dtype=np.float64)
        if action.shape != (4,) or not np.isfinite(action).all():
            return self._reject("invalid-action")
        if bool(state.get("on_ground", False)) or bool(state.get("stall", False)) or bool(state.get("overspeed", False)):
            return self._reject("hard-envelope")
        uncertainty = float(state.get("uncertainty", 0.0))
        bank = float(state.get("bank_deg", 0.0)); pitch = float(state.get("pitch_deg", 0.0))
        ias = float(state.get("ias_kts", 0.0)); stall_speed = float(state.get("stall_speed_kts", 44.0))
        sink_rate = max(0.0, -float(state.get("vs_fpm", 0.0)))
        cross_track = abs(float(state.get("cross_m", 0.0)))
        if not np.isfinite([uncertainty, bank, pitch, ias, stall_speed, sink_rate, cross_track, dt_seconds]).all() or dt_seconds <= 0.0:
            return self._reject("invalid-runtime")
        if uncertainty < 0.0 or uncertainty > self.limits.max_uncertainty:
            return self._reject("uncertainty")

        on_final = str(state.get("phase", "")) in {"Final", "Flare"}
        bank_limit = self.limits.max_final_bank_deg if on_final else self.limits.max_bank_deg
        bank_margin = bank_limit - abs(bank)
        pitch_margin = min(self.limits.max_pitch_deg - pitch, pitch - self.limits.min_pitch_deg)
        speed_margin = min(ias - stall_speed - self.limits.min_speed_margin_kts, self.limits.max_speed_kts - ias)
        margins = [
            np.clip(bank_margin / 8.0, 0.0, 1.0),
            np.clip(pitch_margin / 5.0, 0.0, 1.0),
            np.clip(speed_margin / 12.0, 0.0, 1.0),
            np.clip(1.0 - uncertainty / self.limits.max_uncertainty, 0.0, 1.0),
        ]
        if on_final:
            margins.extend((
                np.clip((self.limits.max_sink_rate_fpm - sink_rate) / 300.0, 0.0, 1.0),
                np.clip((self.limits.max_cross_track_final_m - cross_track) / 15.0, 0.0, 1.0),
            ))
        scale = min(margins)
        desired = np.clip(action, -1.0, 1.0) * self.authority * float(scale)
        max_delta = self.limits.max_action_rate_per_second * float(np.clip(dt_seconds, 0.001, 0.2))
        applied = self._previous + np.clip(desired - self._previous, -max_delta, max_delta)
        intervened = not np.allclose(applied, action * self.authority, atol=1e-7)
        self._previous = applied
        return ProjectionResult(applied.astype(np.float32), float(scale), intervened, "projected" if intervened else "ok")

    def _reject(self, reason: str) -> ProjectionResult:
        self._previous.fill(0.0)
        return ProjectionResult(self._previous.astype(np.float32), 0.0, True, reason)
