"""Real stdio MCP exercise against a supplied WildcardMatch checkout.

All generated source, builds, telemetry, and catalogs stay in --scratch.
No source checkout is modified by this driver. Run stages in order.
"""
import argparse
import base64
import contextlib
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import threading
import time
import uuid

p = argparse.ArgumentParser()
p.add_argument("stage", choices=["setup", "explore", "diagnostic", "analyze", "confirm", "native", "extension", "archive"])
p.add_argument("--scratch", type=Path, required=True)
p.add_argument("--source", type=Path, required=True)
p.add_argument("--host", type=Path, required=True)
p.add_argument("--dotnet", type=Path, required=True)
p.add_argument("--sdk-dotnet", type=Path, default=Path(r"C:\Program Files\dotnet\dotnet.exe"))
a = p.parse_args()
root = a.scratch.resolve()
if not str(root).lower().startswith("d:\\workspaces\\ai\\"):
    raise ValueError("Scratch must be a dedicated directory beneath D:\\Workspaces\\AI")
root.mkdir(parents=True, exist_ok=True)
for name in ["tmp", "cli"]: (root / name).mkdir(exist_ok=True)
os.environ.update(TEMP=str(root / "tmp"), TMP=str(root / "tmp"), DOTNET_CLI_HOME=str(root / "cli"),
                  DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
                  DOTNET_GENERATE_ASPNET_CERTIFICATE="false")
catalog = root / "catalog"
events = root / "transcript.jsonl"

def save(name, value):
    (root / (name + ".json")).write_text(json.dumps(value, indent=2), encoding="utf-8")

def read(name):
    return json.loads((root / (name + ".json")).read_text(encoding="utf-8"))

def log(kind, value):
    with events.open("a", encoding="utf-8") as f:
        f.write(json.dumps({"time": time.time(), "kind": kind, "value": value}) + "\n")

def cli(*args, payload=None):
    result = subprocess.run([str(a.dotnet), str(a.host), *map(str, args)],
                            input=None if payload is None else json.dumps(payload),
                            text=True, capture_output=True, cwd=root, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    value = json.loads(result.stdout)
    log("cli", {"args": list(map(str, args)), "result": value})
    return value

class Mcp:
    def __init__(self, extension=False):
        self.extension = extension
    def __enter__(self):
        self.err = (root / ("mcp-" + uuid.uuid4().hex + ".stderr")).open("w", encoding="utf-8")
        command = [str(a.dotnet), str(a.host), "serve", str(catalog)]
        if self.extension: command.append("--enable-native-extension")
        self.proc = subprocess.Popen(command,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.err, text=True, cwd=root,
            creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()
        def reader():
            for line in self.proc.stdout: self.lines.put(line)
            self.lines.put(None)
        threading.Thread(target=reader, daemon=True).start()
        self.serial = 0
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                               "clientInfo": {"name": "wildcard-validation", "version": "1.0"}})
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.proc.stdin.flush()
        advertised = self.rpc("tools/list", {})
        save("schemas-extension" if self.extension else "schemas", advertised)
        expected = ["lab.compare", "lab.discover", "lab.experiment", "lab.inspect", "lab.query", "lab.run"]
        if self.extension: expected.append("lab.patch")
        assert sorted(t["name"] for t in advertised["tools"]) == sorted(expected)
        return self
    def rpc(self, method, params):
        self.serial += 1
        msg = {"jsonrpc": "2.0", "id": self.serial, "method": method, "params": params}
        log("request", msg)
        self.proc.stdin.write(json.dumps(msg) + "\n"); self.proc.stdin.flush()
        deadline = time.monotonic() + 90
        while True:
            line = self.lines.get(timeout=max(.1, deadline-time.monotonic()))
            if line is None: raise RuntimeError("MCP stdout closed")
            value = json.loads(line); log("response", value)
            if value.get("id") == self.serial:
                if "error" in value: raise RuntimeError(value["error"])
                return value["result"]
    def call(self, tool, request):
        response = self.rpc("tools/call", {"name": tool, "arguments": {"request": request}})
        value = json.loads(response["content"][0]["text"])
        if response.get("isError") or value["status"] != "ok": raise RuntimeError(value)
        data = value["data"]
        if isinstance(data, dict) and data.get("representation") == "artifact":
            raw = self.artifact(data["artifact_id"])
            assert len(raw) == int(data["artifact_length"])
            assert hashlib.sha256(raw).hexdigest() == data["artifact_hash"]
            retained = json.loads(raw)
            data = retained["data"] if data["result_kind"] == "lab_response" else retained
        return data
    def artifact(self, identity):
        manifest = self.call("lab.inspect", {"action": "artifact", "artifact_id": identity})
        result = bytearray()
        while len(result) < int(manifest["length"]):
            part = self.call("lab.query", {"view": "artifact_slice", "artifact_id": identity, "offset": str(len(result)), "count": 16000})
            result.extend(base64.b64decode(part["data"]))
        assert len(result) == int(manifest["length"])
        assert hashlib.sha256(result).hexdigest() == manifest["hash"]
        return bytes(result)
    def poll(self, operation):
        while operation["state"] in ["accepted", "running", "cancellation_requested"]:
            time.sleep(1)
            operation = self.call("lab.run", {"action": "status", "operation_id": operation["id"]})
        if operation["state"] != "completed": raise RuntimeError(operation)
        return operation["result_id"]
    def record(self, identity):
        result = self.call("lab.inspect", {"action": "record", "id": identity})
        return json.loads(result["json"]) if isinstance(result, dict) and "json" in result else result
    def __exit__(self, *_):
        self.proc.stdin.close()
        try: self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill(); self.proc.wait(); raise
        self.err.close()
        if self.proc.returncode: raise RuntimeError("MCP shutdown failed")

@contextlib.contextmanager
def target(build="exploratory", diagnostic=False):
    env = os.environ.copy()
    for key in list(env):
        if key.startswith(("COR_", "CORECLR_", "COMPlus_", "DOTNET_")): del env[key]
    env["DOTNET_EnableDiagnostics"] = "1"
    env["DOTNETLAB_DISPOSABLE"] = "1"
    env["DOTNETLAB_WORKSPACE"] = str(catalog)
    process = subprocess.Popen([str(a.dotnet), str(root / build / "bin" / "WildcardRig.dll"), "--listen", str(catalog)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, cwd=root, env=env,
        creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        line = process.stdout.readline()
        if not line: raise RuntimeError(process.stderr.read())
        identity = json.loads(line); log("target", identity)
        yield identity
    finally:
        process.stdin.close()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait(); raise
        if process.returncode: raise RuntimeError(process.stderr.read())

def setup():
    if (root / "setup.json").exists(): raise ValueError("Setup already exists; preserve frozen sources")
    files = sorted((a.source / "src" / "Tedd.WildcardMatch").glob("*.cs"))
    revision = subprocess.check_output(["git", "-C", str(a.source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(a.source), "status", "--porcelain"], text=True)
    assert not dirty, "Require a clean source baseline"
    old = "if (!_singleline && input.Slice(position, candidate - position).IndexOf('\\n') >= 0) return false;"
    new = "if (!_singleline && input.Slice(checkedUntil, candidate - checkedUntil).IndexOf('\\n') >= 0) return false;\n                checkedUntil = candidate;"
    for build in ["baseline", "candidate", "exploratory"]:
        directory = root / build; directory.mkdir(exist_ok=True)
        for source in files:
            content = source.read_text(encoding="utf-8-sig")
            if build != "baseline" and source.name == "CompiledSegments.cs":
                assert content.count(old) == 1
                content = content.replace("int candidate = position;", "int candidate = position;\n            int checkedUntil = position;").replace(old, new)
            (directory / source.name).write_text(content, encoding="utf-8")
            if build == "exploratory":
                # Compile both frozen implementations into the measured entry assembly.
                original = source.read_text(encoding="utf-8-sig").replace("namespace Tedd;", "namespace Control;")
                original = original.replace("[assembly: CLSCompliant(true)]", "")
                (directory / ("Control-" + source.name)).write_text(original, encoding="utf-8")
        shutil.copy2(Path(__file__).with_name("Workload.cs"), directory / "Workload.cs")
        references = "".join(f'<Reference Include="{name}"><HintPath>{a.host.parent / (name + ".dll")}</HintPath></Reference>'
            for name in ["Tedd.DotNetLab.Harness", "Tedd.DotNetLab.Contracts", "Tedd.DotNetLab.Transport.Local"])
        (directory / "WildcardRig.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><RuntimeFrameworkVersion>10.0.12</RuntimeFrameworkVersion>
<RollForward>Disable</RollForward><AssemblyName>WildcardRig</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks><EnableNETAnalyzers>false</EnableNETAnalyzers><UseAppHost>false</UseAppHost>
<DefineConstants>{'EXPLORATORY' if build == 'exploratory' else ''}</DefineConstants>
<OutputPath>bin/</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup><ItemGroup>{references}</ItemGroup></Project>''', encoding="utf-8")
        (directory / "global.json").write_text('{"sdk":{"version":"10.0.402","rollForward":"disable"}}')
        result = subprocess.run([str(a.sdk_dotnet), "build", "-c", "Release", "--nologo"], cwd=directory, capture_output=True, text=True)
        (root / (build + "-build.log")).write_text(result.stdout + result.stderr, encoding="utf-8")
        if result.returncode: raise RuntimeError(result.stdout + result.stderr)
    import difflib
    before = (root / "baseline" / "CompiledSegments.cs").read_text().splitlines(True)
    after = (root / "candidate" / "CompiledSegments.cs").read_text().splitlines(True)
    (root / "candidate.patch").write_text("".join(difflib.unified_diff(before, after, fromfile="a/src/Tedd.WildcardMatch/CompiledSegments.cs", tofile="b/src/Tedd.WildcardMatch/CompiledSegments.cs")))
    save("setup", {"revision": revision, "source": str(a.source), "source_hashes": {s.name: hashlib.sha256(s.read_bytes()).hexdigest() for s in files},
        "protocol": {"blocks": 10, "seed": "20261015", "independent_blocks": False, "shapes": {"dense-4096": 512, "dense-16384": 512, "ordinary": 300000, "newline": 1000},
        "confirmation": "10 fresh paired process sessions; same four-input corpus; useful improvement 1%; ordinary/newline regression guardrail 5%; no failed-session replacement", "exclusions": "none"}})
    with Mcp() as m:
        save("workspace", m.call("lab.discover", {"action": "workspace"}))
        save("profiles", m.call("lab.discover", {"action": "profiles"}))
        save("compatibility", m.call("lab.discover", {"action": "compatibility"}))
    print("Setup, builds, real MCP handshake, six advertised schemas, workspace, profiles, compatibility: passed", flush=True)

def explore(diagnostic=False):
    with target(diagnostic=diagnostic) as identity:
        grant = cli("grant", catalog, identity["process_id"], "execute_registered_scenario", 1200, 100)
        observation = cli("grant", catalog, identity["process_id"], "observe", 1200, 10) if diagnostic else None
        with Mcp() as m:
            save("capabilities-" + str(diagnostic), m.call("lab.discover", {"action": "capabilities", "target_id": identity["id"]}))
            scenarios = m.call("lab.discover", {"action": "scenarios", "target_id": identity["id"]})
            save("scenarios", scenarios)
            scenarios = scenarios["scenarios"]
            print("Scenario discovery: " + json.dumps(scenarios)[:400], flush=True)
            results = []
            for shape, count in ({"dense-16384": 2048} if diagnostic else read("setup")["protocol"]["shapes"]).items():
                scenario = next(s for s in scenarios if s["id"] == "wildcard-" + shape)
                plan = m.call("lab.experiment", {"action": "create_plan", "scenario_id": scenario["id"], "scenario_version": scenario["version"],
                    "input_hash": scenario["input_hash"], "variants": ["baseline", "candidate"], "blocks": 1 if diagnostic else 10,
                    "operations_per_run": count, "warmup_batches": 2, "seed": "20261015", "profile": "cpu-diagnostic" if diagnostic else "quiet-validation",
                    "independent_blocks": False, "confirmatory": False, "maximum_duration_seconds": 300})
                request = {"action": "start", "target_id": identity["id"], "expected_target_epoch": identity["epoch"], "plan_id": plan["id"], "grant_id": grant["id"], "idempotency_key": "wildcard-" + uuid.uuid4().hex}
                if observation: request["observation_grant_id"] = observation["id"]
                operation = m.call("lab.run", request); result = m.poll(operation)
                execution = m.record(result)
                save(("diagnostic-" if diagnostic else "execution-") + shape, execution)
                runs = [m.record(identity) for identity in execution["run_ids"]]
                save(("diagnostic-runs-" if diagnostic else "runs-") + shape, runs)
                comparison = m.call("lab.compare", {"execution_id": result, "include_objectives": True})
                save(("diagnostic-comparison-" if diagnostic else "comparison-") + shape, comparison)
                results.append({"shape": shape, "execution_id": result, "operation_id": operation["id"], "comparison": comparison})
                print(shape + ": " + json.dumps(comparison)[:1000], flush=True)
            save("diagnostic" if diagnostic else "exploration", results)

def confirm():
    # Importing bytes uses the supported CLI; never opens a second catalog writer.
    bundles = {}
    for build in ["baseline", "candidate"]:
        files = []
        for path in sorted((root / build / "bin").iterdir()):
            if path.suffix == ".dll" or path.name.endswith((".deps.json", ".runtimeconfig.json")):
                artifact = cli("import", catalog, path, "application/vnd.microsoft.portable-executable" if path.suffix == ".dll" else "application/json")
                files.append({"name": path.name, "artifact_id": artifact["id"]})
        bundles[build] = files
    source = cli("import", catalog, root / "candidate.patch", "text/x-diff")
    with Mcp() as m:
        builds = {name: m.call("lab.experiment", {"action": "register_normal_build", "entry_assembly": "WildcardRig.dll", "files": files}) for name, files in bundles.items()}
        hypothesis = m.call("lab.experiment", {"action": "create_hypothesis", "mechanism": "A01: advance the newline-validation frontier instead of rescanning the same prefix after every rejected anchor.",
            "prediction": "Dense-anchor inputs improve with length; ordinary and newline inputs remain within the frozen 5% regression bound.", "evidence_ids": [r["execution_id"] for r in read("exploration")]})
        candidate = m.call("lab.experiment", {"action": "create_candidate", "name": "Incremental newline frontier", "build_identity": builds["candidate"]["content_hash"], "components": []})
        plans = []
        for scenario in read("scenarios")["scenarios"]:
            shape = scenario["id"].removeprefix("wildcard-")
            experiment = {"id": "", "scenario_id": scenario["id"], "scenario_version": scenario["version"], "input_hash": scenario["input_hash"],
                "variants": ["baseline", "candidate"], "blocks": 10, "operations_per_run": read("setup")["protocol"]["shapes"][shape],
                "warmup_batches": 2, "seed": "20261015", "profile": "quiet-validation", "independent_blocks": True,
                "independence_justification": "Each randomized paired block uses two fresh sequential processes with identical dependencies and runtime. Fresh processes reset managed state and PGO; no workload overlap or adaptive exclusions. OS load and thermal drift remain uncontrolled; process separation alone does not prove stochastic independence.",
                "confirmatory": True, "fresh_normal_build": True, "maximum_duration_seconds": 600,
                "analysis": {"alpha_family": .05, "family_size": 4, "equivalence_lower_ratio": .99 if shape.startswith("dense") else .95,
                    "equivalence_upper_ratio": 1.01 if shape.startswith("dense") else 1.05,
                    "useful_improvement_ratio": .99 if shape.startswith("dense") else .95,
                    "meaningful_regression_ratio": 1.01 if shape.startswith("dense") else 1.05, "minimum_blocks": 10}}
            plan = m.call("lab.experiment", {"action": "create_confirmation_plan", "selected_candidate_id": candidate["id"], "source_patch_artifact_id": source["id"],
                "source_equivalence_rationale": "For each segment, checkedUntil increases monotonically from position to candidate; every consumed star interval is checked exactly once, with unchanged half-open bounds. Regex oracles validate string and span results. Both complete source snapshots are compiled into entry assemblies with identical harness dependencies.",
                "baseline_build_id": builds["baseline"]["id"], "candidate_build_id": builds["candidate"]["id"], "baseline_variant": "baseline", "candidate_variant": "candidate",
                "experiment": experiment, "exploratory_evidence_ids": [r["execution_id"] for r in read("exploration")]})
            plans.append({"shape": shape, "plan": plan})
        save("confirmation-plans", plans)
    grants = {v["plan"]["id"]: cli("grant-confirmation", catalog, v["plan"]["id"], 3600, 20) for v in plans}
    with Mcp() as m:
        results = []
        for item in plans:
            plan = item["plan"]
            operation = m.call("lab.run", {"action": "start_confirmation", "plan_id": plan["id"], "grant_id": grants[plan["id"]]["id"], "idempotency_key": "wildcard-confirm-" + plan["id"]})
            execution_id = m.poll(operation)
            execution = m.record(execution_id)
            sessions = [m.record(identity) for identity in execution["session_ids"]]
            comparison = m.call("lab.compare", {"execution_id": execution_id, "normal_build_confirmation": True})
            result = {"shape": item["shape"], "execution": execution, "sessions": sessions, "decision": comparison,
                      "comparison": m.record(comparison["comparison_id"])}
            save("confirmation-" + item["shape"], result); results.append(result)
            print("Confirmation " + item["shape"] + ": " + json.dumps(comparison), flush=True)
        save("confirmation", results)

def analyze():
    with Mcp() as m:
        for item in read("exploration"):
            execution = m.record(item["execution_id"])
            runs = [m.record(identity) for identity in execution["run_ids"]]
            save("runs-" + item["shape"], runs)
            baseline = m.call("lab.experiment", {"action": "create_baseline", "run_ids": [r["id"] for r in runs if r["measurement"]["variant"] == "baseline"], "role": "historical_anchor"})
            alias = m.call("lab.experiment", {"action": "set_baseline_alias", "name": "wildcard-" + item["shape"], "baseline_id": baseline["id"]})
            save("baseline-" + item["shape"], {"baseline": baseline, "alias": alias})
        execution = read("diagnostic-dense-16384")
        capture = execution["capture_id"]
        save("capture", m.record(capture))
        save("availability", m.call("lab.query", {"view": "available_evidence", "capture_id": capture}))
        analysis = execution.get("analysis_id")
        if not analysis:
            analysis = m.poll(m.call("lab.run", {"action": "analyze_capture", "capture_id": capture, "idempotency_key": "wildcard-runtime-analysis"}))
        save("runtime-analysis", m.record(analysis))
        for view in ["gc_timeline", "counters", "suspensions"]:
            save(view, m.call("lab.query", {"view": view, "analysis_id": analysis, "limit": 100}))
        save("run-windows", [m.call("lab.query", {"view": "run_window", "analysis_id": analysis, "run_id": r}) for r in execution["run_ids"]])
        operation = m.call("lab.run", {"action": "analyze_stacks", "capture_id": capture, "idempotency_key": "wildcard-stacks"})
        stacks = m.poll(operation)
        save("stack-analysis", m.record(stacks))
        for ranking in ["inclusive", "self"]:
            save("hotspots-" + ranking, m.call("lab.query", {"view": "hotspots", "result_id": stacks, "ranking": ranking, "limit": 20}))
        save("controller-resources", m.call("lab.query", {"view": "controller_resources"}))
    print("Historical baselines/aliases, capture evidence, runtime/GC/counters, run windows and sampled hotspots: passed", flush=True)

def native():
    image = cli("import", catalog, root / "candidate" / "bin" / "WildcardRig.dll", "application/vnd.microsoft.portable-executable")
    with target("candidate", diagnostic=True) as identity:
        execute = cli("grant", catalog, identity["process_id"], "execute_registered_scenario", 300, 4)
        with Mcp() as m:
            scenarios = m.call("lab.discover", {"action": "scenarios", "target_id": identity["id"]})
            scenario = next(s for s in scenarios["scenarios"] if s["id"] == "wildcard-dense-4096")
            plan = m.call("lab.experiment", {"action": "create_individual_latency_plan", "scenario_id": scenario["id"], "scenario_version": scenario["version"],
                "input_hash": scenario["input_hash"], "variant": "candidate", "operations": 64, "warmup_batches": 1, "maximum_duration_seconds": 60})
            result = m.poll(m.call("lab.run", {"action": "individual_latencies", "target_id": identity["id"], "expected_target_epoch": "0", "plan_id": plan["id"], "grant_id": execute["id"], "idempotency_key": "wildcard-individual"}))
            save("individual-result", m.record(result))
            save("individual-latencies", m.call("lab.query", {"view": "individual_latency_slice", "run_id": result, "count": 64}))
            methods = m.call("lab.inspect", {"action": "methods", "artifact_id": image["id"], "filter": "Tedd.CompiledSegments.Window", "limit": 10})
            save("methods", methods)
            method = methods["rows"][0]
            save("method-il", m.call("lab.inspect", {"action": "il", "artifact_id": image["id"], "method_token": method["token"]}))
            attachment = m.call("lab.experiment", {"action": "prepare_profiler_attachment", "target_id": identity["id"], "expected_target_epoch": "0"})
            save("attachment-plan", attachment)
        grant = cli("grant-profiler-attach", catalog, attachment["id"], 120, 1)
        with Mcp() as m:
            operation = m.call("lab.run", {"action": "attach_profiler", "plan_id": attachment["id"], "grant_id": grant["id"], "idempotency_key": "wildcard-native-attachment"})
            outcome = m.record(m.poll(operation)); save("attachment", outcome)
            save("profiler", m.call("lab.discover", {"action": "profiler", "target_id": identity["id"]}))
        inspect = cli("grant", catalog, identity["process_id"], "inspect_live_code", 120, 2)
        with Mcp() as m:
            operation = m.call("lab.inspect", {"action": "native", "target_id": identity["id"], "method_id": method["id"], "expected_target_epoch": "0", "grant_id": inspect["id"]})
            snapshot = m.record(m.poll(operation)); save("native-inspection", snapshot)
        print("Individual latency, offline method/IL, profiler attachment and native inspection: passed", flush=True)

def archive():
    # Retain complete exported evidence before deleting the disposable catalog.
    with Mcp() as m:
        inspection = read("native-inspection")
        save("native-listings", [m.call("lab.inspect", {"action": "offline_native", "artifact_id": identity}) for identity in inspection["artifact_ids"]])
        decisions = []
        for item in read("confirmation"):
            outcome = item["comparison"]
            accepted = outcome["effect_class"] in ["improved", "equivalent_within_margin"]
            decisions.append(m.call("lab.experiment", {"action": "record_decision", "comparison_id": outcome["id"], "decision": "retain" if accepted else "inconclusive",
                "rationale": "Dense-input primary evidence and ordinary/newline guardrails are assessed jointly; equivalent ordinary cost satisfies its frozen guardrail, without claiming an ordinary-input improvement."}))
        save("manual-decisions", decisions)
        backup_id = m.poll(m.call("lab.run", {"action": "backup_catalog", "idempotency_key": "wildcard-final-backup"}))
        save("catalog-backup", m.call("lab.query", {"view": "verify_catalog_backup", "backup_id": backup_id}))
        records = []
        for kind in ["hypothesis", "candidate", "plan", "execution", "run", "comparison", "baseline", "baseline_alias", "confirmation_plan", "confirmation_execution", "normal_build_session", "normal_build", "confirmation_decision", "decision", "individual_latency_plan", "individual_latency_run", "capture", "analysis", "stack_analysis", "objective_vector", "execution_window"]:
            cursor = None
            while True:
                request = {"view": "history", "kind": kind, "limit": 100}
                if cursor: request["cursor"] = cursor
                page = m.call("lab.query", request)
                records.extend(r["id"] for r in page["rows"])
                cursor = page.get("next_cursor")
                if cursor is None: break
        records.extend([read("native-inspection")["id"], read("attachment")["id"], read("attachment-plan")["id"]])
        # Include observed artifact identities from actual responses, never guessed paths.
        artifacts = sorted(set(re.findall(r"art-[0-9a-f]{64}", events.read_text(encoding="utf-8"))))
        plan = m.call("lab.experiment", {"action": "prepare_export", "record_ids": sorted(set(records)), "artifact_ids": sorted(set(artifacts)), "privacy_filtered": False,
            "maximum_bundle_bytes": "134217728", "maximum_duration_seconds": 120})
        save("export-plan", plan)
    grant = cli("grant-export", catalog, plan["id"], 180, 1, "--include-raw-traces")
    with Mcp() as m:
        identity = m.poll(m.call("lab.run", {"action": "start_export", "plan_id": plan["id"], "grant_id": grant["id"], "idempotency_key": "wildcard-export"}))
        result = m.record(identity); save("export-result", result)
        save("export-verification", m.call("lab.query", {"view": "verify_export", "result_id": identity}))
        (root / "evidence.zip").write_bytes(m.artifact(result["artifact_id"]))
        # Exercise the bundle import and verification path against the sealed export.
        import_plan = m.call("lab.experiment", {"action": "prepare_bundle_import", "artifact_id": result["artifact_id"], "maximum_decompressed_bytes": "268435456", "maximum_duration_seconds": 120})
        imported = m.poll(m.call("lab.run", {"action": "import_bundle", "plan_id": import_plan["id"], "idempotency_key": "wildcard-reimport"}))
        save("reimport-verification", m.call("lab.query", {"view": "verify_bundle_import", "result_id": imported}))
        save("catalog-checkpoint", m.call("lab.run", {"action": "checkpoint_catalog", "truncate": True}))
    print("Native decoding, decisions, catalog backup, complete evidence export, hash verification, reimport, checkpoint and MCP restart: passed", flush=True)

def extension():
    with Mcp(extension=True) as m:
        try:
            result = m.call("lab.patch", {"action": "prepare_original_control", "inspection_id": read("native-inspection")["id"]})
            save("extension-preparation", {"status": "prepared", "result": result, "executed_mutation": False})
            print("Seventh tool advertised; original-control metadata prepared; no live mutation submitted", flush=True)
        except RuntimeError as error:
            if not error.args or not isinstance(error.args[0], dict): raise
            save("extension-preparation", {"status": "rejected", "error": error.args[0], "executed_mutation": False})
            print("Seventh tool advertised; qualified-subset rejection: " + json.dumps(error.args[0]), flush=True)

if a.stage == "setup": setup()
elif a.stage == "explore": explore()
elif a.stage == "diagnostic": explore(True)
elif a.stage == "confirm": confirm()
elif a.stage == "analyze": analyze()
elif a.stage == "native": native()
elif a.stage == "archive": archive()
elif a.stage == "extension": extension()
