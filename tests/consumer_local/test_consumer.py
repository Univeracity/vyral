import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from examples.python.local_execution_consumer import ConsumerHarness, accept_calculator_edit, is_live, read_request_body


class ConsumerPolicyTests(unittest.TestCase):
    def test_process_disappearing_during_proc_read_is_not_live(self):
        for error in [FileNotFoundError(), ProcessLookupError()]:
            with patch.object(Path, 'read_text', side_effect=error):
                self.assertFalse(is_live(123))
        with patch.object(Path, 'read_text', side_effect=PermissionError()):
            with self.assertRaises(PermissionError):
                is_live(123)

    def test_retained_receipts_cannot_be_overwritten_by_a_new_attempt(self):
        with tempfile.TemporaryDirectory() as temp:
            receipt = Path(temp) / 'receipt.json'
            receipt.write_text('retained evidence')
            with self.assertRaisesRegex(ValueError, 'receipt_directory_must_be_empty'):
                ConsumerHarness(SimpleNamespace(output=temp, work_root=temp))
            self.assertEqual('retained evidence', receipt.read_text())

    def test_api_shutdown_is_attempted_when_owner_cleanup_fails(self):
        harness = ConsumerHarness.__new__(ConsumerHarness)
        harness.owner, harness.api = object(), object()
        stopped = []
        def stop(process):
            stopped.append(process)
            if process is harness.owner:
                raise RuntimeError('owned_descendants_survived')
        harness.stop = stop
        with self.assertRaisesRegex(RuntimeError, 'owned_descendants_survived'):
            harness.shutdown()
        self.assertEqual([harness.owner, harness.api], stopped)

    def test_chunked_transport_preserves_exact_payload_bytes(self):
        payload = '{"messages":[{"content":"café"}]}'.encode()
        chunks = [payload[:9], payload[9:]]
        wire = b''.join(f'{len(chunk):x}\r\n'.encode() + chunk + b'\r\n' for chunk in chunks) + b'0\r\n\r\n'
        handler = SimpleNamespace(headers={'Transfer-Encoding': 'chunked'}, rfile=io.BytesIO(wire))
        self.assertEqual(payload, read_request_body(handler))

    def test_malformed_or_oversized_transport_refuses(self):
        for body in [b'100001\r\n', b'3\r\n{}\r\n0\r\n\r\n', b'bad-header\n']:
            with self.subTest(body=body), self.assertRaises(ValueError):
                read_request_body(SimpleNamespace(headers={'Transfer-Encoding': 'chunked'}, rfile=io.BytesIO(body)))

    def test_consumer_validates_before_any_write(self):
        original = 'def add(left, right):\n    return left - right\n'
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            file = root / 'calculator.py'
            file.write_text(original)
            for edit in [
                {'path': '../other.py', 'before': 'return left - right', 'after': 'return left + right'},
                {'path': 'calculator.py', 'before': 'wrong', 'after': 'return left + right'},
                {'path': 'calculator.py', 'before': 'return left - right', 'after': 'return __import__("os").system("true")'},
                {'path': 'calculator.py', 'before': 'return left - right', 'after': 'return left * right'},
            ]:
                with self.subTest(edit=edit), self.assertRaises(ValueError):
                    accept_calculator_edit(root, json.dumps({'edits': [edit]}))
                self.assertEqual(original, file.read_text())
            result = accept_calculator_edit(root, json.dumps({'edits': [{'path': 'calculator.py',
                'before': 'return left - right', 'after': 'return (left + right)'}]}))
            self.assertEqual(4, result['validationCases'])
            self.assertIn('return (left + right)', file.read_text())


if __name__ == '__main__':
    unittest.main()
