from __future__ import annotations

import json
from pathlib import Path


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    manifest_path = root / "docs" / "IMPLEMENTATION_MANIFEST.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    phases = manifest.get("phases", [])
    if manifest.get("phase_count") != 17 or len(phases) != 17:
        raise SystemExit("implementation manifest must contain exactly 17 phases")
    expected_ids = [f"P{index:02d}" for index in range(17)]
    actual_ids = [phase.get("id") for phase in phases]
    if actual_ids != expected_ids:
        raise SystemExit(f"phase IDs are invalid: {actual_ids}")
    missing = []
    for phase in phases:
        evidence = phase.get("evidence", [])
        if not evidence:
            missing.append(f"{phase['id']}:no-evidence")
        for relative in evidence:
            if not (root / relative).is_file():
                missing.append(f"{phase['id']}:{relative}")
    version = (root / "VERSION").read_text(encoding="utf-8").strip()
    if version != manifest.get("version"):
        missing.append(f"version-mismatch:{version}")
    if missing:
        raise SystemExit("implementation validation failed: " + ", ".join(missing))

    configuration = json.loads((root / "config" / "appsettings.json").read_text(encoding="utf-8"))
    simulator = configuration.get("Simulator", {})
    if simulator.get("Mode") not in {"AutoFailover", "Redundant", "SimConnectOnly", "FsuipcOnly"}:
        raise SystemExit("simulator integration mode is invalid")
    if simulator.get("Mode") in {"AutoFailover", "Redundant"} and not simulator.get("EnableParallelTelemetry"):
        raise SystemExit("dual transport mode requires parallel telemetry")
    if not simulator.get("Fsuipc", {}).get("Enabled"):
        raise SystemExit("FSUIPC integration must be enabled in the v4 default profile")
    aircraft_selection = configuration.get("AircraftProfiles", {})
    if not aircraft_selection.get("Enabled"):
        raise SystemExit("per-aircraft profile selection must be enabled")
    profile_directory = root / "config" / aircraft_selection.get("Directory", "")
    profiles = [json.loads(path.read_text(encoding="utf-8")) for path in sorted(profile_directory.glob("*.json"))]
    if not profiles:
        raise SystemExit("no aircraft profiles were found")
    profile_ids = [profile.get("id") for profile in profiles]
    if len(profile_ids) != len(set(profile_ids)):
        raise SystemExit("aircraft profile IDs must be unique")
    if aircraft_selection.get("DefaultProfileId") not in profile_ids:
        raise SystemExit("default aircraft profile does not exist")
    for profile in profiles:
        if profile.get("schema") != "aircraft-profile-v4":
            raise SystemExit(f"invalid aircraft profile schema: {profile.get('id')}")
        for section in ("aircraft", "pattern", "controllers", "safety"):
            if not isinstance(profile.get(section), dict) or not profile[section]:
                raise SystemExit(f"aircraft profile {profile.get('id')} is missing {section}")
    f22 = next((profile for profile in profiles if profile.get("id") == "f22-raptor-v1"), None)
    if f22 is None or not any("raptor" in title.lower() for title in f22.get("match_titles", [])):
        raise SystemExit("F-22 Raptor aircraft profile or TITLE aliases are missing")

    simconnect_source = (root / "src" / "P3DAIPilot.P3Dv4" / "P3DSimConnectClient.cs").read_text(encoding="utf-8")
    fsuipc_source = (root / "src" / "P3DAIPilot.P3Dv4" / "FsuipcSimulatorClient.cs").read_text(encoding="utf-8")
    gateway_source = (root / "src" / "P3DAIPilot.Core" / "SimulatorGateway.cs").read_text(encoding="utf-8")
    recorder_source = (root / "src" / "P3DAIPilot.P3Dv4" / "FlightCsvRecorder.cs").read_text(encoding="utf-8")
    bridge_source = (root / "src" / "P3DAIPilot.P3Dv4" / "RlBridgeServer.cs").read_text(encoding="utf-8")
    autopilot_source = (root / "src" / "P3DAIPilot.Core" / "AutopilotEngine.cs").read_text(encoding="utf-8")
    simconnect_definitions = sum(1 for line in simconnect_source.splitlines()
                                 if line.strip().startswith("Add(Definitions."))
    simconnect_fields = sum(1 for line in simconnect_source.splitlines() if line.strip().startswith("public double "))
    fsuipc_offsets = fsuipc_source.count("new Offset<")
    if simconnect_definitions != 43 or simconnect_fields != 43:
        raise SystemExit(f"SimConnect contract mismatch: definitions={simconnect_definitions} fields={simconnect_fields}")
    if 'Add("FUEL TOTAL QUANTITY PERCENT"' in simconnect_source:
        raise SystemExit("SimConnect uses the unsupported FUEL TOTAL QUANTITY PERCENT datum")
    for required in ('"FUEL TOTAL QUANTITY", "gallons")',
                     '"FUEL TOTAL CAPACITY", "gallons")'):
        if required not in simconnect_source:
            raise SystemExit(f"SimConnect fuel telemetry is missing: {required}")
    for required in ("EssentialTelemetry", "ExtendedTelemetry", "AircraftIdentity", '"TITLE", null'):
        if required not in simconnect_source:
            raise SystemExit(f"resilient SimConnect channel is missing: {required}")
    if fsuipc_offsets < 40:
        raise SystemExit(f"FSUIPC telemetry coverage is incomplete: offsets={fsuipc_offsets}")
    for required in ("Control authority changed", "Telemetry divergence", "TryReconnect",
                     "telemetry watchdog recycle", "previousPump.IsCompleted"):
        if required not in gateway_source:
            raise SystemExit(f"gateway wiring is missing: {required}")
    for required in ("telemetry_source", "telemetry_sequence", "episode-v4.2"):
        if required not in recorder_source:
            raise SystemExit(f"v4 recorder contract is missing: {required}")
    for required in ("_lastAcceptedStepId", "MaxRequestBodyBytes", "Too many concurrent requests"):
        if required not in bridge_source:
            raise SystemExit(f"RL bridge hardening is missing: {required}")
    for required in ("PilotOverrideDetector", "ControlCommandLimiter", "emergencyEnvelopeRecovery"):
        if required not in autopilot_source:
            raise SystemExit(f"runtime safety hardening is missing: {required}")
    for relative in ("src/P3DAIPilot.Core/PilotOverrideDetector.cs",
                     "src/P3DAIPilot.Core/ControlCommandLimiter.cs"):
        if not (root / relative).is_file():
            raise SystemExit(f"runtime safety file is missing: {relative}")
    print("PASS static source/configuration contracts; live simulator and model training require separate execution")


if __name__ == "__main__":
    main()
