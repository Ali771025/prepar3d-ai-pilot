from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from sklearn.ensemble import ExtraTreesRegressor


@dataclass(frozen=True)
class WorldPrediction:
    next_state: np.ndarray
    uncertainty: float


class DynamicsEnsemble:
    """Short-horizon delta-state ensemble for fast pretraining and diagnostics."""

    def __init__(self, members: int = 5, seed: int = 42):
        if members < 3:
            raise ValueError("members must be >= 3")
        self.models = [ExtraTreesRegressor(n_estimators=80, min_samples_leaf=3, n_jobs=-1, random_state=seed + i)
                       for i in range(members)]
        self.state_size: int | None = None

    def fit(self, states: np.ndarray, actions: np.ndarray, next_states: np.ndarray) -> None:
        states = np.asarray(states, dtype=np.float64)
        actions = np.asarray(actions, dtype=np.float64)
        next_states = np.asarray(next_states, dtype=np.float64)
        if states.ndim != 2 or next_states.shape != states.shape or actions.shape != (len(states), 4):
            raise ValueError("expected states [N,S], actions [N,4], next_states [N,S]")
        if len(states) < 100:
            raise ValueError("at least 100 transitions are required")
        if states.shape[1] < 1 or not all(np.isfinite(value).all() for value in (states, actions, next_states)):
            raise ValueError("world-model transitions must be finite and have at least one state feature")
        self.state_size = states.shape[1]
        features = np.c_[states, actions]
        targets = next_states - states
        rng = np.random.default_rng(12345)
        for model in self.models:
            indices = rng.integers(0, len(features), len(features))
            model.fit(features[indices], targets[indices])

    def predict(self, state: np.ndarray, action: np.ndarray) -> WorldPrediction:
        if self.state_size is None:
            raise RuntimeError("world model is not fitted")
        state = np.asarray(state, dtype=np.float64).reshape(self.state_size)
        action = np.asarray(action, dtype=np.float64).reshape(4)
        if not np.isfinite(state).all() or not np.isfinite(action).all():
            raise ValueError("world-model inputs must be finite")
        features = np.r_[state, action].reshape(1, -1)
        deltas = np.stack([model.predict(features)[0] for model in self.models])
        return WorldPrediction((state + deltas.mean(axis=0)).astype(np.float32), float(deltas.std(axis=0).mean()))

    def rollout(self, state: np.ndarray, policy, horizon: int = 5, maximum_uncertainty: float = 0.25) -> list[WorldPrediction]:
        if isinstance(horizon, bool) or not isinstance(horizon, int) or horizon < 1:
            raise ValueError("horizon must be a positive integer")
        if not np.isfinite(maximum_uncertainty) or maximum_uncertainty < 0.0:
            raise ValueError("maximum_uncertainty must be finite and non-negative")
        predictions: list[WorldPrediction] = []
        current = np.asarray(state, dtype=np.float32)
        for _ in range(horizon):
            action = np.asarray(policy(current), dtype=np.float32)
            prediction = self.predict(current, action)
            predictions.append(prediction)
            if prediction.uncertainty > maximum_uncertainty:
                break
            current = prediction.next_state
        return predictions

    def metadata(self) -> dict:
        return {"members": len(self.models), "state_size": self.state_size, "rollout_policy": "short-horizon-uncertainty-gated"}
