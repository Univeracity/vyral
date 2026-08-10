from __future__ import annotations

import base64
from dataclasses import dataclass
import json
import math
import re
import sqlite3
from typing import Any, Iterable, Mapping, Sequence

from .query_models import FilterNode, OrderExpression, QueryEnvelope


_MAX_FILTER_DEPTH = 16
_MAX_FILTER_NODES = 100
_MAX_ORDER_EXPRESSIONS = 8
_DIRECT_COLUMNS = {
    "/id": "r.id",
    "/partitionkey": "r.partition_key",
    "/updatedat": "r.updated_at_utc",
    "/revision": "r.revision",
}
_FILTER_OPERATORS = frozenset(
    {"eq", "neq", "in", "gt", "gte", "lt", "lte", "exists", "contains", "startswith"}
)
_JSON_PATH_PATTERN = re.compile(r"^[A-Za-z0-9_-]+$")


class QueryValidationError(ValueError):
    """Raised when a query cannot be represented by the portable local profile."""


def encode_continuation_token(offset: int) -> str:
    if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
        raise QueryValidationError("Continuation token offset must be non-negative.")
    return base64.b64encode(str(offset).encode("ascii")).decode("ascii")


def decode_continuation_token(token: str | None) -> int:
    if token is None or not token.strip():
        return 0
    try:
        raw = base64.b64decode(token, validate=True)
        text = raw.decode("utf-8")
        if not text or not text.isascii() or not text.isdecimal():
            raise ValueError
        offset = int(text)
    except (ValueError, UnicodeDecodeError) as exc:
        raise QueryValidationError(
            "Continuation token is not valid for the local SQLite adapter."
        ) from exc
    if offset < 0:
        raise QueryValidationError("Continuation token offset must be non-negative.")
    return offset


def validate_page_limit(limit: int | None, description: str) -> None:
    if limit is not None and limit <= 0:
        raise QueryValidationError(f"{description} must be greater than zero.")


def _sqlite_json_path(path: str) -> str:
    if path.startswith("$."):
        if not all(character.isalnum() or character in "$._-" for character in path):
            raise QueryValidationError(f"JSON path {path!r} is not supported.")
        return path
    if not path.startswith("/"):
        raise QueryValidationError(
            f"Query path {path!r} must be a JSON pointer such as '/metadata/status'."
        )
    segments = [
        segment.replace("~1", "/").replace("~0", "~")
        for segment in path.split("/")
        if segment
    ]
    if not segments or any(_JSON_PATH_PATTERN.fullmatch(segment) is None for segment in segments):
        raise QueryValidationError(
            f"JSON pointer {path!r} contains unsupported segment characters."
        )
    return "$." + ".".join(segments)


def _normalize_operator(op: str | None) -> str:
    normalized = (op or "eq").strip().lower()
    if normalized not in _FILTER_OPERATORS:
        raise QueryValidationError(f"Filter operator {op!r} is not supported.")
    return normalized


def _scalar(value: object) -> str | int | float | bool | None:
    if value is None or isinstance(value, (str, bool)):
        return value
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if isinstance(value, float) and not math.isfinite(value):
            raise QueryValidationError("Filter numeric values must be finite.")
        return value
    raise QueryValidationError(
        "Filter values must be scalar JSON values. Use the 'in' operator for scalar arrays."
    )


def _scalar_list(value: object) -> tuple[str | int | float | bool | None, ...]:
    if not isinstance(value, (list, tuple)):
        raise QueryValidationError("The 'in' operator requires an array value.")
    return tuple(_scalar(item) for item in value)


def _json_scalar(value: str | int | float | bool | None) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


@dataclass
class QueryPlan:
    sql: str
    parameters: Mapping[str, object]


class QueryCompiler:
    def __init__(self, indexed_metadata: Iterable[str] = ()) -> None:
        self._indexed = frozenset(indexed_metadata)
        self._parameters: dict[str, object] = {}
        self._nodes = 0

    @property
    def parameters(self) -> Mapping[str, object]:
        return dict(self._parameters)

    def parameter(self, value: object) -> str:
        name = f"p{len(self._parameters)}"
        self._parameters[name] = value
        return f":{name}"

    def where(self, collection: str, query: QueryEnvelope) -> str:
        clauses = [f"r.collection={self.parameter(collection)}"]
        if query.partition_keys:
            placeholders = ",".join(
                self.parameter(partition_key) for partition_key in query.partition_keys
            )
            clauses.append(f"r.partition_key IN ({placeholders})")
        if query.filter is not None:
            compiled = self._filter(query.filter, depth=1)
            if compiled:
                clauses.append(compiled)
        return " WHERE " + " AND ".join(clauses)

    def order(self, order_by: Sequence[OrderExpression] | None) -> str:
        if not order_by:
            return " ORDER BY r.partition_key ASC, r.id ASC"
        if len(order_by) > _MAX_ORDER_EXPRESSIONS:
            raise QueryValidationError(
                f"Query orderBy supports at most {_MAX_ORDER_EXPRESSIONS} expressions."
            )
        expressions: list[str] = []
        paths: set[str] = set()
        for item in order_by:
            direction = item.direction.strip().lower()
            if direction not in {"asc", "desc"}:
                raise QueryValidationError(
                    f"Order direction {item.direction!r} is not supported."
                )
            paths.add(item.path.lower())
            expressions.append(f"{self._order_expression(item.path)} {direction.upper()}")
        if "/partitionkey" not in paths:
            expressions.append("r.partition_key ASC")
        if "/id" not in paths:
            expressions.append("r.id ASC")
        return " ORDER BY " + ", ".join(expressions)

    def _filter(self, node: FilterNode, *, depth: int) -> str:
        self._nodes += 1
        if self._nodes > _MAX_FILTER_NODES:
            raise QueryValidationError(
                f"Filter trees support at most {_MAX_FILTER_NODES} nodes."
            )
        if depth > _MAX_FILTER_DEPTH:
            raise QueryValidationError(
                f"Filter trees support at most {_MAX_FILTER_DEPTH} levels."
            )
        if node.children is not None and node.combine is not None and node.combine.strip():
            combine = node.combine.strip().lower()
            if combine not in {"all", "any"}:
                raise QueryValidationError(
                    f"Filter combine mode {node.combine!r} is not supported."
                )
            clauses = [
                self._filter(child, depth=depth + 1) for child in node.children
            ]
            material = [clause for clause in clauses if clause]
            if not material:
                return ""
            separator = " AND " if combine == "all" else " OR "
            return "(" + separator.join(material) + ")"
        if node.path is None or not node.path.strip():
            return ""
        return self._leaf(node.path, _normalize_operator(node.op), node.value)

    def _leaf(self, path: str, op: str, raw_value: object) -> str:
        if path in self._indexed:
            return self._indexed_leaf(path, op, raw_value)
        value_expression = self._path_expression(path)
        type_expression = self._type_expression(path)
        if op == "exists":
            if raw_value is None:
                should_exist = True
            elif isinstance(raw_value, bool):
                should_exist = raw_value
            else:
                raise QueryValidationError(
                    "Filter operator 'exists' requires a boolean value or null."
                )
            return (
                f"{type_expression} IS NOT NULL"
                if should_exist
                else f"{type_expression} IS NULL"
            )
        if op == "in":
            values = _scalar_list(raw_value)
            if not values:
                return "0=1"
            placeholders = ",".join(self.parameter(value) for value in values)
            return f"{value_expression} IN ({placeholders})"

        value = _scalar(raw_value)
        if value is None:
            if op == "eq":
                return f"{type_expression}='null'"
            if op == "neq":
                return f"({type_expression} IS NOT NULL AND {type_expression}!='null')"
            raise QueryValidationError(f"Operator {op!r} cannot be used with null values.")
        if op in {"contains", "startswith"}:
            if not isinstance(value, str):
                raise QueryValidationError(
                    f"Filter operator {op!r} requires a string value."
                )
            placeholder = self.parameter(value)
            if op == "contains":
                return f"instr({value_expression},{placeholder})>0"
            return (
                f"substr({value_expression},1,length({placeholder}))={placeholder}"
            )
        sql_operator = {
            "eq": "=",
            "neq": "!=",
            "gt": ">",
            "gte": ">=",
            "lt": "<",
            "lte": "<=",
        }[op]
        return f"{value_expression}{sql_operator}{self.parameter(value)}"

    def _indexed_leaf(self, path: str, op: str, raw_value: object) -> str:
        correlation = (
            "mi.collection=r.collection AND mi.partition_key=r.partition_key "
            "AND mi.record_id=r.id AND mi.path=" + self.parameter(path)
        )
        if op == "exists":
            if raw_value is None:
                should_exist = True
            elif isinstance(raw_value, bool):
                should_exist = raw_value
            else:
                raise QueryValidationError(
                    "Filter operator 'exists' requires a boolean value or null."
                )
            prefix = "EXISTS" if should_exist else "NOT EXISTS"
            return (
                f"{prefix} (SELECT 1 FROM vyral_py_metadata_index mi "
                f"WHERE {correlation})"
            )
        if op == "in":
            values = _scalar_list(raw_value)
            if not values:
                return "0=1"
            placeholders = ",".join(
                self.parameter(_json_scalar(value)) for value in values
            )
            condition = f"mi.value_json IN ({placeholders})"
        else:
            value = _scalar(raw_value)
            if value is None:
                if op == "eq":
                    condition = "mi.value_json='null'"
                elif op == "neq":
                    condition = "mi.value_json!='null'"
                else:
                    raise QueryValidationError(
                        f"Operator {op!r} cannot be used with null values."
                    )
            elif op in {"contains", "startswith"}:
                if not isinstance(value, str):
                    raise QueryValidationError(
                        f"Filter operator {op!r} requires a string value."
                    )
                placeholder = self.parameter(value)
                condition = (
                    f"instr(mi.value_text,{placeholder})>0"
                    if op == "contains"
                    else (
                        "substr(mi.value_text,1,length("
                        f"{placeholder}))={placeholder}"
                    )
                )
            else:
                sql_operator = {
                    "eq": "=",
                    "neq": "!=",
                    "gt": ">",
                    "gte": ">=",
                    "lt": "<",
                    "lte": "<=",
                }[op]
                column: str
                parameter_value: object
                if isinstance(value, bool):
                    column, parameter_value = "mi.value_bool", int(value)
                elif isinstance(value, (int, float)):
                    column, parameter_value = "mi.value_number", float(value)
                elif isinstance(value, str):
                    if op in {"gt", "gte", "lt", "lte"}:
                        raise QueryValidationError(
                            f"Indexed range filter {path!r} requires a numeric value."
                        )
                    column, parameter_value = "mi.value_text", value
                else:
                    column, parameter_value = "mi.value_json", _json_scalar(value)
                condition = (
                    f"{column}{sql_operator}{self.parameter(parameter_value)}"
                )
        return (
            "EXISTS (SELECT 1 FROM vyral_py_metadata_index mi "
            f"WHERE {correlation} AND {condition})"
        )

    def _path_expression(self, path: str) -> str:
        direct = _DIRECT_COLUMNS.get(path.lower())
        if direct is not None:
            return direct
        return f"json_extract(r.record_json,{self.parameter(_sqlite_json_path(path))})"

    def _type_expression(self, path: str) -> str:
        direct = _DIRECT_COLUMNS.get(path.lower())
        if direct is not None:
            return direct
        return f"json_type(r.record_json,{self.parameter(_sqlite_json_path(path))})"

    def _order_expression(self, path: str) -> str:
        if path in self._indexed:
            return (
                "(SELECT COALESCE(mi.value_number,mi.value_text,mi.value_bool,mi.value_json) "
                "FROM vyral_py_metadata_index mi "
                "WHERE mi.collection=r.collection AND mi.partition_key=r.partition_key "
                "AND mi.record_id=r.id AND mi.path="
                + self.parameter(path)
                + " LIMIT 1)"
            )
        return self._path_expression(path)


def build_query_plan(
    collection: str,
    query: QueryEnvelope,
    indexed_metadata: Iterable[str],
) -> QueryPlan:
    validate_page_limit(query.limit, "Query page size")
    offset = decode_continuation_token(query.continuation_token)
    compiler = QueryCompiler(indexed_metadata)
    sql = "SELECT r.record_json FROM vyral_py_records r"
    sql += compiler.where(collection, query)
    sql += compiler.order(query.order_by)
    if query.limit is not None:
        sql += f" LIMIT {compiler.parameter(query.limit + 1)}"
    elif offset > 0:
        sql += " LIMIT -1"
    if offset > 0:
        sql += f" OFFSET {compiler.parameter(offset)}"
    return QueryPlan(sql=sql, parameters=compiler.parameters)


def build_vector_candidate_plan(
    collection: str,
    query: QueryEnvelope,
    indexed_metadata: Iterable[str],
    vector_field: str,
) -> QueryPlan:
    compiler = QueryCompiler(indexed_metadata)
    sql = (
        "SELECT r.record_json,v.vector_data,v.dimensions "
        "FROM vyral_py_records r "
        "JOIN vyral_py_vectors v ON "
        "r.collection=v.collection AND r.partition_key=v.partition_key AND r.id=v.record_id"
    )
    sql += compiler.where(collection, query)
    sql += f" AND v.vector_name={compiler.parameter(vector_field)}"
    sql += " ORDER BY r.partition_key ASC,r.id ASC"
    return QueryPlan(sql=sql, parameters=compiler.parameters)


def build_lexical_candidate_plan(
    collection: str,
    query: QueryEnvelope,
    indexed_metadata: Iterable[str],
    fts_expression: str,
    scan_limit: int | None,
) -> QueryPlan:
    compiler = QueryCompiler(indexed_metadata)
    sql = (
        "SELECT r.record_json FROM vyral_py_records r "
        "JOIN vyral_py_record_fts ON "
        "r.collection=vyral_py_record_fts.collection "
        "AND r.partition_key=vyral_py_record_fts.partition_key "
        "AND r.id=vyral_py_record_fts.record_id "
        f"WHERE vyral_py_record_fts MATCH {compiler.parameter(fts_expression)}"
    )
    where = compiler.where(collection, query)
    sql += " AND " + where.removeprefix(" WHERE ")
    sql += " ORDER BY bm25(vyral_py_record_fts),r.partition_key ASC,r.id ASC"
    if scan_limit is not None:
        sql += f" LIMIT {compiler.parameter(scan_limit)}"
    return QueryPlan(sql=sql, parameters=compiler.parameters)


def read_record_rows(
    connection: sqlite3.Connection,
    plan: QueryPlan,
) -> tuple[Mapping[str, Any], ...]:
    rows = connection.execute(plan.sql, plan.parameters)
    return tuple(json.loads(row["record_json"]) for row in rows)
