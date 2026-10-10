"""No-model Unix transport, binding, recovery and edit-boundary tests."""
import importlib.util
import json
from pathlib import Path
import socket
from socketserver import UnixStreamServer
from http.server import BaseHTTPRequestHandler
import tempfile
import threading
import time
import unittest

spec = importlib.util.spec_from_file_location("workspace_ipc_agent", Path(__file__).with_name("agent.py"))
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class Fixture:
    def __enter__(self):
        self.temp = tempfile.TemporaryDirectory(prefix="vyral-ipc-test-")
        self.root = Path(self.temp.name)
        (self.root / "src").mkdir()
        (self.root / "src/seed.txt").write_text("seed\n")
        self.token = "synthetic-worker-credential-" + "x" * 40
        (self.root / "credential").write_text(self.token)
        (self.root / "credential").chmod(0o600)
        self.profile = {
            "schemaVersion": "vyral.workspace-ipc-profile.v1", "socketPath": str(self.root / "ipc.sock"),
            "credentialFile": str(self.root / "credential"), "sessionId": "fixture-session", "operationId": "fixture-op",
            "contextRef": "urn:example:governed-context", "taskSha256": agent.sha("Fix π".encode()),
            "baseCommit": "a" * 40, "allowedPaths": ["src"], "avoidedPaths": ["checks"],
            "grantSha256": "sha256:" + "a" * 64, "backendSha256": "sha256:" + "b" * 64,
            "providerId": "fixture-chat", "modelId": "fixture-model-pinned",
            "contextFiles": [{"path": "src/seed.txt", "sha256": agent.sha(b"seed\n")}],
            "maxInputBytes": 4096, "maxOutputTokens": 128, "reservationOverheadTokens": 2,
            "maxReservationTokens": 8192, "maxExecutionSeconds": 2, "maxResponseBytes": 32768,
            "maxEdits": 4, "maxEditBytes": 1024, "allowInvoke": True,
        }
        self.save()
        self.envelope = {"schemaVersion": "vyral.workspace-agent-input.v1", "task": "Fix π", "taskSha256": agent.sha("Fix π".encode()).split(":")[1],
                         "baseCommit": "a" * 40, "allowedPaths": ["src"], "avoidedPaths": ["checks"],
                         "modelId": "fixture-model-pinned", "remainingMilliseconds": 2000}
        self.posts = 0
        self.routes = []
        self.call = None
        self.output = {"schemaVersion": "vyral.workspace-edits.v1", "edits": [{"path": "src/new.txt", "previousSha256": None, "content": "bounded\n"}]}
        self.drop = False
        self.delay = 0
        self.bad_binding = False
        self.unknown = False
        self.echo_secret = False
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_GET(self): self.handle_request(False)
            def do_POST(self): self.handle_request(True)
            def handle_request(self, post):
                fixture.routes.append(("POST" if post else "GET", self.path))
                if self.headers.get("Authorization") != "Bearer " + fixture.token:
                    self.respond({"state": "held"}, 409); return
                p = fixture.profile
                if self.path.endswith("/calls/fixture-op"):
                    if post:
                        fixture.posts += 1
                        body = self.rfile.read(int(self.headers["Content-Length"]))
                        request = agent.parse(body)
                        self.server.last_request = request
                        time.sleep(fixture.delay)
                        response = {"status": "Succeeded", "output": {"message": {"role": "assistant", "content": json.dumps(fixture.output)}},
                                    "usage": {"input_tokens": 2, "output_tokens": 3},
                                    "provider_result": {"provider": p["providerId"], "modelId": p["modelId"]}}
                        rule = p.get("reservationRule", {"method": "utf8_bytes_plus_output", "overhead_tokens": p.get("reservationOverheadTokens")})
                        reservation = rule["context_tokens"] if rule["method"] == "native_context_window" else len(request["prompt"].encode()) + rule["overhead_tokens"] + request["max_output_tokens"]
                        fixture.call = {"state": "succeeded", "session_id": p["sessionId"], "key": p["operationId"],
                                        "request_sha256": agent.sha(agent.encode(request)), "reservation_tokens": reservation,
                                        "response": response, "result_sha256": agent.sha(agent.encode(response)), "usage_tokens": 5}
                        if fixture.unknown:
                            fixture.call["state"] = "unresolved"; fixture.call["usage_tokens"] = None
                        if fixture.drop:
                            self.connection.shutdown(socket.SHUT_RDWR); return
                    value = fixture.call or {"state": "not_found"}
                else:
                    value = {"session_id": p["sessionId"], "state": "open", "grant_sha256": p["grantSha256"], "backend_sha256": p["backendSha256"]}
                    if fixture.bad_binding: value["backend_sha256"] = "sha256:" + "c" * 64
                if fixture.echo_secret: value["unexpected"] = fixture.token
                self.respond(value)
            def respond(self, value, status=200):
                body = agent.encode(value)
                self.send_response(status); self.send_header("Content-Length", str(len(body))); self.end_headers()
                try: self.wfile.write(body)
                except (BrokenPipeError, ConnectionResetError): pass
        self.server = UnixStreamServer(self.profile["socketPath"], Handler)
        Path(self.profile["socketPath"]).chmod(0o600)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return self

    def save(self):
        path = self.root / "profile.json"
        path.write_bytes(agent.encode(self.profile)); path.chmod(0o600)

    def loaded(self):
        self.save(); return agent.load_profile(self.root / "profile.json")

    def run(self, recover=False):
        return agent.run(self.loaded(), self.envelope, self.root, recover)

    def __exit__(self, *_):
        self.server.shutdown(); self.server.server_close(); self.thread.join(); self.temp.cleanup()


class AgentTests(unittest.TestCase):
    def large_source(self, f, native=False):
        lines = [f"const repeated_{n:04d} = 'unchanged source padding';\n" for n in range(3000)]
        lines[2221] = "const unique_π = old_duration;\n"
        body = "".join(lines).encode()
        self.assertGreater(len(body), 100 * 1024)
        (f.root / "src/large.ts").write_bytes(body)
        f.profile.update(schemaVersion="vyral.workspace-ipc-profile.v2", maxSourceBytes=262144, maxInputBytes=8192,
                         reservationRule={"method": "native_context_window", "context_tokens": 4096, "max_prompt_bytes": 8192} if native else {"method": "utf8_bytes_plus_output", "overhead_tokens": 2},
                         contextFiles=[{"path": "src/large.ts", "sha256": agent.sha(body), "windows": [{"startLine": 2220, "lineCount": 5, "sha256": agent.sha("".join(lines[2219:2224]).encode())}]}])
        del f.profile["reservationOverheadTokens"]
        f.output = {"schemaVersion": "vyral.workspace-edits.v2", "edits": [{"path": "src/large.ts", "previousSha256": agent.sha(body), "replacements": [{"oldText": "old_duration", "newText": "new_duration"}]}]}
        return body

    def test_large_source_window_and_small_patch_preserve_all_other_bytes(self):
        with Fixture() as f:
            before = self.large_source(f)
            f.run()
            self.assertEqual((f.root / "src/large.ts").read_bytes(), before.replace(b"old_duration", b"new_duration"))
            prompt = json.loads(f.server.last_request["prompt"])
            self.assertNotIn("content", prompt["files"][0])
            self.assertEqual(len(prompt["files"][0]["windows"]), 1)
            self.assertNotIn("repeated_0000", f.server.last_request["prompt"])
            self.assertIn("unique_π", f.server.last_request["prompt"])
            self.assertLess(len(f.server.last_request["prompt"].encode()), 8192)

    def test_large_source_stale_hash_and_window_refuse_before_post(self):
        for mode in ("file", "window"):
            with self.subTest(mode=mode), Fixture() as f:
                before = self.large_source(f)
                if mode == "file": (f.root / "src/large.ts").write_bytes(before + b"changed")
                else: f.profile["contextFiles"][0]["windows"][0]["sha256"] = "sha256:" + "0" * 64
                with self.assertRaisesRegex(agent.Held, "context_binding"): f.run()
                self.assertEqual(f.posts, 0)

    def test_anchored_patch_refuses_whole_batch_before_any_edit(self):
        for fault in ("stale", "missing", "ambiguous", "overlap", "scope", "symlink"):
            with self.subTest(fault=fault), Fixture() as f:
                before = self.large_source(f)
                f.profile["contextFiles"].append({"path": "src/seed.txt", "sha256": agent.sha(b"seed\n")})
                edit = f.output["edits"][0]
                if fault == "stale": edit["previousSha256"] = "sha256:" + "0" * 64
                if fault == "missing": edit["replacements"][0]["oldText"] = "not present"
                if fault == "ambiguous": edit["replacements"][0]["oldText"] = "unchanged source padding"
                if fault == "overlap": edit["replacements"].append({"oldText": "const unique_π = old_duration;", "newText": "replacement"})
                if fault == "scope": edit["path"] = "checks/large.ts"
                if fault == "symlink":
                    (f.root / "src/link.ts").symlink_to(f.root / "src/large.ts"); edit["path"] = "src/link.ts"
                # A valid first full-file-independent patch cannot be applied
                # before the invalid second edit has been checked.
                f.output["edits"].insert(0, {"path": "src/seed.txt", "previousSha256": agent.sha(b"seed\n"), "replacements": [{"oldText": "seed", "newText": "modified"}]})
                with self.assertRaises(agent.Held): f.run()
                self.assertEqual((f.root / "src/large.ts").read_bytes(), before)
                self.assertEqual((f.root / "src/seed.txt").read_bytes(), b"seed\n")

    def test_native_window_reservation_is_fixed_and_v1_remains_closed(self):
        with Fixture() as f:
            self.large_source(f, native=True)
            f.profile["maxReservationTokens"] = 4096
            f.run()
            self.assertEqual(f.call["reservation_tokens"], 4096)
        with Fixture() as f:
            f.profile["maxSourceBytes"] = 262144
            with self.assertRaisesRegex(agent.Held, "unsupported_profile"): f.loaded()
        with Fixture() as f:
            self.large_source(f, native=True)
            f.profile["reservationRule"]["max_prompt_bytes"] = 8
            with self.assertRaisesRegex(agent.Held, "input_limit"): f.run()
            self.assertEqual(f.posts, 0)

    def test_context_is_delivered_and_one_main_call_stages_bounded_edits(self):
        with Fixture() as f:
            result = f.run()
            self.assertEqual(f.posts, 1)
            prompt = json.loads(f.server.last_request["prompt"])
            self.assertEqual(prompt["task"], "Fix π")
            self.assertEqual(prompt["files"][0]["content"], "seed\n")
            self.assertNotIn(f.token, f.server.last_request["prompt"])
            self.assertEqual((f.root / "src/new.txt").read_text(), "bounded\n")
            self.assertEqual(result["derivedTotalTokens"], 5)
            self.assertFalse(result["independentlyIssued"])
            self.assertEqual(result["scopeCompleteness"], "partial")

    def test_lost_ack_recovery_uses_get_without_resend(self):
        with Fixture() as f:
            f.drop = True
            with self.assertRaises(Exception): f.run()
            self.assertFalse((f.root / "src/new.txt").exists())
            f.drop = False
            index = len(f.routes)
            f.run(recover=True)
            self.assertEqual(f.posts, 1)
            self.assertTrue(all(method == "GET" for method, _ in f.routes[index:]))

    def test_recovery_of_missing_invocation_does_not_create_one(self):
        with Fixture() as f:
            with self.assertRaisesRegex(agent.Held, "not_admitted"): f.run(recover=True)
            self.assertEqual(f.posts, 0)

    def test_bindings_and_context_mutation_refuse_before_post(self):
        for field, value in [("task", "different"), ("baseCommit", "b" * 40), ("allowedPaths", ["."]), ("modelId", "other-model")]:
            with self.subTest(field=field), Fixture() as f:
                f.envelope[field] = value
                with self.assertRaises(agent.Held): f.run()
                self.assertEqual(f.posts, 0)
        with Fixture() as f:
            (f.root / "src/seed.txt").write_text("changed")
            with self.assertRaisesRegex(agent.Held, "context_binding"): f.run()
            self.assertEqual(f.posts, 0)

    def test_backend_mismatch_and_disabled_invocation_refuse_before_post(self):
        with Fixture() as f:
            f.bad_binding = True
            with self.assertRaisesRegex(agent.Held, "owner_binding"): f.run()
            self.assertEqual(f.posts, 0)
        with Fixture() as f:
            f.profile["allowInvoke"] = False
            with self.assertRaisesRegex(agent.Held, "not_admitted"): f.run()
            self.assertEqual(f.posts, 0)

    def test_reservation_and_context_byte_limits_refuse_before_post(self):
        for field, value in [("maxReservationTokens", 1), ("maxInputBytes", 2)]:
            with self.subTest(field=field), Fixture() as f:
                f.profile[field] = value
                with self.assertRaises(agent.Held): f.run()
                self.assertEqual(f.posts, 0)

    def test_bad_credentials_and_secret_echo_are_held(self):
        with Fixture() as f:
            (f.root / "credential").write_text("wrong-credential-" + "y" * 40)
            with self.assertRaises(agent.Held): f.run()
            self.assertEqual(f.posts, 0)
        with Fixture() as f:
            f.echo_secret = True
            with self.assertRaisesRegex(agent.Held, "credential_in_response"): f.run()
            self.assertEqual(f.posts, 0)

    def test_unsafe_edit_paths_and_preconditions_leave_workspace_unchanged(self):
        for path, previous in [("../escape", None), ("checks/evaluator.py", None), (".git/config", None), ("src/seed.txt", None)]:
            with self.subTest(path=path), Fixture() as f:
                f.output["edits"] = [{"path": path, "previousSha256": previous, "content": "bad"}]
                with self.assertRaises(agent.Held): f.run()
                self.assertEqual((f.root / "src/seed.txt").read_text(), "seed\n")
                self.assertFalse((f.root / "src/new.txt").exists())

    def test_symlink_and_oversized_edits_are_held(self):
        with Fixture() as f:
            (f.root / "src/link").symlink_to(f.root / "credential")
            f.output["edits"][0]["path"] = "src/link"
            with self.assertRaisesRegex(agent.Held, "symlink"): f.run()
        with Fixture() as f:
            f.output["edits"][0]["content"] = "z" * 1025
            with self.assertRaisesRegex(agent.Held, "edit_limit"): f.run()
            self.assertFalse((f.root / "src/new.txt").exists())

    def test_unknown_usage_never_becomes_zero_or_a_successful_edit(self):
        with Fixture() as f:
            f.unknown = True; f.envelope["remainingMilliseconds"] = 120
            with self.assertRaises(agent.Held): f.run()
            self.assertEqual(f.posts, 1)
            self.assertFalse((f.root / "src/new.txt").exists())

    def test_deadline_on_uncertain_send_has_no_automatic_resend(self):
        with Fixture() as f:
            f.delay = 0.3; f.envelope["remainingMilliseconds"] = 100
            start = time.monotonic()
            with self.assertRaises(Exception): f.run()
            self.assertLess(time.monotonic() - start, 0.5)
            self.assertEqual(f.posts, 1)
            self.assertFalse((f.root / "src/new.txt").exists())

    def test_duplicate_json_and_private_credential_links_are_rejected(self):
        with self.assertRaises(agent.Held): agent.parse('{"x":1,"x":2}')
        with Fixture() as f:
            (f.root / "linked").symlink_to(f.root / "credential")
            with self.assertRaises(OSError): agent.private_read(f.root / "linked", 4096)
            (f.root / "credential").chmod(0o644)
            with self.assertRaises(agent.Held): f.run()


if __name__ == "__main__": unittest.main()
