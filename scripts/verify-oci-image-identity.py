#!/usr/bin/env python3
"""Resolve and verify a single-platform image identity from an attested OCI archive."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import tarfile
from typing import NoReturn


DIGEST_PATTERN = re.compile(r"^sha256:([0-9a-f]{64})$")
INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
MANIFEST_MEDIA_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}
MAX_JSON_BYTES = 16 * 1024 * 1024


def fail(message: str) -> NoReturn:
    raise SystemExit(message)


def descriptor_digest(descriptor: object, context: str) -> str:
    if not isinstance(descriptor, dict):
        fail(f"{context} is not an OCI descriptor.")
    digest = descriptor.get("digest")
    if not isinstance(digest, str) or DIGEST_PATTERN.fullmatch(digest) is None:
        fail(f"{context} has an unsupported digest.")
    return digest


def resolve_identity(
    archive_path: Path,
    metadata_path: Path,
    image_os: str,
    image_architecture: str,
) -> tuple[str, str]:
    with tarfile.open(archive_path, mode="r:*") as archive:
        regular_members: dict[str, tarfile.TarInfo] = {}
        for member in archive.getmembers():
            if not member.isfile():
                continue
            if member.name in regular_members:
                fail(f"OCI archive contains duplicate member {member.name!r}.")
            regular_members[member.name] = member

        def read_member(name: str, maximum_size: int) -> bytes:
            member = regular_members.get(name)
            if member is None:
                fail(f"OCI archive is missing {name!r}.")
            if member.size < 0 or member.size > maximum_size:
                fail(f"OCI archive member {name!r} has an invalid size.")
            stream = archive.extractfile(member)
            if stream is None:
                fail(f"OCI archive member {name!r} could not be read.")
            content = stream.read(maximum_size + 1)
            if len(content) != member.size:
                fail(f"OCI archive member {name!r} was truncated.")
            return content

        def read_json_member(name: str, maximum_size: int = MAX_JSON_BYTES) -> object:
            try:
                return json.loads(read_member(name, maximum_size))
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                fail(f"OCI archive member {name!r} is not valid JSON: {error}.")

        def read_descriptor(
            descriptor: object, context: str
        ) -> tuple[str, dict[str, object]]:
            digest = descriptor_digest(descriptor, context)
            assert isinstance(descriptor, dict)
            digest_hex = digest.removeprefix("sha256:")
            content = read_member(f"blobs/sha256/{digest_hex}", MAX_JSON_BYTES)
            if hashlib.sha256(content).hexdigest() != digest_hex:
                fail(f"{context} content does not match {digest}.")
            declared_size = descriptor.get("size")
            if not isinstance(declared_size, int) or declared_size != len(content):
                fail(f"{context} size does not match its descriptor.")
            try:
                document = json.loads(content)
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                fail(f"{context} is not valid JSON: {error}.")
            if not isinstance(document, dict):
                fail(f"{context} is not a JSON object.")
            return digest, document

        layout = read_json_member("oci-layout", 4096)
        if not isinstance(layout, dict) or layout.get("imageLayoutVersion") != "1.0.0":
            fail("OCI archive does not declare image layout version 1.0.0.")

        index = read_json_member("index.json")
        if not isinstance(index, dict) or index.get("schemaVersion") != 2:
            fail("OCI archive index is invalid.")
        root_descriptors = index.get("manifests")
        if not isinstance(root_descriptors, list) or len(root_descriptors) != 1:
            fail("OCI archive must contain exactly one top-level image descriptor.")

        root_descriptor = root_descriptors[0]
        root_digest, root_document = read_descriptor(
            root_descriptor, "OCI root descriptor"
        )

        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
            fail(f"Build metadata is not valid JSON: {error}.")
        if not isinstance(metadata, dict):
            fail("Build metadata is not a JSON object.")
        if metadata.get("containerimage.digest") != root_digest:
            fail("Build metadata digest does not match the archived OCI artifact.")
        metadata_descriptor = metadata.get("containerimage.descriptor")
        if not isinstance(metadata_descriptor, dict):
            fail("Build metadata descriptor does not match the archived OCI artifact.")
        assert isinstance(root_descriptor, dict)
        for field in ("digest", "mediaType", "size"):
            if metadata_descriptor.get(field) != root_descriptor.get(field):
                fail(
                    "Build metadata descriptor does not match the archived "
                    f"OCI artifact field {field!r}."
                )

        candidates: list[tuple[str, dict[str, object]]] = []

        def visit(
            descriptor: dict[str, object],
            document: dict[str, object],
            context: str,
        ) -> None:
            media_type = descriptor.get("mediaType") or document.get("mediaType")
            if media_type in INDEX_MEDIA_TYPES:
                children = document.get("manifests")
                if not isinstance(children, list):
                    fail(f"{context} omits its manifests array.")
                for position, child in enumerate(children):
                    if not isinstance(child, dict):
                        fail(f"{context} manifest {position} is not a descriptor.")
                    annotations = child.get("annotations")
                    if isinstance(annotations, dict) and annotations.get(
                        "vnd.docker.reference.type"
                    ) == "attestation-manifest":
                        continue
                    platform = child.get("platform")
                    if isinstance(platform, dict):
                        if platform.get("os") not in {None, image_os}:
                            continue
                        if platform.get("architecture") not in {
                            None,
                            image_architecture,
                        }:
                            continue
                    _, child_document = read_descriptor(
                        child, f"{context} manifest {position}"
                    )
                    visit(child, child_document, f"{context} manifest {position}")
                return
            if media_type not in MANIFEST_MEDIA_TYPES:
                fail(f"{context} has unsupported media type {media_type!r}.")
            candidates.append((context, document))

        visit(root_descriptor, root_document, "OCI root descriptor")
        if len(candidates) != 1:
            fail(
                "OCI archive did not resolve to exactly one "
                f"{image_os}/{image_architecture} image manifest."
            )

        context, manifest = candidates[0]
        config = manifest.get("config")
        config_digest = descriptor_digest(config, f"{context} config")
        assert isinstance(config, dict)
        config_hex = config_digest.removeprefix("sha256:")
        config_content = read_member(
            f"blobs/sha256/{config_hex}", MAX_JSON_BYTES
        )
        if hashlib.sha256(config_content).hexdigest() != config_hex:
            fail(f"{context} config content does not match {config_digest}.")
        declared_config_size = config.get("size")
        if (
            not isinstance(declared_config_size, int)
            or declared_config_size != len(config_content)
        ):
            fail(f"{context} config size does not match its descriptor.")

    return config_digest, root_digest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("metadata", type=Path)
    parser.add_argument("--os", required=True, dest="image_os")
    parser.add_argument("--architecture", required=True)
    arguments = parser.parse_args()
    config_digest, artifact_digest = resolve_identity(
        arguments.archive,
        arguments.metadata,
        arguments.image_os,
        arguments.architecture,
    )
    print(f"{config_digest}\t{artifact_digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
