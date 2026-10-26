"""Build and verify a bounded, reproducible local Windows distribution.

Hashes detect corruption; the caller must obtain the archive hash independently.
No network access, extraction, application execution, or database access occurs.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import xml.etree.ElementTree as ET
import zipfile
import io

SCHEMA = "windows-package/v1"
VERSION = "1.0.0"
MAX_FILE = 64 * 1024 * 1024
MAX_TOTAL = 512 * 1024 * 1024
MAX_ENTRIES = 4096
MANIFEST = "package-manifest.json"
REQUIRED = {
    "Tedd.DotNetLab.Host.dll", "Tedd.DotNetLab.Host.deps.json", "Tedd.DotNetLab.Host.runtimeconfig.json",
    "Tedd.DotNetLab.TraceWorker.dll", "Tedd.DotNetLab.TraceWorker.deps.json",
    "Tedd.DotNetLab.TraceWorker.runtimeconfig.json", "Tedd.DotNetLab.Profiler.dll",
    "runtimes/win-x64/native/e_sqlite3.dll", "LICENSE", "THIRD-PARTY-NOTICES.json",
    "locks/host-packages.lock.json", "global.json",
}


def safe_name(name):
    if not isinstance(name, str) or not name or len(name) > 240 or "\\" in name:
        raise ValueError("Invalid package path")
    parts = name.split("/")
    if PurePosixPath(name).is_absolute() or any(
        p in ("", ".", "..") or p[-1:] in (" ", ".") or
        re.search(r'[\x00-\x1f<>:"|?*]', p) or
        p.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *[f"COM{i}" for i in range(1, 10)], *[f"LPT{i}" for i in range(1, 10)]}
        for p in parts
    ):
        raise ValueError("Invalid package path")
    return name


def regular(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Package paths cannot traverse links or reparse points")
    return path


def read(path, maximum=MAX_FILE):
    path = regular(path)
    if not path.is_file() or path.stat().st_size > maximum:
        raise ValueError("File exceeds its bounded read budget")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("File exceeds its bounded read budget")
    return data


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON member")
        result[key] = value
    return result


def load(data):
    return json.loads(data, object_pairs_hook=unique_object)


def encode(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n").encode("utf-8")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def add(files, name, data):
    name = safe_name(name)
    if name in files or len(data) > MAX_FILE or len(files) >= MAX_ENTRIES or sum(map(len, files.values())) + len(data) > MAX_TOTAL:
        raise ValueError("Package inventory exceeds its bound or contains a duplicate")
    files[name] = data


def amd64_native(data):
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError("Missing native Windows PE image")
    offset = int.from_bytes(data[60:64], "little")
    if offset > len(data) - 24 or data[offset:offset + 4] != b"PE\0\0" or data[offset + 4:offset + 6] != b"\x64\x86":
        raise ValueError("Native image must be Windows AMD64")
    optional = offset + 24
    optional_size = int.from_bytes(data[offset + 20:offset + 22], "little")
    if optional_size < 240 or optional + optional_size > len(data) or data[optional:optional + 2] != b"\x0b\x02" or not int.from_bytes(data[offset + 22:offset + 24], "little") & 0x2000:
        raise ValueError("Native image must be an AMD64 PE32+ DLL")
    clr = optional + 112 + 14 * 8
    if any(data[clr:clr + 8]):
        raise ValueError("Native image cannot contain a managed CLR header")


def check_runtime(files):
    for name in ("Tedd.DotNetLab.Host.runtimeconfig.json", "Tedd.DotNetLab.TraceWorker.runtimeconfig.json"):
        runtime = load(files[name])["runtimeOptions"]
        allowed_rf = ("Major", "LatestMajor") if name == "Tedd.DotNetLab.Host.runtimeconfig.json" else ("Major", "LatestMajor", "LatestPatch", "Disable")
        fw = runtime.get("framework")
        if runtime.get("rollForward") not in allowed_rf or not isinstance(fw, dict) or fw.get("name") != "Microsoft.NETCore.App" or not valid_runtime_version(fw.get("version")):
            raise ValueError("Package runtime configuration differs from the supported runtime contract")
    sdk_cfg = load(files["global.json"]).get("sdk")
    if not isinstance(sdk_cfg, dict) or set(sdk_cfg) != {"version", "rollForward", "allowPrerelease"} or not valid_sdk(sdk_cfg.get("version")) or sdk_cfg.get("rollForward") not in (
        "disable", "patch", "feature", "minor", "major", "latestPatch", "latestFeature", "latestMinor", "latestMajor"
    ) or not isinstance(sdk_cfg.get("allowPrerelease"), bool):
        raise ValueError("Package SDK configuration differs from the supported SDK contract")
    amd64_native(files["Tedd.DotNetLab.Profiler.dll"])
    amd64_native(files["runtimes/win-x64/native/e_sqlite3.dll"])


def check_signing(signing, binaries):
    if not isinstance(signing, dict) or set(signing) != set(binaries):
        raise ValueError("Signing observations do not cover the packaged binaries")
    for name, observation in signing.items():
        if not isinstance(observation, dict) or observation.get("sha256") != binaries[name] or observation.get("status") not in (
            "Valid", "NotSigned", "NotTrusted", "UnknownError", "NotSupportedFileFormat", "Incompatible"
        ):
            raise ValueError("Signing observations differ from the package inventory")
        thumbprint = observation.get("signer_thumbprint")
        if thumbprint is not None and (not isinstance(thumbprint, str) or not re.fullmatch(r"[0-9A-Fa-f]{40}", thumbprint)):
            raise ValueError("Invalid signer certificate identity")
        if observation["status"] == "Valid" and thumbprint is None:
            raise ValueError("A valid signature requires a signer certificate identity")


def dependency_notices(repo, assets, files, observed_hashes):
    lock_bytes = read(repo / "src/Tedd.DotNetLab.Host/packages.lock.json", 1024 * 1024)
    lock = load(lock_bytes)
    if lock.get("version") != 2 or set(lock["dependencies"]) != {"net10.0"}:
        raise ValueError("Unexpected dependency lock target")
    locked = {k.lower(): v for k, v in lock["dependencies"]["net10.0"].items() if v["type"].lower() != "project"}
    inventory = []
    libraries = [(key, value) for key, value in assets["libraries"].items() if value["type"] == "package"]
    if not 1 <= len(libraries) <= 256:
        raise ValueError("Dependency inventory exceeds its bound")
    for key, item in sorted(libraries):
        package, version = key.split("/")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", package) or not re.fullmatch(r"[A-Za-z0-9_.+-]+", version):
            raise ValueError("Invalid dependency identity")
        pin = locked.get(package.lower())
        if pin is None or pin["resolved"] != version or item.get("sha512") != pin["contentHash"]:
            raise ValueError("Assets differ from the committed dependency lock")
        relative = safe_name(item["path"])
        if relative.lower() != f"{package.lower()}/{version.lower()}":
            raise ValueError("Unexpected dependency cache path")
        filename = f"{package.lower()}.{version.lower()}.nupkg"
        candidates = [Path(root) / relative / filename for root in assets["packageFolders"]]
        archives = [path for path in candidates if path.is_file()]
        if not archives:
            raise ValueError("Locked NuGet archive is absent from the local cache")
        archive = read(archives[0])
        archive_hash = base64.b64encode(hashlib.sha512(archive).digest()).decode()
        with zipfile.ZipFile(io.BytesIO(archive)) as source:
            signed = ".signature.p7s" in source.namelist()
            observation = observed_hashes.get(key) if observed_hashes is not None else None
            if observation is not None:
                if observation.get("integrity_verified") is not True or observation.get("signed") is not signed or observation.get("archive_sha512") != archive_hash or observation.get("content_sha512") != pin["contentHash"]:
                    raise ValueError("NuGet archive differs from SDK-verified content and archive hashes")
            elif signed or archive_hash != pin["contentHash"]:
                raise ValueError("NuGet archive does not match the committed content hash; signed packages require SDK integrity verification")
            entries = source.infolist()
            if len(entries) > MAX_ENTRIES:
                raise ValueError("NuGet archive entry bound exceeded")
            nuspecs = [e for e in entries if e.filename.endswith(".nuspec")]
            if len(nuspecs) != 1 or nuspecs[0].file_size > 1024 * 1024:
                raise ValueError("Ambiguous NuGet metadata")
            nuspec = source.read(nuspecs[0])
            if b"<!DOCTYPE" in nuspec.upper() or b"<!ENTITY" in nuspec.upper():
                raise ValueError("NuGet metadata cannot declare entities")
            metadata = next(e for e in ET.fromstring(nuspec).iter() if e.tag.split("}")[-1] == "metadata")
            fields = {e.tag.split("}")[-1]: e for e in metadata}
            if fields["id"].text.lower() != package.lower() or fields["version"].text != version or "license" not in fields:
                raise ValueError("NuGet license/identity metadata is absent or mismatched")
            license_node = fields["license"]
            if not license_node.text or (license_node.get("type") == "file" and license_node.text not in source.namelist()):
                raise ValueError("Missing dependency license")
            prefix = f"notices/nuget/{package}/{version}"
            add(files, f"{prefix}/package.nuspec", nuspec)
            notices = []
            for entry in entries:
                leaf = entry.filename.split("/")[-1].lower()
                requested = license_node.get("type") == "file" and entry.filename == license_node.text
                if entry.is_dir() or not (requested or re.search(r"license|notice|copyright", leaf)):
                    continue
                path = safe_name(entry.filename)
                if entry.file_size > 1024 * 1024:
                    raise ValueError("Dependency notice exceeds its bounded read budget")
                destination = safe_name(f"{prefix}/{path}")
                if destination in files:
                    raise ValueError("Duplicate dependency notice")
                add(files, destination, source.read(entry))
                notices.append(destination)
            if license_node.get("type") not in ("expression", "file") or (license_node.get("type") == "file" and not notices):
                raise ValueError("Unsupported or missing dependency license")
            inventory.append({"id": package, "version": version, "content_sha512": pin["contentHash"],
                              "archive_sha512": archive_hash, "signed": signed,
                              "license_type": license_node.get("type"), "license": license_node.text,
                              "authors": fields["authors"].text if "authors" in fields else None,
                              "copyright": fields["copyright"].text if "copyright" in fields else None,
                              "metadata": f"{prefix}/package.nuspec", "embedded_notices": notices})
    if {p["id"].lower() for p in inventory} != set(locked):
        raise ValueError("Package inventory is not the complete dependency lock")
    if observed_hashes is not None and set(observed_hashes) != {key for key, _ in libraries}:
        raise ValueError("SDK hash inventory is not the complete dependency inventory")
    add(files, "locks/host-packages.lock.json", lock_bytes)
    vendors = []
    for name, path in (("CoreCLR headers", "native/vendor/coreclr/LICENSE.TXT"), ("nlohmann/json", "native/vendor/nlohmann/LICENSE.MIT")):
        content = read(repo / path, 1024 * 1024)
        destination = "notices/" + path
        add(files, destination, content)
        vendors.append({"name": name, "license_file": destination, "sha256": sha(content)})
    add(files, "THIRD-PARTY-NOTICES.json", encode({"schema_version": "third-party-notices/v1", "dependencies": inventory,
        "native_sources": vendors, "meaning": "Original locked NuGet metadata and embedded notices; license expressions are retained verbatim. This inventory is not a vulnerability scan or legal certification."}))


def valid_runtime_version(version):
    return isinstance(version, str) and re.fullmatch(r"(?:10|[1-9][1-9]\d*)\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version) is not None


def valid_sdk(version):
    return isinstance(version, str) and re.fullmatch(r"(?:10|[1-9][1-9]\d*)\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version) is not None and int(version.split(".")[2].split("-")[0].split("+")[0]) >= 100


def build(repo, publish, assets_path, output, revision, signing_path, nuget_hashes_path=None, sdk="10.0.401"):
    repo, publish = regular(repo), regular(publish)
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Source revision must be a full Git commit")
    if not valid_sdk(sdk):
        raise ValueError("Package SDK differs from the qualified SDK feature band")
    files = {}
    for path in sorted(publish.rglob("*")):
        regular(path)
        if path.is_file() and path.suffix.lower() != ".pdb":
            name = safe_name(path.relative_to(publish).as_posix())
            add(files, name, read(path))
    for name in ("LICENSE", "README.md", "global.json", "SECURITY.md"):
        add(files, name, read(repo / name, 1024 * 1024))
    for path in sorted((repo / "skills").rglob("*")):
        regular(path)
        if path.is_file() and path.suffix in (".md", ".yaml", ".yml"):
            rel = safe_name(path.relative_to(repo).as_posix())
            content = read(path, 1024 * 1024)
            if rel in files:
                if files[rel] != content:
                    raise ValueError("Published skill file differs from repository skill")
            else:
                add(files, rel, content)
    add(files, "native/dependencies.lock.json", read(repo / "native/dependencies.lock.json", 1024 * 1024))
    for path in sorted((repo / "docs").glob("*.md")):
        add(files, "docs/" + path.name, read(path, 1024 * 1024))
    for path in sorted((repo / "schemas").rglob("*.json")):
        add(files, path.relative_to(repo).as_posix(), read(path, 1024 * 1024))
    observed_hashes = load(read(nuget_hashes_path, 1024 * 1024)) if nuget_hashes_path else None
    dependency_notices(repo, load(read(assets_path, 8 * 1024 * 1024)), files, observed_hashes)
    if not REQUIRED <= files.keys():
        raise ValueError("Package lacks a required controller, parser worker, native binary, or dependency")
    check_runtime(files)
    signing = load(read(signing_path, 1024 * 1024))
    binary_names = {name for name in files if name.lower().endswith((".dll", ".exe"))}
    check_signing(signing, {name: sha(files[name]) for name in binary_names})
    records = [{"path": safe_name(name), "length": len(data), "sha256": sha(data)} for name, data in sorted(files.items())]
    host_runtime = load(files["Tedd.DotNetLab.Host.runtimeconfig.json"])["runtimeOptions"]["framework"]["version"]
    manifest = {"schema_version": SCHEMA, "version": VERSION, "source_revision": revision,
                "platform": "windows-x64", "runtime": host_runtime, "sdk": sdk,
                "deployment": "framework-dependent", "authenticode": signing, "files": records,
                "trust": "File and archive hashes detect corruption. Authenticate the archive hash through an independent trusted channel before execution. Signing observations do not constitute platform certification."}
    add(files, MANIFEST, encode(manifest))
    if len(files) > MAX_ENTRIES or sum(map(len, files.values())) > MAX_TOTAL or len({n.casefold() for n in files}) != len(files):
        raise ValueError("Package exceeds its bound or contains ambiguous Windows paths")
    output = Path(os.path.abspath(output))
    if output.parent == output:
        raise ValueError("Package output must be beneath a non-root directory")
    if os.name == "nt" and Path("D:/Workspaces/AI").exists() and not output.is_relative_to(Path("D:/Workspaces/AI")):
        raise ValueError("Package output must be beneath D:\\Workspaces\\AI")
    regular(output.parent)
    # Exclusive creation preserves an earlier artifact instead of overwriting it.
    with output.open("xb") as stream, zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    with output.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    verify(output, digest)
    return {"archive": str(output), "sha256": digest, "version": VERSION, "file_count": len(files)}


def verify(path, expected_hash):
    if not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
        raise ValueError("An independently trusted lowercase archive SHA-256 is required")
    path = regular(path)
    if not path.is_file() or path.stat().st_size > MAX_TOTAL:
        raise ValueError("Archive exceeds its bounded read budget")
    with path.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != expected_hash:
            raise ValueError("Archive hash differs from the trusted hash")
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if not 1 <= len(entries) <= MAX_ENTRIES or sum(e.file_size for e in entries) > MAX_TOTAL:
            raise ValueError("Archive expands beyond its bounded budget")
        names = [safe_name(e.filename) for e in entries]
        if len({n.casefold() for n in names}) != len(names) or any(
            e.is_dir() or e.file_size > MAX_FILE or e.flag_bits & 1 or stat.S_IFMT(e.external_attr >> 16) not in (0, stat.S_IFREG) or
            e.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) for e in entries
        ):
            raise ValueError("Ambiguous or unsupported archive entry")
        if MANIFEST not in names or archive.getinfo(MANIFEST).file_size > 1024 * 1024:
            raise ValueError("Missing or excessive package manifest")
        manifest = load(archive.read(MANIFEST))
        host_runtime = load(archive.read("Tedd.DotNetLab.Host.runtimeconfig.json"))["runtimeOptions"]["framework"]["version"] if "Tedd.DotNetLab.Host.runtimeconfig.json" in names else None
        if manifest.get("schema_version") != SCHEMA or manifest.get("version") != VERSION or manifest.get("platform") != "windows-x64" or not valid_runtime_version(manifest.get("runtime")) or manifest.get("runtime") != host_runtime or not valid_sdk(manifest.get("sdk")) or manifest.get("deployment") != "framework-dependent" or not re.fullmatch(r"[0-9a-f]{40}", manifest.get("source_revision", "")):
            raise ValueError("Unsupported package manifest")
        records = manifest["files"]
        if not isinstance(records, list) or len(records) > MAX_ENTRIES:
            raise ValueError("Invalid package inventory")
        declared = [safe_name(record["path"]) for record in records]
        if len(set(declared)) != len(declared) or set(declared) != set(names) - {MANIFEST} or not REQUIRED <= set(declared):
            raise ValueError("Manifest does not cover exactly the required archive contents")
        runtime_files = {}
        for record in records:
            data = archive.read(record["path"])
            if type(record["length"]) is not int or len(data) != record["length"] or sha(data) != record["sha256"]:
                raise ValueError("Package file differs from its manifest")
            if record["path"] in REQUIRED:
                runtime_files[record["path"]] = data
        check_runtime(runtime_files)
        signing = manifest["authenticode"]
        binaries = {r["path"]: r["sha256"] for r in records if r["path"].lower().endswith((".dll", ".exe"))}
        check_signing(signing, binaries)
        return {"verified": True, "version": manifest["version"], "source_revision": manifest["source_revision"], "file_count": len(entries)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    create = sub.add_parser("build")
    for name in ("repo", "publish", "assets", "output", "revision", "signing"):
        create.add_argument("--" + name, required=True)
    create.add_argument("--nuget-hashes")
    create.add_argument("--sdk", default="10.0.401")
    check = sub.add_parser("verify")
    check.add_argument("--archive", required=True)
    check.add_argument("--sha256", required=True)
    args = parser.parse_args()
    result = build(args.repo, args.publish, args.assets, args.output, args.revision, args.signing, args.nuget_hashes, args.sdk) if args.command == "build" else verify(args.archive, args.sha256)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
