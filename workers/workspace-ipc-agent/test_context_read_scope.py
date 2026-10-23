"""Synthetic read authority controls; no sockets/providers/consumed proposals."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import time
import unittest
from unittest.mock import patch
from test_public_wire import PublicWireTests, agent


class ContextReadScopeTests(PublicWireTests):
    # Reuse independently authored DTO fixture, not its test methods.
    def setUp(self):
        super().setUp()
        self.profile['contextFiles'] = [{'path': 'src/source.txt', 'sha256': agent.sha(self.before)}]
        self.profile['contextReadPaths'] = ['src/source.txt']
        self.profile['allowedPaths'] = ['src/new.txt']; self.envelope['allowedPaths'] = ['src/new.txt']
        self.envelope['task'] = 'Create src/new.txt containing bounded newline; use supplied source as context only.'
        self.profile['taskSha256'] = agent.sha(self.envelope['task'].encode()); self.envelope['taskSha256'] = self.profile['taskSha256'][7:]
        self.creation = {'schemaVersion': 'vyral.workspace-edits.v1', 'edits': [{'path': 'src/new.txt', 'previousSha256': None, 'content': 'bounded\n'}]}

    def before_gateway(self, reason=None):
        with patch.object(agent, 'Gateway') as gateway:
            with self.assertRaises(agent.Held if reason else (agent.Held, OSError, UnicodeError)) as caught:
                agent.run(self.loaded(), self.envelope, self.root, recover=True)
            if reason: self.assertEqual(str(caught.exception), reason)
            gateway.assert_not_called()
        self.assertFalse((self.root / 'src/new.txt').exists())

    def test_read_authority_positive_keeps_exact_write_scope_full_context_and_v1_creation(self):
        p = self.loaded(); request, _ = agent.input_request(p, self.envelope, self.root)
        prompt = json.loads(request['prompt']); self.assertEqual(prompt['task'], self.envelope['task'])
        self.assertEqual(prompt['allowedPaths'], ['src/new.txt'])
        self.assertEqual(prompt['files'], [{'path': 'src/source.txt', 'sha256': agent.sha(self.before), 'content': self.before.decode()}])
        self.assertIn('null for new files', prompt['instruction']); self.assertIn('vyral.workspace-edits.v1', prompt['instruction'])
        self.assertFalse((self.root / 'src/new.txt').exists())
        self.assertEqual(self.apply(self.response(self.creation)), ['src/new.txt'])
        self.assertEqual((self.root / 'src/new.txt').read_bytes(), b'bounded\n'); self.assertEqual(self.path.read_bytes(), self.before)

    def test_read_authority_types_paths_duplicate_unknown_and_v1_refuse_before_transport(self):
        valid = copy.deepcopy(self.profile)
        for value in (None, False, 1, {}, 'src/source.txt', [], [None], [False], [1], ['src/source.txt'] * 33):
            self.profile = copy.deepcopy(valid); self.profile['contextReadPaths'] = value
            with patch.object(agent, 'Gateway') as gateway:
                with self.assertRaises(agent.Held): self.loaded()
                gateway.assert_not_called()
        for value in ('', '.', '/src/source.txt', '../source.txt', 'src//source.txt', 'src/./source.txt', 'src\\source.txt', '.git/config', 'src/.git/config', 'src/\nsource.txt', 'x' * 513):
            self.profile = copy.deepcopy(valid); self.profile['contextReadPaths'] = [value]
            self.profile['contextFiles'][0]['path'] = value
            with self.assertRaises(agent.Held): self.loaded()
        self.profile = copy.deepcopy(valid); self.profile['unknownReadScope'] = []
        with self.assertRaisesRegex(agent.Held, 'unsupported_profile'): self.loaded()
        self.profile = json.loads((Path(__file__).parents[2] / 'deploy/workspace-ipc/example-worker.json').read_text())
        self.loaded(); self.profile['contextReadPaths'] = ['src/source.txt']
        with self.assertRaisesRegex(agent.Held, 'unsupported_profile'): self.loaded()

    def test_read_manifest_order_exactness_overlap_and_avoided_refuse(self):
        valid = copy.deepcopy(self.profile)
        for paths in (['src/missing.txt'], ['src/source.txt', 'src/missing.txt'], ['src/source.txt', 'src/source.txt']):
            self.profile = copy.deepcopy(valid); self.profile['contextReadPaths'] = paths
            with self.assertRaisesRegex(agent.Held, 'context_read_manifest'): self.loaded()
        for scope in ('src', 'src/source.txt', 'src/source.txt/sub', '.'):
            for key in ('allowedPaths', 'avoidedPaths'):
                self.profile = copy.deepcopy(valid); self.profile[key] = [scope]
                with self.assertRaisesRegex(agent.Held, 'context_read_overlap'): self.loaded()
        self.profile = copy.deepcopy(valid)
        self.profile['contextFiles'].append({'path': 'src/source.txt/sub', 'sha256': agent.sha(b'synthetic')})
        self.profile['contextReadPaths'].append('src/source.txt/sub')
        with self.assertRaisesRegex(agent.Held, 'context_read_overlap'): self.loaded()
        self.profile = copy.deepcopy(valid)
        self.profile['contextFiles'].append({'path': 'src/other.txt', 'sha256': agent.sha(b'synthetic')})
        self.profile['contextReadPaths'] = ['src/other.txt', 'src/source.txt']
        with self.assertRaisesRegex(agent.Held, 'context_read_manifest'): self.loaded()

    def test_regular_existing_hash_utf8_ancestor_symlink_and_fifo_guards_before_gateway(self):
        self.path.write_bytes(b'changed'); self.before_gateway('context_binding'); self.path.write_bytes(self.before)
        self.path.unlink(); self.before_gateway(); self.path.mkdir(); self.before_gateway('regular_file_required'); self.path.rmdir()
        os.mkfifo(self.path); self.before_gateway('regular_file_required'); self.path.unlink()
        outside = self.root / 'outside.txt'; outside.write_bytes(self.before); self.path.symlink_to(outside)
        self.before_gateway('symlink'); self.path.unlink(); self.path.write_bytes(b'\xff')
        self.profile['contextFiles'][0]['sha256'] = agent.sha(b'\xff'); self.before_gateway(); self.path.write_bytes(self.before)
        self.profile['contextFiles'][0] = {'path': 'src/source.txt/sub', 'sha256': agent.sha(self.before)}
        self.profile['contextReadPaths'] = ['src/source.txt/sub']; self.before_gateway('path_ancestor')

    def test_context_source_window_final_input_and_reservation_bounds_preserved(self):
        valid = copy.deepcopy(self.profile); request, _ = agent.input_request(self.loaded(), self.envelope, self.root)
        size = len(request['prompt'].encode()); self.profile['maxInputBytes'] = size - 1; self.before_gateway('input_limit')
        self.profile['maxInputBytes'] = size; agent.input_request(self.loaded(), self.envelope, self.root)
        self.profile = copy.deepcopy(valid); self.profile['reservationRule']['max_prompt_bytes'] = size - 1; self.before_gateway('input_limit')
        self.profile = copy.deepcopy(valid); self.profile['maxReservationTokens'] = 4095; self.before_gateway('reservation_limit')
        self.profile = copy.deepcopy(valid); self.profile['maxInputBytes'] = len(self.before) - 1; self.before_gateway('context_limit')
        self.profile = copy.deepcopy(valid); self.profile['contextFiles'][0]['windows'] = [{'startLine': 1, 'lineCount': 1, 'sha256': agent.sha(b'wrong')}]
        self.before_gateway('context_binding')
        self.profile['contextFiles'][0]['windows'][0]['sha256'] = agent.sha(b'alpha\r\n')
        self.profile['maxSourceBytes'] = len(self.before) - 1; self.before_gateway('context_limit')

    def test_read_manifest_grants_no_edit_delete_or_sibling_authority_and_batch_is_atomic(self):
        for path, content in [('src/source.txt', 'modified'), ('src/source.txt', None), ('src/sibling.txt', 'sibling')]:
            proposal = copy.deepcopy(self.creation)
            proposal['edits'].append({'path': path, 'previousSha256': agent.sha(self.before) if path == 'src/source.txt' else None, 'content': content})
            with self.assertRaisesRegex(agent.Held, 'path_scope'): self.apply(self.response(proposal))
            self.unchanged(); self.assertFalse((self.root / 'src/sibling.txt').exists())

    def test_final_descriptor_read_does_not_follow_a_replaced_selected_file(self):
        outside = self.root / 'outside.txt'; outside.write_bytes(self.before)
        resolver = agent.scoped_path
        def replace(root, name, allowed, avoided):
            result = resolver(root, name, allowed, avoided)
            self.path.unlink(); self.path.symlink_to(outside); return result
        with patch.object(agent, 'scoped_path', replace): self.before_gateway()
        self.assertEqual(outside.read_bytes(), self.before)

    def test_get_only_recovery_preserves_manifest_original_request_and_no_renewal(self):
        self.profile['invocationDeadlineUtc'] = '2030-01-01T00:00:00Z'
        response = self.response(self.creation); p = self.loaded(); request, reservation = agent.input_request(p, self.envelope, self.root)
        original = {'state': 'succeeded', 'session_id': p['sessionId'], 'key': p['operationId'], 'request_sha256': agent.sha(agent.encode(request)),
            'reservation_tokens': reservation, 'result_sha256': agent.sha(agent.encode(response)), 'response': response, 'usage_tokens': 5}
        methods = []
        class Gateway:
            def __init__(self, p, _): self.p = p
            def request(self, method): methods.append(method); return {'session_id': p['sessionId'], 'grant_sha256': p['grantSha256'], 'backend_sha256': p['backendSha256']}
            def call(self, method, *_): methods.append(method); assert method == 'GET'; return original
        with patch.object(agent, 'Gateway', Gateway):
            for remaining in (60000, 30000):
                self.envelope['remainingMilliseconds'] = remaining
                result = agent.run(self.loaded(), self.envelope, self.root, recover=True); self.assertEqual(result['requestSha256'], original['request_sha256'])
                (self.root / 'src/new.txt').unlink()
            original_profile = copy.deepcopy(self.profile)
            self.profile.pop('contextReadPaths')
            with self.assertRaisesRegex(agent.Held, 'path_scope'): agent.run(self.loaded(), self.envelope, self.root, recover=True)
            self.profile = copy.deepcopy(original_profile); self.profile['invocationDeadlineUtc'] = '2030-01-01T00:00:01Z'
            with self.assertRaisesRegex(agent.Held, 'invocation_binding'): agent.run(self.loaded(), self.envelope, self.root, recover=True)
            self.profile = copy.deepcopy(original_profile); other = self.root / 'src/other.txt'; other.write_bytes(b'other')
            self.profile['contextFiles'].append({'path': 'src/other.txt', 'sha256': agent.sha(b'other')}); self.profile['contextReadPaths'].append('src/other.txt')
            with self.assertRaisesRegex(agent.Held, 'invocation_binding'): agent.run(self.loaded(), self.envelope, self.root, recover=True)
            self.unchanged()
        self.assertTrue(methods); self.assertTrue(all(m == 'GET' for m in methods))


# Do not inherit or discover the fixture base's wire controls here; those run
# independently against their original meaning in test_public_wire.py.
for name in list(vars(PublicWireTests)):
    if name.startswith('test_'): setattr(ContextReadScopeTests, name, None)
del PublicWireTests
if __name__ == '__main__': unittest.main()
