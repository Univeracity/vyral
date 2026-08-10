from __future__ import annotations

import asyncio
from concurrent.futures import ThreadPoolExecutor
from threading import Lock
from typing import Callable, TypeVar
from weakref import WeakKeyDictionary


T = TypeVar("T")


class RuntimeExecutor:
    """Bounded executor for complete blocking runtime operations.

    The queue bound is maintained per event loop so one runtime can be used by
    normal application loops and by isolated ``asyncio.run`` test invocations
    without reusing loop-bound synchronization primitives.
    """

    def __init__(
        self,
        *,
        max_workers: int = 4,
        max_pending: int = 32,
        thread_name_prefix: str = "vyral-runtime",
    ) -> None:
        if max_workers <= 0:
            raise ValueError("Runtime executor max_workers must be greater than zero.")
        if max_pending < max_workers:
            raise ValueError(
                "Runtime executor max_pending must be at least max_workers."
            )
        self.max_workers = max_workers
        self.max_pending = max_pending
        self._executor = ThreadPoolExecutor(
            max_workers=max_workers,
            thread_name_prefix=thread_name_prefix,
        )
        self._lock = Lock()
        self._semaphores: WeakKeyDictionary[
            asyncio.AbstractEventLoop,
            asyncio.Semaphore,
        ] = WeakKeyDictionary()
        self._closed = False

    async def run(self, operation: Callable[[], T]) -> T:
        loop = asyncio.get_running_loop()
        with self._lock:
            if self._closed:
                raise RuntimeError("Runtime executor is closed.")
            semaphore = self._semaphores.get(loop)
            if semaphore is None:
                semaphore = asyncio.Semaphore(self.max_pending)
                self._semaphores[loop] = semaphore
        async with semaphore:
            return await loop.run_in_executor(self._executor, operation)

    def close(self, *, wait: bool = True) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
        self._executor.shutdown(wait=wait, cancel_futures=True)

    def __enter__(self) -> RuntimeExecutor:
        return self

    def __exit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()

    async def __aenter__(self) -> RuntimeExecutor:
        return self

    async def __aexit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()
