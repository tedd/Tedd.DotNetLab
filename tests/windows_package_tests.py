"""Deterministic corruption and distribution-boundary tests; no target launch."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import sys
import unittest
import uuid
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import windows_package as package

ROOT = None


class WindowsPackageTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / uuid.uuid4().hex
        self.root.mkdir(parents=True)
        self.repo = self.root / "repo"
        self.publish = self.root / "publish"
        self.cache = self.root / "cache"
        self.publish.mkdir()
        self.repo.mkdir()
        self.files = {}
        runtime = package.encode({"runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App", "version": "10.0.12"}, "rollForward": "Disable"}})
        pe = bytearray(512)
        pe[:2] = b"MZ"
        pe[60:64] = (64).to_bytes(4, "little")
        pe[64:70] = b"PE\0\0\x64\x86"
        pe[84:86] = (240).to_bytes(2, "little")
        pe[86:88] = (0x2000).to_bytes(2, "little")
        pe[88:90] = b"\x0b\x02"
        for name in package.REQUIRED:
            if name in ("LICENSE", "THIRD-PARTY-NOTICES.json", "locks/host-packages.lock.json", "global.json"):
                continue
            data = runtime if name.endswith("runtimeconfig.json") else bytes(pe) if name in ("Tedd.DotNetLab.Profiler.dll", "runtimes/win-x64/native/e_sqlite3.dll") else b"test-payload"
            if name == "Tedd.DotNetLab.Host.runtimeconfig.json":
                configuration = package.load(data)
                configuration["runtimeOptions"]["rollForward"] = "Major"
                data = package.encode(configuration)
            self.write(self.publish / name, data)
            self.files[name] = data
        for name in ("LICENSE", "README.md", "SECURITY.md", "native/vendor/coreclr/LICENSE.TXT", "native/vendor/nlohmann/LICENSE.MIT"):
            self.write(self.repo / name, b"Fixture notice.\n")
        self.write(self.repo / "native/dependencies.lock.json", b'{"coreclr_version":"10.0.12","files":[]}')
        self.write(self.repo / "skills/dotnetlab/SKILL.md", b"# Fixture skill\n")
        self.write(self.repo / "global.json", package.encode({"sdk": {"version": "10.0.401", "rollForward": "latestPatch", "allowPrerelease": False}}))
        (self.repo / "docs").mkdir()
        (self.repo / "schemas").mkdir()
        self.nupkg = self.cache / "example/1.0.0/example.1.0.0.nupkg"
        self.nupkg.parent.mkdir(parents=True)
        with zipfile.ZipFile(self.nupkg, "w") as archive:
            archive.writestr("example.nuspec", '<package><metadata><id>Example</id><version>1.0.0</version><authors>Fixture</authors><license type="expression">MIT</license></metadata></package>')
            archive.writestr("LICENSE.txt", "Fixture embedded license.")
        content_hash = base64.b64encode(hashlib.sha512(self.nupkg.read_bytes()).digest()).decode()
        self.lock_path = self.repo / "src/Tedd.DotNetLab.Host/packages.lock.json"
        self.write(self.lock_path, package.encode({"version": 2, "dependencies": {"net10.0": {"Example": {"type": "Transitive", "resolved": "1.0.0", "contentHash": content_hash}}}}))
        self.assets = self.root / "assets.json"
        self.write(self.assets, package.encode({"packageFolders": {str(self.cache): {}}, "libraries": {"Example/1.0.0": {"type": "package", "path": "example/1.0.0", "sha512": content_hash}}}))
        self.signing = self.root / "signing.json"
        self.write(self.signing, package.encode({name: {"sha256": package.sha(data), "status": "NotSigned", "signer_thumbprint": None} for name, data in self.files.items() if name.endswith(".dll")}))
        self.output = self.root / "test.zip"

    @staticmethod
    def write(path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def tearDown(self):
        shutil.rmtree(self.root)

    def build(self, output=None):
        return package.build(self.repo, self.publish, self.assets, output or self.output, "a" * 40, self.signing)

    def rewrite(self, change):
        self.build()
        with zipfile.ZipFile(self.output) as archive:
            self.assertIn("SECURITY.md", archive.namelist())
            self.assertIn("native/dependencies.lock.json", archive.namelist())
            self.assertEqual(b"# Fixture skill\n", archive.read("skills/dotnetlab/SKILL.md"))
            entries = {name: archive.read(name) for name in archive.namelist()}
        change(entries)
        self.output.unlink()
        with zipfile.ZipFile(self.output, "w") as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        return package.sha(self.output.read_bytes())

    def test_reproducible_package_retains_original_metadata_and_notices(self):
        first = self.build()
        second = self.build(self.root / "second.zip")
        self.assertEqual(first["sha256"], second["sha256"])
        self.assertTrue(package.verify(self.output, first["sha256"])["verified"])
        with zipfile.ZipFile(self.output) as archive:
            notices = package.load(archive.read("THIRD-PARTY-NOTICES.json"))
            dependency = notices["dependencies"][0]
            self.assertEqual("Example", dependency["id"])
            self.assertEqual("MIT", dependency["license"])
            self.assertEqual(b"Fixture embedded license.", archive.read(dependency["embedded_notices"][0]))

    def test_independent_archive_hash_rejects_replaced_manifest_and_file(self):
        original = self.build()["sha256"]
        with self.output.open("ab") as stream:
            stream.write(b"alteration")
        with self.assertRaisesRegex(ValueError, "trusted hash"):
            package.verify(self.output, original)

    def test_tampered_payload_rejects_even_with_new_archive_hash(self):
        digest = self.rewrite(lambda entries: entries.__setitem__("Tedd.DotNetLab.Host.dll", b"tampered"))
        with self.assertRaisesRegex(ValueError, "differs from its manifest"):
            package.verify(self.output, digest)

    def test_missing_parser_worker_rejects(self):
        digest = self.rewrite(lambda entries: entries.pop("Tedd.DotNetLab.TraceWorker.dll"))
        with self.assertRaisesRegex(ValueError, "cover exactly"):
            package.verify(self.output, digest)

    def test_undeclared_extra_executable_rejects(self):
        digest = self.rewrite(lambda entries: entries.__setitem__("extra.exe", b"unlisted"))
        with self.assertRaisesRegex(ValueError, "cover exactly"):
            package.verify(self.output, digest)

    def test_manifest_cannot_change_runtime_or_platform(self):
        def change(entries):
            manifest = package.load(entries[package.MANIFEST])
            manifest["runtime"] = "10.0.13"
            entries[package.MANIFEST] = package.encode(manifest)
        digest = self.rewrite(change)
        with self.assertRaisesRegex(ValueError, "Unsupported package manifest"):
            package.verify(self.output, digest)

    def test_ambiguous_windows_paths_reject(self):
        digest = self.rewrite(lambda entries: entries.__setitem__("tedd.dotnetlab.host.dll", b"duplicate"))
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            package.verify(self.output, digest)

    def test_original_sdk_pin_remains_verifiable(self):
        self.write(self.repo / "global.json", package.encode({"sdk": {"version": "10.0.401", "rollForward": "disable", "allowPrerelease": False}}))
        digest = self.build()["sha256"]
        package.verify(self.output, digest)

    def test_duplicate_archive_entries_reject(self):
        self.build()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(self.output, "a") as archive:
                archive.writestr("Tedd.DotNetLab.Host.dll", b"duplicate")
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            package.verify(self.output, package.sha(self.output.read_bytes()))

    def test_archive_symlink_rejects(self):
        self.build()
        with zipfile.ZipFile(self.output, "a") as archive:
            link = zipfile.ZipInfo("link")
            link.external_attr = (stat.S_IFLNK | 0o777) << 16
            archive.writestr(link, "../outside")
        with self.assertRaisesRegex(ValueError, "unsupported archive entry"):
            package.verify(self.output, package.sha(self.output.read_bytes()))

    def test_expanded_size_limit_rejects_before_reading_payload(self):
        self.build()
        original = package.MAX_TOTAL
        try:
            with zipfile.ZipFile(self.output, "a", compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("compressible.txt", b"x" * 100000)
            package.MAX_TOTAL = 30000
            self.assertLess(self.output.stat().st_size, package.MAX_TOTAL)
            with self.assertRaisesRegex(ValueError, "expands beyond"):
                package.verify(self.output, package.sha(self.output.read_bytes()))
        finally:
            package.MAX_TOTAL = original

    def test_traversal_ads_reserved_and_control_paths_reject(self):
        for name in ("../escape", "/absolute", "a\\b", "C:/drive", "payload:stream", "a//b", "a/./b", "NUL.txt", "COM1", "a./file", "a /file", "line\nname", "a/../b"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                package.safe_name(name)

    def test_archive_path_traversal_rejects_without_extraction(self):
        digest = self.rewrite(lambda entries: entries.__setitem__("../escape", b"attack"))
        with self.assertRaisesRegex(ValueError, "Invalid package path"):
            package.verify(self.output, digest)
        self.assertFalse((self.root.parent / "escape").exists())

    def test_cache_archive_tampering_rejects(self):
        with self.nupkg.open("ab") as stream:
            stream.write(b"changed")
        with self.assertRaisesRegex(ValueError, "committed content hash"):
            self.build()

    def test_assets_version_mismatch_rejects(self):
        assets = package.load(self.assets.read_bytes())
        assets["libraries"]["Example/2.0.0"] = assets["libraries"].pop("Example/1.0.0")
        self.write(self.assets, package.encode(assets))
        with self.assertRaisesRegex(ValueError, "committed dependency lock"):
            self.build()

    def test_missing_native_image_blocks_package_creation(self):
        (self.publish / "Tedd.DotNetLab.Profiler.dll").unlink()
        with self.assertRaisesRegex(ValueError, "lacks a required"):
            self.build()

    def test_unqualified_runtime_roll_forward_blocks_package_creation(self):
        runtime_path = self.publish / "Tedd.DotNetLab.Host.runtimeconfig.json"
        runtime = package.load(runtime_path.read_bytes())
        runtime["runtimeOptions"]["rollForward"] = "LatestPatch"
        self.write(runtime_path, package.encode(runtime))
        with self.assertRaisesRegex(ValueError, "runtime configuration"):
            self.build()

    def test_signing_observation_hash_mismatch_rejects(self):
        signing = package.load(self.signing.read_bytes())
        signing["Tedd.DotNetLab.Profiler.dll"]["sha256"] = "0" * 64
        self.write(self.signing, package.encode(signing))
        with self.assertRaisesRegex(ValueError, "Signing observations"):
            self.build()

    def test_unqualified_native_architecture_rejects(self):
        path = self.publish / "Tedd.DotNetLab.Profiler.dll"
        image = bytearray(path.read_bytes())
        image[68:70] = b"\x4c\x01"
        self.write(path, image)
        with self.assertRaisesRegex(ValueError, "AMD64"):
            self.build()

    def test_managed_amd64_dll_cannot_substitute_for_native_profiler(self):
        path = self.publish / "Tedd.DotNetLab.Profiler.dll"
        image = bytearray(path.read_bytes())
        image[312:316] = (1024).to_bytes(4, "little")
        self.write(path, image)
        with self.assertRaisesRegex(ValueError, "managed CLR header"):
            self.build()

    def test_signature_cannot_claim_valid_without_a_certificate(self):
        signing = package.load(self.signing.read_bytes())
        signing["Tedd.DotNetLab.Profiler.dll"]["status"] = "Valid"
        self.write(self.signing, package.encode(signing))
        with self.assertRaisesRegex(ValueError, "signer certificate identity"):
            self.build()

    def test_previous_package_cannot_be_overwritten(self):
        original = self.build()["sha256"]
        with self.assertRaises(FileExistsError):
            self.build()
        self.assertEqual(original, package.sha(self.output.read_bytes()))

    def test_duplicate_json_metadata_rejects(self):
        with self.assertRaisesRegex(ValueError, "Duplicate JSON"):
            package.load(b'{"schema_version":"a","schema_version":"b"}')

    def test_file_and_inventory_limits_reject_before_archive_creation(self):
        original = package.MAX_FILE
        try:
            package.MAX_FILE = 32
            with self.assertRaises(ValueError):
                self.build()
            self.assertFalse(self.output.exists())
        finally:
            package.MAX_FILE = original


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    args = parser.parse_args()
    ROOT = Path(os.path.abspath(args.workspace))
    if sys.platform == "win32" and Path("D:/Workspaces/AI").exists() and not ROOT.is_relative_to(Path("D:/Workspaces/AI")):
        parser.error("Tests require a dedicated AI workspace")
    ROOT.mkdir(parents=True, exist_ok=True)
    package.regular(ROOT)
    unittest.main(argv=[sys.argv[0]], verbosity=2)
