from __future__ import annotations

import argparse
import json
import hashlib
from pathlib import Path

import numpy as np
import pandas as pd

from .dataset import build_manifest, inspect_episode, split_by_episode
from .observation import encode, feature_names
from .reward_cost import compute


def _bool(value: object) -> bool:
    if isinstance(value, str):
        normalized = value.strip().lower()
        if normalized in {"1", "true", "yes"}: return True
        if normalized in {"0", "false", "no", ""}: return False
        raise ValueError(f"invalid boolean: {value}")
    if pd.isna(value):
        return False
    number=float(value)
    if number not in (0.,1.):raise ValueError("boolean must be 0 or 1")
    return number==1.


def _raw(row: pd.Series) -> dict:
    applied = [float(row.get(f"rl_applied_{name}", 0.0)) for name in ("aileron", "elevator", "rudder", "throttle")]
    command = [float(row.get(f"cmd_{name}", 0.0)) for name in ("aileron", "elevator", "rudder", "throttle")]
    baseline = [value - residual for value, residual in zip(command, applied, strict=True)]
    return {
        "phase": str(row.get("phase", "Disarmed")), "telemetry_source": str(row.get("telemetry_source", "")),
        "ias_kts": row.get("ias_kts", 0.0),
        "tas_kts": row.get("tas_kts", row.get("ias_kts", 0.0)), "gs_kts": row.get("gs_kts", 0.0),
        "alt_agl_ft": row.get("alt_agl_ft", 0.0), "alt_msl_ft": row.get("alt_msl_ft", 0.0),
        "vs_fpm": row.get("vs_fpm", 0.0), "pitch_deg": row.get("pitch_deg", 0.0),
        "bank_deg": row.get("bank_deg", 0.0), "heading_deg": row.get("heading_deg", 0.0),
        "target_heading_deg": row.get("target_heading", row.get("heading_deg", 0.0)),
        "target_ias_kts": row.get("target_ias", row.get("ias_kts", 0.0)),
        "target_alt_msl_ft": row.get("target_alt_msl", row.get("alt_msl_ft", 0.0)),
        "target_vs_fpm": row.get("target_vs", 0.0), "aoa_deg": row.get("aoa_deg", 0.0),
        "beta_deg": row.get("beta_deg", 0.0), "body_rate_p_dps": row.get("body_rate_p_dps", 0.0),
        "body_rate_q_dps": row.get("body_rate_q_dps", 0.0), "body_rate_r_dps": row.get("body_rate_r_dps", 0.0),
        "accel_x_g": row.get("accel_x_g", 0.0), "accel_y_g": row.get("accel_y_g", 0.0),
        "accel_z_g": row.get("accel_z_g", 1.0), "wind_speed_kts": row.get("wind_speed_kts", 0.0),
        "wind_cross_kts": row.get("wind_cross_kts", 0.0), "along_m": row.get("along_m", 0.0),
        "cross_m": row.get("cross_m", 0.0), "distance_threshold_m": row.get("distance_threshold_m", 0.0),
        "rpm": row.get("rpm", 0.0), "fuel_fraction": row.get("fuel_fraction", 1.0),
        "weight_fraction": row.get("weight_fraction", 1.0), "cg_fraction": row.get("cg_fraction", 0.5),
        "pilot_yoke_x": row.get("pilot_yoke_x", 0.0), "pilot_yoke_y": row.get("pilot_yoke_y", 0.0),
        "pilot_rudder": row.get("pilot_rudder", 0.0), "pilot_throttle": row.get("pilot_throttle", 0.0),
        "baseline": baseline, "applied_residual": applied, "safety_scale": row.get("rl_safety_scale", 0.0),
        "uncertainty": row.get("rl_uncertainty", 0.0), "on_ground": _bool(row.get("on_ground", False)),
        "stall": _bool(row.get("stall", False)), "overspeed": _bool(row.get("overspeed", False)),
        "residual_rejection": str(row.get("rl_rejection", "")), "success": str(row.get("phase", "")) == "Completed",
        "terminal": str(row.get("phase", "")) in {"Completed", "Failsafe"},
    }


def convert(paths: list[Path], output: Path, action_source: str, expected_hz: float = 25.0) -> pd.DataFrame:
    if action_source not in {"pilot", "residual"}:
        raise ValueError("action_source must be 'pilot' or 'residual'")
    if not paths:
        raise ValueError("at least one episode is required")
    names = feature_names()
    records: list[dict] = []
    for path in paths:
        quality = inspect_episode(path, expected_hz)
        if not quality.accepted:
            raise ValueError(f"rejected episode {path}: {quality.reasons}")
        frame = pd.read_csv(path).fillna({"rl_rejection":""})
        groups=list(frame.groupby("episode_id",sort=False)) if "episode_id" in frame else [(path.stem,frame)]
        for episode_id, episode in groups:
            _convert_episode(episode, str(episode_id), path, action_source, names, records)
    result = pd.DataFrame.from_records(records)
    if result.empty: raise ValueError("no transitions")
    ids=sorted(result.episode_id.unique(),key=lambda x: hashlib.sha256(str(x).encode()).hexdigest())
    n=max(1,round(len(ids)*.15))
    split={name:("test" if i<n else "validation" if i<2*n else "train") for i,name in enumerate(ids)} if len(ids)>=3 else {name:"unpartitioned" for name in ids}
    result["split"]=result.episode_id.map(split)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.suffix.lower() == ".parquet":
        try: result.to_parquet(output, index=False)
        except ImportError as exc: raise RuntimeError("Install pyarrow or choose a .csv output") from exc
    else: result.to_csv(output, index=False)
    output.with_suffix(".metadata.json").write_text(json.dumps({"observation_names":names,"action_semantics":"full_pilot_actuator" if action_source=="pilot" else "normalized_residual", "flight_validated":False},indent=2),encoding="utf-8")
    return result


def _convert_episode(episode, episode_id, path, action_source, names, records):
        raws = [_raw(row) for _, row in episode.iterrows()]
        previous_action = np.zeros(4, dtype=np.float32)
        for index, raw in enumerate(raws[:-1]):
            nxt = raws[index + 1]
            label_index=index+1 if raw["telemetry_source"]=="SYNTHETIC_F22_TOY" else index
            if action_source == "pilot":
                label=raws[label_index]
                action = np.asarray([label["pilot_yoke_x"], label["pilot_yoke_y"], label["pilot_rudder"], label["pilot_throttle"]], dtype=np.float32)
            else:
                row=episode.iloc[label_index]
                required=[f"rl_req_{axis}" for axis in ("aileron","elevator","rudder","throttle")]
                if not set(required).issubset(episode.columns): raise ValueError("normalized residual labels require rl_req_*; applied physical deltas are not normalized actions")
                action=np.asarray([row[name] for name in required],dtype=np.float32)
            if not np.isfinite(action).all() or np.any(np.abs(action) > 1.0 + 1e-6):
                raise ValueError(f"invalid action in {path} at row {index}")
            vector = encode(raw, previous_action)
            outcome = compute(raw, nxt, action, previous_action)
            record = {f"obs_{name}": float(value) for name, value in zip(names, vector, strict=True)}
            record.update({f"next_obs_{name}":float(value) for name,value in zip(names,encode(nxt,action),strict=True)})
            record.update({
                "episode_id": episode_id, "action_semantics": "full_pilot_actuator" if action_source=="pilot" else "normalized_residual", "step": index, "phase": raw["phase"],
                "telemetry_source": raw["telemetry_source"],
                "action_aileron": float(action[0]), "action_elevator": float(action[1]),
                "action_rudder": float(action[2]), "action_throttle": float(action[3]),
                "reward": outcome.reward, "terminal": float(nxt["terminal"]), "timeout":float(index==len(raws)-2 and not nxt["terminal"]),
                **{f"cost_{name}": value for name, value in outcome.costs.items()},
            })
            records.append(record)
            previous_action = action


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--expected-hz",type=float,default=25.0)
    parser.add_argument("--action-source", choices=("pilot", "residual"), default="pilot")
    parser.add_argument("--output", type=Path, default=Path("datasets/transitions.parquet"))
    parser.add_argument("--manifest", type=Path, default=Path("datasets/dataset_manifest.json"))
    parser.add_argument("--splits", type=Path, default=Path("datasets/splits.json"))
    args = parser.parse_args()
    manifest = build_manifest(args.logs, args.manifest, args.expected_hz)
    if manifest["rejected"]:
        raise SystemExit("dataset contains rejected episodes; inspect the manifest")
    args.splits.parent.mkdir(parents=True, exist_ok=True)
    result = convert(args.logs, args.output, args.action_source, args.expected_hz)
    args.splits.write_text(json.dumps({str(name):list(group.episode_id.unique()) for name,group in result.groupby("split")},indent=2),encoding="utf-8")
    print(f"rows={len(result)} output={args.output}")


if __name__ == "__main__":
    main()
