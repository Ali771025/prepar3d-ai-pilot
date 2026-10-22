from __future__ import annotations

import argparse
import json
from pathlib import Path

from .model_registry import ModelRegistry
from .contracts import ACTION_VERSION,FULL_ACTION_VERSION
from .models import export_gru_onnx


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/export/pilot_policy.onnx"))
    parser.add_argument("--registry", type=Path, default=Path("artifacts/model_registry"))
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--algorithm", default="gru-bc")
    args = parser.parse_args()
    try:
        import torch
    except ImportError as exc:
        raise RuntimeError("Install advanced/requirements.txt before ONNX export") from exc
    payload = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    export_gru_onnx(args.checkpoint, args.output, int(payload["input_size"]), int(payload["sequence_length"]))
    record = ModelRegistry(args.registry).register(args.output, args.model_id, args.algorithm,
                                                   {"validation_loss": float(payload.get("validation_loss", -1.0))},
                                                   action_schema=ACTION_VERSION if payload["action_semantics"]=="normalized_residual" else FULL_ACTION_VERSION)
    print(json.dumps(record.__dict__, indent=2))


if __name__ == "__main__":
    main()

