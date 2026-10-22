from __future__ import annotations

import time
from typing import Any, ClassVar

import numpy as np

from .bridge import BridgeClient, BridgeError
from .curriculum import AdaptiveCurriculum, ScenarioParameters
from .observation import FrameStack, encode, feature_names
from .reward_cost import compute, lagrangian_objective


def _gym():
    try:
        import gymnasium as gym
        from gymnasium import spaces
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt to use the live environment") from exc
    return gym, spaces


try:
    _gym_module, _spaces = _gym()
    _BaseEnv = _gym_module.Env
except RuntimeError:
    _gym_module = None
    _spaces = None
    _BaseEnv = object


class AdvancedP3DEnv(_BaseEnv):
    metadata: ClassVar[dict[str, list[str]]] = {"render_modes": []}

    def __init__(
        self,
        bridge_url: str = "http://127.0.0.1:8787",
        stage: str = "airwork",
        agent_hz: float = 5.0,
        frames: int = 5,
        curriculum_level: int = 0,
        max_episode_seconds: float = 600.0,
        cost_multipliers: dict[str, float] | None = None,
    ):
        if _gym_module is None or _spaces is None:
            _gym()
        super().__init__()
        if agent_hz <= 0.0 or agent_hz > 20.0:
            raise ValueError("agent_hz must be in (0, 20]")
        if frames < 1 or frames > 20:
            raise ValueError("frames must be 1..20")
        if max_episode_seconds <= 0.0:
            raise ValueError("max_episode_seconds must be positive")
        if stage not in {"takeoff", "airwork", "pattern", "approach", "full"}:
            raise ValueError(f"unknown curriculum stage: {stage}")
        self.client = BridgeClient(bridge_url)
        self.stage = stage
        self.period = 1.0 / agent_hz
        self.max_steps = int(max_episode_seconds * agent_hz)
        self.stack = FrameStack(frames)
        self.curriculum = AdaptiveCurriculum(level=curriculum_level)
        self.cost_multipliers = {
            "stall": 150.0, "overspeed": 100.0, "attitude": 80.0,
            "unstable_approach": 40.0, "runway_excursion": 200.0,
            "hard_landing": 120.0, "shield_intervention": 0.5, "invalid_runtime": 250.0,
        } if cost_multipliers is None else dict(cost_multipliers)
        if any(not np.isfinite(float(value)) or float(value) < 0.0 for value in self.cost_multipliers.values()):
            raise ValueError("cost multipliers must be finite and non-negative")
        self.action_space = _spaces.Box(-1.0, 1.0, shape=(4,), dtype=np.float32)
        self.observation_space = _spaces.Box(-10.0, 10.0, shape=(len(feature_names()) * frames,), dtype=np.float32)
        self._raw: dict[str, Any] | None = None
        self._previous_action = np.zeros(4, dtype=np.float32)
        self._episode_steps = 0
        self._step_id = 0
        self._scenario: ScenarioParameters | None = None

    def reset(self, *, seed: int | None = None, options: dict[str, Any] | None = None):
        super().reset(seed=seed)
        episode_seed = int(seed if seed is not None else self.np_random.integers(0, 2**31 - 1))
        self._scenario = self.curriculum.sample(episode_seed)
        before = self.client.observation()
        self.client.reset()
        current = self.client.wait_reset(int(before["reset_id"]))
        self._raw = self._randomize_observation(current)
        self._previous_action.fill(0.0)
        self._episode_steps = 0
        self._step_id = 0
        vector = self.stack.reset(encode(self._raw, self._previous_action))
        return vector, {"raw": self._raw, "scenario": self._scenario.__dict__}

    def step(self, action: np.ndarray):
        if self._raw is None or self._scenario is None:
            raise RuntimeError("reset() must be called first")
        action = np.asarray(action, dtype=np.float32)
        if action.size != 4 or not np.isfinite(action).all():
            raise ValueError("action must contain four finite values")
        action = np.clip(action.reshape(4), -1.0, 1.0)
        if self._scenario.action_delay_ms:
            time.sleep(self._scenario.action_delay_ms / 1000.0)
        self._step_id += 1
        previous = self._raw
        sequence = int(previous["sequence"])
        # Training actions do not have an ensemble epistemic estimate. Sending a
        # previous bridge trace as current uncertainty would be stale and could
        # incorrectly pass/fail the runtime gate, so training explicitly uses 0.
        self.client.action(action, self.stage, self._step_id, int(previous["reset_id"]), 0.0)
        deadline = time.monotonic() + max(1.0, self.period * 3.0)
        target = time.monotonic() + self.period
        current = previous
        while time.monotonic() < deadline:
            if time.monotonic() < target:
                time.sleep(min(0.01, target - time.monotonic()))
                continue
            candidate = self.client.observation()
            if int(candidate["sequence"]) > sequence:
                current = candidate
                break
            time.sleep(0.01)
        else:
            raise BridgeError("telemetry did not advance")
        current = self._randomize_observation(current)
        result = compute(previous, current, action, self._previous_action)
        reward = lagrangian_objective(result, self.cost_multipliers)
        self._raw = current
        self._previous_action = action.copy()
        self._episode_steps += 1
        vector = self.stack.append(encode(current, self._previous_action))
        terminated = bool(current.get("terminal") or result.costs["stall"] or result.costs["overspeed"] or result.costs["runway_excursion"])
        truncated = self._episode_steps >= self.max_steps
        info = {"raw": current, "components": result.components, "costs": result.costs,
                "is_success": bool(current.get("success", False)), "scenario": self._scenario.__dict__}
        if terminated or truncated:
            self.curriculum.observe(bool(current.get("success", False)), not result.unsafe)
        return vector, float(reward), terminated, truncated, info

    def _randomize_observation(self, raw: dict[str, Any]) -> dict[str, Any]:
        assert self._scenario is not None
        result = dict(raw)
        noise = self._scenario.sensor_noise_fraction
        if noise > 0.0:
            for name in ("ias_kts", "gs_kts", "alt_agl_ft", "vs_fpm", "pitch_deg", "bank_deg", "heading_deg"):
                value = float(result.get(name, 0.0))
                result[name] = value + float(self.np_random.normal(0.0, max(1.0, abs(value)) * noise))
        return result

    def render(self):
        return None
