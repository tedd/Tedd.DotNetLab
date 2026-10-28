"""Export compact evaluation evidence; excludes auth, raw traces and process inventories."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sqlite3
import xml.etree.ElementTree as ET

import evaluate as e


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sanitize(value, task_root):
    if isinstance(value, dict):
        return {key: sanitize(item, task_root) for key, item in value.items()}
    if isinstance(value, list):
        return [sanitize(item, task_root) for item in value]
    if isinstance(value, str):
        for source, replacement in ((str(task_root), "$TASK"), (task_root.as_posix(), "$TASK"),
                                    (str(e.REPO), "$REPO"), (e.REPO.as_posix(), "$REPO")):
            value = value.replace(source, replacement)
        return value
    return value


def metrics(data):
    output = []
    for row in data if isinstance(data, list) else [data]:
        if not isinstance(row, dict):
            continue
        effects = row.get("effects_without_run_ids", row.get("effects", []))
        for effect in effects:
            output.append({"result_id": row.get("id"), "plan_id": row.get("plan_id"),
                           **{key: effect.get(key) for key in ("metric", "availability", "effect", "effect_unit", "effect_class", "quality", "reason_codes")}})
        if "ratio" in row:
            output.append({key: row.get(key) for key in ("ratio", "change_percent", "effect_class", "quality", "promotion_eligible", "reason_codes")})
    return output


def measurements(catalog):
    """Read only the fixed task-local catalog; never load assemblies or create a database."""
    catalog = e.safe_root(catalog)
    if not catalog.is_file():
        return []
    connection = sqlite3.connect(catalog.as_uri() + "?mode=ro", uri=True)
    try:
        rows = connection.execute("""
            SELECT Kind, Json FROM Record AS current
            WHERE Kind IN ('run','normal_build_session','comparison','confirmation_plan','confirmation_decision')
              AND Revision=(SELECT MAX(Revision) FROM Record AS latest WHERE latest.Id=current.Id)
            ORDER BY Kind, Id
        """)
        evidence = []
        for kind, serialized in rows:
            value = json.loads(serialized)
            if kind in {"run", "normal_build_session"}:
                retained = {key: value.get(key) for key in ("id", "execution_id", "plan_id", "block_ordinal", "arm_ordinal", "build_id", "compilation_regime")}
                measure = value["measurement"]
                retained["measurement"] = {key: measure.get(key) for key in (
                    "run_id", "block_id", "variant", "start_ticks", "end_ticks", "frequency", "completed_operations",
                    "process_allocation_bytes", "thread_allocation_bytes", "correct", "quiescent", "nanoseconds_per_operation")}
                retained["runtime_version"] = value.get("target", {}).get("runtime_version")
                retained["target_id"] = value.get("target", {}).get("id")
                retained["process_start_utc_ticks"] = value.get("target", {}).get("start_time_utc_ticks")
                retained["entry_assembly_hash"] = value.get("runtime", {}).get("entry_assembly_hash")
                retained["process_id"] = value.get("target", {}).get("process_id")
                value = retained
            evidence.append({"kind": kind, "value": value})
        return evidence
    finally:
        connection.close()


def trial(path):
    data = read(path / "summary.json")
    result = {key: data.get(key) for key in ("case", "score", "maximum", "checks", "elapsed_seconds", "timed_out",
                                           "exit_code", "infrastructure_error", "oracle", "final", "candidate_changed",
                                           "prepared_variants", "owner_pregranted", "skill_mode", "candidate_origin",
                                           "candidate_source_sha256", "candidate_final_sha256", "owner_confirmation")}
    events_path = path / "events.jsonl"
    events = [json.loads(line) for line in events_path.read_text(encoding="utf-8").splitlines()] if events_path.exists() else []
    calls = e.completed_calls(events)
    errors = []
    comparisons = []
    dispositions = []
    plans = []
    for call in calls:
        try:
            envelope = e.unwrap(call["result"] or {})
        except (ValueError, KeyError, TypeError):
            envelope = {"status": "error", "code": "mcp_binding_or_transport_error", "message": call.get("result")}
        if envelope["status"] != "ok" or (call.get("result") or {}).get("isError"):
            errors.append({"tool": call["tool"], "request": call["request"], "code": envelope.get("code"), "message": envelope.get("message")})
        else:
            if call["tool"] == "lab.compare":
                comparisons.extend(metrics(envelope["data"]))
            if call["request"].get("action") == "revise_hypothesis":
                dispositions.append(call["request"].get("state"))
            if call["request"].get("action") == "create_plan":
                plans.append({**call["request"], "id": envelope["data"].get("id")})
    result.update(dotnetlab_calls=len(calls), dotnetlab_errors=errors,
                  error_counts=dict(Counter(error["code"] for error in errors)), comparisons=comparisons,
                  notebook_dispositions=dispositions, prospective_plans=plans,
                  usage=[event["usage"] for event in events if event.get("type") == "turn.completed"],
                  events_sha256=e.digest(events_path) if events_path.exists() else None)
    for name in ("fixture-manifest.json", "skill-manifest.json"):
        if (path / name).exists():
            result[name.removesuffix(".json")] = read(path / name)
    parent = path.parent
    result["run"] = parent.name
    result["environment"] = read(parent / "environment.json") if (parent / "environment.json").exists() else {}
    if data.get("owner_confirmation"):
        result["retained_evidence"] = measurements(path / "catalog/catalog.sqlite")
    return result


def trx(path):
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    document = ET.parse(path).getroot()
    counters = document.find("t:ResultSummary/t:Counters", namespace)
    failures = []
    for item in document.findall("t:Results/t:UnitTestResult", namespace):
        if item.get("outcome") != "Failed":
            continue
        message = item.find("t:Output/t:ErrorInfo/t:Message", namespace)
        failures.append({"test": item.get("testName"), "message": message.text if message is not None else None})
    return {"file": path.name, "sha256": e.digest(path), "counters": dict(counters.attrib) if counters is not None else {}, "failures": failures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--reference-run", required=True, help="Explicit qualified reference run directory name")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = e.safe_root(args.root)
    reference = e.safe_root(root / "runs" / args.reference_run, root)
    trials = []
    for summary in sorted((root / "runs").glob("codex-*/*/summary.json")):
        trials.append(trial(summary.parent))
    reference_summary = read(reference / "summary.json")
    reference_summary["environment"] = read(reference / "environment.json")
    reference_summary["comparisons"] = {path.stem: metrics(read(path)) for path in sorted(reference.glob("comparison-*.json"))}
    reference_summary["retained_evidence"] = measurements(reference / "catalog/catalog.sqlite")
    output = {"schema": "dotnetlab-client-evaluation/v1", "reference": reference_summary, "codex_trials": trials,
              "repository_regressions": [trx(path) for path in sorted((root / "regression").glob("*.trx"))],
              "interrupted_runs": [read(path) for path in sorted((root / "runs").glob("*/run-disposition.json"))]}
    native_log = root / "build/native/Testing/Temporary/LastTest.log"
    if native_log.is_file():
        native_text = native_log.read_text(encoding="utf-8", errors="replace")
        native_tests = re.findall(r"^\d+/\d+ Testing: (.+)$", native_text, flags=re.M)
        output["native_contracts"] = {"total": len(native_tests), "passed": native_text.count("Test Passed."),
                                      "tests": native_tests, "log_sha256": e.digest(native_log)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    e.dump(args.output, sanitize(output, root))
    print(f"Evidence exported: {args.output}")
    print(f"Reference: {reference_summary['passed']}/{reference_summary['total']}; completed trial summaries: {len(trials)}")


if __name__ == "__main__":
    main()
