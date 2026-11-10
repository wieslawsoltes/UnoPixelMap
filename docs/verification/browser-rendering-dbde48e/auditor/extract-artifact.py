#!/usr/bin/env python3
"""Verify downloaded workflow ZIP bytes, then safely extract without overwriting evidence."""
import argparse
import hashlib
import json
import shutil
import stat
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("artifact_metadata", type=Path, help="The raw API artifact object, not an envelope")
    parser.add_argument("output", type=Path)
    parser.add_argument("--pages-artifact-metadata", type=Path, help="Independent metadata for the exact candidate Pages archive")
    parser.add_argument("--run", type=int, default=42907891884)
    parser.add_argument("--source", default="dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6")
    args = parser.parse_args()
    metadata = json.loads(args.artifact_metadata.read_text())
    assert metadata["name"] == "atlas-play-performance" and not metadata.get("expired", False)
    assert metadata["workflow_run"]["id"] == args.run and metadata["workflow_run"]["head_sha"] == args.source
    actual_hash = hashlib.sha256(args.archive.read_bytes()).hexdigest()
    assert metadata["digest"] == "sha256:" + actual_hash, "Downloaded performance ZIP digest differs from GitHub metadata"
    assert metadata["size_in_bytes"] == args.archive.stat().st_size, "Downloaded performance ZIP size differs from GitHub metadata"
    assert not args.output.exists(), "Refuse to overwrite an existing extracted evidence directory"
    record_path = args.output.parent / (args.output.name + "-acquisition.json")
    assert not record_path.exists(), "Refuse to overwrite an acquisition record"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=args.output.name + ".extracting-", dir=args.output.parent))
    try:
        files = {}
        with zipfile.ZipFile(args.archive) as archive:
            names = set()
            for entry in archive.infolist():
                name = PurePosixPath(entry.filename)
                assert not name.is_absolute() and ".." not in name.parts and "\\" not in entry.filename, "Unsafe ZIP path"
                assert entry.filename not in names, "Duplicate ZIP entry"
                names.add(entry.filename)
                assert not stat.S_ISLNK(entry.external_attr >> 16), "ZIP symlinks are not extracted"
                destination = temporary.joinpath(*name.parts)
                assert destination.resolve().is_relative_to(temporary.resolve())
                if entry.is_dir():
                    destination.mkdir(parents=True, exist_ok=True)
                else:
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    content = archive.read(entry)
                    destination.write_bytes(content)
                    files[entry.filename] = {"bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        provenance = json.loads((temporary / "provenance.json").read_text())
        assert provenance["candidate"]["run_id"] == args.run and provenance["candidate"]["source_sha"] == args.source
        pages = None
        if args.pages_artifact_metadata:
            pages = json.loads(args.pages_artifact_metadata.read_text())
            assert pages["name"] == "github-pages" and pages["workflow_run"]["id"] == args.run
            for field, source in (("artifact_id", "id"), ("artifact_digest", "digest"), ("artifact_size_bytes", "size_in_bytes")):
                assert provenance["candidate"][field] == pages[source], "Candidate Pages metadata mismatch: " + field
            assert pages["workflow_run"]["head_sha"] == args.source
        temporary.rename(args.output)
        record = {"acquired_utc": datetime.now(timezone.utc).isoformat(), "archive": args.archive.name,
                  "archive_sha256": actual_hash, "archive_bytes": args.archive.stat().st_size,
                  "artifact": metadata, "candidate_pages_metadata_crosscheck": pages,
                  "files": files, "scope": "Downloaded performance archive bytes match GitHub's recorded SHA256 and size. ZIP contents are preserved exactly. Pages archive content is audited separately."}
        record_path.write_text(json.dumps(record, indent=2) + "\n")
        print(json.dumps({"verified": True, "artifact_id": metadata["id"], "sha256": actual_hash,
                          "bytes": args.archive.stat().st_size, "files": len(files), "output": str(args.output)}, indent=2))
    except BaseException:
        if temporary.exists():
            shutil.rmtree(temporary)
        raise


if __name__ == "__main__":
    main()
