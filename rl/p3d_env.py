from __future__ import annotations

import time
from typing import Any, ClassVar

import gymnasium as gym
import numpy as np
from gymnasium import spaces
from protocol import BridgeClient, BridgeError
from reward import compute_reward, heading_error, unsafe_terminal

OBSERVATION_NAMES = [
    "ias",
    "ias_error",
    "ground_speed",
    "agl",
    "altitude_error",
    "vertical_speed",
    "pitch",
    "bank",
    "heading_error",
    "angle_of_attack",
    "side_slip",
    "along_track",
    "cross_track",
    "on_ground",
    "stall",
    "overspeed",
    "target_ias",
    "target_altitude",
    "target_vertical_speed",
    "baseline_aileron",
    "baseline_elevator",
    "baseline_rudder",
    "baseline_throttle",
    "applied_aileron",
    "applied_elevator",
    "applied_rudder",
    "applied_throttle",
    "phase",
    "safety_scale",
]

PHASE_IDS = {
    "Disarmed": 0, "Armed": 1, "TakeoffRoll": 2, "Rotate": 3, "InitialClimb": 4,
    "Crosswind": 5, "Downwind": 6, "Base": 7, "Final": 8, "Flare": 9,
    "Rollout": 10, "GoAround": 11, "Completed": 12, "Failsafe": 13,
}


def _boolean_value(value: Any, name: str) -> bool:
    if isinstance(value, (bool, np.bool_)):
        return bool(value)
    if isinstance(value, (int, float, np.integer, np.floating)):
        numeric = float(value)
        if np.isfinite(numeric) and numeric in {0.0, 1.0}:
            return bool(numeric)
    raise ValueError(f"{name} must be a boolean")


def encode_observation(value: dict[str, Any]) -> np.ndarray:
    baseline = np.asarray(value.get("baseline", [0.0] * 4), dtype=np.float32)
    applied = np.asarray(value.get("applied_residual", [0.0] * 4), dtype=np.float32)
    if baseline.shape != (4,) or applied.shape != (4,) or not np.isfinite(baseline).all() or not np.isfinite(applied).all():
        raise ValueError("Baseline and applied residual must contain four finite values")
    phase = str(value.get("phase", ""))
    phase_id = value.get("phase_id", -1)
    if isinstance(phase_id, bool) or not isinstance(phase_id, int) or phase not in PHASE_IDS or phase_id != PHASE_IDS[phase]:
        raise ValueError("Invalid or inconsistent flight phase")
    vector = np.asarray(
        [
            float(value["ias_kts"]) / 200.0,
            (float(value["target_ias_kts"]) - float(value["ias_kts"])) / 50.0,
            float(value["gs_kts"]) / 200.0,
            float(value["alt_agl_ft"]) / 2000.0,
            (float(value["target_alt_msl_ft"]) - float(value["alt_msl_ft"])) / 1000.0,
            float(value["vs_fpm"]) / 1500.0,
            float(value["pitch_deg"]) / 30.0,
            float(value["bank_deg"]) / 45.0,
            heading_error(float(value["target_heading_deg"]), float(value["heading_deg"])) / 180.0,
            float(value["aoa_deg"]) / 20.0,
            float(value["beta_deg"]) / 15.0,
            float(value["along_m"]) / 4000.0,
            float(value["cross_m"]) / 2000.0,
            float(_boolean_value(value["on_ground"], "on_ground")),
            float(_boolean_value(value["stall"], "stall")),
            float(_boolean_value(value["overspeed"], "overspeed")),
            float(value["target_ias_kts"]) / 200.0,
            float(value["target_alt_msl_ft"]) / 10000.0,
            float(value["target_vs_fpm"]) / 1000.0,
            *baseline.tolist(),
            *applied.tolist(),
            float(PHASE_IDS[phase]) / 13.0,
            float(value["safety_scale"]),
        ],
        dtype=np.float32,
    )
    if vector.size != len(OBSERVATION_NAMES):
        raise ValueError(f"Observation schema mismatch: {vector.size} != {len(OBSERVATION_NAMES)}")
    if not np.isfinite(vector).all():
        raise ValueError("Non-finite observation from P3D bridge")
    return np.clip(vector, -10.0, 10.0)


class P3DResidualEnv(gym.Env[np.ndarray, np.ndarray]):
    metadata: ClassVar[dict[str, list[str]]] = {"render_modes": []}

    def __init__(
        self,
        bridge_url: str = "http://127.0.0.1:8787",
        stage: str = "airwork",
        agent_hz: float = 5.0,
        max_episode_seconds: float = 600.0,
        reset_timeout_seconds: float = 45.0,
    ):
        super().__init__()
        if agent_hz <= 0.0 or agent_hz > 20.0:
            raise ValueError("agent_hz must be in (0, 20]")
        if max_episode_seconds <= 0.0 or reset_timeout_seconds <= 0.0:
            raise ValueError("episode and reset timeouts must be positive")
        if stage not in {"takeoff", "airwork", "pattern", "approach", "full"}:
            raise ValueError(f"Unknown curriculum stage: {stage}")
        self.client = BridgeClient(bridge_url)
        self.stage = stage
        self.agent_hz = agent_hz
        self.step_seconds = 1.0 / agent_hz
        self.max_steps = int(max_episode_seconds * agent_hz)
        self.reset_timeout_seconds = reset_timeout_seconds
        self.action_space = spaces.Box(-1.0, 1.0, shape=(4,), dtype=np.float32)
        self.observation_space = spaces.Box(-10.0, 10.0, shape=(len(OBSERVATION_NAMES),), dtype=np.float32)
        self._raw: dict[str, Any] | None = None
        self._previous_action = np.zeros(4, dtype=np.float32)
        self._step_id = 0
        self._episode_steps = 0

    def reset(self, *, seed: int | None = None, options: dict[str, Any] | None = None):
        super().reset(seed=seed)
        before = self.client.observation()
        previous_reset_id = int(before["reset_id"])
        self.client.request_reset()
        deadline = time.monotonic() + self.reset_timeout_seconds
        while time.monotonic() < deadline:
            current = self.client.observation()
            if int(current["reset_id"]) > previous_reset_id:
                if current.get("reset_status") != "ready":
                    raise BridgeError(f"P3D reset failed: {current.get('reset_status')}")
                self._raw = current
                self._previous_action = np.zeros(4, dtype=np.float32)
                self._step_id = 0
                self._episode_steps = 0
                return encode_observation(current), {"raw": current, "stage": self.stage}
            time.sleep(0.05)
        raise BridgeError("P3D scenario reset timed out")

    def step(self, action: np.ndarray):
        if self._raw is None:
            raise RuntimeError("reset() must be called before step()")
        action = np.asarray(action, dtype=np.float32)
        if action.size != 4 or not np.isfinite(action).all():
            raise ValueError("action must contain four finite values")
        action = np.clip(action.reshape(4), -1.0, 1.0)
        self._step_id += 1
        previous = self._raw
        previous_sequence = int(previous["sequence"])
        self.client.action(action.tolist(), self.stage, self._step_id, int(previous["reset_id"]))

        deadline = time.monotonic() + max(1.0, self.step_seconds * 3.0)
        target_time = time.monotonic() + self.step_seconds
        current = previous
        while time.monotonic() < deadline:
            if time.monotonic() < target_time:
                time.sleep(min(0.01, target_time - time.monotonic()))
                continue
            current = self.client.observation()
            if int(current["sequence"]) > previous_sequence:
                break
            time.sleep(0.01)
        else:
            raise BridgeError("P3D telemetry did not advance after action")

        reward, components = compute_reward(previous, current, action, self._previous_action, self.stage)
        self._raw = current
        self._previous_action = action.copy()
        self._episode_steps += 1
        terminated = unsafe_terminal(current)
        truncated = self._episode_steps >= self.max_steps
        info = {
            "raw": current,
            "reward_components": components,
            "is_success": bool(current.get("success")),
            "stage": self.stage,
        }
        return encode_observation(current), reward, terminated, truncated, info

    def render(self):
        return None
