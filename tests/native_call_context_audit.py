"""Check profiler API coverage and, optionally, contracts in pinned runtime sources."""
import argparse
import hashlib
import json
from pathlib import Path
import re

MAXIMUM_BYTES = 2 * 1024 * 1024

def read_bounded(path):
    path = Path(path)
    if path.stat().st_size > MAXIMUM_BYTES:
        raise ValueError("Call-context input exceeds its read budget")
    return path.read_bytes()

def runtime_contract(source, method):
    signature = "HRESULT ProfToEEInterfaceImpl::" + method + "("
    if source.count(signature) != 1:
        raise ValueError("Runtime API definition is absent or ambiguous: " + method)
    start = source.index(signature)
    end = source.find("\nHRESULT ProfToEEInterfaceImpl::", start + 1)
    body = source[start:end if end != -1 else len(source)]
    contract = body[body.index("CONTRACTL"):body.index("CONTRACTL_END")]
    contract = re.sub(r"//[^\n]*", "", contract)
    entry = re.search(r"PROFILER_TO_CLR_ENTRYPOINT_(SYNC|ASYNC)_EX\(\s*([^,]+),", body)
    if entry is None:
        raise ValueError("Runtime API entrypoint requires explicit review: " + method)
    gc = "DISABLED_GC_NOTRIGGER" if re.search(r"DISABLED\(GC_NOTRIGGER\)", contract) else "GC_TRIGGERS" if "GC_TRIGGERS;" in contract else "GC_NOTRIGGER" if "GC_NOTRIGGER;" in contract else None
    if gc is None:
        raise ValueError("Runtime GC contract requires explicit review: " + method)
    return {"entrypoint": entry[1], "flags": sorted(flag.strip() for flag in entry[2].split("|")), "gc_contract": gc}

def audit(matrix, production, sources=None):
    if matrix.get("schema_version") != "native-call-context/v1" or matrix.get("runtime") not in ("10.0.12", "10.0.13", "11.0.0-rc.2.26504.105"):
        raise ValueError("Unqualified call-context matrix")
    entries = matrix["info_apis"]
    lookup = {row["api"]: row for row in entries}
    if not entries or len(lookup) != len(entries):
        raise ValueError("API matrix rows must be unique")
    called = set(re.findall(r"\binfo\s*->\s*(\w+)\s*\(", production)) - {"Release"}
    if called != set(lookup):
        raise ValueError("Unreviewed or stale profiler API rows: " + ",".join(sorted(called ^ set(lookup))))
    controls = set(re.findall(r"\bcontrol\s*->\s*(\w+)\s*\(", production))
    if controls != {row["api"] for row in matrix["function_control_apis"]}:
        raise ValueError("Unreviewed function-control API")
    for row in entries:
        if row["project_context"] not in ("startup_initialize", "startup_or_windows_attach_initialize", "registered_worker") or row["entrypoint"] not in ("SYNC", "ASYNC"):
            raise ValueError("Unqualified project call context")
        if row["project_context"] == "startup_or_windows_attach_initialize" and "kP2EEAllowableAfterAttach" not in row["flags"]:
            raise ValueError("Attach initialization requires a pinned attach-allowable API")
    if sources is not None:
        for pinned in matrix["sources"]:
            if hashlib.sha256(sources[pinned["file"]]).hexdigest() != pinned["sha256"]:
                raise ValueError("Runtime source hash differs: " + pinned["file"])
        runtime = sources["proftoeeinterfaceimpl.cpp"].decode("utf-8")
        for row in entries:
            actual = runtime_contract(runtime, row["api"])
            if actual != {key: row[key] for key in actual}:
                raise ValueError("Runtime entrypoint/GC contract differs: " + row["api"])
        rejit = sources["rejit.cpp"].decode("utf-8")
        start = rejit.index("HRESULT ProfilerFunctionControl::SetILFunctionBody(")
        end = rejit.index("\nHRESULT ProfilerFunctionControl::", start + 1)
        body = rejit[start:end]
        if "GC_NOTRIGGER;" not in body or "memcpy(m_pbIL, pbNewILMethodHeader, cbNewILMethodHeader)" not in body:
            raise ValueError("Function-control copy contract differs")
    return {"runtime": matrix["runtime"], "info_api_count": len(entries), "function_control_api_count": len(controls),
            "source_contracts_verified": sources is not None, "runtime_integration_qualified": False}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-source")
    parser.add_argument("--rejit-source")
    parser.add_argument("--matrix", help="Explicit version-specific call-context matrix")
    args = parser.parse_args()
    if bool(args.runtime_source) != bool(args.rejit_source):
        parser.error("Both pinned runtime source files are required together")
    root = Path(__file__).resolve().parent.parent
    matrix = json.loads(read_bounded(Path(args.matrix) if args.matrix else root / "native/call-context-matrix.json"))
    native = root / "native/Tedd.DotNetLab.Profiler"
    production = "\n".join(read_bounded(native / name).decode("utf-8") for name in ("Profiler.cpp", "LinuxProfiler.cpp", "Inspector.h", "Inlining.h", "Mutation.h"))
    sources = {"proftoeeinterfaceimpl.cpp": read_bounded(args.runtime_source), "rejit.cpp": read_bounded(args.rejit_source)} if args.runtime_source else None
    print(json.dumps(audit(matrix, production, sources)))

if __name__ == "__main__":
    main()
