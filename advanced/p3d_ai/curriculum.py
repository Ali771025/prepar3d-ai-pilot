from __future__ import annotations

import random
from collections.abc import Iterable
from dataclasses import dataclass


@dataclass(frozen=True)
class ScenarioParameters:
    seed: int
    level: int
    wind_speed_kts: float
    wind_direction_offset_deg: float
    gust_kts: float
    weight_fraction: float
    cg_fraction: float
    sensor_noise_fraction: float
    action_delay_ms: int
    telemetry_drop_probability: float
    start_position_error_m: float
    start_heading_error_deg: float
    pattern_direction: str


class AdaptiveCurriculum:
    def __init__(self, level: int = 0, promote_rate: float = 0.90, demote_rate: float = 0.65, window: int = 40):
        if level not in range(6):
            raise ValueError("level must be 0..5")
        if not 0.0 <= demote_rate < promote_rate <= 1.0:
            raise ValueError("rates must satisfy 0 <= demote_rate < promote_rate <= 1")
        if isinstance(window, bool) or not isinstance(window, int) or window < 1:
            raise ValueError("window must be a positive integer")
        self.level = level
        self.promote_rate = promote_rate
        self.demote_rate = demote_rate
        self.window = window
        self._history: list[bool] = []

    def observe(self, success: bool, safe: bool) -> int:
        self._history.append(bool(success and safe))
        self._history = self._history[-self.window :]
        if len(self._history) < self.window:
            return self.level
        rate = sum(self._history) / len(self._history)
        if rate >= self.promote_rate and self.level < 5:
            self.level += 1
            self._history.clear()
        elif rate < self.demote_rate and self.level > 0:
            self.level -= 1
            self._history.clear()
        return self.level

    def sample(self, seed: int) -> ScenarioParameters:
        if isinstance(seed, bool) or not isinstance(seed, int):
            raise TypeError("seed must be an integer")
        rng = random.Random(seed)
        level = self.level
        wind_max = (0.0, 4.0, 8.0, 12.0, 18.0, 24.0)[level]
        gust_max = (0.0, 0.0, 2.0, 4.0, 7.0, 10.0)[level]
        delay_max = (0, 25, 50, 90, 140, 200)[level]
        noise_max = (0.0, 0.002, 0.005, 0.01, 0.02, 0.03)[level]
        drop_max = (0.0, 0.0, 0.001, 0.003, 0.007, 0.01)[level]
        return ScenarioParameters(
            seed=seed,
            level=level,
            wind_speed_kts=rng.uniform(0.0, wind_max),
            wind_direction_offset_deg=rng.uniform(-180.0, 180.0),
            gust_kts=rng.uniform(0.0, gust_max),
            weight_fraction=rng.uniform(max(0.75, 1.0 - level * 0.04), min(1.15, 1.0 + level * 0.03)),
            cg_fraction=rng.uniform(max(0.30, 0.50 - level * 0.025), min(0.70, 0.50 + level * 0.025)),
            sensor_noise_fraction=rng.uniform(0.0, noise_max),
            action_delay_ms=rng.randint(0, delay_max),
            telemetry_drop_probability=rng.uniform(0.0, drop_max),
            start_position_error_m=rng.uniform(-level * 2.0, level * 2.0),
            start_heading_error_deg=rng.uniform(-level * 1.5, level * 1.5),
            pattern_direction=rng.choice(("Left", "Right")) if level >= 3 else "Left",
        )


def deterministic_suite(seeds: Iterable[int], level: int) -> list[ScenarioParameters]:
    curriculum = AdaptiveCurriculum(level=level)
    return [curriculum.sample(int(seed)) for seed in seeds]
