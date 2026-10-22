from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np


def require_torch():
    try:
        import torch
        from torch import nn
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt in a Python 3.11/3.12 virtual environment") from exc
    return torch, nn


def build_gru_policy(input_size: int, hidden_size: int = 192, layers: int = 2):
    _torch, nn = require_torch()

    class PilotGru(nn.Module):
        def __init__(self):
            super().__init__()
            self.encoder = nn.GRU(input_size, hidden_size, layers, batch_first=True,
                                  dropout=0.15 if layers > 1 else 0.0)
            self.actor = nn.Sequential(nn.Linear(hidden_size, hidden_size), nn.SiLU(), nn.Linear(hidden_size, 4), nn.Tanh())

        def forward(self, sequence):
            encoded, _ = self.encoder(sequence)
            return self.actor(encoded[:, -1])

    return PilotGru()


@dataclass(frozen=True)
class EnsemblePrediction:
    mean: np.ndarray
    uncertainty: float


class PolicyEnsemble:
    def __init__(self, models: list[Any]):
        if len(models) < 2:
            raise ValueError("an ensemble requires at least two models")
        self.models = models

    def predict(self, observation: np.ndarray) -> EnsemblePrediction:
        observation = np.asarray(observation, dtype=np.float32)
        if observation.ndim != 1 or not np.isfinite(observation).all():
            raise ValueError("observation must be a finite one-dimensional vector")
        values = []
        for model in self.models:
            prediction = model.predict(observation, deterministic=True)
            action = prediction[0] if isinstance(prediction, tuple) else prediction
            action_array = np.asarray(action, dtype=np.float64)
            if action_array.size != 4 or not np.isfinite(action_array).all():
                raise ValueError("ensemble member returned an invalid action")
            values.append(action_array.reshape(4))
        stacked = np.stack(values)
        mean = stacked.mean(axis=0)
        uncertainty = float(stacked.std(axis=0).mean())
        if np.any(np.abs(mean) > 1.0 + 1e-6) or not 0.0 <= uncertainty <= 1.0:
            raise ValueError("ensemble prediction is outside the residual-action contract")
        return EnsemblePrediction(np.clip(mean, -1.0, 1.0).astype(np.float32), uncertainty)


def export_gru_onnx(checkpoint: Path, destination: Path, input_size: int, sequence_length: int) -> None:
    torch, _ = require_torch()
    model = build_gru_policy(input_size)
    payload = torch.load(checkpoint, map_location="cpu", weights_only=True)
    if payload.get("action_semantics") not in {"full_pilot_actuator","normalized_residual"}:raise ValueError("checkpoint action semantics missing")
    if payload.get("input_size")!=input_size or payload.get("sequence_length")!=sequence_length:raise ValueError("checkpoint dimensions mismatch")
    output_name="residual_action" if payload["action_semantics"]=="normalized_residual" else "pilot_actuator"
    model.load_state_dict(payload["model_state"])
    model.eval()
    sample = torch.zeros(1, sequence_length, input_size, dtype=torch.float32)
    destination.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(model, sample, destination, input_names=["observation_sequence"],
                      output_names=[output_name], dynamic_axes={"observation_sequence": {0: "batch"}, output_name: {0: "batch"}},
                      opset_version=17)
