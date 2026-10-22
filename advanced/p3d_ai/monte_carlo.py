from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np

from .evaluation import EpisodeMetrics, write_report
from .observation import heading_error


def load_policy(path: Path | None, algorithm: str):
    if path is None:
        return None
    try:
        if algorithm == "tqc":
            from sb3_contrib import TQC
            return TQC.load(path, device="auto")
        from stable_baselines3 import SAC
        return SAC.load(path, device="auto")
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt for policy evaluation") from exc


def percentile(values: list[float], q: float) -> float:
    return float(np.percentile(np.asarray(values or [0.0]), q))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("model", type=Path, nargs="?")
    parser.add_argument("--algorithm", choices=("sac", "tqc"), default="tqc")
    parser.add_argument("--bridge", default="http://127.0.0.1:8787")
    parser.add_argument("--episodes", type=int, default=200)
    parser.add_argument("--seed", type=int, default=1000)
    parser.add_argument("--stage", choices=("takeoff", "airwork", "pattern", "approach", "full"), default="full")
    parser.add_argument("--curriculum-level", type=int, choices=range(6), default=5)
    parser.add_argument("--baseline", action="store_true")
    parser.add_argument("--output", type=Path, default=Path("reports/monte_carlo.json"))
    args = parser.parse_args()
    if args.episodes < 1:
        parser.error("--episodes must be positive")
    if not args.baseline and args.model is None:
        parser.error("model is required unless --baseline is set")
    from .live_env import AdvancedP3DEnv
    env = AdvancedP3DEnv(args.bridge, args.stage, curriculum_level=args.curriculum_level)
    policy = None if args.baseline else load_policy(args.model, args.algorithm)
    metrics: list[EpisodeMetrics] = []
    for episode_index in range(args.episodes):
        seed = args.seed + episode_index
        observation, _ = env.reset(seed=seed)
        ias_errors: list[float] = []; heading_errors: list[float] = []; cross_tracks: list[float] = []
        interventions = 0; steps = 0; latency: list[float] = []
        stall = overspeed = runway = failsafe = go_around = 0
        touchdown_sink = touchdown_cross = 0.0; previous_on_ground = True; success = False
        previous_raw: dict | None = None; previous_phase = ""
        while True:
            started = time.perf_counter()
            action = np.zeros(4, dtype=np.float32) if policy is None else np.asarray(policy.predict(observation, deterministic=True)[0], dtype=np.float32)
            latency.append((time.perf_counter() - started) * 1000.0)
            observation, _, terminated, truncated, info = env.step(action)
            raw = info["raw"]; steps += 1
            ias_errors.append(abs(float(raw.get("target_ias_kts", 0.0)) - float(raw.get("ias_kts", 0.0))))
            heading_errors.append(abs(heading_error(float(raw.get("target_heading_deg", 0.0)), float(raw.get("heading_deg", 0.0)))))
            cross_tracks.append(abs(float(raw.get("cross_m", 0.0))))
            interventions += int(bool(raw.get("shield_intervened", False)))
            stall += int(bool(raw.get("stall", False))); overspeed += int(bool(raw.get("overspeed", False)))
            runway += int(info["costs"].get("runway_excursion", 0.0) > 0.0)
            phase = str(raw.get("phase", ""))
            failsafe += int(phase == "Failsafe" and previous_phase != "Failsafe")
            go_around += int(phase == "GoAround" and previous_phase != "GoAround")
            if bool(raw.get("on_ground", False)) and not previous_on_ground:
                touchdown_sink = max(0.0, -float((previous_raw or raw).get("vs_fpm", 0.0)))
                touchdown_cross = abs(float(raw.get("cross_m", 0.0)))
            previous_on_ground = bool(raw.get("on_ground", False)); success = bool(raw.get("success", False))
            previous_raw = raw; previous_phase = phase
            if terminated or truncated:
                break
        metrics.append(EpisodeMetrics(
            episode_id=f"mc-{seed}", seed=seed, success=success, stall_count=stall,
            overspeed_count=overspeed, runway_excursion_count=runway, failsafe_count=failsafe,
            go_around_count=go_around, touchdown_sink_fpm=touchdown_sink,
            touchdown_cross_track_m=touchdown_cross, p95_ias_error_kts=percentile(ias_errors, 95),
            p95_heading_error_deg=percentile(heading_errors, 95), p95_cross_track_m=percentile(cross_tracks, 95),
            shield_intervention_rate=interventions / max(1, steps), inference_latency_p95_ms=percentile(latency, 95),
        ))
        print(f"episode={episode_index + 1}/{args.episodes} success={success} seed={seed}")
    env.close()
    report = write_report(metrics, args.output)
    print(f"release_gate_passed={report['release_gate_passed']} output={args.output}")


if __name__ == "__main__":
    main()
