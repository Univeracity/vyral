"""Independently authored public-result wire and creation controls; synthetic only."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('wire_worker', Path(__file__).with_name('agent.py'))
agent = importlib.util.module_from_spec(spec); spec.loader.exec_module(agent)


class PublicWireTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='vyral-public-wire-'); self.addCleanup(temp.cleanup)
        self.root = Path(temp.name); (self.root / 'src').mkdir()
        self.path = self.root / 'src/source.txt'; self.before = b'alpha\r\nomega\r\n'; self.path.write_bytes(self.before)
        self.config = agent.sha(b'synthetic-config')
        self.profile = json.loads((Path(__file__).parents[2] / 'deploy/workspace-ipc/example-worker-v2.json').read_text())
        self.profile.update(providerId='synthetic-provider', modelId='synthetic-model', providerConfigHash=self.config,
            taskSha256=agent.sha(b'Replace alpha with beta, preserving omega.'),
            contextFiles=[{'path': 'src/source.txt', 'sha256': agent.sha(self.before),
                'windows': [{'startLine': 1, 'lineCount': 2, 'sha256': agent.sha(self.before)}]}])
        self.envelope = {'schemaVersion': 'vyral.workspace-agent-input.v1', 'task': 'Replace alpha with beta, preserving omega.',
            'taskSha256': self.profile['taskSha256'].split(':')[1], 'baseCommit': self.profile['baseCommit'],
            'allowedPaths': self.profile['allowedPaths'], 'avoidedPaths': self.profile['avoidedPaths'],
            'modelId': self.profile['modelId'], 'remainingMilliseconds': 60000}
        self.proposal = {'schemaVersion': 'vyral.workspace-edits.v2', 'edits': [{'path': 'src/source.txt',
            'previousSha256': agent.sha(self.before), 'replacements': [{'oldText': 'alpha', 'newText': 'beta'}]}]}

    def loaded(self):
        p = self.root / 'profile.json'; p.write_bytes(agent.encode(self.profile)); p.chmod(0o600)
        return agent.load_profile(p)

    def response(self, proposal=None):
        # Public ProviderRunResult fields, independently authored from its DTO.
        content = agent.encode(self.proposal if proposal is None else proposal, utf8=True).decode()
        result = {'status': 'Succeeded', 'provider': 'synthetic-provider', 'capability': 'ai.chat', 'operation': 'run',
            'mode': 'advisory', 'output': {'message': {'role': 'assistant', 'content': content}},
            'trace': {'provider': 'synthetic-provider', 'capability': 'ai.chat', 'operation': 'run', 'mode': 'advisory',
                'modelId': 'synthetic-model', 'configHash': self.config}}
        return {'status': 'Succeeded', 'output': result['output'], 'usage': {'input_tokens': 2, 'output_tokens': 3}, 'provider_result': result}

    def apply(self, response):
        return agent.apply_edits(self.profile, self.envelope, response, self.root, time.monotonic() + 2)

    def unchanged(self):
        self.assertEqual(self.path.read_bytes(), self.before); self.assertFalse((self.root / 'src/new.txt').exists())

    def test_public_trace_shape_binds_exact_provider_model_config_without_top_level_model(self):
        response = self.response(); self.assertNotIn('modelId', response['provider_result'])
        self.assertEqual(self.apply(response), ['src/source.txt'])
        self.assertEqual(self.path.read_bytes(), b'beta\r\nomega\r\n')

    def test_explicitly_limited_traceless_legacy_route_remains_and_cannot_bypass_pin(self):
        response = self.response(); response['provider_result'] = {'provider': 'synthetic-provider', 'modelId': 'synthetic-model'}
        with self.assertRaisesRegex(agent.Held, 'provider_binding'): self.apply(response)
        self.unchanged(); self.profile.pop('providerConfigHash')
        self.assertEqual(self.apply(response), ['src/source.txt']); self.path.write_bytes(self.before)
        response['provider_result']['modelId'] = 'wrong'
        with self.assertRaisesRegex(agent.Held, 'provider_binding'): self.apply(response)
        self.unchanged()
        with self.assertRaisesRegex(agent.Held, 'provider_config_binding_required'): self.apply(self.response())
        self.unchanged()

    def test_config_profile_field_is_closed_digest_and_preserves_request_bytes(self):
        configured = self.loaded(); request = agent.input_request(configured, self.envelope, self.root)
        self.profile.pop('providerConfigHash'); original = self.loaded()
        self.assertEqual(request, agent.input_request(original, self.envelope, self.root))
        self.assertNotEqual(configured['profileSha256'], original['profileSha256'])
        for value in (None, False, 1, [], {}, '', 'a' * 64, 'sha256:' + 'A' * 64, 'sha256:bad'):
            self.profile['providerConfigHash'] = value
            with self.assertRaisesRegex(agent.Held, 'invalid_digest'): self.loaded()
        self.profile = json.loads((Path(__file__).parents[2] / 'deploy/workspace-ipc/example-worker.json').read_text())
        self.loaded(); self.profile['providerConfigHash'] = self.config
        with self.assertRaisesRegex(agent.Held, 'unsupported_profile'): self.loaded()

    def test_missing_malformed_mismatching_and_conflicting_identities_refuse_before_parse(self):
        mutations = []
        for field in ('provider', 'modelId', 'configHash'):
            for value in (None, False, 1, 'wrong'):
                mutations.append(('trace', field, value))
            mutations.append(('trace', field, 'REMOVE'))
        for field in ('status', 'provider', 'capability', 'operation', 'mode'):
            mutations += [('provider', field, 'wrong'), ('provider', field, 'REMOVE')]
        for field in ('modelId', 'configHash'):
            mutations += [('provider', field, None), ('provider', field, 'wrong')]
        for field in ('capability', 'operation', 'mode'):
            mutations += [('trace', field, 'wrong'), ('trace', field, 'REMOVE')]
        for where, field, value in mutations:
            with self.subTest(where=where, field=field, value=value):
                response = self.response(); target = response['provider_result']['trace'] if where == 'trace' else response['provider_result']
                if value == 'REMOVE': target.pop(field)
                else: target[field] = value
                with patch.object(agent, 'proposal_json') as parser:
                    with self.assertRaisesRegex(agent.Held, 'provider_binding'): self.apply(response)
                    parser.assert_not_called()
                self.unchanged()
        for value in (None, [], 'wrong'):
            response = self.response(); response['provider_result']['trace'] = value
            with self.assertRaisesRegex(agent.Held, 'provider_binding'): self.apply(response)
            self.unchanged()
        response = self.response(); response['provider_result'].update(modelId='synthetic-model', configHash=self.config)
        self.assertEqual(self.apply(response), ['src/source.txt'])

    def test_public_wire_keeps_hash_window_json_and_atomic_batch_guards(self):
        for fault, reason in (('hash', 'edit_precondition'), ('anchor', 'anchor_not_unique'), ('schema', 'windowed_patch_required')):
            proposal = copy.deepcopy(self.proposal)
            if fault == 'hash': proposal['edits'][0]['previousSha256'] = agent.sha(b'wrong')
            if fault == 'anchor': proposal['edits'][0]['replacements'][0]['oldText'] = 'absent'
            if fault == 'schema': proposal = {'schemaVersion': 'vyral.workspace-edits.v1', 'edits': []}
            with self.assertRaisesRegex(agent.Held, reason): self.apply(self.response(proposal))
            self.unchanged()
        proposal = copy.deepcopy(self.proposal); proposal['edits'].append({'path': 'src/other.txt', 'previousSha256': None, 'replacements': [{'oldText': 'x', 'newText': 'y'}]})
        with self.assertRaisesRegex(agent.Held, 'edit_precondition'): self.apply(self.response(proposal))
        self.unchanged()
        response = self.response(); response['output']['message']['content'] = '```json\n' + response['output']['message']['content'] + '\n```'
        with self.assertRaisesRegex(agent.Held, 'invalid_json'): self.apply(response)
        self.unchanged()

    def test_get_only_recovery_revalidates_original_public_config(self):
        self.profile['invocationDeadlineUtc'] = '2030-01-01T00:00:00Z'
        response = self.response(); p = self.loaded(); request, reservation = agent.input_request(p, self.envelope, self.root)
        original = {'state': 'succeeded', 'session_id': p['sessionId'], 'key': p['operationId'], 'request_sha256': agent.sha(agent.encode(request)),
            'reservation_tokens': reservation, 'result_sha256': agent.sha(agent.encode(response)), 'response': response, 'usage_tokens': 5}
        methods = []
        class Gateway:
            def __init__(self, p, deadline): self.p = p
            def request(self, method):
                methods.append(method)
                return {'session_id': self.p['sessionId'], 'grant_sha256': self.p['grantSha256'], 'backend_sha256': self.p['backendSha256'], 'state': 'open'}
            def call(self, method, *_):
                methods.append(method); assert method == 'GET'; return original
        with patch.object(agent, 'Gateway', Gateway):
            for remaining in (60000, 30000):
                self.path.write_bytes(self.before); self.envelope['remainingMilliseconds'] = remaining
                result = agent.run(self.loaded(), self.envelope, self.root, recover=True)
                self.assertEqual(result['requestSha256'], original['request_sha256'])
            self.path.write_bytes(self.before); self.profile['providerConfigHash'] = agent.sha(b'changed-config')
            with self.assertRaisesRegex(agent.Held, 'provider_binding'): agent.run(self.loaded(), self.envelope, self.root, recover=True)
            self.unchanged()
        self.assertTrue(methods); self.assertTrue(all(m == 'GET' for m in methods))

    def test_unknown_or_invalid_usage_still_refuses_without_writes(self):
        response = self.response(); p = self.loaded(); request, reservation = agent.input_request(p, self.envelope, self.root)
        result = {'state': 'succeeded', 'session_id': p['sessionId'], 'key': p['operationId'], 'request_sha256': agent.sha(agent.encode(request)),
            'reservation_tokens': reservation, 'usage_tokens': 5}
        class Gateway:
            def __init__(self, *_): pass
            def request(self, *_): return {'session_id': p['sessionId'], 'grant_sha256': p['grantSha256'], 'backend_sha256': p['backendSha256'], 'state': 'open'}
            def call(self, method, *_): assert method == 'GET'; return result
        with patch.object(agent, 'Gateway', Gateway):
            for usage in (None, {}, {'input_tokens': -1, 'output_tokens': 3}, {'input_tokens': True, 'output_tokens': 3}, {'input_tokens': 2, 'output_tokens': 513}):
                response['usage'] = usage; result.update(response=response, result_sha256=agent.sha(agent.encode(response)))
                with self.assertRaises(agent.Held): agent.run(p, self.envelope, self.root, recover=True)
                self.unchanged()

    def test_full_context_v1_exact_new_file_creation_and_invalid_batch_are_atomic(self):
        self.profile['contextFiles'] = [{'path': 'src/source.txt', 'sha256': agent.sha(self.before)}]
        self.profile['allowedPaths'] = ['src/source.txt', 'src/new.txt']; self.envelope['allowedPaths'] = self.profile['allowedPaths']
        creation = {'schemaVersion': 'vyral.workspace-edits.v1', 'edits': [{'path': 'src/new.txt', 'previousSha256': None, 'content': 'bounded\n'}]}
        for fault, reason in (('second', 'edit_precondition'), ('oversize', 'edit_limit'), ('deletion', 'edit_precondition')):
            proposal = copy.deepcopy(creation)
            if fault == 'second': proposal['edits'].append({'path': 'src/source.txt', 'previousSha256': agent.sha(b'wrong'), 'content': 'wrong'})
            if fault == 'oversize': proposal['edits'][0]['content'] = 'x' * (self.profile['maxEditBytes'] + 1)
            if fault == 'deletion': proposal['edits'][0]['content'] = None
            with self.assertRaisesRegex(agent.Held, reason): self.apply(self.response(proposal))
            self.unchanged()
        self.assertEqual(self.apply(self.response(creation)), ['src/new.txt'])
        self.assertEqual((self.root / 'src/new.txt').read_bytes(), b'bounded\n'); self.assertEqual(self.path.read_bytes(), self.before)


if __name__ == '__main__': unittest.main()
