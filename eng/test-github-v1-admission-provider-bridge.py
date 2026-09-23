#!/usr/bin/env python3
"""Independent fake-provider controls for the scoped admission stdio bridge."""

import base64
import importlib.util
import io
import json
import pathlib
import sys
import unittest


sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-provider-bridge.py")
spec = importlib.util.spec_from_file_location("v1_admission_provider_bridge", SOURCE)
bridge_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge_module)
transport = bridge_module.transport


class FakeProvider:
    def __init__(self):
        self.ref = None
        self.objects = {}
        self.created = 0

    def read_ref(self, name):
        assert name == transport.OPERATION_REF
        return self.ref

    def protection_snapshot(self):
        return {"schema": "fake-protection"}

    def put_object(self, kind, oid, raw):
        self.objects[(kind, oid)] = raw
        return oid

    def read_object(self, kind, oid):
        return self.objects[(kind, oid)]

    def create_ref_expected_absent(self, name, commit):
        assert name == transport.OPERATION_REF and self.ref is None
        self.ref = commit
        self.created += 1


def plan():
    event = b'{}\n'
    head = b'{"head":1}\n'
    event_oid = transport.git_oid("blob", event)
    head_oid = transport.git_oid("blob", head)
    tree = (b"100644 event.json\0" + bytes.fromhex(event_oid)
            + b"100644 head.json\0" + bytes.fromhex(head_oid))
    tree_oid = transport.git_oid("tree", tree)
    commit = (f"tree {tree_oid}\nauthor FS.GG Coordination <coordination@fs.gg> 0 +0000\n"
              "committer FS.GG Coordination <coordination@fs.gg> 0 +0000\n\n"
              "fsgg admission test\n").encode()
    commit_oid = transport.git_oid("commit", commit)
    objects = [("blob", event_oid, event), ("blob", head_oid, head),
               ("tree", tree_oid, tree), ("commit", commit_oid, commit)]
    return {"op": "bind", "ref": transport.OPERATION_REF, "commit": commit_oid,
            "objects": [{"kind": kind, "oid": oid,
                         "bytesBase64": base64.b64encode(raw).decode()}
                        for kind, oid, raw in objects]}


class BridgeTests(unittest.TestCase):
    def test_exact_plan_object_and_ref_scope(self):
        provider = FakeProvider()
        bridge = bridge_module.Bridge(provider)
        with self.assertRaisesRegex(transport.Refused, "unbound"):
            bridge.handle({"op": "create_ref", "ref": transport.OPERATION_REF,
                           "commit": "a" * 40})
        binding = plan()
        self.assertEqual({"bound": True}, bridge.handle(binding))
        with self.assertRaisesRegex(transport.Refused, "plan"):
            bridge.handle(binding)
        with self.assertRaisesRegex(transport.Refused, "not-planned"):
            bridge.handle({"op": "put_object", "kind": "blob", "oid": "a" * 40})
        for item in binding["objects"]:
            key = {"kind": item["kind"], "oid": item["oid"]}
            self.assertEqual(item["oid"], bridge.handle({"op": "put_object", **key})["oid"])
            self.assertEqual(item["bytesBase64"], bridge.handle({"op": "read_object", **key})["bytesBase64"])
        with self.assertRaisesRegex(transport.Refused, "ref-not-planned"):
            bridge.handle({"op": "create_ref", "ref": transport.OPERATION_REF,
                           "commit": "a" * 40})
        bridge.handle({"op": "create_ref", "ref": transport.OPERATION_REF,
                       "commit": binding["commit"]})
        self.assertEqual(binding["commit"], bridge.handle({"op": "read_ref",
                                                            "ref": transport.OPERATION_REF})["oid"])
        self.assertEqual(1, provider.created)

    def test_changed_bindings_and_unknown_fields_refuse(self):
        original = plan()
        changed = json.loads(json.dumps(original))
        changed["objects"][0]["bytesBase64"] = base64.b64encode(b"different").decode()
        with self.assertRaisesRegex(transport.Refused, "object"):
            bridge_module.Bridge(FakeProvider()).handle(changed)
        changed = json.loads(json.dumps(original))
        changed["extra"] = True
        with self.assertRaisesRegex(transport.Refused, "shape"):
            bridge_module.Bridge(FakeProvider()).handle(changed)
        changed = json.loads(json.dumps(original))
        changed["ref"] = "refs/heads/main"
        with self.assertRaisesRegex(transport.Refused, "plan"):
            bridge_module.Bridge(FakeProvider()).handle(changed)

    def test_stdio_never_echoes_jwt(self):
        jwt = b"synthetic.jwt.value"
        source = io.BytesIO(jwt + b"\n" + json.dumps(plan()).encode() + b"\n")
        output = io.BytesIO()
        bridge_module.serve(source, output, lambda value: FakeProvider())
        self.assertNotIn(jwt, output.getvalue())
        self.assertEqual({"ready": True}, json.loads(output.getvalue().splitlines()[0]))
        self.assertEqual({"ok": {"bound": True}}, json.loads(output.getvalue().splitlines()[1]))


if __name__ == "__main__":
    unittest.main()
