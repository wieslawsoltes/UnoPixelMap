#!/usr/bin/env python3
"""Freeze this harness, build both revisions, and replay native CPU workloads in ABBA order.

No browser, HTTP server or graphics device is launched. Every build finishes before
measurement. Failed scene/texel/glyph/picking identity gates still preserve raw JSON.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import tempfile


HARNESS_FILES = (
    "tests/PixelMap.Rendering.Performance/PixelMap.Rendering.Performance.csproj",
    "tests/PixelMap.Rendering.Performance/Program.cs",
    "tests/PixelMap.Rendering.Performance/WebGpuFrameBenchmark.cs",
    "tests/PixelMap.Rendering.Tests/TestLabelFont.cs",
    "tests/PixelMap.Rendering.Tests/TerrainArtworkInvalidationChecks.cs",
)
FRAME_IDENTITIES = ("Index", "TerrainPatches", "BuildingTriangles", "Labels", "OverlayTriangles",
                    "GeometryHash", "ArtworkHash", "OverlayHash", "AtlasHash", "PickingHash")
STREAM_IDENTITIES = ("Frame", "TerrainPatches", "TextureHash")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def run(command, log, env):
    with log.open("w") as output:
        result = subprocess.run([str(x) for x in command], stdout=output, stderr=subprocess.STDOUT, env=env)
    if result.returncode:
        print(log.read_text()[-12000:], flush=True)
        raise RuntimeError(f"Command failed ({result.returncode}); full output: {log}")


def summarize(values):
    if any(value is None for value in values):
        return None
    ordered = sorted(values)
    at = (len(ordered) - 1) * .95
    low = int(at)
    high = min(low + 1, len(ordered) - 1)
    return {"samples": len(ordered), "median": statistics.median(ordered),
            "p95": ordered[low] + (ordered[high] - ordered[low]) * (at - low)}


def compare_metric(before, after):
    a, b = summarize(before), summarize(after)
    return {"baseline": a, "candidate": b,
            "medianSpeedup": a["median"] / b["median"] if a and b and b["median"] > 0 else None,
            "medianReductionPercent": 100 * (1 - b["median"] / a["median"]) if a and b and a["median"] > 0 else None}


def compare(runs):
    checks, failures, output = 0, [], {"frames": {}, "streaming": {}}
    for kind, collection, identity in (("frames", "Results", FRAME_IDENTITIES),
                                       ("streaming", "Reports", STREAM_IDENTITIES)):
        reference = runs[0][kind]
        names = [item["Name"] for item in reference[collection]]
        for run_index, candidate in enumerate(runs):
            report = candidate[kind]
            for field in ("Runtime", "Platform", "Processors", "Width", "Height", "Warmup"):
                if report[field] != reference[field]:
                    failures.append(f"{kind} run {run_index}: {field} changed")
            if kind == "frames" and report["Font"] != reference["Font"]:
                failures.append(f"{kind} run {run_index}: font identity changed")
            if [item["Name"] for item in report[collection]] != names:
                failures.append(f"{kind} run {run_index}: workload order/set changed")
        for ordinal, name in enumerate(names):
            workloads = [r[kind][collection][ordinal] for r in runs]
            for run_index, workload in enumerate(workloads[1:], 1):
                if len(workload["Samples"]) != len(workloads[0]["Samples"]):
                    failures.append(f"{name} run {run_index}: sample count changed")
                for sample_index, (original, current) in enumerate(zip(workloads[0]["Samples"], workload["Samples"])):
                    for field in identity:
                        checks += 1
                        if original[field] != current[field]:
                            failures.append(f"{name} run {run_index} sample {sample_index}: {field} differs")
            before = [s for i in (0, 3) for s in workloads[i]["Samples"]]
            after = [s for i in (1, 2) for s in workloads[i]["Samples"]]
            metrics = {}
            if kind == "frames":
                for phase in ("Preparation", "Overlay", "Picking", "PreparationAndOverlay"):
                    for unit in ("WallMilliseconds", "ThreadCpuMilliseconds", "AllocatedBytes"):
                        def value(sample):
                            if phase != "PreparationAndOverlay":
                                return sample[phase][unit]
                            values = [sample[part][unit] for part in ("Preparation", "Overlay")]
                            return sum(values) if all(v is not None for v in values) else None
                        metrics[f"{phase}.{unit}"] = compare_metric([value(s) for s in before], [value(s) for s in after])
            else:
                for unit in ("PrepareMs", "AllocatedBytes", "RebakedTextures", "RebakedBaseBytes"):
                    metrics[unit] = compare_metric([s[unit] for s in before], [s[unit] for s in after])
            output[kind][name] = metrics
    output.update(identityChecks=checks, identityFailures=failures, passed=not failures)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--font", type=Path, default=Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))
    parser.add_argument("--samples", type=int, default=32)
    args = parser.parse_args()
    if not 1 <= args.samples <= 256:
        parser.error("samples must be between 1 and 256")
    roots = {"baseline": args.baseline.resolve(), "candidate": args.candidate.resolve()}
    harness = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    args.font = args.font.resolve(strict=True)
    environment = {**os.environ, "PIXELMAP_TEST_FONT": str(args.font), "DOTNET_TieredCompilation": "0"}
    manifest = {
        "scope": "Native CPU preparation/overlay/picking and source invalidation; no device, GPU timing, browser or display FPS.",
        "order": ["baseline", "candidate", "candidate", "baseline"],
        "warmupFrames": 32, "measuredFramesPerRun": args.samples,
        "tieredCompilation": False, "fontSha256": sha256(args.font),
        "platform": platform.platform(), "architecture": platform.machine(),
        "processor": platform.processor(),
        "affinity": sorted(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
        "sources": {variant: {"commit": git(root, "rev-parse", "HEAD"),
                              "tree": git(root, "rev-parse", "HEAD^{tree}"),
                              "uncommitted": git(root, "status", "--porcelain")}
                    for variant, root in roots.items()},
        "frozenHarnessFiles": {name: sha256(harness / name) for name in HARNESS_FILES},
        "driverSha256": sha256(Path(__file__)),
    }
    if Path("/proc/cpuinfo").exists():
        manifest["cpuModels"] = sorted({line.split(":", 1)[1].strip() for line in Path("/proc/cpuinfo").read_text().splitlines()
                                       if line.startswith("model name")})
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    with tempfile.TemporaryDirectory(prefix="pixelmap-native-") as temporary:
        hosts = {}
        for variant, root in roots.items():
            frozen = Path(temporary) / variant
            for name in HARNESS_FILES:
                destination = frozen / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(harness / name, destination)
            hosts[variant] = frozen / "host"
            print(f"Building {variant} from {manifest['sources'][variant]['commit']}...", flush=True)
            run([args.dotnet, "build", frozen / HARNESS_FILES[0], "-c", "Release", "-m:1",
                 "-p:UseSharedCompilation=false", "-p:RendererSourceRoot=" + str(root), "-o", hosts[variant]],
                output / f"build-{variant}.log", environment)
        runs = []
        for index, variant in enumerate(manifest["order"]):
            label = f"{index + 1}-{variant}"
            destination = output / label
            destination.mkdir()
            program = [args.dotnet, hosts[variant] / "PixelMap.Rendering.Tests.dll"]
            print(f"Measuring {label}: warm camera replay...", flush=True)
            run([*program, "--output=" + str(destination / "frames.json"),
                 "--source=" + manifest["sources"][variant]["commit"], "--warmup=32", f"--samples={args.samples}"],
                destination / "frames.log", environment)
            print(f"Measuring {label}: source streaming...", flush=True)
            run([*program, "--terrain-streaming-cost", "--terrain-streaming-output=" + str(destination),
                 "--terrain-streaming-warmup=32", f"--terrain-streaming-samples={args.samples}"],
                destination / "streaming.log", environment)
            runs.append({"frames": json.loads((destination / "frames.json").read_text()),
                         "streaming": json.loads((destination / "terrain-streaming.json").read_text())})
    comparison = compare(runs)
    (output / "comparison.json").write_text(json.dumps(comparison, indent=2) + "\n")
    print(f"Identity checks: {comparison['identityChecks']}; failures: {len(comparison['identityFailures'])}", flush=True)
    for kind, key in (("frames", "PreparationAndOverlay.WallMilliseconds"), ("streaming", "PrepareMs")):
        for name, metrics in comparison[kind].items():
            metric = metrics[key]
            print(f"{name}: {metric['baseline']['median']:.3f} -> {metric['candidate']['median']:.3f} ms "
                  f"({metric['medianSpeedup']:.2f}x)", flush=True)
    if not comparison["passed"]:
        raise SystemExit("Scene identity failed; inspect comparison.json before using any timing claim.")


if __name__ == "__main__":
    main()
