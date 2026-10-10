"""Public worker protocol controls on synthetic files; no service/model/candidate."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import unittest

spec = importlib.util.spec_from_file_location("workspace_ipc_agent", Path(__file__).with_name("agent.py"))
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class WindowedProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="vyral-windowed-protocol-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "src").mkdir()
        lines = [f"const repeated_{n:04d} = 'unchanged source padding';\r\n" for n in range(3000)]
        lines[2221] = "const unique_π = old_duration;\r\n"
        self.before = ("\ufeff" + "".join(lines)).encode()
        self.path = self.root / "src/large.ts"
        self.path.write_bytes(self.before)
        self.seed = self.root / "src/seed.txt"
        self.seed.write_bytes(b"seed\r\n")
        content = "".join(self.before.decode().splitlines(keepends=True)[2208:2235])
        self.profile = json.loads((Path(__file__).parents[2] / "deploy/workspace-ipc/example-worker-v2.json").read_text())
        self.profile.update(taskSha256=agent.sha(b"fixture task"), providerId="fixture", modelId="fixture",
            contextFiles=[{"path": "src/large.ts", "sha256": agent.sha(self.before),
                "windows": [{"startLine": 2209, "lineCount": 27, "sha256": agent.sha(content.encode())}]},
                {"path": "src/seed.txt", "sha256": agent.sha(b"seed\r\n")}])
        self.envelope = {"schemaVersion": "vyral.workspace-agent-input.v1", "task": "fixture task",
            "taskSha256": self.profile["taskSha256"].split(":")[1], "baseCommit": self.profile["baseCommit"],
            "allowedPaths": self.profile["allowedPaths"], "avoidedPaths": self.profile["avoidedPaths"],
            "modelId": "fixture", "remainingMilliseconds": 60000}
        self.patch = {"schemaVersion": "vyral.workspace-edits.v2", "edits": [{"path": "src/large.ts",
            "previousSha256": agent.sha(self.before), "replacements": [{"oldText": "const unique_π = old_duration;",
            "newText": "const unique_π = new_duration;"}]}]}

    def response(self, content):
        return {"status": "Succeeded", "provider_result": {"provider": "fixture", "modelId": "fixture"},
            "output": {"message": {"role": "assistant", "content": content}}}

    def apply(self, proposal):
        content = proposal if isinstance(proposal, str) else agent.encode(proposal, utf8=True).decode()
        return agent.apply_edits(self.profile, self.envelope, self.response(content), self.root, time.monotonic() + 2)

    def assert_unchanged(self):
        self.assertEqual(self.path.read_bytes(), self.before)
        self.assertEqual(self.seed.read_bytes(), b"seed\r\n")
        self.assertFalse((self.root / "src/new.ts").exists())

    def test_windowed_prompt_selects_only_patch_and_raw_object(self):
        request, reservation = agent.input_request(self.profile, self.envelope, self.root)
        prompt = json.loads(request["prompt"])
        instruction = prompt["instruction"]
        self.assertIn("use only schemaVersion vyral.workspace-edits.v2", instruction)
        self.assertNotIn("workspace-edits.v1", instruction)
        self.assertNotIn("Alternatively", instruction)
        self.assertIn("without Markdown fences", instruction)
        self.assertIn("oldText must be exact text inside a supplied window", instruction)
        self.assertNotIn("repeated_0000", request["prompt"])
        self.assertIn("unique_π", request["prompt"])
        self.assertEqual(reservation, 4096)
        self.assertLess(len(request["prompt"].encode()), 8192)

    def test_valid_raw_patch_preserves_bom_crlf_unicode_and_every_other_byte(self):
        self.assertEqual(self.apply(" \n" + agent.encode(self.patch, utf8=True).decode() + "\t"), ["src/large.ts"])
        expected = self.before.replace(b"old_duration", b"new_duration")
        self.assertEqual(self.path.read_bytes(), expected)
        self.assertEqual(self.seed.read_bytes(), b"seed\r\n")

    def test_windowed_v1_fragment_and_even_noop_schema_refuse(self):
        for edits in ([{"path": "src/large.ts", "previousSha256": agent.sha(self.before), "content": "x" * 1030}], []):
            with self.subTest(edits=len(edits)), self.assertRaisesRegex(agent.Held, "windowed_patch_required"):
                self.apply({"schemaVersion": "vyral.workspace-edits.v1", "edits": edits})
            self.assert_unchanged()

    def test_strict_framing_refuses_fences_prose_multiple_objects_and_json_faults(self):
        raw = agent.encode(self.patch).decode()
        cases = ["```json\n" + raw + "\n```", "```\n" + raw + "\n```", "Result: " + raw,
            raw + "\nThanks", raw + raw, "```json\n" + raw + "\n```\n```json\n" + raw + "\n```",
            raw[:-1], "[]", "null", '{"schemaVersion":"vyral.workspace-edits.v2","schemaVersion":"vyral.workspace-edits.v1","edits":[]}',
            '{"schemaVersion":"vyral.workspace-edits.v2","edits":[],"extra":true}',
            '{"schemaVersion":"unknown","edits":[]}', '{"schemaVersion":"vyral.workspace-edits.v2","edits":[],"n":NaN}']
        for content in cases:
            with self.subTest(content=content[:18]), self.assertRaises(agent.Held): self.apply(content)
            self.assert_unchanged()

    def test_byte_limit_refuses_before_parse_or_write(self):
        self.profile["maxResponseBytes"] = 128
        with self.assertRaisesRegex(agent.Held, "response_limit"): self.apply(" " * 129)
        self.assert_unchanged()

    def test_batch_refusals_leave_valid_first_edit_unapplied(self):
        reasons = {"hash": "edit_precondition", "window_hash": "context_binding", "missing": "anchor_not_unique",
            "ambiguous": "anchor_not_unique", "overlap": "overlapping_replacements", "outside_window": "anchor_outside_context",
            "cross_window": "anchor_outside_context", "unknown_field": "invalid_edit", "size": "edit_limit",
            "new_file": "edit_precondition", "unprovided": "context_window_scope", "scope": "path_scope", "symlink": "symlink"}
        for fault, reason in reasons.items():
            with self.subTest(fault=fault):
                patch = copy.deepcopy(self.patch)
                prefix = {"path": "src/seed.txt", "previousSha256": agent.sha(b"seed\r\n"),
                    "replacements": [{"oldText": "seed", "newText": "modified"}]}
                patch["edits"].insert(0, prefix)
                edit = patch["edits"][1]
                if fault == "hash": edit["previousSha256"] = agent.sha(b"wrong")
                if fault == "window_hash": self.profile["contextFiles"][0]["windows"][0]["sha256"] = agent.sha(b"wrong")
                if fault == "missing": edit["replacements"][0]["oldText"] = "absent anchor"
                if fault == "ambiguous": edit["replacements"][0]["oldText"] = "unchanged source padding"
                if fault == "overlap": edit["replacements"].append({"oldText": "old_duration", "newText": "different"})
                if fault == "outside_window": edit["replacements"][0]["oldText"] = "const repeated_0000 = 'unchanged source padding';"
                if fault == "cross_window": edit["replacements"][0]["oldText"] = "const repeated_2207 = 'unchanged source padding';\r\nconst repeated_2208"
                if fault == "unknown_field": edit["replacements"][0]["command"] = "forbidden"
                if fault == "size": edit["replacements"][0]["newText"] = "x" * (self.profile["maxEditBytes"] + 1)
                if fault == "new_file": edit.update(path="src/new.ts", previousSha256=None)
                if fault == "unprovided": self.profile["contextFiles"][0]["path"] = "src/unprovided.ts"
                if fault == "scope": edit["path"] = "checks/large.ts"
                if fault == "symlink":
                    (self.root / "src/link.ts").symlink_to(self.path); edit["path"] = "src/link.ts"
                with self.assertRaisesRegex(agent.Held, reason): self.apply(patch)
                self.assert_unchanged()
                self.profile["contextFiles"] = [{"path": "src/large.ts", "sha256": agent.sha(self.before), "windows": [{"startLine": 2209, "lineCount": 27,
                    "sha256": agent.sha("".join(self.before.decode().splitlines(keepends=True)[2208:2235]).encode())}]},
                    {"path": "src/seed.txt", "sha256": agent.sha(b"seed\r\n")}]

    def test_full_context_v1_and_v2_remain_supported(self):
        self.profile["contextFiles"] = [{"path": "src/seed.txt", "sha256": agent.sha(b"seed\r\n")}]
        self.apply({"schemaVersion": "vyral.workspace-edits.v1", "edits": [{"path": "src/new.ts", "previousSha256": None, "content": "new\n"}]})
        self.assertEqual((self.root / "src/new.ts").read_bytes(), b"new\n")
        self.apply({"schemaVersion": "vyral.workspace-edits.v2", "edits": [{"path": "src/seed.txt", "previousSha256": agent.sha(b"seed\r\n"),
            "replacements": [{"oldText": "seed", "newText": "changed"}]}]})
        self.assertEqual(self.seed.read_bytes(), b"changed\r\n")
        prompt = json.loads(agent.input_request(self.profile | {"contextFiles": []}, self.envelope, self.root)[0]["prompt"])
        self.assertIn("workspace-edits.v1", prompt["instruction"])
        self.assertIn("Alternatively", prompt["instruction"])


if __name__ == "__main__": unittest.main()
