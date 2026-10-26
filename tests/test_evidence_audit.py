"""Join executed TRX methods to an assembly-bound declaration catalog.

Passing tagged tests are evidence, never an automatic completion certificate.
The output separates unexecuted declarations, failures, and missing named gates.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

def checked_bytes(path, maximum):
    path = Path(path)
    if path.stat().st_size > maximum:
        raise ValueError(f"Input exceeds its bounded read budget: {path.name}")
    return path.read_bytes()

def digest(data):
    return hashlib.sha256(data).hexdigest()

def audit(spec_bytes, plan_bytes, declarations, trx_bytes):
    if declarations.get("schema_version") != "test-declarations/v1":
        raise ValueError("Unsupported test declaration schema")
    declared = declarations.get("tests", [])
    if not 1 <= len(declared) <= 10000 or len({t["method"] for t in declared}) != len(declared):
        raise ValueError("Declaration methods must be bounded and unique")
    if b"<!DOCTYPE" in trx_bytes.upper() or b"<!ENTITY" in trx_bytes.upper():
        raise ValueError("Test result XML cannot declare entities")
    root = ET.fromstring(trx_bytes)
    definitions = {}
    for test in root.findall("t:TestDefinitions/t:UnitTest", NAMESPACE):
        method = test.find("t:TestMethod", NAMESPACE)
        if method is None or test.attrib["id"] in definitions:
            raise ValueError("TRX test identity is absent or duplicated")
        definitions[test.attrib["id"]] = {"method": method.attrib["className"] + "." + method.attrib["name"],
                                          "assembly": method.attrib["codeBase"]}
    lookup = {test["method"]: test for test in declared}
    results = []; identifiers = set()
    for result in root.findall("t:Results/t:UnitTestResult", NAMESPACE):
        key = (result.attrib["testId"], result.attrib["executionId"])
        if key in identifiers or result.attrib["testId"] not in definitions:
            raise ValueError("Executed test has a duplicate or undefined identity")
        identifiers.add(key); definition = definitions[result.attrib["testId"]]
        if definition["method"] not in lookup:
            raise ValueError("Executed test lacks a matching declared requirement map: " + definition["method"])
        if Path(definition["assembly"]).resolve() != Path(declarations["assembly_path"]).resolve():
            raise ValueError("TRX and declaration catalog refer to different test assemblies")
        metadata = lookup[definition["method"]]
        results.append({"test_id": key[0], "execution_id": key[1], "name": result.attrib["testName"], "method": definition["method"],
                        "outcome": result.attrib["outcome"], "cases": metadata["cases"], "requirements": metadata["requirements"],
                        "milestone": metadata["milestone"], "level": metadata["level"]})
    terminal = root.find("t:ResultSummary", NAMESPACE)
    if terminal is None or not terminal.attrib.get("outcome"):
        raise ValueError("TRX terminal run outcome is absent")
    run_outcome = terminal.attrib["outcome"]
    run_errors = [info.attrib.get("outcome", "Unknown") for info in terminal.findall("t:RunInfos/t:RunInfo", NAMESPACE)
                  if info.attrib.get("outcome") not in ("Passed", "Completed", "Warning")]
    counters = terminal.find("t:Counters", NAMESPACE)
    if not results or counters is None or int(counters.attrib["total"]) != len(results) or int(counters.attrib["executed"]) != len(results):
        raise ValueError("TRX result rows do not match the terminal executed-test counters")
    counter_errors = [key for key in ("error", "timeout", "aborted", "passedButRunAborted", "disconnected", "inProgress", "pending")
                      if int(counters.attrib.get(key, "0")) != 0]
    successful_terminal_run = run_outcome in ("Passed", "Completed") and not run_errors and not counter_errors
    requirements = dict(re.findall(r"^\|\s*(FR-[0-9]{3})\s*\|\s*(.*?)\s*\|\s*$", spec_bytes.decode("utf-8-sig"), re.M))
    gates = dict(re.findall(r"^\|\s*((?:UT|CT|IT|PERF)-[A-Z0-9-]+)\s*\|\s*(.*?)\s*\|.*$", plan_bytes.decode("utf-8-sig"), re.M))
    if not requirements or not gates:
        raise ValueError("Normative requirement or named test-gate tables are absent")
    unknown = sorted({r for test in declared for r in test["requirements"]} - set(requirements))
    if unknown:
        raise ValueError("Declared tests reference unknown requirements: " + ", ".join(unknown))
    def evidence(selected):
        failed = [r["name"] for r in selected if r["outcome"] != "Passed"]
        passed = [r["name"] for r in selected if r["outcome"] == "Passed"]
        return {"status": "no_executed_test_evidence" if not selected else "failing_or_uncompleted_run_evidence" if not successful_terminal_run else "failing_or_uncompleted_test_evidence" if failed else "passing_test_evidence_completion_unproven",
                "passed": passed, "other_outcomes": failed, "levels": sorted({r["level"] for r in selected})}
    mapped_requirements = [{"id": key, "requirement": description, **evidence([r for r in results if key in r["requirements"]])}
                           for key, description in sorted(requirements.items())]
    mapped_gates = [{"id": key, "definition": description, **evidence([r for r in results if key in r["cases"]])}
                    for key, description in sorted(gates.items())]
    executed_methods = {r["method"] for r in results}
    return {"schema_version": "requirement-test-evidence/v1", "requirement_completion_proven": False,
            "meaning": "Tags identify executed test relationships only. Assertions, full requirements, runtime/platform conditions, calibration, review, and deliverables require separate completion audit.",
            "inputs": {"specification_hash": digest(spec_bytes), "test_plan_hash": digest(plan_bytes), "test_assembly_hash": declarations["assembly_hash"], "trx_hash": digest(trx_bytes)},
            "summary": {"run_outcome": run_outcome, "run_errors": run_errors, "run_counter_errors": counter_errors,
                        "successful_terminal_run": successful_terminal_run,
                        "suite_passed": successful_terminal_run and all(r["outcome"] == "Passed" for r in results),
                        "executed": len(results), "passed": sum(r["outcome"] == "Passed" for r in results), "requirements": len(requirements), "named_gates": len(gates),
                        "requirements_without_executed_evidence": [r["id"] for r in mapped_requirements if not r["passed"]],
                        "named_gates_without_executed_evidence": [r["id"] for r in mapped_gates if not r["passed"]],
                        "unexecuted_declared_methods": sorted(set(lookup) - executed_methods)},
            "requirements": mapped_requirements, "named_gates": mapped_gates, "executed_tests": results}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("spec", "test-plan", "declarations", "trx", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    task_prefix = "d:\\workspaces\\ai\\" if os.name == "nt" else "/mnt/d/Workspaces/AI/"
    output_text = str(output).lower() if os.name == "nt" else str(output)
    if not output_text.startswith(task_prefix) or output.suffix != ".json":
        raise ValueError("Evidence output must be a JSON file beneath the designated AI workspace")
    declarations = json.loads(checked_bytes(args.declarations, 4*1024*1024))
    assembly = checked_bytes(declarations["assembly_path"], 64*1024*1024)
    if digest(assembly) != declarations["assembly_hash"]:
        raise ValueError("Declaration catalog does not match the current test assembly bytes")
    result = audit(checked_bytes(args.spec, 4*1024*1024), checked_bytes(args.test_plan, 4*1024*1024), declarations, checked_bytes(args.trx, 64*1024*1024))
    result["inputs"]["declarations_hash"] = digest(checked_bytes(args.declarations, 4*1024*1024))
    output.parent.mkdir(parents=True, exist_ok=True)
    for ancestor in [output.parent, *output.parents]:
        if ancestor.is_symlink() or (hasattr(ancestor, "is_junction") and ancestor.is_junction()):
            raise ValueError("Evidence output cannot traverse reparse points")
    output.write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    summary = result["summary"]
    print(json.dumps({"run_outcome": summary["run_outcome"], "suite_passed": summary["suite_passed"],
                      "executed": summary["executed"], "passed": summary["passed"], "requirements": summary["requirements"],
                      "named_gates": summary["named_gates"], "requirements_without_executed_evidence": summary["requirements_without_executed_evidence"],
                      "missing_named_gate_count": len(summary["named_gates_without_executed_evidence"]),
                      "unexecuted_method_count": len(summary["unexecuted_declared_methods"]), "completion_proven": False}))

if __name__ == "__main__":
    main()
