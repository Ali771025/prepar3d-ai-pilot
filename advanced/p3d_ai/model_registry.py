from __future__ import annotations

import json
import math
import re
import shutil
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

from .contracts import ACTION_VERSION, SCHEMA_VERSION, FULL_ACTION_VERSION, sha256_file

ALLOWED_STATUS = {"candidate", "shadow-approved", "release-approved", "rejected"}
SAFE_MODEL_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")


@dataclass(frozen=True)
class ModelRecord:
    model_id: str
    algorithm: str
    artifact: str
    artifact_sha256: str
    observation_schema: str
    action_schema: str
    status: str
    created_utc: str
    metrics: dict[str, float]


class ModelRegistry:
    def __init__(self, root: Path):
        self.root = root
        self.models = root / "models"
        self.index = root / "registry.json"
        self.models.mkdir(parents=True, exist_ok=True)

    def register(self, source: Path, model_id: str, algorithm: str, metrics: dict[str, float], action_schema: str=ACTION_VERSION) -> ModelRecord:
        if action_schema not in {ACTION_VERSION,FULL_ACTION_VERSION}:raise ValueError("unsupported action schema")
        if not source.is_file():
            raise FileNotFoundError(source)
        if not SAFE_MODEL_ID.fullmatch(model_id) or ".." in model_id:
            raise ValueError("model_id must be a safe 1..80 character identifier")
        if not algorithm.strip():
            raise ValueError("algorithm is required")
        if any(not math.isfinite(float(value)) for value in metrics.values()):
            raise ValueError("model metrics must be finite")
        records = self._load()
        if any(item["model_id"] == model_id for item in records):
            raise ValueError(f"model_id already exists: {model_id}")
        destination_directory = self.models / model_id
        destination = destination_directory / source.name
        destination_directory.mkdir(parents=True, exist_ok=False)
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        try:
            shutil.copy2(source, temporary)
            temporary.replace(destination)
            record = ModelRecord(
                model_id=model_id, algorithm=algorithm, artifact=str(destination.relative_to(self.root)),
                artifact_sha256=sha256_file(destination), observation_schema=SCHEMA_VERSION,
                action_schema=action_schema, status="candidate",
                created_utc=datetime.now(timezone.utc).isoformat(), metrics=dict(metrics),
            )
            records.append(asdict(record))
            self._write(records)
            return record
        except Exception:
            shutil.rmtree(destination_directory, ignore_errors=True)
            raise

    def promote(self, model_id: str, status: str) -> None:
        if status not in ALLOWED_STATUS:
            raise ValueError(f"invalid status: {status}")
        records = self._load()
        matches = [item for item in records if item["model_id"] == model_id]
        if len(matches) != 1:
            raise KeyError(model_id)
        if not self.verify(model_id):
            raise ValueError("model artifact hash verification failed")
        current = matches[0]["status"]
        legal = {
            "candidate": {"shadow-approved", "rejected"},
            "shadow-approved": {"release-approved", "rejected"},
            "release-approved": {"rejected"},
            "rejected": set(),
        }
        if status not in legal[current]:
            raise ValueError(f"illegal transition: {current} -> {status}")
        matches[0]["status"] = status
        self._write(records)

    def verify(self, model_id: str) -> bool:
        records = [item for item in self._load() if item["model_id"] == model_id]
        if len(records) != 1:
            return False
        try:
            record = records[0]
            artifact = (self.root / record["artifact"]).resolve()
            artifact.relative_to(self.root.resolve())
            expected_hash = str(record["artifact_sha256"])
        except (KeyError, TypeError, ValueError):
            return False
        return artifact.is_file() and sha256_file(artifact) == expected_hash

    def _load(self) -> list[dict]:
        if not self.index.exists():
            return []
        payload = json.loads(self.index.read_text(encoding="utf-8"))
        if not isinstance(payload, dict) or not isinstance(payload.get("models"), list):
            raise TypeError("model registry index is malformed")
        return payload["models"]

    def _write(self, records: list[dict]) -> None:
        temporary = self.index.with_suffix(".tmp")
        temporary.write_text(json.dumps({"models": records}, indent=2, ensure_ascii=False), encoding="utf-8")
        temporary.replace(self.index)
