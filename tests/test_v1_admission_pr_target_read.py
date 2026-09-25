"""Synthetic complete native PR/source/target census controls."""

import copy
import importlib.util
import pathlib
import sys
import unittest


SOURCE = pathlib.Path(__file__).resolve().parents[1] / "eng/github-v1-admission-pr-target-read.py"
SPEC = importlib.util.spec_from_file_location("target_read", SOURCE)
TARGET = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = TARGET
SPEC.loader.exec_module(TARGET)

PREFIX = "repos/FS-GG/.github"
PR = 47
BASE = "a" * 40
HEAD = "b" * 40
SOURCE_REF = "routine/admission-source"
ENCODED_REF = "routine%2Fadmission-source"


class Fixture:
    def __init__(self):
        self.data = {
            f"{PREFIX}/pulls/{PR}": {
                "number": PR, "id": 147, "node_id": "PR_node", "state": "open",
                "draft": False, "merged": False, "commits": 1, "changed_files": 1,
                "base": {"ref": "main", "sha": BASE, "repo": {"id": TARGET.REPOSITORY_ID}},
                "head": {"ref": SOURCE_REF, "sha": HEAD,
                         "repo": {"id": TARGET.REPOSITORY_ID}},
            },
            f"{PREFIX}/branches/main": {
                "name": "main", "protected": True, "commit": {"sha": BASE},
            },
            f"{PREFIX}/branches/{ENCODED_REF}": {
                "name": SOURCE_REF, "commit": {"sha": HEAD},
            },
            f"{PREFIX}/pulls/{PR}/commits?per_page=100&page=1": [
                {"sha": HEAD, "commit": {"message": "source"}},
            ],
            f"{PREFIX}/pulls/{PR}/files?per_page=100&page=1": [
                {"filename": "tools/source.py", "sha": "c" * 40,
                 "status": "modified"},
            ],
            f"{PREFIX}/pulls/{PR}/reviews?per_page=100&page=1": [
                {"id": 9, "state": "APPROVED", "body": "reviewed"},
            ],
            f"{PREFIX}/commits/{HEAD}/check-runs?per_page=100&filter=all&page=1": {
                "total_count": 1, "check_runs": [{"id": 10, "name": "required"}],
            },
            f"{PREFIX}/commits/{HEAD}/statuses?per_page=100&page=1": [],
        }
        self.links = {}
        self.calls = []

    def read(self, path):
        self.calls.append(path)
        if path not in self.data and (path.endswith("&page=2") or path.endswith("&page=3")):
            return TARGET.NATIVE.NativeResponse([], None)
        return TARGET.NATIVE.NativeResponse(copy.deepcopy(self.data[path]), self.links.get(path))


class TargetTests(unittest.TestCase):
    def test_two_complete_reads_bind_source_target_and_census(self):
        fixture = Fixture()
        result = TARGET.collect_two(fixture.read, PR)
        self.assertEqual((result["base_sha"], result["head_sha"]), (BASE, HEAD))
        self.assertEqual((result["file_count"], result["review_count"], result["check_run_count"]),
                         (1, 1, 1))
        self.assertEqual(fixture.calls.count(f"{PREFIX}/pulls/{PR}"), 2)
        self.assertEqual(fixture.calls.count(f"{PREFIX}/pulls/{PR}/files?per_page=100&page=2"), 2)

    def test_moved_main_source_and_incomplete_file_count_refuse(self):
        for mutate in (
            lambda f: f.data[f"{PREFIX}/branches/main"]["commit"].update(sha="d" * 40),
            lambda f: f.data[f"{PREFIX}/branches/{ENCODED_REF}"]["commit"].update(sha="d" * 40),
            lambda f: f.data[f"{PREFIX}/pulls/{PR}"].update(changed_files=2),
        ):
            fixture = Fixture()
            mutate(fixture)
            with self.assertRaises(TARGET.NATIVE.Refused):
                TARGET.collect_once(fixture.read, PR)

    def test_unrelated_review_change_between_reads_refuses(self):
        fixture = Fixture()
        original = fixture.read
        reads = 0

        def changed(path):
            nonlocal reads
            if path == f"{PREFIX}/pulls/{PR}":
                reads += 1
                if reads == 2:
                    fixture.data[f"{PREFIX}/pulls/{PR}/reviews?per_page=100&page=1"][0]["body"] = "moved"
            return original(path)

        with self.assertRaisesRegex(TARGET.NATIVE.Refused, "native-target-two-read-drift"):
            TARGET.collect_two(changed, PR)

    def test_nonterminal_short_file_page_and_overlimit_refuse(self):
        fixture = Fixture()
        path = f"{PREFIX}/pulls/{PR}/files?per_page=100&page=1"
        fixture.links[path] = (
            f'<https://api.github.com/repositories/{TARGET.REPOSITORY_ID}/pulls/{PR}/files?'
            'per_page=100&page=2>; rel="next"'
        )
        fixture.data[path[:-1] + "2"] = []
        TARGET.collect_once(fixture.read, PR)
        self.assertIn(path[:-1] + "2", fixture.calls)

        fixture = Fixture()
        fixture.data[f"{PREFIX}/pulls/{PR}"]["commits"] = 251
        with self.assertRaisesRegex(TARGET.NATIVE.Refused, "native-target-census-size"):
            TARGET.collect_once(fixture.read, PR)


if __name__ == "__main__":
    unittest.main()
