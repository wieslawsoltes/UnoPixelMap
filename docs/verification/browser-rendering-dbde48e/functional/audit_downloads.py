#!/usr/bin/env python3
"""Optionally re-audit original GitHub ZIPs without extracting them or running code."""
from __future__ import annotations

import argparse
import hashlib
import json
import tarfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def read_json(path: Path):
    return json.loads(path.read_bytes())


def hash_stream(stream):
    return hashlib.file_digest(stream, "sha256").hexdigest()


def check_archive(path: Path, metadata):
    assert path.stat().st_size == metadata["size_in_bytes"]
    with path.open("rb") as stream:
        digest = hash_stream(stream)
    assert "sha256:" + digest == metadata["digest"]
    return {"artifact_id": metadata["id"], "sha256": digest, "bytes": path.stat().st_size,
            "api_digest_verified": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--screens-zip", type=Path, help="Original atlas-browser-screens ZIP")
    parser.add_argument("--pages-zip", type=Path, help="Original github-pages ZIP containing artifact.tar")
    args = parser.parse_args()
    if not args.screens_zip and not args.pages_zip:
        parser.error("Provide at least one original ZIP")
    original = read_json(ROOT / "audit.json")
    result = {"schema": 1, "source_sha": original["source_sha"], "workflow_run_id": original["run_id"]}
    if args.screens_zip:
        metadata = read_json(ROOT / "screens-artifact.json")
        result["screens"] = check_archive(args.screens_zip, metadata)
        assert metadata["workflow_run"]["head_sha"] == result["source_sha"]
        expected = {name.removeprefix("evidence/"): identity for name, identity in original["files"].items()}
        with zipfile.ZipFile(args.screens_zip) as archive:
            names = [member.filename for member in archive.infolist() if not member.is_dir()]
            assert len(names) == len(set(names)) == len(expected) and set(names) == set(expected)
            for name, identity in expected.items():
                assert archive.getinfo(name).file_size == identity["bytes"], name
                with archive.open(name) as stream:
                    assert hash_stream(stream) == identity["sha256"], name
        failures = [name for name in expected if "failure" in Path(name).name.lower()]
        assert failures == original["failure_artifacts"] == []
        result["screens"].update({"rechecked_original_members": len(expected), "failure_members": failures})
    if args.pages_zip:
        metadata = read_json(ROOT / "pages-artifact.json")
        result["pages"] = check_archive(args.pages_zip, metadata)
        linkage = read_json(ROOT / "package-linkage.json")
        assert metadata["workflow_run"]["head_sha"] == result["source_sha"] == linkage["source_sha"]
        expected = linkage["matched_report_resources"]
        matched = set()
        with zipfile.ZipFile(args.pages_zip) as archive, archive.open("artifact.tar") as stream:
            with tarfile.open(fileobj=stream, mode="r|*") as tar:
                for member in tar:
                    name = member.name
                    while name.startswith("./"):
                        name = name[2:]
                    if member.isfile() and name in expected:
                        assert name not in matched
                        with tar.extractfile(member) as resource:
                            assert hash_stream(resource) == expected[name], name
                        matched.add(name)
        assert matched == set(expected)
        result["pages"].update({"package": linkage["package"], "rechecked_report_resources": len(matched)})
    result["scope"] = "Original archive digests and explicitly reported contents only; no browser or performance rerun."
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
