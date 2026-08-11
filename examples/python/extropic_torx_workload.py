"""A real Torx program behind Vyral's durable execution boundary.

Install ``runtimes/python[extropic-torx]`` on Python 3.11 or newer, then run
this file directly for the local result. Remote dispatch remains an explicit,
credit-consuming operator action; see ``scripts/verify-python-extropic-torx-live.py``.
"""

from __future__ import annotations

import json
from typing import Any

from vyral_runtime import (
    ExecutionRunContext,
    ExecutionRunResult,
    execution_plugin,
    vyral,
)
from vyral_runtime.integrations.extropic import (
    ExtropicAdapterOptions,
    ExtropicExecutionAdapter,
)


WORKLOAD_ID = "example.extropic.torx-density.v1"
HANDLER_ID = "example.extropic.run-torx-density"
PLUGIN_ID = "example.extropic.torx"
EXAMPLE_PAYLOAD = {"seed": 7}


def torx_density_workload(payload: Any) -> dict[str, Any]:
    """Evaluate a two-wire parameterized stochastic circuit with Torx.

    Imports stay inside the registered function: the remote Extropic sandbox
    supplies Torx and JAX, while Vyral transports this application function by
    value. The explicit seed makes parameter initialization replayable.
    """

    if not isinstance(payload, dict):
        raise TypeError("Torx workload payload must be an object.")
    seed = payload.get("seed")
    if isinstance(seed, bool) or not isinstance(seed, int):
        raise TypeError("Torx workload seed must be an integer.")
    if not 0 <= seed <= 2**31 - 1:
        raise ValueError("Torx workload seed must be between 0 and 2^31 - 1.")

    import jax
    import jax.numpy as jnp
    from torx import psc

    circuit = psc.DiscretePCircuit(
        [
            psc.PNOT(0),
            psc.PCNOT([0, 1]),
        ]
    )
    parameters = circuit.init_params(jax.random.key(seed))
    simulator = psc.StateVectorSimulator()
    compiled = simulator.build_circuit(circuit, parameters)
    initial_state = jnp.array([1.0, 0.0, 0.0, 0.0])
    density = simulator.density(compiled, initial_state)
    distribution = [float(value) for value in density.tolist()]
    return {
        "seed": seed,
        "distribution": distribution,
        "normalization": float(sum(distribution)),
    }


extropic_torx = ExtropicExecutionAdapter(
    WORKLOAD_ID,
    torx_density_workload,
    options=ExtropicAdapterOptions(
        tier="l4",
        timeout_seconds=60,
        require_seed=True,
    ),
)


@vyral(
    HANDLER_ID,
    plugin=PLUGIN_ID,
    name="Run Torx density",
    max_attempts=1,
    tags={"provider": "extropic", "library": "torx"},
)
async def run_torx_density(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    return await extropic_torx.execute(context)


plugin = execution_plugin(
    PLUGIN_ID,
    name="Example Extropic Torx workloads",
    version="0.1.0",
    handlers=(run_torx_density,),
)


if __name__ == "__main__":
    print(json.dumps(torx_density_workload(EXAMPLE_PAYLOAD), sort_keys=True))
