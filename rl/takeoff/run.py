from __future__ import annotations
import argparse
import json
import time
from datetime import datetime,timezone
from pathlib import Path
from rl.protocol import BridgeClient,BridgeError
from .contracts import encode,sha256,save_json
from .train import load_model,predict


def run(model_path: Path,bridge: str,seconds: float=30,hz: float=5,output: Path=Path('logs/rl/takeoff-inference.jsonl'),send: bool=False,validation: Path|None=None) -> dict:
    if not 1<=seconds<=86400 or not 1<=hz<=20:raise ValueError('invalid duration/rate')
    model=load_model(model_path);digest=sha256(model_path)
    if send:
        if validation is None:raise ValueError('sending requires an explicit live validation report')
        report=json.loads(validation.read_text())
        if report.get('model_sha256')!=digest or report.get('live_policy_eligible') is not True or report.get('environment')!='P3D' or report.get('episodes',0)<20:
            raise ValueError('model has no qualifying P3D validation')
    client=BridgeClient(bridge);output.parent.mkdir(parents=True,exist_ok=True)
    deadline=time.monotonic()+seconds;step=0;reset=-1;last_sequence=-1;rows=0;sent=0;errors=0
    with output.open('w',encoding='utf-8') as log:
        while time.monotonic()<deadline:
            start=time.monotonic()
            try:
                raw=client.observation()
                age=(datetime.now(timezone.utc)-datetime.fromisoformat(raw['utc'].replace('Z','+00:00'))).total_seconds()
                if not -.5<=age<=1.0:raise BridgeError('stale telemetry; inference suspended')
                if int(raw['sequence'])<=last_sequence:raise BridgeError('telemetry did not advance')
                last_sequence=int(raw['sequence'])
                if int(raw['reset_id'])!=reset:reset=int(raw['reset_id']);step=0
                vector=encode(raw);action=predict(model['weights'],vector)
                active=raw['phase']=='InitialClimb' and raw['control_role']=='ai' and raw['aircraft_id']!=0 and not raw['paused'] and not raw['slew'] and not raw['on_ground'] and not raw['terminal'] and raw['reset_status']=='ready'
                if not active:action=[0.]*4
                if send and active:step+=1;client.action(action,'takeoff',step,reset);sent+=1
                event=dict(sequence=raw['sequence'],utc=raw['utc'],aircraft_id=raw['aircraft_id'],phase=raw['phase'],action=action,
                           mode='live' if send else 'shadow-no-command',model_sha256=digest,eligible_state=active)
                log.write(json.dumps(event,allow_nan=False)+'\n');rows+=1
            except (BridgeError,KeyError,ValueError) as exc:
                errors+=1;log.write(json.dumps(dict(event='inference_suspended',error=str(exc)))+'\n')
            time.sleep(max(0,1/hz-(time.monotonic()-start)))
    return dict(observations=rows,commands=sent,errors=errors,mode='live' if send else 'shadow',model_sha256=digest)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('model',type=Path);parser.add_argument('--bridge',default='http://127.0.0.1:8787');parser.add_argument('--duration-seconds',type=float,default=300)
    parser.add_argument('--hz',type=float,default=5);parser.add_argument('--output',type=Path,default=Path('logs/rl/takeoff-inference.jsonl'))
    parser.add_argument('--send',action='store_true');parser.add_argument('--validation',type=Path)
    a=parser.parse_args();print(json.dumps(run(a.model,a.bridge,a.duration_seconds,a.hz,a.output,a.send,a.validation),indent=2))


if __name__=='__main__':main()
