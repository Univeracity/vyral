"""Pure no-service controls for the public profile and gateway deadline bytes."""
from datetime import datetime, timezone
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("workspace_ipc_agent", Path(__file__).with_name("agent.py"))
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class DeadlineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="vyral-deadline-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.profile = {"schemaVersion": "vyral.workspace-ipc-profile.v2", "socketPath": "/private/no-launch.sock",
            "credentialFile": "/private/no-launch.credential", "sessionId": "fixture", "operationId": "original",
            "contextRef": "urn:example:context", "taskSha256": agent.sha(b"fixture"), "baseCommit": "a" * 40,
            "allowedPaths": ["src"], "avoidedPaths": ["checks"], "grantSha256": agent.sha(b"grant"),
            "backendSha256": agent.sha(b"backend"), "providerId": "fixture", "modelId": "fixture", "contextFiles": [],
            "maxInputBytes": 4096, "maxOutputTokens": 512, "reservationRule": {"method": "native_context_window",
            "context_tokens": 4096, "max_prompt_bytes": 4096}, "maxSourceBytes": 4096, "maxReservationTokens": 4096,
            "maxExecutionSeconds": 240, "maxResponseBytes": 32768, "maxEdits": 1, "maxEditBytes": 1024,
            "allowInvoke": True, "invocationDeadlineUtc": "2026-10-10T10:04:00.000000Z"}
        self.envelope = {"schemaVersion": "vyral.workspace-agent-input.v1", "task": "fixture",
            "taskSha256": agent.sha(b"fixture").split(":")[1], "baseCommit": "a" * 40,
            "allowedPaths": ["src"], "avoidedPaths": ["checks"], "modelId": "fixture", "remainingMilliseconds": 240000}

    def loaded(self):
        with patch.object(agent, "private_read", return_value=agent.encode(self.profile)):
            return agent.load_profile(self.root / "profile.json")

    def test_long_profile_accepts_only_frozen_deadline_and_closed_limits(self):
        for maximum in (240, 600, 1500, 1800, 3600):
            self.profile["maxExecutionSeconds"] = maximum
            self.assertEqual(self.loaded()["maxExecutionSeconds"], maximum)
        for maximum in (0, 3601, True):
            self.profile["maxExecutionSeconds"] = maximum
            with self.assertRaisesRegex(agent.Held, "invalid_limit"): self.loaded()
        self.profile["maxExecutionSeconds"] = 240
        self.profile.pop("invocationDeadlineUtc")
        with self.assertRaisesRegex(agent.Held, "invocation_deadline_required"): self.loaded()
        self.profile["maxExecutionSeconds"] = 120
        self.assertEqual(self.loaded()["maxExecutionSeconds"], 120)
        self.profile["unknownDeadline"] = "2026-10-10T10:04:00Z"
        with self.assertRaisesRegex(agent.Held, "unsupported_profile"): self.loaded()

    def test_deadline_format_is_utc_exact_and_expired_profile_remains_readable(self):
        for text in ("2026-10-10T10:04:00Z", "2026-10-10T10:04:00.0123456+00:00", "2000-01-01T00:00:00Z"):
            self.profile["invocationDeadlineUtc"] = text
            self.assertEqual(self.loaded()["invocationDeadlineUtc"], text)
        for text in (None, 123, "2026-10-10T10:04:00", "2026-10-10T10:04:00+01:00", "2026-99-10T10:04:00Z", "2026-10-10T10:04:00.12345678Z"):
            self.profile["invocationDeadlineUtc"] = text
            with self.assertRaisesRegex(agent.Held, "invalid_invocation_deadline"): self.loaded()

    def test_original_request_digest_survives_fresh_remaining_envelope(self):
        original, _ = agent.input_request(self.loaded(), self.envelope, self.root)
        self.envelope["remainingMilliseconds"] = 30000
        recovered, _ = agent.input_request(self.loaded(), self.envelope, self.root)
        self.assertEqual(original["deadline_utc"], self.profile["invocationDeadlineUtc"])
        self.assertEqual(agent.sha(agent.encode(original)), agent.sha(agent.encode(recovered)))
        self.profile["invocationDeadlineUtc"] = "2026-10-10T10:04:01Z"
        changed, _ = agent.input_request(self.loaded(), self.envelope, self.root)
        self.assertNotEqual(agent.sha(agent.encode(original)), agent.sha(agent.encode(changed)))

    def deadline_at_gateway(self, milliseconds, maximum=240, now="2026-10-10T10:03:50Z", recover=False):
        self.envelope["remainingMilliseconds"] = milliseconds
        self.profile["maxExecutionSeconds"] = maximum
        captured = []
        class Clock:
            fromisoformat = datetime.fromisoformat
            @staticmethod
            def now(_): return datetime.fromisoformat(now.replace("Z", "+00:00"))
        def gateway(profile, deadline):
            captured.append(deadline)
            raise agent.Held("captured_before_transport")
        with patch.object(agent, "datetime", Clock), patch.object(agent.time, "monotonic", return_value=100), patch.object(agent, "Gateway", gateway):
            with self.assertRaisesRegex(agent.Held, "captured_before_transport"):
                agent.run(self.profile, self.envelope, self.root, recover=recover)
        return captured[0]

    def test_local_deadline_is_minimum_without_recovery_renewal(self):
        self.assertEqual(self.deadline_at_gateway(240000), 110)  # absolute UTC remainder
        self.assertEqual(self.deadline_at_gateway(3000), 103)  # host envelope remainder
        self.assertEqual(self.deadline_at_gateway(600000, maximum=2), 102)  # profile ceiling
        self.assertEqual(self.deadline_at_gateway(240000, recover=True), 110)

    def test_expired_and_excess_remaining_refuse_before_transport(self):
        self.profile["invocationDeadlineUtc"] = "2000-01-01T00:00:00Z"
        with patch.object(agent, "Gateway") as gateway:
            for recover in (False, True):
                with self.assertRaisesRegex(agent.Held, "deadline"): agent.run(self.profile, self.envelope, self.root, recover)
            self.envelope["remainingMilliseconds"] = 3600001
            with self.assertRaisesRegex(agent.Held, "invalid_limit"): agent.run(self.profile, self.envelope, self.root)
            gateway.assert_not_called()


if __name__ == "__main__": unittest.main()
