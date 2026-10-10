"""Opt-in framing and immutable recovery controls; synthetic files, no services."""
import copy
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import test_windowed_protocol as protocol_tests

agent = protocol_tests.agent


class ResponseFramingTests(unittest.TestCase):
    def setUp(self):
        self.f = protocol_tests.WindowedProtocolTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)

    def loaded(self):
        path = self.f.root / "profile.json"
        path.write_bytes(agent.encode(self.f.profile)); path.chmod(0o600)
        return agent.load_profile(path)

    def fence(self, value, newline="\n"):
        raw = value if isinstance(value, str) else agent.encode(value, utf8=True).decode()
        return "```json" + newline + raw + newline + "```"

    def test_closed_optional_enum_and_default_are_profile_bound(self):
        original = self.loaded()
        self.assertEqual(agent.response_framing(original), "raw-json")
        original_request = agent.input_request(original, self.f.envelope, self.f.root)[0]
        for mode in ("raw-json", "raw-json-or-single-json-fence"):
            self.f.profile["responseFraming"] = mode
            loaded = self.loaded()
            self.assertEqual(agent.response_framing(loaded), mode)
            self.assertNotEqual(original["profileSha256"], loaded["profileSha256"])
            request = agent.input_request(loaded, self.f.envelope, self.f.root)[0]
            if mode == "raw-json": self.assertEqual(request, original_request)
            else:
                self.assertNotEqual(agent.sha(agent.encode(request)), agent.sha(agent.encode(original_request)))
                self.assertIn("exactly one complete lowercase ```json fence", json.loads(request["prompt"])["instruction"])
        for value in (None, True, 1, [], {}, "unknown", "RAW-JSON", " raw-json"):
            self.f.profile["responseFraming"] = value
            with self.assertRaisesRegex(agent.Held, "unsupported_response_framing"): self.loaded()
        self.f.profile["responseFraming"] = "raw-json"
        self.f.profile["response_framing"] = "raw-json"
        with self.assertRaisesRegex(agent.Held, "unsupported_profile"): self.loaded()
        self.f.profile = json.loads((Path(__file__).parents[2] / "deploy/workspace-ipc/example-worker.json").read_text())
        self.loaded()  # Confirm the old closed profile is valid before adding the new field.
        self.f.profile["responseFraming"] = "raw-json"
        with self.assertRaisesRegex(agent.Held, "unsupported_profile"): self.loaded()

    def test_default_and_explicit_raw_refuse_single_fence_without_writes(self):
        for explicit in (False, True):
            if explicit: self.f.profile["responseFraming"] = "raw-json"
            with self.assertRaisesRegex(agent.Held, "invalid_json"): self.f.apply(self.fence(self.f.patch))
            self.f.assert_unchanged()

    def test_opt_in_accepts_raw_lf_crlf_and_surrounding_whitespace_only(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        raw = agent.encode(self.f.patch, utf8=True).decode()
        for content in (raw, self.fence(raw), self.fence(raw, "\r\n"), " \t\r\n" + self.fence(raw) + "\r\n\t "):
            self.f.path.write_bytes(self.f.before)
            self.assertEqual(self.f.apply(content), ["src/large.ts"])
            self.assertEqual(self.f.path.read_bytes(), self.f.before.replace(b"old_duration", b"new_duration"))
            self.assertEqual(self.f.seed.read_bytes(), b"seed\r\n")

    def test_malformed_multiple_nested_tags_and_prose_refuse_atomically(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        raw = agent.encode(self.f.patch).decode(); valid = self.fence(raw)
        cases = ["```js\n" + raw + "\n```", "```JSON\n" + raw + "\n```", "```\n" + raw + "\n```",
            "```json extra\n" + raw + "\n```", "```json \n" + raw + "\n```", "``` json\n" + raw + "\n```",
            "```json" + raw + "```", "```json\n" + raw, "```json\n" + raw + "\n ```",
            valid + " trailing", "Prose\n" + valid, valid + "\n" + valid,
            "```json\n" + valid + "\n```", "```json\n" + raw + "\n````", "~~~json\n" + raw + "\n~~~",
            self.fence(raw + "\n" + raw), self.fence('{"schemaVersion":"vyral.workspace-edits.v2","edits":[],"extra":true}'),
            self.fence('{"schemaVersion":"vyral.workspace-edits.v2","schemaVersion":"vyral.workspace-edits.v1","edits":[]}'),
            self.fence('{"schemaVersion":"vyral.workspace-edits.v2","edits":[],"n":NaN}'),
            self.fence('{"schemaVersion":"vyral.workspace-edits.v2","edits":[],"n":Infinity}'),
            self.fence('[]'), self.fence('null'), self.fence(raw[:-1])]
        for content in cases:
            with self.subTest(prefix=content[:20]), self.assertRaises(agent.Held): self.f.apply(content)
            self.f.assert_unchanged()

    def test_original_utf8_byte_limit_is_checked_before_trimming_or_decoding(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        raw = agent.encode(self.f.patch, utf8=True).decode()
        self.f.profile["maxResponseBytes"] = len(raw.encode())
        with self.assertRaisesRegex(agent.Held, "response_limit"): self.f.apply(self.fence(raw))
        with self.assertRaisesRegex(agent.Held, "response_limit"): self.f.apply(" " * 1000 + raw)
        self.f.assert_unchanged()

    def test_framed_invented_anchor_still_refuses_valid_first_edit(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        proposal = copy.deepcopy(self.f.patch)
        proposal["edits"][0]["replacements"][0]["oldText"] = "invented anchor absent from supplied source"
        proposal["edits"].insert(0, {"path": "src/seed.txt", "previousSha256": agent.sha(b"seed\r\n"),
            "replacements": [{"oldText": "seed", "newText": "modified"}]})
        with self.assertRaisesRegex(agent.Held, "anchor_not_unique"): self.f.apply(self.fence(proposal))
        self.f.assert_unchanged()
        with self.assertRaisesRegex(agent.Held, "windowed_patch_required"):
            self.f.apply(self.fence({"schemaVersion": "vyral.workspace-edits.v1", "edits": []}))
        self.f.assert_unchanged()

    def test_all_existing_atomic_window_guards_still_apply_to_framed_output(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        raw_apply = self.f.apply
        self.f.apply = lambda proposal: raw_apply(self.fence(proposal))
        self.f.test_batch_refusals_leave_valid_first_edit_unapplied()

    def test_same_mode_recovery_is_get_only_and_changed_mode_refuses_original_hash(self):
        self.f.profile["responseFraming"] = "raw-json-or-single-json-fence"
        self.f.profile["invocationDeadlineUtc"] = "2030-01-01T00:00:00Z"
        profile = self.loaded()
        request, reservation = agent.input_request(profile, self.f.envelope, self.f.root)
        response = self.f.response(self.fence(self.f.patch)); response["usage"] = {"input_tokens": 2, "output_tokens": 3}
        original = {"state": "succeeded", "session_id": profile["sessionId"], "key": profile["operationId"],
            "request_sha256": agent.sha(agent.encode(request)), "reservation_tokens": reservation,
            "result_sha256": agent.sha(agent.encode(response)), "response": response, "usage_tokens": 5}
        methods = []
        class Gateway:
            def __init__(self, p, deadline): self.p = p
            def request(self, method):
                methods.append(method)
                return {"session_id": self.p["sessionId"], "grant_sha256": self.p["grantSha256"], "backend_sha256": self.p["backendSha256"], "state": "open"}
            def call(self, method, *_):
                methods.append(method)
                if method != "GET": raise AssertionError("No POST authorized in recovery control")
                return original
        with patch.object(agent, "Gateway", Gateway):
            for milliseconds in (60000, 30000):
                self.f.path.write_bytes(self.f.before)
                self.f.envelope["remainingMilliseconds"] = milliseconds
                fresh_request = agent.input_request(self.loaded(), self.f.envelope, self.f.root)[0]
                self.assertEqual(fresh_request, request)
                result = agent.run(self.loaded(), self.f.envelope, self.f.root, recover=True)
                self.assertEqual(result["requestSha256"], original["request_sha256"])
                self.assertEqual(fresh_request["deadline_utc"], "2030-01-01T00:00:00Z")
            self.f.path.write_bytes(self.f.before)
            self.f.profile["responseFraming"] = "raw-json"
            with self.assertRaisesRegex(agent.Held, "invocation_binding"):
                agent.run(self.loaded(), self.f.envelope, self.f.root, recover=True)
            self.f.assert_unchanged()
        self.assertTrue(methods); self.assertTrue(all(method == "GET" for method in methods))


if __name__ == "__main__": unittest.main()
