"""Causal toy dynamics for pipeline validation, NOT an F-22 flight model."""
from __future__ import annotations
import math
import random
from .contracts import PHASES,clip,heading_error


class ToyTakeoffEnvironment:
    def __init__(self,seed: int=42,dt: float=.2):
        if not 0.02<=dt<=1: raise ValueError('dt must be 0.02..1')
        self.dt=dt
        self.reset(seed)

    def reset(self,seed: int=42) -> dict:
        self.rng=random.Random(seed);self.t=0.;self.ias=0.;self.agl=0.;self.pitch=0.;self.bank=0.
        self.heading=90.;self.cross=0.;self.along=0.;self.stable=0.;self.step_id=0
        self.wind=self.rng.uniform(-5,5);self.pitch_bias=self.rng.uniform(-.7,.7);self.roll_bias=self.rng.uniform(-.3,.3)
        self.phase='TakeoffRoll';self.applied=[0.]*4;self.last_action=[0.]*4
        return self.observation()

    def baseline(self) -> list[float]:
        desired_pitch=10 if self.phase=='Rotate' else 8 if self.phase=='InitialClimb' else 0
        return [clip(-self.bank*.038),clip(.08+(desired_pitch-self.pitch)*.05) if self.phase!='TakeoffRoll' else 0,
                clip(heading_error(90,self.heading)*.055),1.]

    def observation(self) -> dict:
        airborne=self.phase in ('InitialClimb','Completed','Failsafe') and self.agl>1
        vs=self.ias*101.2686*math.sin(math.radians(self.pitch)) if airborne else 0.
        stall=airborne and self.ias<125
        return dict(sequence=self.step_id+1,reset_id=0,reset_status='ready',phase=self.phase,phase_id=PHASES[self.phase],
                    ias_kts=self.ias,tas_kts=self.ias,gs_kts=self.ias,alt_msl_ft=200+self.agl,alt_agl_ft=self.agl,
                    vs_fpm=vs,pitch_deg=self.pitch,bank_deg=self.bank,heading_deg=self.heading,
                    aoa_deg=max(0,self.pitch*.6),beta_deg=self.wind*.08,along_m=self.along,cross_m=self.cross,
                    target_heading_deg=90.,target_ias_kts=155. if self.phase=='TakeoffRoll' else 250.,target_alt_msl_ft=900.,target_vs_fpm=1500.,
                    baseline=self.baseline(),applied_residual=self.applied.copy(),safety_scale=1. if airborne else 0.,
                    on_ground=not airborne,stall=stall,overspeed=self.ias>650,paused=False,slew=False,
                    terminal=self.phase in ('Completed','Failsafe'),success=self.phase=='Completed',sim_time_s=self.t,
                    telemetry_source='SYNTHETIC_F22_TOY',wind_cross_kts=self.wind)

    def expert_action(self) -> list[float]:
        if self.phase!='InitialClimb': return [0.]*4
        return [clip(-self.roll_bias/.9),clip(-self.pitch_bias/1.0),clip(-self.wind/12),0.]

    def step(self,action: list[float]) -> tuple[dict,float,bool,dict]:
        if len(action)!=4 or any(not math.isfinite(x) or abs(x)>1 for x in action): raise ValueError('invalid normalized action')
        if self.observation()['terminal']: raise RuntimeError('reset required after terminal')
        previous=self.observation();base=previous['baseline']
        limits=(.05,.05,.04,.03)
        self.applied=[a*l for a,l in zip(action,limits)] if self.phase=='InitialClimb' else [0.]*4
        cmd=[clip(b+a,-1 if i<3 else 0,1) for i,(b,a) in enumerate(zip(base,self.applied))]
        self.t+=self.dt;self.step_id+=1
        self.ias=max(0,self.ias+(7.5*cmd[3]-.014*self.ias-.06*max(self.pitch,0))*self.dt)
        if self.phase=='TakeoffRoll' and self.ias>=155:self.phase='Rotate'
        if self.phase!='TakeoffRoll':self.pitch+=(20*cmd[1]-1.6*self.pitch+.65+self.pitch_bias)*self.dt
        self.bank+=(18*cmd[0]-.7*self.bank+self.roll_bias)*self.dt
        self.heading=(self.heading+(7*cmd[2]+.05*self.bank+self.wind*.025)*self.dt)%360
        self.along+=self.ias*.514444*self.dt
        self.cross+=(self.ias*.514444*math.sin(math.radians(self.heading-90))+self.wind*.15)*self.dt
        if self.phase=='Rotate' and self.pitch>=3.5:self.phase='InitialClimb';self.agl=2.
        if self.phase=='InitialClimb':self.agl+=max(-100,self.ias*101.2686*math.sin(math.radians(self.pitch)))*self.dt/60
        raw=self.observation()
        healthy=not raw['on_ground'] and self.agl>=700 and raw['vs_fpm']>=100 and abs(self.bank)<=15 and self.ias>=150
        self.stable=self.stable+self.dt if healthy else 0
        if self.stable>=3:self.phase='Completed'
        if self.t>=120 or abs(self.cross)>22.5 and raw['on_ground'] or abs(self.bank)>30 or raw['stall'] or raw['overspeed']:self.phase='Failsafe'
        raw=self.observation()
        reward=-.015*(heading_error(90,self.heading)/5)**2-.015*(self.cross/22.5)**2
        if raw['phase']=='InitialClimb':reward-=.02*((self.pitch-8)/5)**2+.01*(self.bank/10)**2
        reward-=.02*sum(x*x for x in action)/4+.04*sum((x-y)**2 for x,y in zip(action,self.last_action))/4
        reward+=20 if raw['success'] else -30 if raw['terminal'] else 0
        self.last_action=action.copy()
        return raw,reward,raw['terminal'],dict(command=cmd,previous=previous,provenance='toy-dynamics-unvalidated')
