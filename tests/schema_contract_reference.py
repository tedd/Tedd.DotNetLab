"""Independent Draft 2020-12 validation of published wire-contract fixtures."""
import argparse
import importlib.metadata
import json
import hashlib
import platform
import random
from pathlib import Path
from jsonschema import Draft202012Validator

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--schemas", required=True)
    parser.add_argument("--cases", required=True)
    args = parser.parse_args()
    cases_path = Path(args.cases)
    if cases_path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("fixture budget exceeded")
    cases = json.loads(cases_path.read_text(encoding="utf-8"))
    if not isinstance(cases, list) or not 1 <= len(cases) <= 1000:
        raise ValueError("fixture count invalid")
    validators = {}
    schemas = {}
    hashes = {}
    for name in ("plan", "measurement", "capability", "comparison", "error"):
        source = Path(args.schemas) / (name + ".schema.json")
        if source.stat().st_size > 1024 * 1024:
            raise ValueError("schema budget exceeded")
        schema = json.loads(source.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        validators[name] = Draft202012Validator(schema)
        schemas[name] = schema
        hashes[name] = hashlib.sha256(source.read_bytes()).hexdigest()
    names = set()
    for case in cases:
        if case["name"] in names:
            raise ValueError("duplicate fixture name")
        names.add(case["name"])
        valid = validators[case["schema"]].is_valid(case["payload"])
        if valid != case["valid"]:
            errors = [error.message for error in validators[case["schema"]].iter_errors(case["payload"])]
            raise ValueError(f"{case['name']}: expected {case['valid']}, observed {valid}; {errors[:3]}")
    rng = random.Random(928164)
    integer_cases = 0
    for name, field, minimum, maximum in (("measurement", "start_ticks", -(2**63), 2**63-1), ("plan", "seed", 0, 2**64-1)):
        scalar = Draft202012Validator(schemas[name]["properties"][field])
        candidates = [str(rng.randrange(-(2**65), 2**65)) for _ in range(500)]
        candidates += [str(minimum + offset) for offset in (-2, -1, 0, 1, 2)]
        candidates += [str(maximum + offset) for offset in (-2, -1, 0, 1, 2)]
        candidates += ["0", "-0", "00", "01", "+1", " 1", "1\n", "١", "1.0", "1e3", "", "1\x00"]
        for candidate in candidates:
            try:
                integer = int(candidate)
                expected = candidate == str(integer) and minimum <= integer <= maximum
            except ValueError:
                expected = False
            if scalar.is_valid(candidate) != expected:
                raise ValueError(f"{name}.{field} range/canonical disagreement: {candidate!r}")
            integer_cases += 1
    print(json.dumps({"validator": "jsonschema", "version": importlib.metadata.version("jsonschema"), "python": platform.python_version(),
                      "draft": "2020-12", "cases": len(cases), "passed": len(cases), "integer_cases": integer_cases,
                      "schema_hashes": hashes, "fixture_hash": hashlib.sha256(cases_path.read_bytes()).hexdigest()}))

if __name__ == "__main__":
    main()
