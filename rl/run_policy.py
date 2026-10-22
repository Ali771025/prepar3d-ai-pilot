from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np
from p3d_env import encode_observation
from protocol import BridgeClient
from stable_baselines3 import SAC


def main() -> None:
    parser = argparse.ArgumentParser(description="Run a trained residual policy without training or automatic reset")
    parser.add_argument("model", type=Path)
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--stage", choices=("takeoff", "airwork", "pattern", "approach", "full"), default="full")
    parser.add_argument("--agent-hz", type=float, default=5.0)
    args = parser.parse_args()
    if not 0.0 < args.agent_hz <= 20.0:
        parser.error("--agent-hz must be in (0, 20]")

    model = SAC.load(args.model, device="auto")
    client = BridgeClient(args.bridge)
    period = 1.0 / args.agent_hz
    step_id = 0
    reset_id = -1
    print(client.health())
    while True:
        started = time.monotonic()
        raw = client.observation()
        if raw.get("terminal") or raw.get("reset_status") != "ready":
            time.sleep(period)
            continue
        current_reset_id = int(raw["reset_id"])
        if current_reset_id != reset_id:
            reset_id = current_reset_id
            step_id = 0
        observation = encode_observation(raw)
        action, _ = model.predict(observation, deterministic=True)
        step_id += 1
        client.action(np.asarray(action, dtype=np.float32).tolist(), args.stage, step_id, reset_id)
        elapsed = time.monotonic() - started
        time.sleep(max(0.0, period - elapsed))


if __name__ == "__main__":
    main()
