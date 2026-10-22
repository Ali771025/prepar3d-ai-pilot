from __future__ import annotations

import argparse
import json
from pathlib import Path

from .evaluation import compare_to_baseline


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate", type=Path)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("--output", type=Path, default=Path("reports/comparison.json"))
    args = parser.parse_args()
    candidate = json.loads(args.candidate.read_text(encoding="utf-8"))["summary"]
    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))["summary"]
    passed, failures = compare_to_baseline(candidate, baseline)
    payload = {"passed": passed, "failures": failures, "candidate": candidate, "baseline": baseline}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(f"baseline_comparison_passed={passed} failures={failures}")
    if not passed:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
