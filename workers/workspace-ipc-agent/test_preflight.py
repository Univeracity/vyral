"""Synthetic public CLI controls; frozen old-renderer digests are test fixtures.
No sockets, credentials, provider calls or consumer private production APIs.
"""
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

WORKER = Path(__file__).with_name('agent.py').resolve()
GOLDEN = json.loads(Path(__file__).with_name('preflight-render-fixtures.json').read_text())['cases']

def load_module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result

agent = load_module(WORKER, 'preflight_worker')


class PreflightTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='vyral-preflight-')
        self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        (self.root/'src').mkdir();(self.root/'checks').mkdir();(self.root/'.git').mkdir()
        (self.root/'.git/HEAD').write_text('ref: refs/heads/synthetic\n')
        for name in ('admission.json','pending.json','outcome.json'):
            (self.root/name).write_text('Synthetic original receipt\n')
        self.source = self.root/'src/source.ts'
        self.body = '\ufeffCORPUS-ONLY-Ω\r\nconst label = "old";\nlast\n'.encode()
        self.source.write_bytes(self.body);self.source.chmod(0o444)
        self.task = 'TASK-ONLY-Ω\nKeep "quotes" and \\slashes exact.'
        self.profile = {'schemaVersion':'vyral.workspace-ipc-profile.v2',
            'socketPath':str(self.root/'NO-OPEN-SOCKET'),'credentialFile':str(self.root/'NO-OPEN-CREDENTIAL'),
            'sessionId':'PRIVATE-SESSION','operationId':'PRIVATE-OPERATION','contextRef':'PRIVATE-HISTORY-REF',
            'taskSha256':agent.sha(self.task.encode()),'grantSha256':agent.sha(b'grant'),'backendSha256':agent.sha(b'backend'),
            'providerId':'PRIVATE-PROVIDER','modelId':'PRIVATE-MODEL','baseCommit':'a'*40,
            'allowedPaths':['src/source.ts'],'avoidedPaths':['checks'],
            'contextFiles':[{'path':'src/source.ts','sha256':agent.sha(self.body),
                'windows':[{'startLine':2,'lineCount':1,'sha256':agent.sha(b'const label = "old";\n')}]}],
            'maxInputBytes':8192,'maxOutputTokens':1024,'reservationRule':{'method':'native_context_window','context_tokens':4096,'max_prompt_bytes':8192},
            'maxReservationTokens':4096,'maxSourceBytes':65536,'maxExecutionSeconds':120,
            'maxResponseBytes':32768,'maxEdits':4,'maxEditBytes':1024,'allowInvoke':False,
            'invocationDeadlineUtc':'2030-01-01T00:00:00.0000000Z'}
        self.envelope = {'schemaVersion':'vyral.workspace-agent-input.v1','task':self.task,
            'taskSha256':self.profile['taskSha256'].split(':')[1],'baseCommit':'a'*40,
            'allowedPaths':['src/source.ts'],'avoidedPaths':['checks'],'modelId':'PRIVATE-MODEL','remainingMilliseconds':60000}
        self.profile_path=self.root/'profile.json'

    def snapshot(self):
        result={}
        for p in sorted(self.root.rglob('*')):
            s=p.lstat();item={'mode':stat.S_IMODE(s.st_mode),'type':stat.S_IFMT(s.st_mode)}
            if p.is_symlink():item['link']=os.readlink(p)
            elif stat.S_ISREG(s.st_mode):item['sha']=hashlib.sha256(p.read_bytes()).hexdigest()
            result[str(p.relative_to(self.root))]=item
        return result

    def save(self,raw=None):
        self.profile_path.write_bytes(agent.encode(self.profile) if raw is None else raw);self.profile_path.chmod(0o600)

    def cli(self,raw=None,profile_raw=None,extra=(),workspace=None,isolated=False):
        self.save(profile_raw);raw=agent.encode(self.envelope) if raw is None else raw
        argv=[sys.executable,'-B',str(WORKER),'preflight','--profile',str(self.profile_path),'--workspace',str(self.root if workspace is None else workspace),*extra]
        if isolated:argv=['bwrap','--die-with-parent','--unshare-all','--ro-bind','/','/','--proc','/proc','--dev','/dev','--',*argv]
        before=self.snapshot();r=subprocess.run(argv,input=raw,capture_output=True,timeout=8)
        self.assertEqual(before,self.snapshot());self.assertLess(len(r.stdout)+len(r.stderr),2048)
        for secret in (self.task,'CORPUS-ONLY',str(self.root),'PRIVATE-HISTORY-REF','PRIVATE-SESSION','PRIVATE-OPERATION','PRIVATE-PROVIDER','PRIVATE-MODEL','NO-OPEN-CREDENTIAL','src/source.ts'):
            self.assertNotIn(secret.encode(),r.stdout+r.stderr)
        return r

    def held(self,reason,**kw):
        r=self.cli(**kw);self.assertEqual(r.returncode,20);self.assertEqual(r.stdout,b'')
        self.assertEqual(json.loads(r.stderr),{'schemaVersion':'vyral.workspace-ipc-preflight.v1','state':'held','reason':reason,'resendAuthorized':False,'providerEffects':'not_invoked_by_preflight'})

    def test_public_cli_exact_full_envelope_and_reference_renderer_parity(self):
        original=copy.deepcopy(self.profile)
        for mode in ('structured-omitted','structured-explicit','readable','readonly-context','legacy-v1'):
            with self.subTest(mode=mode):
                self.profile=copy.deepcopy(original);self.envelope['allowedPaths']=original['allowedPaths']
                if mode=='structured-explicit':self.profile['promptFormat']='structured-json-v1'
                if mode=='readable':self.profile.update(promptFormat='readable-windowed-v1',responseFraming='raw-json-or-single-json-fence')
                if mode=='readonly-context':
                    self.profile['allowedPaths']=self.envelope['allowedPaths']=['src/new.ts'];self.profile['contextReadPaths']=['src/source.ts'];self.profile['contextFiles'][0].pop('windows')
                if mode=='legacy-v1':
                    self.profile.update(schemaVersion='vyral.workspace-ipc-profile.v1',maxOutputTokens=128,reservationOverheadTokens=2,maxReservationTokens=65536)
                    for key in ('reservationRule','maxSourceBytes','invocationDeadlineUtc'):self.profile.pop(key)
                    self.profile['contextFiles'][0].pop('windows')
                self.save();p=agent.load_profile(self.profile_path);expected=GOLDEN[mode]
                raw=b'\n '+json.dumps(self.envelope,ensure_ascii=False,indent=2).encode()+b'\n';r=self.cli(raw=raw)
                self.assertEqual(r.returncode,0,r.stderr.decode());v=json.loads(r.stdout)
                self.assertEqual(v,{'schemaVersion':'vyral.workspace-ipc-preflight.v1','state':'prepared','profileSha256':agent.sha(self.profile_path.read_bytes()),
                    'hostInputBytes':len(raw),'hostInputSha256':agent.sha(raw),'taskUtf8Bytes':len(self.task.encode()),'taskSha256':p['taskSha256'],
                    'promptUtf8Bytes':expected['promptUtf8Bytes'],'promptSha256':expected['promptSha256'],
                    'requestJsonBytes':expected['requestJsonBytes'],'requestSha256':expected['requestSha256'],
                    'contextFileCount':1,'sourceWindowCount':int(mode not in ('readonly-context','legacy-v1')),
                    'maxInputBytes':8192,'maxOutputTokens':p['maxOutputTokens'],'calculatedReservationTokens':expected['calculatedReservationTokens'],
                    'invocationCount':0,'nativeTokenFitVerified':False,'independentlyIssued':False})

    def test_shared_run_recover_keeps_reference_request_and_legacy_result(self):
        self.save();p=agent.load_profile(self.profile_path);req,reservation=agent.input_request(p,self.envelope,self.root)
        self.assertEqual(agent.sha(agent.encode(req)),GOLDEN['structured-omitted']['requestSha256'])
        response={'status':'Succeeded','provider_result':{'provider':p['providerId'],'modelId':p['modelId']},'usage':{'input_tokens':2,'output_tokens':3},
            'output':{'message':{'role':'assistant','content':agent.encode({'schemaVersion':'vyral.workspace-edits.v2','edits':[]}).decode()}}}
        terminal={'state':'succeeded','session_id':p['sessionId'],'key':p['operationId'],'request_sha256':agent.sha(agent.encode(req)),
            'reservation_tokens':reservation,'response':response,'result_sha256':agent.sha(agent.encode(response)),'usage_tokens':5};methods=[]
        class Gateway:
            def __init__(self,p,deadline):pass
            def request(self,method):methods.append(method);return {'session_id':p['sessionId'],'grant_sha256':p['grantSha256'],'backend_sha256':p['backendSha256']}
            def call(self,method,*_):methods.append(method);return terminal
        before=self.snapshot()
        for recover in (False,True):
            with patch.object(agent,'Gateway',Gateway):
                self.assertEqual(agent.run(p,self.envelope,self.root,recover),{'schemaVersion':'vyral.workspace-ipc-outcome.v1','state':'staged_for_host_validation',
                    'profileSha256':p['profileSha256'],'taskSha256':p['taskSha256'],'requestSha256':terminal['request_sha256'],'resultSha256':terminal['result_sha256'],
                    'changedPaths':[],'invocationCount':1,'helperOrRetryInvocations':0,'reportedInputTokens':2,'reportedOutputTokens':3,'derivedTotalTokens':5,
                    'measurementBasis':'provider_response/reported; total derived by consumer','scopeCompleteness':'partial','independentlyIssued':False,
                    'remainingProviderEffects':'not independently established'})
        self.assertEqual(methods,['GET']*4);self.assertEqual(self.snapshot(),before)

    def test_no_gateway_credential_read_or_edit_parser(self):
        self.save();p=agent.load_profile(self.profile_path)
        with patch.object(agent,'Gateway',side_effect=AssertionError('forbidden')),patch.object(agent,'private_read',side_effect=AssertionError('forbidden')),patch.object(agent,'apply_edits',side_effect=AssertionError('forbidden')):
            self.assertEqual(agent.preflight(p,self.envelope,self.root,agent.encode(self.envelope))['invocationCount'],0)

    def test_public_cli_readonly_network_denied_fifo_credential_unopened(self):
        self.assertIsNotNone(shutil.which('bwrap'))
        # A disposable real repository, prepared by the fixture, not the command.
        shutil.rmtree(self.root/'.git')
        subprocess.run(['git','init','-q',str(self.root)],check=True)
        subprocess.run(['git','-C',str(self.root),'add','src/source.ts'],check=True)
        subprocess.run(['git','-C',str(self.root),'-c','user.name=Fixture','-c','user.email=fixture@invalid','commit','-qm','Synthetic baseline'],check=True)
        head=subprocess.check_output(['git','-C',str(self.root),'rev-parse','HEAD']).decode().strip()
        self.profile['baseCommit']=self.envelope['baseCommit']=head
        os.mkfifo(self.profile['credentialFile'],0o600)
        self.save();before_status=subprocess.check_output(['git','-C',str(self.root),'status','--porcelain=v1','--untracked-files=all'])
        r=self.cli(isolated=True);self.assertEqual(r.returncode,0,r.stderr.decode())
        self.assertEqual(head,subprocess.check_output(['git','-C',str(self.root),'rev-parse','HEAD']).decode().strip())
        after_status=subprocess.check_output(['git','-C',str(self.root),'status','--porcelain=v1','--untracked-files=all'])
        self.assertEqual(before_status,after_status)

    def test_invalid_json_utf8_duplicate_and_oversized_envelope(self):
        for raw,reason in [(b'\xff','invalid_json'),(b'{"task":1,"task":2}','duplicate_json_key'),(b'{"n":NaN}','invalid_json'),(b'x'*98305,'host_input_limit')]:
            with self.subTest(reason=reason):self.held(reason,raw=raw)

    def test_profile_json_size_and_closed_shape(self):
        for raw,reason in [(b'\xff','invalid_json'),(b'{"n":1,"n":2}','duplicate_json_key'),(b'[]','unsupported_profile'),(b'x'*16385,'private_file_required')]:
            with self.subTest(reason=reason):self.held(reason,profile_raw=raw)
        self.profile['extra']='SECRET-NOT-AUTHORITY';self.held('unsupported_profile')

    def test_profile_types_digests_and_enums(self):
        original=copy.deepcopy(self.profile)
        for key,value,reason in [('taskSha256','bad','invalid_digest'),('maxInputBytes',True,'invalid_limit'),('allowInvoke',1,'invalid_profile'),
            ('responseFraming','unknown','unsupported_response_framing'),('promptFormat','unknown','unsupported_prompt_format'),
            ('socketPath','relative','absolute_private_path_required'),('baseCommit','bad','base_binding'),('contextFiles',[{'path':'src/source.ts','sha256':'bad'}],'invalid_digest')]:
            with self.subTest(key=key):self.profile=copy.deepcopy(original);self.profile[key]=value;self.held(reason)

    def test_envelope_identity_types_and_closed_fields(self):
        original=copy.deepcopy(self.envelope)
        for key,value,reason in [('task','different','task_binding'),('taskSha256',7,'task_binding'),('baseCommit','b'*40,'workspace_binding'),
            ('allowedPaths',['src'],'workspace_binding'),('modelId','other','model_binding'),('remainingMilliseconds',False,'invalid_limit'),
            ('remainingMilliseconds',3600001,'invalid_limit'),('extra','data','unsupported_host_input')]:
            with self.subTest(key=key):self.envelope=copy.deepcopy(original);self.envelope[key]=value;self.held(reason)

    def test_full_render_and_reservation_boundaries(self):
        size=GOLDEN['structured-omitted']['promptUtf8Bytes'];original=copy.deepcopy(self.profile)
        for mode,reason in [('prompt','input_limit'),('native-prompt','input_limit'),('reservation','reservation_limit')]:
            with self.subTest(mode=mode):
                self.profile=copy.deepcopy(original)
                if mode=='prompt':self.profile['maxInputBytes']=size-1
                if mode=='native-prompt':self.profile['reservationRule']['max_prompt_bytes']=size-1
                if mode=='reservation':self.profile['maxReservationTokens']=4095
                self.held(reason)
        self.profile=original;self.profile['maxInputBytes']=size;self.profile['reservationRule']['max_prompt_bytes']=size
        self.assertEqual(self.cli().returncode,0)

    def test_context_scope_hash_window_links_bounds_and_utf8(self):
        original=copy.deepcopy(self.profile)
        for mode,reason in [('scope','path_scope'),('source-hash','context_binding'),('window-hash','context_binding'),('window-range','context_window'),
            ('source-limit','context_limit'),('symlink','symlink'),('directory','regular_file_required'),('utf8','private_ipc_or_input_failure')]:
            with self.subTest(mode=mode):
                self.profile=copy.deepcopy(original);entry=self.profile['contextFiles'][0]
                if mode=='scope':self.profile['allowedPaths']=self.envelope['allowedPaths']=['src/new.ts']
                if mode=='source-hash':entry['sha256']=agent.sha(b'changed')
                if mode=='window-hash':entry['windows'][0]['sha256']=agent.sha(b'changed')
                if mode=='window-range':entry['windows'][0]['startLine']=99
                if mode=='source-limit':self.profile['maxSourceBytes']=1
                if mode in ('symlink','directory','utf8'):
                    self.source.unlink()
                    if mode=='symlink':self.source.symlink_to(self.root/'admission.json')
                    if mode=='directory':self.source.mkdir()
                    if mode=='utf8':self.source.write_bytes(b'\xff');entry['sha256']=agent.sha(b'\xff')
                self.held(reason)
                if self.source.is_dir():self.source.rmdir()
                else:self.source.unlink()
                self.source.write_bytes(self.body);self.source.chmod(0o444);self.envelope['allowedPaths']=original['allowedPaths']

    def test_explicit_read_manifest_overlap_and_duplicate(self):
        self.profile['allowedPaths']=self.envelope['allowedPaths']=['src/new.ts'];self.profile['contextReadPaths']=['src/source.ts'];self.assertEqual(self.cli().returncode,0)
        original=copy.deepcopy(self.profile)
        for mode,reason in [('mismatch','context_read_manifest'),('overlap','context_read_overlap'),('duplicate','context_read_manifest')]:
            with self.subTest(mode=mode):
                self.profile=copy.deepcopy(original)
                if mode=='mismatch':self.profile['contextReadPaths']=['src/else.ts']
                if mode=='overlap':self.profile['allowedPaths']=self.envelope['allowedPaths']=['src/source.ts']
                if mode=='duplicate':self.profile['contextReadPaths']*=2;self.profile['contextFiles']*=2
                self.held(reason);self.envelope['allowedPaths']=original['allowedPaths']

    def test_deadline_missing_long_deadline_and_no_renewal(self):
        self.profile['invocationDeadlineUtc']='2000-01-01T00:00:00Z';self.held('deadline')
        self.profile.pop('invocationDeadlineUtc');self.profile['maxExecutionSeconds']=240;self.held('invocation_deadline_required')

    def test_workspace_argument_refusals_do_not_echo_private_data(self):
        self.held('preflight_workspace_required',workspace='relative-private-source')
        link=self.root/'linked-root';link.symlink_to(self.root,target_is_directory=True);self.held('preflight_workspace_required',workspace=link)
        self.held('invalid_preflight_arguments',extra=['--unknown','PRIVATE-CREDENTIAL-TEXT'])

    def test_profile_link_collapses_without_path_disclosure(self):
        self.save();target=self.root/'actual-profile';self.profile_path.rename(target);self.profile_path.symlink_to(target)
        before=self.snapshot();r=subprocess.run([sys.executable,'-B',str(WORKER),'preflight','--profile',str(self.profile_path),'--workspace',str(self.root)],input=agent.encode(self.envelope),capture_output=True,timeout=8)
        self.assertEqual(r.returncode,20);self.assertEqual(json.loads(r.stderr)['reason'],'private_ipc_or_input_failure');self.assertNotIn(str(self.root).encode(),r.stderr);self.assertEqual(self.snapshot(),before)

    def test_context_parent_link_avoided_path_and_overlapping_windows(self):
        original=copy.deepcopy(self.profile)
        self.profile['avoidedPaths']=self.envelope['avoidedPaths']=['src']
        self.held('path_scope')
        self.profile=copy.deepcopy(original);self.envelope['avoidedPaths']=original['avoidedPaths']
        self.profile['contextFiles'][0]['windows']*=2;self.held('overlapping_context')
        self.profile=original
        real=self.root/'real-src';(self.root/'src').rename(real);(self.root/'src').symlink_to(real,target_is_directory=True)
        self.held('symlink')

    def test_expired_deadline_agrees_with_run_and_recover_before_transport(self):
        self.profile['invocationDeadlineUtc']='2000-01-01T00:00:00Z'
        self.save();p=agent.load_profile(self.profile_path)
        with patch.object(agent,'Gateway',side_effect=AssertionError('forbidden')):
            for recover in (False,True):
                with self.subTest(recover=recover),self.assertRaisesRegex(agent.Held,'^deadline$'):
                    agent.run(p,self.envelope,self.root,recover)
        self.held('deadline')

    def test_unknown_held_content_is_not_promoted(self):
        class Input:buffer=io.BytesIO(agent.encode(self.envelope))
        err=io.StringIO()
        with patch.object(sys,'argv',['agent.py','preflight','--profile','PRIVATE-PATH','--workspace',str(self.root)]),patch.object(sys,'stdin',Input()),patch.object(sys,'stderr',err),patch.object(agent,'load_profile',side_effect=agent.Held('SECRET-CONTENT')):
            self.assertEqual(agent.main(),20)
        self.assertEqual(json.loads(err.getvalue())['reason'],'private_ipc_or_input_failure');self.assertNotIn('SECRET',err.getvalue())

    def test_preflight_mode_after_options_still_has_safe_held_schema(self):
        self.save()
        for extra in ([],['--unknown','PRIVATE-ARGUMENT']):
            with self.subTest(extra=bool(extra)):
                argv=[sys.executable,'-B',str(WORKER),'--profile',str(self.profile_path),'preflight','--workspace',str(self.root),*extra]
                raw=b'\xff';r=subprocess.run(argv,input=raw,capture_output=True,timeout=8)
                self.assertEqual(r.returncode,20);self.assertEqual(r.stdout,b'')
                v=json.loads(r.stderr);self.assertEqual(v['schemaVersion'],'vyral.workspace-ipc-preflight.v1')
                self.assertEqual(v['reason'],'invalid_preflight_arguments' if extra else 'invalid_json')
                self.assertNotIn(str(self.root).encode(),r.stderr);self.assertNotIn(b'PRIVATE-ARGUMENT',r.stderr)


if __name__=='__main__':unittest.main()
