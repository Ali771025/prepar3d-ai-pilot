from __future__ import annotations
import argparse
import csv
import json
import math
import re
from datetime import datetime,timedelta,timezone
from pathlib import Path
from .contracts import AXES,OBSERVATION_NAMES,PHASES,ROOT,encode,finite,flag,raw_from_row,read_csv,save_json,sha256
from .environment import ToyTakeoffEnvironment


def recorder_columns() -> list[str]:
    text=(ROOT/'src/P3DAIPilot.P3Dv4/FlightCsvRecorder.cs').read_text()
    match=re.search(r'_writer.WriteLine\("(schema_version,[^"]+)"\)',text)
    if not match: raise ValueError('cannot locate authoritative recorder header')
    return match.group(1).split(',')


def generate(output: Path,episodes: int=24,seed: int=42) -> dict:
    if not 3<=episodes<=1000: raise ValueError('episodes must be 3..1000')
    output.parent.mkdir(parents=True,exist_ok=True)
    columns=recorder_columns();start=datetime(2026,10,9,tzinfo=timezone.utc)
    counts={};successes=0
    with output.open('w',encoding='utf-8',newline='') as stream:
        writer=csv.DictWriter(stream,columns);writer.writeheader()
        for episode in range(episodes):
            env=ToyTakeoffEnvironment(seed+episode);previous_action=[0.]*4
            for _ in range(601):
                before=env.observation();action=env.expert_action()
                raw,reward,done,info=env.step(action)
                row={name:0 for name in columns}
                row.update(schema_version='episode-v4.2',episode_id=f'synthetic-f22-{seed+episode:06d}',telemetry_source='SYNTHETIC_F22_TOY',
                           telemetry_sequence=env.step_id,utc=(start+timedelta(seconds=episode*180+env.t)).isoformat(),
                           sim_time_s=env.t,phase=raw['phase'],lat_deg=35.0,lon_deg=51.0+raw['along_m']/(111320*math.cos(math.radians(35))),
                           wind_speed_kts=abs(env.wind),wind_direction_deg=180 if env.wind<0 else 0,wind_cross_kts=env.wind,
                           rpm=0,fuel_fraction=.7,weight_fraction=.75,cg_fraction=.25,target_heading=90,target_ias=raw['target_ias_kts'],
                           target_alt_msl=raw['target_alt_msl_ft'],target_vs=raw['target_vs_fpm'],safety='TOY_NOT_FLIGHT_VALIDATED',
                           llm_intent='HOLD_HEADING',llm_confidence=0,llm_reason='No LLM used during synthetic generation',rl_stage='takeoff',rl_step_id=env.step_id,
                           rl_safety_scale=before['safety_scale'],rl_uncertainty=0,rl_intervened=int(before['on_ground']),
                           rl_rejection='Ground residual prohibited' if before['on_ground'] else '',aircraft_id=1000+episode,
                           aircraft_title='F-22 Raptor (synthetic uncalibrated)',control_role='ai',controller_output_enabled=0)
                for name in ('alt_msl_ft','alt_agl_ft','ias_kts','tas_kts','gs_kts','vs_fpm','pitch_deg','bank_deg','heading_deg','aoa_deg','beta_deg','along_m','cross_m'):row[name]=raw[name]
                for name in ('on_ground','stall','overspeed'):row[name]=int(raw[name])
                for i,axis in enumerate(AXES):
                    row['cmd_'+axis]=info['command'][i];row['rl_req_'+axis]=action[i];row['rl_applied_'+axis]=env.applied[i]
                    row[dict(aileron='pilot_yoke_x',elevator='pilot_yoke_y',rudder='pilot_rudder',throttle='pilot_throttle')[axis]]=info['command'][i]
                row['cmd_gear']=int(raw['alt_agl_ft']<100);row['accel_z_g']=1
                writer.writerow(row);counts[raw['phase']]=counts.get(raw['phase'],0)+1
                previous_action=action
                if done:successes+=int(raw['success']);break
    units={name:('degrees' if name.endswith('_deg') or name in ('target_heading',) else 'feet' if name.endswith('_ft') or name=='target_alt_msl' else 'knots' if name.endswith('_kts') or name=='target_ias' else 'feet/minute' if name.endswith('_fpm') or name=='target_vs' else 'metres' if name.endswith('_m') else 'seconds' if name=='sim_time_s' else 'UTC ISO-8601' if name=='utc' else 'schema-defined') for name in columns}
    manifest=dict(schema='f22-takeoff-dataset-v1',csv=output.name,sha256=sha256(output),episodes=episodes,rows=sum(counts.values()),columns=len(columns),
                  phase_counts=counts,synthetic_successes=successes,seed=seed,dt_seconds=.2,provenance='synthetic toy dynamics; not real P3D or professional pilot data',
                  flight_validated=False,live_policy_eligible=False,unsupported_fields=['rpm','body rates','accelerations other than synthetic 1g'],units=units,
                  aircraft_profile='config/aircraft/f22-raptor-v1.json',normalized_action='requested residual [-1,1], not full actuator',
                  action_alignment='synthetic telemetry is post-step; transition builders use next-row action for current to next state',
                  observation_names=list(OBSERVATION_NAMES))
    save_json(output.with_suffix('.metadata.json'),manifest);return manifest


def prepare(paths: list[Path],output: Path,action_source: str='residual') -> dict:
    if action_source not in ('residual','pilot'): raise ValueError('unsupported action source')
    episodes={};sources={}
    for path in paths:
        columns,rows=read_csv(path)
        missing=set(recorder_columns()[:72])-set(columns)
        if missing: raise ValueError(f'{path}: missing columns {sorted(missing)}')
        sources[str(path)]=sha256(path)
        for row in rows:
            if None in row or any(value is None for value in row.values()): raise ValueError('CSV framing mismatch')
            episode=row['episode_id']
            if not episode: raise ValueError('missing episode ID')
            raw=raw_from_row(row);encode(raw)
            for key in ('sim_time_s','pilot_yoke_x','pilot_yoke_y','pilot_rudder','pilot_throttle'):
                finite(row[key])
            datetime.fromisoformat(row['utc'].replace('Z','+00:00'))
            episodes.setdefault(episode,[]).append((row,raw))
    if len(episodes)<3: raise ValueError('at least three independent episodes required for train/validation/test')
    # Stable episode splits. Adjacent frames are never randomly split.
    names=sorted(episodes,key=lambda x:__import__('hashlib').sha256(x.encode()).hexdigest())
    n=len(names);nval=max(1,round(n*.15));ntest=max(1,round(n*.15))
    splits={name:('test' if i<ntest else 'validation' if i<ntest+nval else 'train') for i,name in enumerate(names)}
    feature_columns=[f'obs_{n}' for n in OBSERVATION_NAMES]
    fields=['episode_id','split','step','phase','provenance','action_semantics','reward','terminal',*feature_columns,
            *[f'next_obs_{n}' for n in OBSERVATION_NAMES],*[f'action_{n}' for n in AXES]]
    output.parent.mkdir(parents=True,exist_ok=True);total=0;rejections=[]
    with output.open('w',encoding='utf-8',newline='') as stream:
        writer=csv.DictWriter(stream,fields);writer.writeheader()
        for episode,items in episodes.items():
            if len(items)<10: raise ValueError('episode too short: '+episode)
            previous_time=-math.inf;previous_sequence=-1
            for row,raw in items:
                t=finite(row['sim_time_s']);sequence=int(row['telemetry_sequence'])
                if t<=previous_time or sequence<=previous_sequence: raise ValueError('non-monotonic telemetry: '+episode)
                if previous_time!=-math.inf and t-previous_time>1: raise ValueError('telemetry gap >1s: '+episode)
                previous_time=t;previous_sequence=sequence
            for i,((row,raw),(next_row,nxt)) in enumerate(zip(items,items[1:])):
                if raw['terminal']: raise ValueError('rows after terminal in episode: '+episode)
                label=next_row if row['telemetry_source']=='SYNTHETIC_F22_TOY' else row
                if action_source=='residual':
                    if label['rl_stage']!='takeoff': raise ValueError('residual labels require takeoff RL trace; human full controls are not residuals')
                    action=[finite(label['rl_req_'+axis]) for axis in AXES]
                else:action=[finite(label[k]) for k in ('pilot_yoke_x','pilot_yoke_y','pilot_rudder','pilot_throttle')]
                if any(abs(a)>1 for a in action) or action_source=='pilot' and action[3]<0: raise ValueError('out-of-range action')
                if raw['stall'] or raw['overspeed'] or raw['phase']=='Failsafe':rejections.append(dict(episode=episode,step=i,reason='unsafe sample excluded'));continue
                vector,next_vector=encode(raw),encode(nxt)
                reward=20. if nxt['success'] else -30. if nxt['terminal'] else -.015*(heading_error(raw['target_heading_deg'],raw['heading_deg'])/5)**2-.015*(raw['cross_m']/22.5)**2
                out=dict(episode_id=episode,split=splits[episode],step=i,phase=raw['phase'],provenance=row['telemetry_source'],
                         action_semantics='normalized_residual' if action_source=='residual' else 'full_pilot_actuator',reward=reward,terminal=int(nxt['terminal']))
                out.update(zip(feature_columns,vector));out.update(zip(fields[7+29:7+58],next_vector));out.update(zip([f'action_{n}' for n in AXES],action))
                writer.writerow(out);total+=1
    report=dict(schema='f22-transitions-v1',rows=total,episodes=len(episodes),splits=splits,source_hashes=sources,output_sha256=sha256(output),
                rejected_samples=rejections,action_semantics='normalized_residual' if action_source=='residual' else 'full_pilot_actuator',
                flight_validated=False,observation_names=list(OBSERVATION_NAMES))
    save_json(output.with_suffix('.metadata.json'),report);return report


# Local import avoids pulling scientific packages into the data utility.
from .contracts import heading_error


def main() -> None:
    parser=argparse.ArgumentParser();sub=parser.add_subparsers(dest='mode',required=True)
    gen=sub.add_parser('generate');gen.add_argument('--output',type=Path,default=Path('data/datasets/f22_takeoff/takeoff.csv'));gen.add_argument('--episodes',type=int,default=24);gen.add_argument('--seed',type=int,default=42)
    prep=sub.add_parser('prepare');prep.add_argument('csv',type=Path,nargs='+');prep.add_argument('--output',type=Path,default=Path('data/datasets/f22_takeoff/transitions.csv'));prep.add_argument('--action-source',choices=('residual','pilot'),default='residual')
    args=parser.parse_args()
    report=generate(args.output,args.episodes,args.seed) if args.mode=='generate' else prepare(args.csv,args.output,args.action_source)
    print(json.dumps({k:report[k] for k in ('rows','episodes')},ensure_ascii=False))


if __name__=='__main__':main()
