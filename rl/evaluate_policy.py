from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from p3d_env import P3DResidualEnv
from stable_baselines3 import SAC


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("model", type=Path, nargs="?")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--stage", choices=("takeoff", "airwork", "pattern", "approach", "full"), default="full")
    parser.add_argument("--episodes", type=int, default=10)
    parser.add_argument("--agent-hz", type=float, default=5.0)
    parser.add_argument("--output", type=Path, default=Path("artifacts/evaluation.json"))
    parser.add_argument("--baseline", action="store_true", help="Evaluate zero residual actions")
    args = parser.parse_args()
    if args.episodes < 1:
        parser.error("--episodes must be positive")

    env = P3DResidualEnv(args.bridge, args.stage, args.agent_hz)
    if not args.baseline and args.model is None:
        parser.error("model is required unless --baseline is used")
    model = None if args.baseline else SAC.load(args.model, env=env, device="auto")
    results: list[dict[str, float | bool]] = []
    for episode in range(args.episodes):
        observation, _ = env.reset(seed=episode)
        total_reward = 0.0
        steps = 0
        success = False
        max_abs_bank = 0.0
        max_abs_pitch = 0.0
        max_final_cross_track = 0.0
        max_final_speed_error = 0.0
        stall_count = 0
        overspeed_count = 0
        while True:
            action = np.zeros(4, dtype=np.float32) if model is None else model.predict(observation, deterministic=True)[0]
            observation, reward, terminated, truncated, info = env.step(np.asarray(action))
            total_reward += float(reward)
            steps += 1
            success = bool(info.get("is_success"))
            raw = info["raw"]
            max_abs_bank = max(max_abs_bank, abs(float(raw["bank_deg"])))
            max_abs_pitch = max(max_abs_pitch, abs(float(raw["pitch_deg"])))
            stall_count += int(bool(raw["stall"]))
            overspeed_count += int(bool(raw["overspeed"]))
            if raw["phase"] in {"Final", "Flare"}:
                max_final_cross_track = max(max_final_cross_track, abs(float(raw["cross_m"])))
                max_final_speed_error = max(
                    max_final_speed_error,
                    abs(float(raw["target_ias_kts"]) - float(raw["ias_kts"])),
                )
            if terminated or truncated:
                break
        results.append({
            "episode": episode, "reward": total_reward, "steps": steps, "success": success,
            "max_abs_bank_deg": max_abs_bank, "max_abs_pitch_deg": max_abs_pitch,
            "max_final_cross_track_m": max_final_cross_track,
            "max_final_speed_error_kts": max_final_speed_error,
            "stall_count": stall_count, "overspeed_count": overspeed_count,
        })
        print(results[-1])
    env.close()

    summary = {
        "model": "baseline-zero-residual" if args.baseline else str(args.model),
        "stage": args.stage,
        "episodes": results,
        "success_rate": sum(bool(item["success"]) for item in results) / len(results),
        "mean_reward": sum(float(item["reward"]) for item in results) / len(results),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in summary.items() if key != "episodes"}, indent=2))


if __name__ == "__main__":
    main()
