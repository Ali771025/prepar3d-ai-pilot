from __future__ import annotations

import math
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

ALLOWED_INTENTS = {
    "CONTINUE_PATTERN", "EXTEND_DOWNWIND", "STABILIZE", "GO_AROUND",
    "HOLD_HEADING", "RETURN_CONTROL_TO_PILOT",
}


@dataclass(frozen=True)
class SupervisorDecision:
    intent: str
    confidence: float
    heading_delta_deg: float
    airspeed_delta_kts: float
    reason_code: str
    received_utc: datetime


def validate_decision(payload: Mapping[str, Any], phase: str, minimum_confidence: float = 0.80) -> SupervisorDecision:
    if not math.isfinite(minimum_confidence) or not 0.0 <= minimum_confidence <= 1.0:
        raise ValueError("minimum_confidence must be finite and in [0, 1]")
    required = {"intent", "confidence", "heading_delta_deg", "airspeed_delta_kts", "reason_code"}
    extra = set(payload).difference(required)
    missing = required.difference(payload)
    if missing or extra:
        raise ValueError(f"schema mismatch; missing={sorted(missing)} extra={sorted(extra)}")
    intent = str(payload["intent"]).upper()
    confidence = float(payload["confidence"])
    heading_delta = float(payload["heading_delta_deg"])
    airspeed_delta = float(payload["airspeed_delta_kts"])
    if not all(math.isfinite(value) for value in (confidence, heading_delta, airspeed_delta)):
        raise ValueError("decision contains a non-finite number")
    if intent not in ALLOWED_INTENTS:
        raise ValueError("intent is not allowed")
    if not 0.0 <= confidence <= 1.0 or confidence < minimum_confidence:
        raise ValueError("confidence is below threshold")
    if abs(heading_delta) > 15.0 or abs(airspeed_delta) > 8.0:
        raise ValueError("suggested target delta exceeds authority")
    if intent == "GO_AROUND" and phase not in {"Base", "Final", "Flare"}:
        raise ValueError("go-around intent is invalid in this phase")
    if intent == "EXTEND_DOWNWIND" and phase != "Downwind":
        raise ValueError("extend-downwind intent is invalid in this phase")
    reason_code = str(payload["reason_code"]).strip()
    if not reason_code or len(reason_code) > 80 or any(ord(character) < 32 for character in reason_code):
        raise ValueError("reason_code is invalid")
    return SupervisorDecision(intent, confidence, heading_delta, airspeed_delta, reason_code, datetime.now(timezone.utc))


def compact_state(raw: Mapping[str, Any]) -> dict[str, Any]:
    keys = (
        "phase", "ias_kts", "alt_agl_ft", "vs_fpm", "pitch_deg", "bank_deg",
        "heading_deg", "target_heading_deg", "target_ias_kts", "cross_m",
        "stall", "overspeed", "safety_scale", "uncertainty",
    )
    return {key: raw.get(key) for key in keys}
