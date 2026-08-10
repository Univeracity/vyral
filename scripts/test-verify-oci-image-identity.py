#!/usr/bin/env python3
"""Regression tests for the archived OCI identity verifier."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location(
    "verify_oci_image_identity", ROOT / "scripts" / "verify-oci-image-identity.py"
)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Could not load verify-oci-image-identity.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def encoded(document: object) -> bytes:
    return json.dumps(document, separators=(",", ":"), sort_keys=True).encode("utf-8")


def descriptor(media_type: str, content: bytes) -> dict[str, object]:
    return {
        "mediaType": media_type,
        "digest": f"sha256:{hashlib.sha256(content).hexdigest()}",
        "size": len(content),
    }


def fixture(*, corrupt_config: bool = False, duplicate_index: bool = False) -> tuple[dict[str, bytes], dict[str, object]]:
    config = encoded({"architecture": "amd64", "os": "linux"})
    config_descriptor = descriptor("application/vnd.oci.image.config.v1+json", config)
    manifest = encoded(
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.oci.image.manifest.v1+json",
            "config": config_descriptor,
            "layers": [],
        }
    )
    root_descriptor = descriptor("application/vnd.oci.image.manifest.v1+json", manifest)
    index = encoded({"schemaVersion": 2, "manifests": [root_descriptor]})
    files = {
        "oci-layout": encoded({"imageLayoutVersion": "1.0.0"}),
        "index.json": index,
        f"blobs/sha256/{str(root_descriptor['digest']).removeprefix('sha256:')}": manifest,
        f"blobs/sha256/{str(config_descriptor['digest']).removeprefix('sha256:')}": b"corrupt" if corrupt_config else config,
    }
    if duplicate_index:
        files["./index.json"] = index
    metadata = {
        "containerimage.digest": root_descriptor["digest"],
        "containerimage.descriptor": root_descriptor,
    }
    return files, metadata


class OciImageIdentityTests(unittest.TestCase):
    def write_fixture(
        self,
        directory: Path,
        files: dict[str, bytes],
        metadata: dict[str, object],
        *,
        duplicate_index: bool = False,
    ) -> tuple[Path, Path]:
        archive_path = directory / "image.oci"
        with tarfile.open(archive_path, "w") as archive:
            for name, content in files.items():
                member_name = "index.json" if duplicate_index and name == "./index.json" else name
                info = tarfile.TarInfo(member_name)
                info.size = len(content)
                archive.addfile(info, io.BytesIO(content))
        metadata_path = directory / "metadata.json"
        metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
        return archive_path, metadata_path

    def test_resolves_config_and_root_artifact_digests(self) -> None:
        files, metadata = fixture()
        with tempfile.TemporaryDirectory() as temporary:
            archive, metadata_path = self.write_fixture(Path(temporary), files, metadata)
            config_digest, artifact_digest = MODULE.resolve_identity(
                archive, metadata_path, "linux", "amd64"
            )
        self.assertEqual(metadata["containerimage.digest"], artifact_digest)
        self.assertIn(config_digest.removeprefix("sha256:"), " ".join(files))

    def test_rejects_corrupted_config_content(self) -> None:
        files, metadata = fixture(corrupt_config=True)
        with tempfile.TemporaryDirectory() as temporary:
            archive, metadata_path = self.write_fixture(Path(temporary), files, metadata)
            with self.assertRaisesRegex(SystemExit, "config content does not match"):
                MODULE.resolve_identity(archive, metadata_path, "linux", "amd64")

    def test_rejects_duplicate_archive_members(self) -> None:
        files, metadata = fixture(duplicate_index=True)
        with tempfile.TemporaryDirectory() as temporary:
            archive, metadata_path = self.write_fixture(
                Path(temporary), files, metadata, duplicate_index=True
            )
            with self.assertRaisesRegex(SystemExit, "duplicate member 'index.json'"):
                MODULE.resolve_identity(archive, metadata_path, "linux", "amd64")

    def test_rejects_metadata_descriptor_drift(self) -> None:
        files, metadata = fixture()
        assert isinstance(metadata["containerimage.descriptor"], dict)
        metadata["containerimage.descriptor"]["size"] = 1
        with tempfile.TemporaryDirectory() as temporary:
            archive, metadata_path = self.write_fixture(Path(temporary), files, metadata)
            with self.assertRaisesRegex(SystemExit, "field 'size'"):
                MODULE.resolve_identity(archive, metadata_path, "linux", "amd64")


if __name__ == "__main__":
    unittest.main()
