#!/usr/bin/env python3
"""Run identical terrain harness binaries with exactly one renderer DLL changed."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files(directory: Path) -> dict[str, str]:
    return {str(path.relative_to(directory)): digest(path)
            for path in sorted(directory.rglob("*")) if path.is_file()}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cpu", type=int, default=8)
    parser.add_argument("--samples", type=int, default=64)
    args = parser.parse_args()
    baseline = files(args.baseline)
    candidate = files(args.candidate)
    assert baseline.keys() == candidate.keys(), "Runtime file sets differ"
    different = [name for name in baseline if baseline[name] != candidate[name]]
    assert different == ["PixelMap.Rendering.dll"], f"Unexpected changed files: {different}"
    args.output.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(args.dotnet.parent), DOTNET_PROCESSOR_COUNT="2",
               DOTNET_TieredCompilation="0", COMPlus_TieredCompilation="0")
    metadata = {
        "baseline_source": "ce82b2c6fccee2d77a9e9489e1c6415e04412e8c",
        "candidate_scope": "Baseline plus TerrainCache.cs and PixelMapRenderer.Terrain.cs changes in candidate.patch",
        "os": platform.platform(), "machine": platform.machine(), "cpu_affinity": [args.cpu],
        "cpu_model": next((line.split(":", 1)[1].strip() for line in Path("/proc/cpuinfo").read_text().splitlines()
                           if line.startswith("model name")), "unknown"),
        "managed_processors": 2, "tiered_compilation": False, "samples_per_process": args.samples,
        "order_per_mode": ["baseline", "candidate", "candidate", "baseline"],
        "baseline_files": baseline, "candidate_files": candidate,
        "dotnet_sha256": digest(args.dotnet),
        "dotnet_info": subprocess.check_output([str(args.dotnet), "--info"], text=True, env=env),
        "runs": [],
    }
    for mode in ("micro", "frames"):
        for number, variant in enumerate(metadata["order_per_mode"], 1):
            directory = getattr(args, variant)
            output = args.output / f"{mode}-{number:02d}-{variant}"
            if mode == "micro":
                options = ["--terrain-texture-copy-cost", f"--terrain-copy-output={output}",
                           f"--terrain-copy-samples={args.samples}"]
            else:
                options = ["--terrain-frame-cost", f"--terrain-frame-output={output}",
                           f"--terrain-frame-samples={args.samples}"]
            command = ["taskset", "-c", str(args.cpu), str(args.dotnet),
                       str(directory / "PixelMap.Rendering.Tests.dll"), *options]
            log = args.output / f"{mode}-{number:02d}-{variant}.log"
            started = time.time()
            with log.open("w") as stream:
                subprocess.run(command, check=True, env=env, stdout=stream, stderr=subprocess.STDOUT)
            report = output / ("terrain-copy.json" if mode == "micro" else "terrain-frame-cost.json")
            metadata["runs"].append({"mode": mode, "number": number, "variant": variant,
                "command": command, "started_unix_seconds": started,
                "elapsed_process_seconds": time.time() - started,
                "report": str(report.relative_to(args.output)), "report_sha256": digest(report),
                "log": log.name, "log_sha256": digest(log)})
            (args.output / "provenance.json").write_text(json.dumps(metadata, indent=2) + "\n")
            print(f"Completed {mode} {number}/4: {variant}", flush=True)
    assert files(args.baseline) == baseline and files(args.candidate) == candidate, "Runtime changed during measurement"


if __name__ == "__main__":
    main()
