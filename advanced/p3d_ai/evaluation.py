from __future__ import annotations

import json
from collections.abc import Iterable
from dataclasses import asdict, dataclass
from pathlib import Path
from statistics import mean

import numpy as np

SUMMARY_NON_NEGATIVE = (
    "episodes", "stall_count", "overspeed_count", "runway_excursion_count", "failsafe_count",
    "go_around_count", "touchdown_sink_fpm_p95", "touchdown_cross_track_m_p95",
    "ias_error_kts_p95", "heading_error_deg_p95", "cross_track_m_p95",
    "shield_intervention_rate_mean", "inference_latency_ms_p95",
)


def _validate_summary(summary: dict) -> None:
    required = set(SUMMARY_NON_NEGATIVE) | {"success_rate"}
    missing = required.difference(summary)
    if missing:
        raise ValueError(f"evaluation summary is missing: {sorted(missing)}")
    values = {name: float(summary[name]) for name in required}
    if any(not np.isfinite(value) for value in values.values()):
        raise ValueError("evaluation summary must contain only finite values")
    if any(values[name] < 0.0 for name in SUMMARY_NON_NEGATIVE):
        raise ValueError("evaluation summary contains a negative metric")
    if not 0.0 <= values["success_rate"] <= 1.0 or values["shield_intervention_rate_mean"] > 1.0:
        raise ValueError("evaluation rates must be in [0, 1]")


@dataclass(frozen=True)
class EpisodeMetrics:
    episode_id: str
    seed: int
    success: bool
    stall_count: int
    overspeed_count: int
    runway_excursion_count: int
    failsafe_count: int
    go_around_count: int
    touchdown_sink_fpm: float
    touchdown_cross_track_m: float
    p95_ias_error_kts: float
    p95_heading_error_deg: float
    p95_cross_track_m: float
    shield_intervention_rate: float
    inference_latency_p95_ms: float


@dataclass(frozen=True)
class ReleaseThresholds:
    minimum_episodes: int = 200
    minimum_success_rate: float = 0.98
    maximum_touchdown_sink_fpm: float = 600.0
    maximum_touchdown_cross_track_m: float = 20.0
    maximum_inference_latency_p95_ms: float = 80.0


def summarize(episodes: Iterable[EpisodeMetrics]) -> dict:
    items = list(episodes)
    if not items:
        raise ValueError("no episodes")
    for item in items:
        counts = (item.stall_count, item.overspeed_count, item.runway_excursion_count,
                  item.failsafe_count, item.go_around_count)
        measurements = (item.touchdown_sink_fpm, item.touchdown_cross_track_m,
                        item.p95_ias_error_kts, item.p95_heading_error_deg, item.p95_cross_track_m,
                        item.inference_latency_p95_ms)
        if any(isinstance(value, bool) or not isinstance(value, int) or value < 0 for value in counts):
            raise ValueError("episode event counts must be non-negative integers")
        if any(not np.isfinite(float(value)) or float(value) < 0.0 for value in measurements):
            raise ValueError("episode measurements must be finite and non-negative")
        if not np.isfinite(item.shield_intervention_rate) or not 0.0 <= item.shield_intervention_rate <= 1.0:
            raise ValueError("shield_intervention_rate must be in [0, 1]")
    numeric = lambda name: np.asarray([float(getattr(item, name)) for item in items], dtype=np.float64)
    summary = {
        "episodes": len(items),
        "success_rate": mean(float(item.success) for item in items),
        "stall_count": sum(item.stall_count for item in items),
        "overspeed_count": sum(item.overspeed_count for item in items),
        "runway_excursion_count": sum(item.runway_excursion_count for item in items),
        "failsafe_count": sum(item.failsafe_count for item in items),
        "go_around_count": sum(item.go_around_count for item in items),
        "touchdown_sink_fpm_p95": float(np.percentile(numeric("touchdown_sink_fpm"), 95)),
        "touchdown_cross_track_m_p95": float(np.percentile(numeric("touchdown_cross_track_m"), 95)),
        "ias_error_kts_p95": float(np.percentile(numeric("p95_ias_error_kts"), 95)),
        "heading_error_deg_p95": float(np.percentile(numeric("p95_heading_error_deg"), 95)),
        "cross_track_m_p95": float(np.percentile(numeric("p95_cross_track_m"), 95)),
        "shield_intervention_rate_mean": float(numeric("shield_intervention_rate").mean()),
        "inference_latency_ms_p95": float(np.percentile(numeric("inference_latency_p95_ms"), 95)),
    }
    _validate_summary(summary)
    return summary


def release_gate(summary: dict, thresholds: ReleaseThresholds | None = None) -> tuple[bool, list[str]]:
    if thresholds is None:
        thresholds = ReleaseThresholds()
    _validate_summary(summary)
    threshold_values = tuple(float(value) for value in asdict(thresholds).values())
    if any(not np.isfinite(value) or value < 0.0 for value in threshold_values):
        raise ValueError("release thresholds must be finite and non-negative")
    if thresholds.minimum_episodes < 1 or not 0.0 <= thresholds.minimum_success_rate <= 1.0:
        raise ValueError("release episode and success thresholds are invalid")
    failures: list[str] = []
    if summary["episodes"] < thresholds.minimum_episodes:
        failures.append("insufficient-episodes")
    if summary["success_rate"] < thresholds.minimum_success_rate:
        failures.append("success-rate")
    for name in ("stall_count", "overspeed_count", "runway_excursion_count", "failsafe_count"):
        if summary[name] != 0:
            failures.append(name)
    if summary["touchdown_sink_fpm_p95"] > thresholds.maximum_touchdown_sink_fpm:
        failures.append("touchdown-sink")
    if summary["touchdown_cross_track_m_p95"] > thresholds.maximum_touchdown_cross_track_m:
        failures.append("touchdown-cross-track")
    if summary["inference_latency_ms_p95"] > thresholds.maximum_inference_latency_p95_ms:
        failures.append("inference-latency")
    return not failures, failures


def compare_to_baseline(candidate: dict, baseline: dict, relative_tolerance: float = 0.02) -> tuple[bool, list[str]]:
    _validate_summary(candidate)
    _validate_summary(baseline)
    if not np.isfinite(relative_tolerance) or relative_tolerance < 0.0:
        raise ValueError("relative_tolerance must be finite and non-negative")
    failures: list[str] = []
    if candidate["success_rate"] + 1e-12 < baseline["success_rate"]:
        failures.append("success-regression")
    lower_is_better = (
        "touchdown_sink_fpm_p95", "touchdown_cross_track_m_p95", "ias_error_kts_p95",
        "heading_error_deg_p95", "cross_track_m_p95",
    )
    for name in lower_is_better:
        allowed = float(baseline[name]) * (1.0 + relative_tolerance)
        if float(candidate[name]) > allowed:
            failures.append(f"regression:{name}")
    return not failures, failures


def write_report(episodes: list[EpisodeMetrics], output: Path) -> dict:
    summary = summarize(episodes)
    passed, failures = release_gate(summary)
    payload = {"release_gate_passed": passed, "failures": failures, "summary": summary,
               "episodes": [asdict(item) for item in episodes]}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    return payload
