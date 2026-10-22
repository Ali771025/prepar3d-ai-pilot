from __future__ import annotations

import argparse
from pathlib import Path

from .model_registry import ModelRegistry


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("model_id")
    parser.add_argument("status", choices=("shadow-approved", "release-approved", "rejected"))
    parser.add_argument("--registry", type=Path, default=Path("artifacts/model_registry"))
    args = parser.parse_args()
    registry = ModelRegistry(args.registry)
    if not registry.verify(args.model_id):
        raise SystemExit("model hash verification failed")
    registry.promote(args.model_id, args.status)
    print(f"model={args.model_id} status={args.status}")


if __name__ == "__main__":
    main()

