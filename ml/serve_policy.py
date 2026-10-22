"""Serve a trained imitation policy for shadow-mode evaluation."""

from __future__ import annotations

import argparse
from pathlib import Path
from typing import Any

import numpy as np
import torch
import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from train_behavior_clone import FEATURES, TARGETS, PilotGru


class PredictionRequest(BaseModel):
    sequence: list[dict[str, float]]


class PredictionResponse(BaseModel):
    actions: dict[str, float]
    confidence: float


def create_app(checkpoint_path: Path) -> FastAPI:
    checkpoint: dict[str, Any] = torch.load(checkpoint_path, map_location="cpu", weights_only=True)
    features: list[str] = checkpoint["features"]
    targets: list[str] = checkpoint["targets"]
    sequence_length = int(checkpoint["sequence_length"])
    norm = checkpoint["normalization"]
    feature_mean = np.asarray(norm["feature_mean"], dtype=np.float32)
    feature_std = np.asarray(norm["feature_std"], dtype=np.float32)
    target_mean = np.asarray(norm["target_mean"], dtype=np.float32)
    target_std = np.asarray(norm["target_std"], dtype=np.float32)

    if features != FEATURES or targets != TARGETS:
        raise ValueError("checkpoint feature or target contract does not match this server")
    if sequence_length < 1:
        raise ValueError("checkpoint sequence_length must be positive")
    if any(value.shape != (len(FEATURES),) for value in (feature_mean, feature_std)):
        raise ValueError("checkpoint feature normalization has an invalid shape")
    if any(value.shape != (len(TARGETS),) for value in (target_mean, target_std)):
        raise ValueError("checkpoint target normalization has an invalid shape")
    if not all(np.isfinite(value).all() for value in (feature_mean, feature_std, target_mean, target_std)):
        raise ValueError("checkpoint normalization must be finite")
    if np.any(feature_std <= 0.0) or np.any(target_std <= 0.0):
        raise ValueError("checkpoint normalization scales must be positive")

    model = PilotGru(len(features))
    model.load_state_dict(checkpoint["model_state"])
    model.eval()
    app = FastAPI(title="P3D Pilot Imitation Policy", version="1.0")

    @app.get("/health")
    def health() -> dict[str, Any]:
        return {"ok": True, "sequence_length": sequence_length, "features": features}

    @app.post("/predict", response_model=PredictionResponse)
    def predict(request: PredictionRequest) -> PredictionResponse:
        if len(request.sequence) != sequence_length:
            raise HTTPException(400, f"Expected exactly {sequence_length} samples")
        try:
            values = np.asarray([[sample[name] for name in features] for sample in request.sequence], dtype=np.float32)
        except KeyError as exc:
            raise HTTPException(400, f"Missing feature: {exc.args[0]}") from exc
        if not np.isfinite(values).all():
            raise HTTPException(400, "Non-finite input")
        normalized = (values - feature_mean) / feature_std
        with torch.inference_mode():
            prediction = model(torch.from_numpy(normalized).unsqueeze(0)).squeeze(0).numpy()
        if prediction.shape != (len(TARGETS),) or not np.isfinite(prediction).all():
            raise HTTPException(503, "Policy returned an invalid prediction")
        actions = prediction * target_std + target_mean
        actions[:3] = np.clip(actions[:3], -1.0, 1.0)
        actions[3] = np.clip(actions[3], 0.0, 1.0)
        return PredictionResponse(actions=dict(zip(targets, map(float, actions), strict=True)), confidence=0.5)

    return app


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    uvicorn.run(create_app(args.checkpoint), host=args.host, port=args.port)


if __name__ == "__main__":
    main()
