from __future__ import annotations

import argparse
import time
import json
from datetime import datetime,timezone
from pathlib import Path

import numpy as np

from .bridge import BridgeClient
from .models import PolicyEnsemble
from .observation import FrameStack, encode


def main() -> None:
    parser = argparse.ArgumentParser(description="Run a deterministic policy ensemble with uncertainty gating")
    parser.add_argument("models", nargs="+", type=Path)
    parser.add_argument("--algorithm", choices=("sac", "tqc"), default="tqc")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--stage", choices=("takeoff", "airwork", "pattern", "approach", "full"), default="full")
    parser.add_argument("--frames", type=int, default=5)
    parser.add_argument("--agent-hz", type=float, default=5.0)
    parser.add_argument("--send",action="store_true",help="requires independent P3D validation report")
    parser.add_argument("--validation",type=Path)
    args = parser.parse_args()
    if args.send:
        if args.validation is None:parser.error("--send requires --validation")
        from .contracts import sha256_file
        report=json.loads(args.validation.read_text())
        if report.get("environment")!="P3D" or report.get("live_policy_eligible") is not True or report.get("model_sha256s")!=[sha256_file(p) for p in args.models]:parser.error("unvalidated ensemble")
    if len(args.models) < 2:
        parser.error("at least two independently trained models are required")
    if not 0.0 < args.agent_hz <= 20.0:
        parser.error("--agent-hz must be in (0, 20]")
    if args.algorithm == "tqc":
        from sb3_contrib import TQC
        models = [TQC.load(path, device="auto") for path in args.models]
    else:
        from stable_baselines3 import SAC
        models = [SAC.load(path, device="auto") for path in args.models]
    from .observation import feature_names
    for model in models:
        if model.observation_space.shape!=(len(feature_names())*args.frames,):raise ValueError("model observation schema/frame stack mismatch")
    ensemble = PolicyEnsemble(models)
    client = BridgeClient(args.bridge)
    stack = FrameStack(args.frames)
    previous_action = np.zeros(4, dtype=np.float32)
    reset_id = -1; step_id = 0; last_sequence=-1; period = 1.0 / args.agent_hz
    print(client.health())
    while True:
        started = time.monotonic(); raw = client.observation()
        if raw.get("reset_status") != "ready" or raw.get("terminal"):
            time.sleep(period); continue
        age=(datetime.now(timezone.utc)-datetime.fromisoformat(raw["utc"].replace("Z","+00:00"))).total_seconds()
        if not -.5<=age<=1 or int(raw["sequence"])<=last_sequence:
            previous_action.fill(0);time.sleep(period);continue
        last_sequence=int(raw["sequence"])
        if int(raw["reset_id"])!=reset_id:previous_action.fill(0)
        encoded = encode(raw, previous_action)
        if int(raw["reset_id"]) != reset_id:
            reset_id = int(raw["reset_id"]); step_id = 0; observation = stack.reset(encoded)
        else:
            observation = stack.append(encoded)
        prediction = ensemble.predict(observation)
        step_id += 1
        if args.send:client.action(prediction.mean, args.stage, step_id, reset_id, prediction.uncertainty)
        else:print("shadow",reset_id,step_id,prediction.mean.tolist(),prediction.uncertainty)
        previous_action = prediction.mean
        time.sleep(max(0.0, period - (time.monotonic() - started)))


if __name__ == "__main__":
    main()
