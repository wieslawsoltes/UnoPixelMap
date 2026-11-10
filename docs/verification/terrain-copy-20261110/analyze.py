#!/usr/bin/env python3
"""Audit exact controls and derive pooled terrain benchmark results from raw reports."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import statistics


def read(path: Path) -> bytes:
    if path.exists():
        return path.read_bytes()
    return gzip.decompress(Path(str(path) + ".gz").read_bytes())


def summary(values: list[float]) -> dict:
    ordered = sorted(values)
    return {"count": len(values), "median": statistics.median(ordered),
            "p95": ordered[math.ceil(len(ordered) * .95) - 1], "mean": statistics.mean(ordered),
            "minimum": ordered[0], "maximum": ordered[-1]}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    provenance = json.loads(read(args.input / "provenance.json"))
    assert provenance["order_per_mode"] == ["baseline", "candidate", "candidate", "baseline"]
    reports = {mode: [] for mode in ("micro", "frames")}
    harnesses = set()
    for run in provenance["runs"]:
        raw = read(args.input / run["report"])
        assert hashlib.sha256(raw).hexdigest() == run["report_sha256"]
        report = json.loads(raw)
        assert report["Samples"] == provenance["samples_per_process"] and report["Warmup"] == 16
        expected = provenance[f'{run["variant"]}_files']["PixelMap.Rendering.dll"]
        assert report["RendererSha256"] == expected
        harnesses.add(report["HarnessSha256"])
        reports[run["mode"]].append((run["variant"], report))
    assert len(harnesses) == 1, "Harness binaries differ"
    result = {"baseline_source": provenance["baseline_source"], "harness_sha256": next(iter(harnesses)),
              "quantiles": "Pooled median averages middle samples; p95 is nearest rank. Every raw sample retained.",
              "modes": {}}
    for mode, runs in reports.items():
        assert [variant for variant, _ in runs] == provenance["order_per_mode"]
        names = [item["Name"] for item in runs[0][1]["Reports"]]
        assert all([item["Name"] for item in report["Reports"]] == names for _, report in runs)
        workloads = []
        for index, name in enumerate(names):
            controls = []
            values = {"baseline": {"milliseconds": [], "bytes": []}, "candidate": {"milliseconds": [], "bytes": []}}
            individual = []
            for variant, report in runs:
                item = report["Reports"][index]
                if mode == "micro":
                    times, allocations = item["SamplesMs"], item["AllocatedBytes"]
                    controls.append({"Source": item["Source"], "ZoomDelta": item["ZoomDelta"], "Patches": item["Patches"],
                                     "Hashes": item["Hashes"]})
                else:
                    times = [sample["PrepareMs"] for sample in item["Samples"]]
                    allocations = [sample["AllocatedBytes"] for sample in item["Samples"]]
                    controls.append([{key: value for key, value in sample.items() if key not in ("PrepareMs", "AllocatedBytes")}
                                     for sample in item["Samples"]])
                assert len(times) == len(allocations) == provenance["samples_per_process"]
                values[variant]["milliseconds"].extend(times)
                values[variant]["bytes"].extend(allocations)
                individual.append({"variant": variant, "milliseconds": summary(times), "bytes": summary(allocations)})
            assert all(control == controls[0] for control in controls), f"Exact controls differ for {mode}/{name}"
            stats = {variant: {metric: summary(samples) for metric, samples in data.items()} for variant, data in values.items()}
            baseline, candidate = stats["baseline"], stats["candidate"]
            workload = {"name": name, "baseline": baseline, "candidate": candidate,
                "median_change_percent": (candidate["milliseconds"]["median"] / baseline["milliseconds"]["median"] - 1) * 100,
                "mean_allocation_change_bytes": candidate["bytes"]["mean"] - baseline["bytes"]["mean"],
                "control_sha256": hashlib.sha256(json.dumps(controls[0], sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
                "exact_controls": True, "processes": individual}
            if mode == "frames":
                first = runs[0][1]["Reports"][index]
                workload["per_process_texture_misses"] = first["TotalTextureMisses"]
                workload["per_process_eligible_misses"] = first["EligibleTextureMisses"]
                workload["eligible_miss_percent"] = first["EligibleMissPercent"]
            workloads.append(workload)
        result["modes"][mode] = workloads
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    for mode, workloads in result["modes"].items():
        for item in workloads:
            print(f'{mode}/{item["name"]}: {item["baseline"]["milliseconds"]["median"]:.4f} → '
                  f'{item["candidate"]["milliseconds"]["median"]:.4f} ms ({item["median_change_percent"]:+.2f}%), '
                  f'{item["mean_allocation_change_bytes"]:+.0f} bytes; exact controls')


if __name__ == "__main__":
    main()
