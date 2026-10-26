"""Preserve a completed exercise in the wildcard repository's research area."""
import argparse
import gzip
import hashlib
import html
import json
from pathlib import Path
import shutil
import statistics
import xml.etree.ElementTree as ET

p = argparse.ArgumentParser()
p.add_argument("scratch", type=Path)
p.add_argument("destination", type=Path)
a = p.parse_args()
a.destination.mkdir(parents=True, exist_ok=True)
evidence = a.destination / "evidence"
evidence.mkdir(exist_ok=True)
read = lambda name: json.loads((a.scratch / (name + ".json")).read_text(encoding="utf-8"))
for source in a.scratch.glob("*.json"):
    shutil.copy2(source, evidence / source.name)
shutil.copy2(a.scratch / "evidence.zip", evidence / "evidence.zip")
shutil.copy2(a.scratch / "tests.log", evidence / "tests.txt")
for source in (a.scratch / "test-results").glob("*.trx"):
    shutil.copy2(source, evidence / source.name)
for build in ["baseline", "candidate", "exploratory"]:
    destination = a.destination / "sources" / build
    destination.mkdir(parents=True, exist_ok=True)
    for source in (a.scratch / build).iterdir():
        if source.is_file() and source.suffix in [".cs", ".csproj", ".json"]:
            shutil.copy2(source, destination / source.name)
shutil.copy2(a.scratch / "candidate.patch", a.destination / "candidate.patch")

# Keep every protocol request and structured reply, replacing bulk artifact-slice
# bytes with their digest. Exported evidence retains those immutable bytes.
with gzip.open(evidence / "protocol-audit.jsonl.gz", "wt", encoding="utf-8") as output:
    for line in (a.scratch / "transcript.jsonl").open(encoding="utf-8"):
        event = json.loads(line)
        reply = event.get("value", {}).get("result") if isinstance(event.get("value"), dict) else None
        if isinstance(reply, dict) and "structuredContent" in reply:
            reply.pop("content", None)
            data = reply["structuredContent"].get("data")
            if isinstance(data, dict) and data.get("encoding") == "base64":
                import base64
                raw = base64.b64decode(data.pop("data"))
                data.update(omitted_slice_bytes=len(raw), omitted_slice_sha256=hashlib.sha256(raw).hexdigest())
        output.write(json.dumps(event, separators=(",", ":")) + "\n")

rows = []
for item in read("confirmation"):
    costs = {}
    for arm in ["baseline", "candidate"]:
        costs[arm] = statistics.geometric_mean(
            (int(s["measurement"]["end_ticks"]) - int(s["measurement"]["start_ticks"])) * 1e9 /
            int(s["measurement"]["frequency"]) / int(s["measurement"]["completed_operations"])
            for s in item["sessions"] if s["variant"] == arm)
    comparison = item["comparison"]
    rows.append({"shape": item["shape"], "cost_ns": costs, "ratio": comparison["ratio"],
                 "interval": comparison["difference_interval_ratio"], "classification": comparison["effect_class"]})
tests = []
for source in (a.scratch / "test-results").glob("*.trx"):
    tree = ET.parse(source)
    counters = tree.find(".//{*}Counters")
    tests.append({"file": source.name, **counters.attrib})
summary = {"results": rows, "tests": tests, "source": read("setup"), "export": read("export-result"),
           "platform": {"cpu": "AMD Ryzen 9 5950X 16-Core Processor", "os": "Windows 10.0.26200 x64",
                        "runtime": "CoreCLR 10.0.12", "sdk": "10.0.402", "dotnetlab_revision": "ab314bd"},
           "retained_mechanism": "Incremental newline frontier; registry A01",
           "limitations": ["One pattern and four synthetic input distributions; no whole-application claim",
               "Uncontrolled host load, affinity, frequency, and thermal state; fresh PIDs alone do not establish independence",
               "Stack sample/conversion caps, unknown loss status, and partial runtime provider coverage",
               "No ReJIT/probe mutation or p99 claim; selected method exceeds the mutation signature subset"]}
(a.destination / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
table = "".join(f'<tr><td>{html.escape(r["shape"])}</td><td>{r["cost_ns"]["baseline"]:,.1f}</td><td>{r["cost_ns"]["candidate"]:,.1f}</td><td>{r["ratio"]:.4f}</td><td>{r["interval"]["lower"]:.4f}–{r["interval"]["upper"]:.4f}</td><td>{html.escape(r["classification"])}</td></tr>' for r in rows)
page = '''<!doctype html><meta charset="utf-8"><title>WildcardMatch: Tedd.DotNetLab validation</title>
<style>body{font:16px/1.55 system-ui;max-width:1100px;margin:48px auto;padding:0 24px;color:#17202a}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:10px;border-bottom:1px solid #ccd1d1}code{background:#f2f4f4;padding:2px 5px}a{color:#145a90}h2{margin-top:36px}</style>
<h1>WildcardMatch: Tedd.DotNetLab validation</h1><p>15 October 2026 · integration branch: <code>main</code> in both repositories</p>
<p>The real stdio MCP workflow functioned across discovery, scenario execution, telemetry, comparisons, native inspection, and retained evidence. One source optimization passed fresh-process confirmation and the complete correctness suite. This establishes the exercised workflow within its qualified subset, not every feature or arbitrary-method automation.</p>
<h2>Measured result</h2><p><code>CompiledSegments.Window</code> previously rescanned the same star-consumed prefix for LF after each rejected literal anchor. A monotonic frontier checks only the newly skipped interval. This removes quadratic work from that newline check; literal-anchor search and candidate validation retain their existing costs.</p>
<table><thead><tr><th>Input</th><th>Baseline ns/call</th><th>Candidate ns/call</th><th>Cost ratio</th><th>98.75% interval</th><th>Classification</th></tr></thead><tbody>TABLE</tbody></table>
<p>Costs are geometric means of ten retained batch means per arm. Ratios are candidate/baseline. Dense workloads improved by 27.4% and 66.4%; ordinary inputs satisfied the frozen ±5% equivalence guardrail, and newline inputs improved. All 80 confirmation sessions reported correct, quiescent results and zero current-thread allocation during timing. Process-wide allocation and controller memory are separate quantities.</p>
<h2>Protocol and applicability</h2><p>Baseline wildcard revision: <code>4f1a8b7442168698a3ee936978d504f2b3a8fe32</code>. Tedd.DotNetLab checkout: <code>ab314bd</code>. CPU: AMD Ryzen 9 5950X. Windows x64 build 26200. SDK 10.0.402; benchmark runtime CoreCLR 10.0.12. Both normal builds embed the selected library source in the entry assembly, with byte-identical managed harness dependencies and runtime settings. This follows the service’s entry-assembly treatment boundary.</p>
<p>Pattern: <code>*</code> + 70 lowercase <code>a</code> units + <code>?b*</code>. Each batch cycles four fixed inputs. Dense shapes contain long repeated anchors with successful and failing suffixes; ordinary and newline distributions guard fast rejection and LF behavior. See <a href="sources/exploratory/Workload.cs">the complete workload and independent Regex oracle</a>.</p>
<p>Exploration: ten randomized paired blocks in one process, explicitly descriptive. Confirmation: ten randomized paired fresh-process blocks for each shape, two warm-up batches, fixed seed 20261015, no exclusions, replacements, or sample expansion. Operations per batch: dense 512, ordinary 300000, newline 1000. Four comparisons share family alpha 0.05, giving local alpha 0.0125. Dense benefit requires ratio below 0.99; ordinary/newline guardrails use ±5%. The ordinary plan’s isolated promotion flag is false because equivalence is not a primary improvement; the combined adoption decision treats it as a guardrail.</p>
<p>Fresh processes reset managed state and PGO. Host load, affinity, thermal state, and frequency were uncontrolled; no exclusive host lock was held. Statistical intervals depend on the declared paired-session assumptions. These results cover the specified matching calls, not construction, arbitrary patterns, other runtimes’ performance, or application throughput.</p>
<h2>Feature evidence</h2><ul>
<li>Actual JSON-RPC initialization, six advertised schemas, workspace identity, profiles and compatibility; repeated clean EOF shutdown/reconnect. The client was the retained Python MCP driver; no permanent Codex client configuration was changed.</li>
<li>Four registered scenarios, application-owned lifecycle and oracle, scoped CLI grants, frozen plans, paired comparisons, historical baselines and revisioned aliases.</li>
<li>CPU-diagnostic EventPipe capture; retained GC/suspensions and System.Runtime counters; target-marker run windows; sampled hotspots and explicit quality flags. Runtime quality remained partial. Stack analysis reached 100000 samples and reported conversion/sample caps and unknown loss status. Inclusive shares overlap and are not call counts or per-call latency.</li>
<li>64 individual-operation observations and bounded slice retrieval. Because ExecuteBatch(1) restarts the local batch index, these observations cover the first dense input; they do not establish p99.</li>
<li>Stored method/IL inspection, content-bound native-profiler attachment, live native acquisition and offline instruction decoding. The retained snapshot explicitly does not prove which code served earlier measurements; offline decoding retains its unverified-origin qualification.</li>
<li>Optional seventh tool advertised. Original-control preparation correctly rejected this instance/span method: the mutation subset requires static Int32 returns and bounded Int32 arguments. No ReJIT or probes were installed. Quantile inference, factorial experiments, and interruption/recovery fault injection were not exercised.</li>
<li>Decisions, catalog backup verification, declared export of 226 records and 111 artifacts, hash verification, bundle reimport, bounded artifact assembly and checkpointing. Reimport preserves the qualification that integrity does not prove origin.</li></ul>
<h2>Correctness and disposition</h2><p>Complete wildcard suites passed: .NET 8: 242; .NET 10: 242; .NET 11: 245. New regressions vary rejected anchors, LF placement, final LF, multi-segment patterns, Singleline and string/span entry points. The .NET 8 suite exercises the .NET Standard fallback. Existing analyzer and legacy test-package warnings remain in the test transcript.</p>
<p>Retain the incremental frontier. No conclusion is drawn about overall optimality: anchor search and segment validation remain possible adjacent costs. SIMD changes, parallelism and offload were not pursued because this investigation targeted repeated logical work without changing representation or concurrency. The optimization and research record are integrated into the original wildcard <code>main</code>; the reusable MCP exerciser is integrated into Tedd.DotNetLab <code>main</code>. No remote push or publication.</p>
<h2>Retained evidence</h2><ul><li><a href="summary.json">Summary, policies, identities and test counts</a></li><li><a href="candidate.patch">Measured source delta</a> and <a href="sources/candidate/CompiledSegments.cs">candidate source snapshot</a></li><li><a href="evidence/evidence.zip">Verified explicit evidence export, including synthetic trace and build bytes</a></li><li><a href="evidence/confirmation.json">All confirmation sessions and comparisons</a></li><li><a href="evidence/exploration.json">All exploratory comparisons</a></li><li><a href="evidence/stack-analysis.json">Stack quality</a>, <a href="evidence/runtime-analysis.json">runtime quality</a>, <a href="evidence/run-windows.json">run windows</a></li><li><a href="evidence/extension-preparation.json">Mutation qualification rejection</a></li><li><a href="evidence/tests.txt">Complete correctness build/test transcript</a></li><li><a href="evidence/protocol-audit.jsonl.gz">Protocol audit</a>: every request and structured response; duplicate text blocks removed and bulk slice bytes replaced by length/SHA-256. Immutable bytes are retained in the verified export.</li></ul>
<p>All mutable SQLite state and generated builds used the task workspace. Required evidence was preserved here before task-owned processes stopped and the disposable workspace was removed. No production database or external telemetry backend was involved.</p>'''.replace("TABLE", table)
(a.destination / "report.html").write_text(page, encoding="utf-8")
manifest = {path.relative_to(a.destination).as_posix(): {"length": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in sorted(a.destination.rglob("*")) if path.is_file() and path.name != "manifest.json"}
(a.destination / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(f"Preserved {len(manifest)} files; export SHA-256 {manifest['evidence/evidence.zip']['sha256']}")
