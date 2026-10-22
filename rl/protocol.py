from __future__ import annotations

import json
import math
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Any

ALLOWED_STAGES = {"takeoff", "airwork", "pattern", "approach", "full"}


def _reject_non_finite_json(value: str) -> None:
    raise ValueError(f"non-finite JSON number: {value}")


class BridgeError(RuntimeError):
    pass


@dataclass
class BridgeClient:
    base_url: str = "http://127.0.0.1:8787"
    timeout_seconds: float = 3.0

    def __post_init__(self) -> None:
        parsed = urllib.parse.urlparse(self.base_url)
        if parsed.scheme != "http" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("bridge_url must be a loopback HTTP URL")
        if not (0.1 <= self.timeout_seconds <= 30.0):
            raise ValueError("timeout_seconds must be 0.1..30")

    def health(self) -> dict[str, Any]:
        return self._request("GET", "/health")

    def observation(self) -> dict[str, Any]:
        return self._request("GET", "/api/v1/observation")

    def action(self, values: list[float], stage: str, step_id: int, reset_id: int, uncertainty: float = 0.0) -> dict[str, Any]:
        action = [float(value) for value in values]
        if len(action) != 4 or not all(math.isfinite(value) and -1.0 <= value <= 1.0 for value in action):
            raise ValueError("action must contain four finite values in [-1, 1]")
        if stage not in ALLOWED_STAGES:
            raise ValueError(f"invalid stage: {stage}")
        if isinstance(step_id, bool) or not isinstance(step_id, int) or step_id < 0:
            raise ValueError("step_id must be a non-negative integer")
        if isinstance(reset_id, bool) or not isinstance(reset_id, int) or reset_id < 0:
            raise ValueError("reset_id must be a non-negative integer")
        uncertainty = float(uncertainty)
        if not math.isfinite(uncertainty) or not 0.0 <= uncertainty <= 1.0:
            raise ValueError("uncertainty must be finite and in [0, 1]")
        return self._request(
            "POST",
            "/api/v1/action",
            {"action": action, "stage": stage, "step_id": step_id, "reset_id": reset_id, "uncertainty": uncertainty},
        )

    def request_reset(self) -> dict[str, Any]:
        return self._request("POST", "/api/v1/reset", {})

    def wait_for_new_observation(self, after_sequence: int, timeout_seconds: float) -> dict[str, Any]:
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            observation = self.observation()
            if int(observation["sequence"]) > after_sequence:
                return observation
            time.sleep(0.01)
        raise BridgeError(f"No new observation after sequence {after_sequence}")

    def _request(self, method: str, path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
        started = time.perf_counter()
        try:
            data = None if payload is None else json.dumps(payload, allow_nan=False).encode("utf-8")
        except (TypeError, ValueError) as exc:
            raise BridgeError(f"Bridge request is not valid JSON: {exc}") from exc
        request = urllib.request.Request(
            self.base_url.rstrip("/") + path,
            method=method,
            data=data,
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout_seconds) as response:
                try:
                    result = json.loads(response.read().decode("utf-8"), parse_constant=_reject_non_finite_json)
                except (UnicodeDecodeError, ValueError) as exc:
                    raise BridgeError("Bridge response is not valid UTF-8 JSON") from exc
                if not isinstance(result, dict):
                    raise BridgeError("bridge response must be a JSON object")
                if path == "/api/v1/observation":
                    result["bridge_latency_ms"] = (time.perf_counter() - started) * 1000.0
                return result
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise BridgeError(f"Bridge HTTP {exc.code}: {body}") from exc
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise BridgeError(f"Bridge unavailable: {exc}") from exc
