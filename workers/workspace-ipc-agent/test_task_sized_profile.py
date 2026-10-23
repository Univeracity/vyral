"""No-effect public CLI task-sized controls; reusable fixture imports are test-only."""
import copy
import json
import unittest
from datetime import datetime, timedelta, timezone
from unittest.mock import patch
import test_preflight as fixture

agent=fixture.agent

class TaskSizedProfileTests(unittest.TestCase):
    def setUp(self):
        self.f=fixture.PreflightTests('test_no_gateway_credential_read_or_edit_parser');self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.f.profile.update(maxInputBytes=32768,maxOutputTokens=2048,maxReservationTokens=8192,maxExecutionSeconds=1800)
        self.f.profile['reservationRule']={'method':'native_context_window','context_tokens':8192,'max_prompt_bytes':32768}
        self.f.envelope['remainingMilliseconds']=1800000

    def test_public_preflight_larger_task_reserves_exact_window_without_invoking(self):
        for output in (2048,4096):
            with self.subTest(output=output):
                self.f.profile['maxOutputTokens']=output
                r=self.f.cli(isolated=True);self.assertEqual(0,r.returncode,r.stderr)
                v=json.loads(r.stdout);self.assertEqual(v['maxInputBytes'],32768)
                self.assertEqual(v['maxOutputTokens'],output);self.assertEqual(v['calculatedReservationTokens'],8192)
                self.assertEqual(v['invocationCount'],0);self.assertIs(v['nativeTokenFitVerified'],False)

    def test_context_output_reservation_and_type_mismatch_refuse(self):
        original=copy.deepcopy(self.f.profile)
        for key,value in [('context',1048577),('context',True),('output',8192),('output',8193),
                          ('output',True),('reservation',8191),('duration',3601),('duration',True)]:
            with self.subTest(key=key,value=value):
                self.f.profile=copy.deepcopy(original)
                if key=='context':self.f.profile['reservationRule']['context_tokens']=value
                else:self.f.profile[{'output':'maxOutputTokens','reservation':'maxReservationTokens','duration':'maxExecutionSeconds'}[key]]=value
                self.f.held('reservation_limit' if key=='reservation' else 'invalid_limit')

    def test_public_preflight_configured_capacity_requires_its_selected_allocation(self):
        for context,output in ((16384,5120),(32768,8192),(1048576,8192)):
            with self.subTest(context=context,output=output):
                self.f.profile['reservationRule']['context_tokens']=context
                self.f.profile.update(maxReservationTokens=context,maxOutputTokens=output)
                r=self.f.cli(isolated=True);self.assertEqual(0,r.returncode,r.stderr)
                v=json.loads(r.stdout);self.assertEqual(context,v['calculatedReservationTokens'])
                self.assertEqual(output,v['maxOutputTokens']);self.assertFalse(v['nativeTokenFitVerified'])
                self.assertEqual(0,v['invocationCount'])
                self.f.profile['maxReservationTokens']=context-1;self.f.held('reservation_limit')

    def test_allocation_ceiling_and_output_bounds_remain_hard(self):
        self.f.profile['reservationRule']['context_tokens']=16384
        self.f.profile.update(maxReservationTokens=16384,maxOutputTokens=5120)
        for value in (1048577,True,0):
            with self.subTest(allocation=value):
                self.f.profile['maxReservationTokens']=value;self.f.held('invalid_limit')
        self.f.profile['maxReservationTokens']=16384
        self.f.profile['maxOutputTokens']=8193;self.f.held('invalid_limit')

    def test_selected_input_and_prompt_byte_guards_remain_hard(self):
        self.f.profile['maxInputBytes']=32768
        self.f.envelope['task']='X'*12000;self.f.envelope['taskSha256']=agent.sha(self.f.envelope['task'].encode()).split(':')[1]
        self.f.profile['taskSha256']='sha256:'+self.f.envelope['taskSha256']
        r=self.f.cli();self.assertEqual(r.returncode,0,r.stderr);self.assertGreater(json.loads(r.stdout)['promptUtf8Bytes'],8192)
        self.f.envelope['task']='X'*32768;self.f.envelope['taskSha256']=agent.sha(self.f.envelope['task'].encode()).split(':')[1]
        self.f.profile['taskSha256']='sha256:'+self.f.envelope['taskSha256'];self.f.held('context_limit')
        self.f.envelope['task']='X'*12000;self.f.envelope['taskSha256']=agent.sha(self.f.envelope['task'].encode()).split(':')[1]
        self.f.profile['taskSha256']='sha256:'+self.f.envelope['taskSha256']
        self.f.profile['reservationRule']['max_prompt_bytes']=8192;self.f.held('input_limit')

    def test_ceiling3600_does_not_renew_smaller_original_deadline(self):
        self.f.profile['maxExecutionSeconds']=3600;self.f.envelope['remainingMilliseconds']=3600000
        now=datetime(2026,10,10,tzinfo=timezone.utc)
        self.f.profile['invocationDeadlineUtc']=(now+timedelta(seconds=2)).isoformat().replace('+00:00','Z')
        self.f.save();p=agent.load_profile(self.f.profile_path)
        with patch.object(agent,'Gateway',side_effect=AssertionError('forbidden')), \
             patch.object(agent,'datetime') as clock,patch.object(agent.time,'monotonic',return_value=1000):
            clock.now.return_value=now;clock.fromisoformat.side_effect=datetime.fromisoformat
            request,reservation,deadline=agent.prepare_input(p,self.f.envelope,self.f.root)
        self.assertEqual(deadline,1002)
        self.assertEqual(request['deadline_utc'],p['invocationDeadlineUtc']);self.assertEqual(reservation,8192)
        self.f.profile['invocationDeadlineUtc']='2000-01-01T00:00:00Z';self.f.held('deadline')

    def test_supported_duration_edges_and_excess_envelope_refuse(self):
        for seconds in (601,1500,1800,3600):
            with self.subTest(seconds=seconds):
                self.f.profile['maxExecutionSeconds']=seconds;self.f.envelope['remainingMilliseconds']=seconds*1000
                r=self.f.cli();self.assertEqual(r.returncode,0,r.stderr)
        self.f.envelope['remainingMilliseconds']=3600001;self.f.held('invalid_limit')
        self.f.envelope['remainingMilliseconds']=3600000;self.f.profile.pop('invocationDeadlineUtc')
        self.f.held('invocation_deadline_required')

    def test_larger_profiles_still_refuse_scope_hash_and_link_drift(self):
        original=copy.deepcopy(self.f.profile)
        for fault in ('hash','scope','link'):
            with self.subTest(fault=fault):
                self.f.profile=copy.deepcopy(original)
                self.f.envelope['allowedPaths']=original['allowedPaths']
                if fault=='hash':self.f.profile['contextFiles'][0]['sha256']=agent.sha(b'changed')
                if fault=='scope':self.f.profile['allowedPaths']=['src/other.ts'];self.f.envelope['allowedPaths']=['src/other.ts']
                if fault=='link':self.f.source.unlink();self.f.source.symlink_to('/NO-PUBLIC-ESCAPE')
                self.f.held({'hash':'context_binding','scope':'path_scope','link':'symlink'}[fault])

if __name__=='__main__':unittest.main()
