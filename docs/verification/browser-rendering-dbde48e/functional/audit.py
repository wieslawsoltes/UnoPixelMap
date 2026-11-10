#!/usr/bin/env python3
"""Audit only the retained functional evidence; no browser or network access.

Requires Python 3.11+ and Pillow. The omitted original artifact members are
covered by the historical audit.json, or by audit_downloads.py when the original
ZIP is supplied. This script never infers their contents from the selected set.
"""
from __future__ import annotations

import base64
import gzip
import hashlib
import io
import json
import re
import zipfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent
SOURCE = "dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6"
RUN = 42907891884
VERIFY_JOB = 130158519794
NAMES = ["presentation-" + name for name in
         ("pixel", "normal", "minecraft", "roblox", "earth-overhead", "elevation")]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_json(path: Path):
    return json.loads(path.read_bytes())


def main():
    retention = read_json(ROOT / "retention.json")
    assert retention["source_sha"] == SOURCE and retention["workflow_run_id"] == RUN
    restored = {}
    for name, identity in retention["files"].items():
        stored = (ROOT / name).read_bytes()
        assert len(stored) == identity["stored_bytes"] and sha256(stored) == identity["stored_sha256"], name
        raw = gzip.decompress(stored) if identity["encoding"] == "gzip" else stored
        assert len(raw) == identity["original_bytes"] and sha256(raw) == identity["original_sha256"], name
        restored[name] = raw

    original = json.loads(restored["audit.json"])
    linkage = json.loads(restored["package-linkage.json"])
    artifacts = [json.loads(restored[name]) for name in ("screens-artifact.json", "pages-artifact.json")]
    for artifact, name, artifact_id in zip(artifacts, ("atlas-browser-screens", "github-pages"),
                                          (13807507180, 13807570605)):
        assert artifact["name"] == name and artifact["id"] == artifact_id
        assert artifact["workflow_run"]["id"] == RUN
        assert artifact["workflow_run"]["head_sha"] == SOURCE
        assert artifact["workflow_run"]["repository_id"] == 1443492055
    assert original["source_sha"] == SOURCE and original["run_id"] == RUN
    assert original["artifact"]["id"] == artifacts[0]["id"]
    assert "sha256:" + original["artifact"]["sha256"] == artifacts[0]["digest"]
    assert original["file_count"] == retention["full_download_artifact_file_count"] == 154
    assert linkage["source_sha"] == SOURCE and linkage["run_id"] == RUN
    assert linkage["pages_artifact_id"] == artifacts[1]["id"]
    assert "sha256:" + linkage["pages_archive_sha256"] == artifacts[1]["digest"]
    job = json.loads(restored["verify-job.json"])
    assert job["id"] == VERIFY_JOB and job["run_id"] == RUN
    assert job["status"] == "completed" and job["conclusion"] == "success"
    assert all(step["conclusion"] == "success" for step in job["steps"])
    for name, identity in retention["files"].items():
        if "original_member" in identity:
            recorded = original["files"]["evidence/" + identity["original_member"]]
            assert recorded["sha256"] == identity["original_sha256"]
            assert recorded["bytes"] == identity["original_bytes"]

    direct = json.loads(restored["gpu-direct-visual.json.gz"])
    detached = json.loads(restored["gpu-transfer-visual.json.gz"])
    for report in (direct, detached):
        assert [case["name"] for case in report["cases"]] == NAMES
        assert report["errors"] == [] and report["requestedGpuRendering"] is True
        assert report["mode"] == "visual"
    for field in ("browser", "viewport", "deviceScaleFactor", "browserArgs", "harnessSha256", "runtime"):
        assert direct[field] == detached[field], field
    source_tests = json.loads(restored["source-tests.json"])
    assert source_tests["source_sha"] == SOURCE
    assert direct["harnessSha256"] == source_tests["files"]["tests/browser/performance.cjs"]
    assert direct["requiredCompositorGpu"] is True and direct["forcedDetachedGpu"] is False
    assert detached["forcedDetachedGpu"] is True and detached["requiredCompositorGpu"] is False
    runtime = direct["runtime"]
    recorded_resources = {
        linkage["package"] + "/uno-config.js": runtime["bootstrapConfigSha256"],
        "_framework/" + runtime["loaderName"]: runtime["loaderSha256"],
    }
    for item in runtime["nativeWasm"] + runtime["assemblies"]:
        recorded_resources["_framework/" + item["name"]] = base64.b64decode(
            item["hash"].removeprefix("sha256-")).hex()
    assert recorded_resources == linkage["matched_report_resources"]

    map_bytes = (ROOT / "oracle-images.json").read_bytes()
    mapping = json.loads(map_bytes)
    assert mapping["source_sha"] == SOURCE and mapping["workflow_run_id"] == RUN
    assert mapping["artifact_id"] == artifacts[0]["id"]
    assert "sha256:" + mapping["original_archive_sha256"] == artifacts[0]["digest"]
    members = {item["original_member"]: item for item in mapping["members"]}
    expected_members = {mode + "/" + name + suffix for mode in ("gpu-direct", "gpu-transfer")
                        for name in NAMES for suffix in (".map.png", ".png")}
    assert len(members) == len(mapping["members"]) == 24 and set(members) == expected_members
    archive_bytes = (ROOT / mapping["archive"]).read_bytes()
    assert len(archive_bytes) == retention["images"]["stored_bytes"]
    assert sha256(archive_bytes) == retention["images"]["stored_sha256"]
    pixels = {}
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        expected_stored = {item["stored_member"] for item in members.values()}
        assert len(expected_stored) == mapping["unique_png_streams"] == 12
        assert set(archive.namelist()) == expected_stored | {"members.json"}
        assert len(archive.namelist()) == len(expected_stored) + 1
        assert archive.read("members.json") == map_bytes
        for member, identity in members.items():
            raw = archive.read(identity["stored_member"])
            assert sha256(raw) == identity["sha256"] and len(raw) == identity["bytes"], member
            recorded = original["files"]["evidence/" + member]
            assert recorded["sha256"] == identity["sha256"] and recorded["bytes"] == identity["bytes"]
            with Image.open(io.BytesIO(raw)) as image:
                rgba = image.convert("RGBA")
                assert rgba.size == (identity["width"], identity["height"])
                pixels[member] = (rgba.size, rgba.tobytes())

    comparisons = []
    full_capture_pixels = 0
    for index, name in enumerate(NAMES):
        for mode, report in (("gpu-direct", direct), ("gpu-transfer", detached)):
            case = report["cases"][index]
            assert "GPU active ·" in case["actualBackend"]
            size, frame = pixels[mode + "/" + name + ".png"]
            map_size, map_frame = pixels[mode + "/" + name + ".map.png"]
            assert sha256(frame) == case["canvasHash"] and sha256(map_frame) == case["mapHash"]
            crop = case["crop"]
            extracted = Image.frombytes("RGBA", size, frame).crop((crop["x"], crop["y"],
                crop["x"] + crop["width"], crop["y"] + crop["height"]))
            assert extracted.size == map_size and extracted.tobytes() == map_frame
            gpu = case["gpuEvidence"]
            assert gpu["mapResolves"]
            assert any(count > 0 for count in gpu["artSizedDepthTestedDrawsByPixelSize"].values())
            if mode == "gpu-direct":
                assert all(resolve["compositorContext"] for resolve in gpu["mapResolves"])
                assert gpu["readbacks"] == []
            else:
                assert all(not resolve["compositorContext"] for resolve in gpu["mapResolves"])
                assert any(read["mode"] == "pixelPack" for read in gpu["readbacks"])
        assert pixels["gpu-direct/" + name + ".png"] == pixels["gpu-transfer/" + name + ".png"]
        assert pixels["gpu-direct/" + name + ".map.png"] == pixels["gpu-transfer/" + name + ".map.png"]
        size = pixels["gpu-direct/" + name + ".png"][0]
        full_capture_pixels += size[0] * size[1]
        map_size = pixels["gpu-direct/" + name + ".map.png"][0]
        comparisons.append({"name": name, "differentMapPixels": 0, "totalMapPixels": map_size[0] * map_size[1],
                            "maxChannelDelta": 0, "exactCanvas": True})
    assert comparisons == json.loads(restored["comparison.json"])
    assert comparisons == original["presentation"]["cases"]
    assert full_capture_pixels == original["presentation"]["total_full_canvas_pixels"]

    log = restored["verify-job.log.gz"].decode()
    for marker, count in original["log_markers"].items():
        assert log.count(marker) == count, marker
    assert not re.search(r"AssertionError|##\[error\]|Unhandled (?:managed )?exception", log)
    close = json.loads(restored["high-dpi-close-camera.json.gz"])
    assert close["failures"] == [] and len(close["checkpoints"]) == 9
    assert close["geometry"] == {"width": 1800, "height": 924, "ratio": 2,
                                  "canvasWidth": 3600, "canvasHeight": 1848}
    assert [item["name"] for item in close["checkpoints"]] == original["close_camera"]["checkpoints"]

    print(json.dumps({
        "schema": 1, "source_sha": SOURCE, "workflow_run_id": RUN, "verify_job_id": VERIFY_JOB,
        "browser": direct["browser"], "runtime_mode": runtime["mode"],
        "retained_original_reports": 4, "retained_original_png_members": len(members),
        "unique_original_png_streams": mapping["unique_png_streams"],
        "presentation": {"cases": comparisons, "total_map_pixels": sum(c["totalMapPixels"] for c in comparisons),
                         "total_full_capture_pixels": full_capture_pixels,
                         "recomputed_image_hashes_and_crops": True,
                         "direct_zero_readbacks": True, "detached_async_pixel_pack": True},
        "verified_log_markers": original["log_markers"],
        "close_camera_checkpoints": len(close["checkpoints"]), "close_camera_failures": close["failures"],
        "recorded_pages_resource_hashes_consistent": len(recorded_resources),
        "scope": "Selected retained reports and 24 original PNG members, complete saved job logs, and recorded API/package metadata. The 126 omitted browser-artifact members and original full ZIP digests are not rechecked by this script. Use audit_downloads.py with those ZIPs to recheck them. Presentation equality compares two paths within the same release; this is not a cross-release timing test."
    }, indent=2))


if __name__ == "__main__":
    main()
