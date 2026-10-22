"""Dependency-free BC + episodic CEM policy search on explicitly toy dynamics."""
from __future__ import annotations
import argparse
import copy
import json
import math
import random
from pathlib import Path
from .contracts import AXES,OBSERVATION_NAMES,encode,finite,read_csv,save_json,sha256
from .environment import ToyTakeoffEnvironment


def features(observation: list[float]) -> list[float]:
    if len(observation)!=29 or not all(math.isfinite(x) for x in observation):raise ValueError('29 finite observations required')
    return [1.,observation[1],observation[6],observation[7],observation[8]*6,observation[10],observation[12]*50]


def predict(weights: list[list[float]],observation: list[float]) -> list[float]:
    xs=features(observation)
    return [math.tanh(sum(w*x for w,x in zip(row,xs))) for row in weights]


def load_model(path: Path) -> dict:
    model=json.loads(path.read_text(encoding='utf-8'))
    if model.get('schema')!='f22-residual-linear-v1' or model.get('observation_names')!=list(OBSERVATION_NAMES) or model.get('action_semantics')!='normalized_residual':raise ValueError('incompatible policy contract')
    weights=model['weights']
    if len(weights)!=4 or any(len(row)!=7 or any(not math.isfinite(float(w)) for w in row) for row in weights):raise ValueError('invalid policy weights')
    return model


def evaluate(weights: list[list[float]],seeds: list[int]) -> dict:
    returns=[];successes=0;max_bank=0.;max_cross=0.
    for seed in seeds:
        env=ToyTakeoffEnvironment(seed);total=0
        for _ in range(601):
            raw=env.observation();action=predict(weights,encode(raw)) if raw['phase']=='InitialClimb' else [0.]*4
            raw,reward,done,_=env.step(action);total+=reward
            max_bank=max(max_bank,abs(raw['bank_deg']));max_cross=max(max_cross,abs(raw['cross_m']))
            if done:successes+=int(raw['success']);break
        returns.append(total)
    return dict(mean_return=sum(returns)/len(returns),successes=successes,episodes=len(seeds),max_bank_deg=max_bank,max_cross_m=max_cross,
                seeds=seeds,environment='toy-not-P3D',flight_validated=False)


def train(dataset: Path,output: Path,iterations: int=8,population: int=16,seed: int=42) -> dict:
    if not 1<=iterations<=1000 or not 4<=population<=256:raise ValueError('invalid training budget')
    columns,rows=read_csv(dataset)
    obs=[f'obs_{name}' for name in OBSERVATION_NAMES]
    required=set(obs+[f'action_{axis}' for axis in AXES]+['split','phase','action_semantics','episode_id'])
    if not required<=set(columns):raise ValueError('transition schema mismatch')
    if any(row['action_semantics']!='normalized_residual' for row in rows):raise ValueError('human full controls cannot be used as residual labels')
    train_rows=[r for r in rows if r['split']=='train' and r['phase']=='InitialClimb']
    validation=[r for r in rows if r['split']=='validation' and r['phase']=='InitialClimb']
    testing=[r for r in rows if r['split']=='test' and r['phase']=='InitialClimb']
    if not train_rows or not validation or not testing:raise ValueError('three nonempty episode splits required')
    rng=random.Random(seed);weights=[[0.]*7 for _ in AXES]
    best=copy.deepcopy(weights);best_loss=math.inf
    def loss(samples,model):
        return sum(sum((p-finite(row[f'action_{axis}']))**2 for axis,p in zip(AXES,predict(model,[finite(row[c]) for c in obs]))) for row in samples)/(4*len(samples))
    # Validation episodes select the BC checkpoint; held-out test never enters gradients.
    for epoch in range(12):
        rng.shuffle(train_rows)
        for row in train_rows:
            vector=[finite(row[c]) for c in obs];xs=features(vector);actions=predict(weights,vector)
            for i,axis in enumerate(AXES):
                target=finite(row[f'action_{axis}'])
                if abs(target)>1:raise ValueError('out-of-range target')
                gradient=max(-2,min(2,(actions[i]-target)*(1-actions[i]**2)))
                weights[i]=[w-.006*(gradient*x+.001*w) for w,x in zip(weights[i],xs)]
        current=loss(validation,weights)
        if current<best_loss:best_loss=current;best=copy.deepcopy(weights)
    weights=best;bc_metrics=dict(validation_mse=best_loss,test_mse=loss(testing,weights),epochs=12)
    # CEM is episodic black-box RL, not SAC. Candidate rewards come from causal toy steps.
    seeds=list(range(2000,2006));zero=[[0.]*7 for _ in AXES]
    champion=copy.deepcopy(weights);champion_score=evaluate(champion,seeds)['mean_return']
    zero_score=evaluate(zero,seeds)['mean_return']
    if zero_score>champion_score:champion=copy.deepcopy(zero);champion_score=zero_score
    mean=copy.deepcopy(weights);sigma=.12;history=[]
    for iteration in range(iterations):
        candidates=[]
        for _ in range(population):
            candidate=[[w+rng.gauss(0,sigma) for w in row] for row in mean]
            score=evaluate(candidate,seeds)['mean_return'];candidates.append((score,candidate))
            if score>champion_score:champion_score=score;champion=copy.deepcopy(candidate)
        candidates.sort(key=lambda x:x[0],reverse=True);elites=candidates[:max(2,population//4)]
        mean=[[sum(c[1][i][j] for c in elites)/len(elites) for j in range(7)] for i in range(4)]
        sigma=max(.015,sigma*.85);history.append(dict(iteration=iteration,best_reward=champion_score))
    held_out=list(range(10000,10020));policy_evaluation=evaluate(champion,held_out);baseline_evaluation=evaluate(zero,held_out)
    model=dict(schema='f22-residual-linear-v1',weights=champion,observation_names=list(OBSERVATION_NAMES),action_semantics='normalized_residual',
               stage='takeoff',aircraft_profile='f22-raptor-v1',residual_limits=[.05,.05,.04,.03],algorithm='behavior cloning + episodic CEM policy search',
               training_environment='synthetic F22 toy, not a calibrated flight model',dataset_sha256=sha256(dataset),seed=seed,flight_validated=False,live_policy_eligible=False)
    save_json(output,model)
    report=dict(model_sha256=sha256(output),bc=bc_metrics,training_history=history,policy=policy_evaluation,baseline=baseline_evaluation,
                verdict='offline_pipeline_only',flight_validated=False,live_policy_eligible=False)
    save_json(output.with_suffix('.evaluation.json'),report);return report


def main() -> None:
    parser=argparse.ArgumentParser();parser.add_argument('dataset',type=Path);parser.add_argument('--output',type=Path,default=Path('artifacts/ai/f22_takeoff/policy.json'))
    parser.add_argument('--iterations',type=int,default=8);parser.add_argument('--population',type=int,default=16);parser.add_argument('--seed',type=int,default=42)
    args=parser.parse_args();report=train(args.dataset,args.output,args.iterations,args.population,args.seed);print(json.dumps(report,indent=2))


if __name__=='__main__':main()
