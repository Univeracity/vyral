from __future__ import annotations

from typing import Any


_SHAPES = (
    (
        "local-sqlite",
        "local_sqlite",
        ("append_only_reviews", "bounded_traversal",
         "local_import_export"),
        ("single_process_sqlite_writer",
         "no_native_distributed_partitioning"),
    ),
    (
        "vyral-collection",
        "vyral_collection",
        ("collection_scoped_objects", "hybrid_retrieval_join",
         "provider_trace_join"),
        ("graph_traversal_may_be_adapter_level",),
    ),
    (
        "cosmos-gremlin",
        "cosmos_gremlin",
        ("partitioned_graph", "remote_traversal"),
        ("provider_specific_query_language",
         "requires_partition_strategy"),
    ),
    (
        "neptune",
        "neptune",
        ("managed_graph", "remote_traversal"),
        ("provider_specific_operations",
         "external_service_dependency"),
    ),
    (
        "spanner-graph",
        "spanner_graph",
        ("relational_graph_mapping", "distributed_sql_substrate"),
        ("requires_schema_mapping", "external_service_dependency"),
    ),
)


def list_graph_provider_shapes() -> tuple[dict[str, Any], ...]:
    return tuple(
        {
            "id": provider_id,
            "providerId": provider_id,
            "kind": kind,
            "graphIdField": "graphId",
            "nodeIdField": "id",
            "edgeIdField": "id",
            "sourceField": "sourceId",
            "targetField": "targetId",
            "partitionField": "partitionKey",
            "tenantField": "tenantId",
            "capabilities": list(capabilities),
            "limitations": list(limitations),
            "metadata": None,
        }
        for provider_id, kind, capabilities, limitations in _SHAPES
    )


def get_graph_provider_shape(
    provider_id: str,
) -> dict[str, Any] | None:
    return next(
        (
            shape
            for shape in list_graph_provider_shapes()
            if shape["providerId"] == provider_id
        ),
        None,
    )


__all__ = [
    "get_graph_provider_shape",
    "list_graph_provider_shapes",
]
