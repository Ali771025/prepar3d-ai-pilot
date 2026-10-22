from __future__ import annotations

import csv
import hashlib
import json
import math
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PHASES = dict(zip(('Disarmed','Armed','TakeoffRoll','Rotate','InitialClimb','Crosswind','Downwind','Base','Final','Flare','Rollout','GoAround','Completed','Failsafe'), range(14)))
AXES = ('aileron','elevator','rudder','throttle')
OBSERVATION_NAMES = ('ias','ias_error','ground_speed','agl','altitude_error','vertical_speed','pitch','bank','heading_error','angle_of_attack','side_slip','along_track','cross_track','on_ground','stall','overspeed','target_ias','target_altitude','target_vertical_speed','baseline_aileron','baseline_elevator','baseline_rudder','baseline_throttle','applied_aileron','applied_elevator','applied_rudder','applied_throttle','phase','safety_scale')


def finite(value: object) -> float:
    if isinstance(value, bool):
        raise ValueError('boolean in numeric field')
    number = float(value)
    if not math.isfinite(number):
        raise ValueError('non-finite number')
    return number


def flag(value: object) -> bool:
    if value in (True,1,'1','true','True'): return True
    if value in (False,0,'0','false','False'): return False
    raise ValueError(f'invalid boolean {value!r}')


def heading_error(target: float, actual: float) -> float:
    return (target-actual+180)%360-180


def clip(value: float, lo: float=-1, hi: float=1) -> float:
    return max(lo,min(hi,value))


def encode(raw: dict) -> list[float]:
    phase = str(raw['phase'])
    if phase not in PHASES or raw.get('phase_id') != PHASES[phase]:
        raise ValueError('inconsistent phase')
    b,a = raw['baseline'],raw['applied_residual']
    if len(b)!=4 or len(a)!=4: raise ValueError('four axes required')
    n=lambda key:finite(raw[key])
    result=[n('ias_kts')/200,(n('target_ias_kts')-n('ias_kts'))/50,n('gs_kts')/200,n('alt_agl_ft')/2000,
            (n('target_alt_msl_ft')-n('alt_msl_ft'))/1000,n('vs_fpm')/1500,n('pitch_deg')/30,n('bank_deg')/45,
            heading_error(n('target_heading_deg'),n('heading_deg'))/180,n('aoa_deg')/20,n('beta_deg')/15,
            n('along_m')/4000,n('cross_m')/2000,float(flag(raw['on_ground'])),float(flag(raw['stall'])),float(flag(raw['overspeed'])),
            n('target_ias_kts')/200,n('target_alt_msl_ft')/10000,n('target_vs_fpm')/1000,
            *map(finite,b),*map(finite,a),PHASES[phase]/13,n('safety_scale')]
    return [clip(x,-10,10) for x in result]


def raw_from_row(row: dict) -> dict:
    phase=row['phase']
    applied=[finite(row['rl_applied_'+axis]) for axis in AXES]
    command=[finite(row['cmd_'+axis]) for axis in AXES]
    raw={k:finite(row[k]) for k in ('ias_kts','tas_kts','gs_kts','alt_agl_ft','alt_msl_ft','vs_fpm','pitch_deg','bank_deg','heading_deg','aoa_deg','beta_deg','along_m','cross_m')}
    raw.update({f'target_{dst}':finite(row[src]) for dst,src in (('heading_deg','target_heading'),('ias_kts','target_ias'),('alt_msl_ft','target_alt_msl'),('vs_fpm','target_vs'))})
    raw.update(phase=phase,phase_id=PHASES[phase],baseline=[c-a for c,a in zip(command,applied)],applied_residual=applied,
               safety_scale=finite(row['rl_safety_scale']),on_ground=flag(row['on_ground']),stall=flag(row['stall']),overspeed=flag(row['overspeed']),
               terminal=phase in ('Completed','Failsafe'),success=phase=='Completed',sequence=int(row['telemetry_sequence']),
               utc=row['utc'],reset_id=0,reset_status='ready',paused=False,slew=False)
    return raw


def read_csv(path: Path) -> tuple[list[str],list[dict]]:
    with path.open(encoding='utf-8-sig',newline='') as stream:
        reader=csv.DictReader(stream)
        names=reader.fieldnames
        if not names or len(names)!=len(set(names)): raise ValueError('missing or duplicate CSV columns')
        rows=list(reader)
    return names,rows


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2,allow_nan=False)+'\n',encoding='utf-8')


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()
