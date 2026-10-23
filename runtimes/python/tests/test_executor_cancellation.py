import asyncio
from concurrent.futures import ThreadPoolExecutor
from threading import Event
import unittest
from unittest.mock import patch

from vyral_runtime.async_runtime import RuntimeExecutor


class ExecutorCancellationTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancelled_running_call_retains_capacity_until_finished(self):
        started = Event()
        release = Event()
        submissions = []
        real_submit = ThreadPoolExecutor.submit

        def submit(executor, fn, *args, **kwargs):
            submissions.append(fn)
            return real_submit(executor, fn, *args, **kwargs)

        def blocking():
            started.set()
            release.wait(3)
            return 'finished'

        executor = RuntimeExecutor(max_workers=1, max_pending=1)
        first = second = None
        try:
            with patch.object(ThreadPoolExecutor, 'submit', submit):
                first = asyncio.create_task(executor.run(blocking))
                for _ in range(100):
                    if started.is_set():
                        break
                    await asyncio.sleep(0.005)
                self.assertTrue(started.is_set())
                first.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await first
                second = asyncio.create_task(executor.run(lambda: 'second'))
                await asyncio.sleep(0)
                await asyncio.sleep(0)
                self.assertEqual(len(submissions), 1, 'running cancelled operation still consumes capacity')
                release.set()
                self.assertEqual(await asyncio.wait_for(second, 2), 'second')
                self.assertEqual(len(submissions), 2)
        finally:
            release.set()
            for task in (first, second):
                if task is not None and not task.done():
                    task.cancel()
                    await asyncio.gather(task, return_exceptions=True)
            executor.close()

    async def test_operation_exception_releases_capacity(self):
        executor = RuntimeExecutor(max_workers=1, max_pending=1)
        try:
            def fail():
                raise ValueError('expected')
            with self.assertRaisesRegex(ValueError, 'expected'):
                await executor.run(fail)
            self.assertEqual(await asyncio.wait_for(executor.run(lambda: 'next'), 2), 'next')
        finally:
            executor.close()
