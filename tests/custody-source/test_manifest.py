import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / "src/FS.GG.Coordination.Orchestration.Execution/Custody/generate-lease-source-manifest.py"
spec = importlib.util.spec_from_file_location("lease_manifest", PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in module.PATHS + (f"{module.PROJECT}/FS.GG.Coordination.Orchestration.Execution.fsproj",):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((ROOT / name).read_bytes())

    def test_exact_canonical_roster_and_hashes(self):
        self.assertEqual(module.manifest(self.root), module.manifest(ROOT))
        self.assertEqual(len(module.manifest(self.root)["files"]), 2)

    def test_namespace_rewrite_refused(self):
        path = self.root / module.PATHS[0]
        path.write_text(path.read_text().replace(module.NAMESPACE, "Other"))
        with self.assertRaisesRegex(ValueError, "namespace"):
            module.manifest(self.root)

    def test_symlink_source_refused(self):
        path = self.root / module.PATHS[0]
        path.unlink()
        path.symlink_to(ROOT / module.PATHS[0])
        with self.assertRaises(OSError):
            module.manifest(self.root)

    def test_symlink_ancestor_refused(self):
        project = self.root / module.PROJECT
        moved = project.with_name("moved")
        project.rename(moved)
        project.symlink_to(moved, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symlink"):
            module.manifest(self.root)

    def test_executable_source_refused(self):
        (self.root / module.PATHS[0]).chmod(0o755)
        with self.assertRaisesRegex(ValueError, "mode"):
            module.manifest(self.root)

    def test_oversize_refused(self):
        (self.root / module.PATHS[0]).write_bytes(b"x" * (module.CAP + 1))
        with self.assertRaisesRegex(ValueError, "size"):
            module.manifest(self.root)

    def test_framework_drift_refused(self):
        path = self.root / module.PROJECT / "FS.GG.Coordination.Orchestration.Execution.fsproj"
        path.write_text(path.read_text().replace("net10.0", "net9.0"))
        with self.assertRaisesRegex(ValueError, "framework"):
            module.manifest(self.root)
