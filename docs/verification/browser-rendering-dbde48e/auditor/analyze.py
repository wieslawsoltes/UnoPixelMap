#!/usr/bin/env python3
"""Audit one paired CI artifact. Never merge timing data from different runners.

Python 3.10+ and Node.js are sufficient; no browser or npm install is required.
The adjacent frozen helpers are copied byte-for-byte from candidate dbde48e.
"""
import argparse
import csv
import gzip
import hashlib
import json
import math
import re
import statistics
import subprocess
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASELINE = "555990d0e086ffb9908d4d98ec30a73a245a6c6e"
CANDIDATE = "dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6"
SCENARIOS = ("map-idle-default", "map-idle-relief", "walk-stationary-firstperson",
             "walk-moving-firstperson", "walk-moving-chase", "car-stationary-chase",
             "car-moving-chase", "car-moving-firstperson", "freeflight-stationary", "freeflight-moving")
REPLAYS = ("map-pan-relief", "map-orbit-relief", "walk-look-firstperson")
PROFILE_CASES = {"map-idle-relief", "walk-stationary-firstperson", "walk-moving-firstperson",
                 "walk-moving-chase", "car-moving-chase", "car-moving-firstperson", "freeflight-moving"}
BASE_ARGS = ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
SCOPE = (
    "This is a paired comparison within one CI runner, with fresh serial browser processes. "
    "Primary summaries are medians across independent process values; every process value and range is retained. "
    "Task CPU uses main-thread threadTicks; occupancy divides TaskDuration by the CDP Timestamp metric interval, "
    "which includes probe-result transfer. Map submissions and rAF rates divide counts by the probe capture interval. "
    "Map submissions are submitted map images; rAF is callback cadence; neither is physical display FPS. "
    "Fence values are the application's observed signal/polling delays, not actual GPU completion or display latency. "
    "They include polling cadence and SwiftShader; a long fence value does not establish visible lag. "
    "Held-key fence distributions include only signals observed in the capture: carry-in signals and unobserved tail submissions are counted separately. "
    "Held-key trajectories may diverge: retain public HUD poses/distances and art dimensions. "
    "One stationary or two moving processes per variant support descriptive comparisons, not confidence intervals. "
    "CPU profiles are separate later passes, only the seven preselected cases in repetition one. "
    "Replay comparisons require all four captures to retain the same ordered 32 trusted inputs and every projection/draw; "
    "otherwise comparative replay latency is withheld. Barrier-paced replay latency is not throughput or FPS. "
    "Replay identity is workload identity, not pixel equivalence. CPU includes observer and automation work."
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def dump(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")


def parse_time(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def change(before, after):
    return None if before in (None, 0) or after is None else (after / before - 1) * 100


def process_summary(values):
    # These are independent process observations, NOT the harness's within-run
    # upper-middle order statistic. With two processes the median is their mean.
    finite = [v for v in values if v is not None]
    return {"values": values, "samples": len(finite),
            "median": statistics.median(finite) if finite else None,
            "minimum": min(finite) if finite else None, "maximum": max(finite) if finite else None}


def distribution(values):
    # Verbatim quantile convention of performance.cjs, on within-process samples.
    ordered = sorted(values)
    if not ordered:
        return {"samples": 0}
    return {"samples": len(ordered), "medianMs": ordered[len(ordered) // 2],
            "p95Ms": ordered[min(len(ordered) - 1, math.floor(len(ordered) * .95))],
            "maxMs": ordered[-1], "meanMs": statistics.mean(ordered)}


class Evidence:
    def __init__(self, root):
        self.root = root.resolve()
        self.files = {}

    def path(self, relative):
        candidate = self.root / relative
        require(candidate.resolve().is_relative_to(self.root), "Evidence path escapes input root")
        if not candidate.is_file():
            candidate = Path(str(candidate) + ".gz")
        require(candidate.resolve().is_relative_to(self.root), "Evidence symlink escapes input root")
        require(candidate.is_file(), "Missing evidence: " + str(relative))
        return candidate

    def read(self, relative):
        stored = self.path(relative)
        data = stored.read_bytes()
        content = gzip.decompress(data) if stored.suffix == ".gz" else data
        self.files[str(stored.relative_to(self.root))] = {
            "stored_bytes": len(data), "stored_sha256": sha256(data),
            "content_bytes": len(content), "content_sha256": sha256(content)}
        return json.loads(content)

    def logical_files(self, pattern):
        result = []
        for p in self.root.glob(pattern):
            if p.is_file():
                relative = str(p.relative_to(self.root))
                result.append(relative[:-3] if relative.endswith(".gz") else relative)
        require(len(result) == len(set(result)), "Duplicate compressed/plain evidence entries: " + pattern)
        return set(result)


class Auditor:
    def __init__(self, args):
        self.args, self.evidence = args, Evidence(args.input)
        self.errors, self.warnings, self.checks = [], [], []
        self.environment, self.provenance = {}, {}
        self.headers = {}
        self.play_records, self.play_raw, self.profiles = [], {}, []
        self.replay_records, self.replay_comparisons = [], []
        self.source = json.loads((HERE / "source-provenance.json").read_text())
        self.expected_hashes = {Path(k).name: v["sha256"] for k, v in self.source["harness"].items()}
        for override in args.harness_hash:
            name, value = override.split("=", 1)
            require(name in self.expected_hashes and re.fullmatch(r"[0-9a-f]{64}", value), "Invalid harness override")
            self.expected_hashes[name] = value

    def checked(self, label, function):
        try:
            value = function()
            self.checks.append({"check": label, "passed": True})
            return value
        except (OSError, ValueError, KeyError, TypeError, AssertionError, IndexError, subprocess.SubprocessError) as error:
            self.errors.append({"check": label, "error": str(error)})
            self.checks.append({"check": label, "passed": False})
            return None

    def node(self, action, *paths):
        run = subprocess.run([self.args.node, str(HERE / "verify-raw.cjs"), action, *map(str, paths)],
                             text=True, capture_output=True, timeout=60)
        try:
            result = json.loads(run.stdout)
        except ValueError:
            raise ValueError("Node audit returned no JSON: " + run.stderr[-2000:])
        require(run.returncode == 0 and result.get("valid"), result.get("error", run.stderr[-2000:]))
        return result

    def metadata(self):
        self.provenance = self.evidence.read("provenance.json")
        self.environment = self.evidence.read("environment.json")
        for name, item in self.source["harness"].items():
            require(sha256((HERE / "frozen-harness" / Path(name).name).read_bytes()) == item["sha256"],
                    "Frozen source bytes equal source manifest: " + name)
        for variant, expected_sha, expected_run in (("baseline", self.args.baseline_sha, self.args.baseline_run),
                                                    ("candidate", self.args.candidate_sha, self.args.candidate_run)):
            p = self.provenance[variant]
            require(p["source_sha"] == expected_sha and p["run_id"] == expected_run,
                    variant + " source SHA and run ID must match the requested paired comparison")
            require(re.fullmatch(r"[0-9a-f]{40}", p["source_sha"]), "Full source commit SHA")
            require(p["run_url"] == f"https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/{expected_run}", "Run URL")
            require(p["runtime_mode"] == "InterpreterAndAOT", "InterpreterAndAOT runtime mode")
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", p["artifact_digest"]), "Pages artifact digest format")
            require(isinstance(p["artifact_id"], int) and p["artifact_id"] > 0, "Pages artifact ID")
            require(isinstance(p["artifact_size_bytes"], int) and p["artifact_size_bytes"] > 0, "Pages artifact size")
            for key in ("index_sha256", "config_sha256", "loader_sha256"):
                require(re.fullmatch(r"[0-9a-f]{64}", p[key]), key)
            resources = p["resources"]
            require(resources and len({(r["group"], r["name"]) for r in resources}) == len(resources), "Nonempty unique resources")
            require(all(re.fullmatch(r"[0-9a-f]{64}", r["sha256"]) for r in resources), "All boot resources have SHA256 values")
            require(any(r["name"].endswith(".wasm") for r in resources), "Boot manifest records WASM resources")
        require(self.provenance["baseline_selection"]["resolved_run_id"] == self.args.baseline_run, "Resolved baseline selection")
        known = json.loads(self.args.known_baseline_artifact.read_text())
        actual = self.provenance["baseline"]
        for key, value in known.items():
            if key == "resources":
                value = sorted(value, key=lambda r: (r["group"], r["name"]))
                actual_value = sorted(actual[key], key=lambda r: (r["group"], r["name"]))
            else:
                actual_value = actual[key]
            require(actual_value == value, "Baseline artifact differs from preserved metadata: " + key)
        required_names = ["play-performance.cjs", "performance.cjs", "explore.cjs"]
        if self.args.replay == "required":
            required_names += ["camera-replay.cjs", "camera-replay-probe.cjs", "camera-replay-contract.cjs"]
        for name in required_names:
            require(self.environment[name + "_sha256"] == self.expected_hashes[name], "Captured harness hash: " + name)
        affinity = self.environment["cpu_affinity"].split(",")
        require(affinity and all(item.isdigit() for item in affinity) and len(set(affinity)) == len(affinity), "CPU affinity")
        require(re.fullmatch(r"[0-9a-f]{64}", self.environment["browser_sha256"]), "Browser binary hash")
        require(self.environment["extra_browser_args"] == ["--in-process-gpu", "--disable-gpu-watchdog", "--renderer-process-limit=1"], "Pinned browser arguments")
        return True

    def execution(self, replay=False):
        names = REPLAYS if replay else SCENARIOS
        expected = []
        for name in names:
            order = [(1, "baseline"), (1, "candidate")]
            if replay or "moving" in name:
                order += [(2, "candidate"), (2, "baseline")]
            expected.extend((name, variant, repetition) for repetition, variant in order)
        entries = self.provenance["camera_replay_execution" if replay else "execution"]
        require([(e["scenario"], e["variant"], e["repetition"]) for e in entries] == expected, "Complete exact execution order")
        require([e["order"] for e in entries] == list(range(1, len(expected) + 1)), "Contiguous execution ordinals")
        previous_end = None
        for e in entries:
            variant, name, repetition = e["variant"], e["scenario"], e["repetition"]
            expected_output = ("camera-replay/" if replay else "") + f"{variant}/{name}/repetition-{repetition}"
            require(e["output"] == expected_output, "Canonical output path")
            require(e["source_sha"] == self.provenance[variant]["source_sha"], "Execution source identity")
            if not replay:
                require(e["cpu_profile_requested"] == (repetition == 1 and name in PROFILE_CASES), "Preselected profile scope")
            start, end = parse_time(e["started_utc"]), parse_time(e["completed_utc"])
            require(start <= end and (previous_end is None or previous_end <= start), "Executions run serially")
            previous_end = end
        return entries

    def header(self, report, e, replay=False):
        require(not report.get("error") and e.get("exit_code") == 0, "Capture completed successfully: " + str(report.get("error")))
        require(report["revision"] == e["source_sha"], "Report revision equals execution revision")
        require(len(report["cases"]) == 1 and report["cases"][0]["name"] == e["scenario"], "Exactly one expected measured case")
        require(report["viewport"] == {"width": 1200, "height": 780} and report["deviceScaleFactor"] == 1, "Fixed viewport/DPR")
        require(report["browserArgs"] == BASE_ARGS + self.environment["extra_browser_args"], "Browser flags equal environment")
        versions = re.findall(r"(?<![\d.])(\d+\.\d+\.\d+\.\d+)(?![\d.])", self.environment["browser"])
        require(len(versions) == 1 and report["browser"] == versions[0], "Full browser version equals environment")
        models = re.findall(r"^Model name:\s*(\S.*)$", self.environment["cpu_description"], re.MULTILINE)
        require(len(models) == 1 and report["cpu"] == models[0].strip(), "Full CPU model equals environment")
        require(report["availableParallelism"] == len(self.environment["cpu_affinity"].split(",")), "Visible parallelism equals CPU affinity")
        require(report["base"] == f"http://127.0.0.1:8100/{e['variant']}/UnoPixelMap/", "Local artifact URL matches variant")
        captured = parse_time(report["capturedAtUtc"])
        require(parse_time(e["started_utc"]) <= captured <= parse_time(e["completed_utc"]), "Report timestamp belongs to process execution")
        fields = ("viewport", "deviceScaleFactor", "browser", "browserArgs", "cpu", "availableParallelism")
        common = {key: report[key] for key in fields}
        if self.headers:
            require(common == self.headers, "Every process uses identical runner/browser configuration")
        else:
            self.headers = common
        if replay:
            for name, value in report["harness"].items():
                require(value == self.environment[name + "_sha256"], "Replay helper matches environment: " + name)
            require(set(report["harness"]) == {"camera-replay.cjs", "camera-replay-probe.cjs", "camera-replay-contract.cjs", "performance.cjs", "explore.cjs"}, "Exact replay helper set")
        else:
            for field, name in (("harnessSha256", "play-performance.cjs"), ("explorationHarnessSha256", "explore.cjs"), ("productionPerfHarnessSha256", "performance.cjs")):
                require(report[field] == self.environment[name + "_sha256"], "Play helper matches environment: " + name)
            require(report["backend"] == "GPU active, SwiftShader" and report["metricsTimeDomain"] == "threadTicks", "Measured driver and CPU clock")
            require(report["durationRequestedMs"] == 8000 and report["warmupMs"] == 2000, "Published timing protocol")

    def profile(self, e, profile_path, case):
        d = self.evidence.read(profile_path)
        nodes = {n["id"]: n for n in d["nodes"]}
        # The Node audit has already checked graph structure and all node/delta references.
        weights = Counter()
        for node_id, delta in zip(d["samples"], d["timeDeltas"]):
            weights[nodes[node_id]["callFrame"]["functionName"]] += delta
        total = sum(d["timeDeltas"]) / 1000
        duration = (d["endTime"] - d["startTime"]) / 1000
        row = {"scenario": e["scenario"], "variant": e["variant"], "repetition": e["repetition"],
               "path": profile_path, "sample_count": len(d["samples"]), "nodes": len(nodes),
               "duration_ms": duration, "recorded_sample_weight_ms": total,
               "duration_minus_sample_weights_ms": duration - total,
               "zero_delta_samples": sum(delta == 0 for delta in d["timeDeltas"]),
               "idle_self_ms": weights["(idle)"] / 1000, "program_self_ms": weights["(program)"] / 1000,
               "garbage_collector_self_ms": weights["(garbage collector)"] / 1000,
               "recorded_nonidle_weight_ms": total - weights["(idle)"] / 1000,
               "summary_top_node_limit": 45, "reported_summary": case["profile"]}
        if row["zero_delta_samples"]:
            self.warnings.append({"profile": profile_path, "warning": "Harness top tables use delta || 1000; zero deltas exist. Exact recorded weights are retained separately."})
        self.profiles.append(row)

    def play(self, e):
        prefix = e["output"]
        report_path, raw_path = prefix + "/report.json", prefix + "/" + e["scenario"] + "-raw.json"
        report, data = self.evidence.read(report_path), self.evidence.read(raw_path)
        self.header(report, e)
        c, raw = report["cases"][0], data["raw"]
        profile_path = prefix + "/" + e["scenario"] + ".cpuprofile"
        node_args = [self.evidence.path(report_path), self.evidence.path(raw_path)]
        if e["cpu_profile_requested"]:
            node_args.append(self.evidence.path(profile_path))
        self.node("play", *node_args)
        if e["cpu_profile_requested"]:
            self.profile(e, profile_path, c)
        before_hud, after_hud = c["beforeHud"], c["afterHud"]
        distance = None if before_hud is None or after_hud is None else after_hud["distance"] - before_hud["distance"]
        if "moving" in c["name"]:
            require(distance is not None and distance > 0, "Moving process advances public distance")
        direct, packs = c["directGpuFrames"] or {}, c["pixelPackReadback"]
        submission_ids = {item["id"] for item in raw["resolves"]}
        observed_signals = [item for item in raw["syncCalls"] if item.get("resolveSignalLatencyMs") is not None]
        signal_ids = {item["resolveId"] for item in observed_signals}
        correlations = {
            "submitted_ids": len(submission_ids), "observed_signal_records": len(observed_signals),
            "observed_signal_ids": len(signal_ids),
            "in_capture_submissions_with_observed_signal": len(submission_ids & signal_ids),
            "signals_for_ids_outside_capture": len(signal_ids - submission_ids),
            "submitted_ids_without_observed_signal": len(submission_ids - signal_ids),
            "outside_capture_signal_ids": sorted(signal_ids - submission_ids),
            "unobserved_submission_ids": sorted(submission_ids - signal_ids)}
        row = {"scenario": c["name"], "variant": e["variant"], "repetition": e["repetition"], "order": e["order"],
               "source_sha": report["revision"], "report": report_path, "raw": raw_path,
               "capture_ms": c["durationMs"], "metric_window_ms": c["metricWindowMs"],
               "cpu_task_ms": c["cpu"]["TaskDurationMs"], "cpu_components_ms": c["cpu"],
               "cpu_occupancy_percent": c["cpuTaskPercent"],
               "map_submissions": c["mapResolves"]["submissions"],
               "map_submissions_per_second": c["mapResolves"]["submissionsPerSecond"],
               "map_submit_median_ms": c["mapResolves"]["intervals"].get("medianMs"),
               "map_submit_p95_ms": c["mapResolves"]["intervals"].get("p95Ms"),
               "signal_median_ms": c["mapResolves"]["submitToSignal"].get("medianMs"),
               "signal_p95_ms": c["mapResolves"]["submitToSignal"].get("p95Ms"),
               "observed_fence_correlations": correlations,
               "raf_count": len(raw["frames"]), "raf_per_second": c["rafPerSecond"], "raf_p95_ms": c["raf"].get("p95Ms"),
               "compositor_submissions": c["mapResolves"]["compositorSubmissions"],
               "detached_submissions": c["mapResolves"]["detachedSubmissions"],
               "pixel_sizes": c["mapResolves"]["byPixelSize"],
               "resolve_dimensions": dict(Counter(f"{r['width']}x{r['height']}" for r in raw["resolves"])),
               "direct_gpu_frames": direct, "pixel_pack_counters": {k: packs[k] for k in ("atStart", "atEnd", "maximumPending", "overwritten", "abandoned", "releasedWithoutCopy", "submissions", "positiveTimeoutPolls")},
               "read_pixels_calls": c["readPixels"]["samples"], "read_pixels_ms": c["readPixels"]["totalMs"],
               "pixel_pack_copies": packs["getBufferSubData"]["samples"],
               "completed_art_copies_per_second": c["completedArtFramesPerSecond"],
               "long_tasks": c["longTasks"], "long_task_ms": c["longTaskDurationMs"],
               "before_hud": before_hud, "after_hud": after_hud, "distance_delta": distance,
               "transfers_by_method": c["transfersByMethod"]}
        self.play_records.append(row)
        self.play_raw[(c["name"], e["variant"], e["repetition"])] = {
            "raf": [b[0] - a[0] for a, b in zip(raw["frames"], raw["frames"][1:])],
            "submissions": [b["time"] - a["time"] for a, b in zip(raw["resolves"], raw["resolves"][1:])],
            "signals": [s["resolveSignalLatencyMs"] for s in raw["syncCalls"] if s.get("resolveSignalLatencyMs") is not None]}
        return True

    def profile_inventory(self):
        expected = {f"{variant}/{name}/repetition-1/{name}.cpuprofile" for name in PROFILE_CASES for variant in ("baseline", "candidate")}
        actual = self.evidence.logical_files("*/*/repetition-*/*.cpuprofile*")
        require(actual == expected, "Profile inventory differs from 14 preselected files; missing=" + str(sorted(expected - actual)) + "; extra=" + str(sorted(actual - expected)))
        require(len(self.profiles) == 14, "All 14 profiles were independently checked")
        return True

    def aggregate_play(self):
        metrics = ("cpu_task_ms", "cpu_occupancy_percent", "map_submissions_per_second", "raf_per_second",
                   "map_submit_median_ms", "map_submit_p95_ms", "signal_median_ms", "signal_p95_ms", "raf_p95_ms")
        result = {}
        for name in SCENARIOS:
            scenario = result[name] = {"comparable": False}
            expected = 2 if "moving" in name else 1
            for variant in ("baseline", "candidate"):
                rows = sorted((r for r in self.play_records if r["scenario"] == name and r["variant"] == variant), key=lambda r: r["repetition"])
                if len(rows) != expected:
                    continue
                raws = [self.play_raw[(name, variant, r["repetition"])] for r in rows]
                scenario[variant] = {
                    "process_count": len(rows), "primary": {k: process_summary([r[k] for r in rows]) for k in metrics},
                    "secondary": {
                        "cpu_task_mean_ms": statistics.mean(r["cpu_task_ms"] for r in rows),
                        "weighted_cpu_occupancy_percent": sum(r["cpu_task_ms"] for r in rows) / sum(r["metric_window_ms"] for r in rows) * 100,
                        "weighted_map_submissions_per_second": sum(r["map_submissions"] for r in rows) / sum(r["capture_ms"] for r in rows) * 1000,
                        "weighted_raf_per_second": sum(r["raf_count"] for r in rows) / sum(r["capture_ms"] for r in rows) * 1000,
                        "pooled_within_process_distributions": {k: distribution([value for raw in raws for value in raw[k]]) for k in ("raf", "submissions", "signals")}},
                    "distance_delta": [r["distance_delta"] for r in rows], "pixel_sizes": [r["pixel_sizes"] for r in rows],
                    "resolve_dimensions": [r["resolve_dimensions"] for r in rows],
                    "observed_fence_correlation_totals": {k: sum(r["observed_fence_correlations"][k] for r in rows)
                        for k in ("submitted_ids", "observed_signal_records", "observed_signal_ids", "in_capture_submissions_with_observed_signal", "signals_for_ids_outside_capture", "submitted_ids_without_observed_signal")}}
            if "baseline" not in scenario or "candidate" not in scenario:
                continue
            a, b = scenario["baseline"], scenario["candidate"]
            scenario["comparable"] = True
            scenario["primary_changes_percent"] = {k: change(a["primary"][k]["median"], b["primary"][k]["median"]) for k in metrics}
            scenario["occupancy_percentage_points"] = b["primary"]["cpu_occupancy_percent"]["median"] - a["primary"]["cpu_occupancy_percent"]["median"]
            scenario["secondary_changes_percent"] = {k: change(a["secondary"][k], b["secondary"][k]) for k in a["secondary"] if k != "pooled_within_process_distributions"}
            scenario["secondary_occupancy_percentage_points"] = b["secondary"]["weighted_cpu_occupancy_percent"] - a["secondary"]["weighted_cpu_occupancy_percent"]
            scenario["paired_by_repetition_changes_percent"] = {
                k: [change(av, bv) for av, bv in zip(a["primary"][k]["values"], b["primary"][k]["values"])] for k in metrics}
            scenario["within_variant_repetition_2_vs_1_percent"] = {
                v: {k: change(scenario[v]["primary"][k]["values"][0], scenario[v]["primary"][k]["values"][1]) for k in metrics}
                for v in ("baseline", "candidate")} if expected == 2 else None
            directions = {"cpu_task_ms": 1, "cpu_occupancy_percent": 1, "map_submissions_per_second": -1,
                          "raf_per_second": -1, "map_submit_p95_ms": 1, "signal_p95_ms": 1, "raf_p95_ms": 1}
            scenario["observed_adverse_directions"] = [k for k, direction in directions.items()
                if scenario["primary_changes_percent"][k] is not None and scenario["primary_changes_percent"][k] * direction > 0]
        return result

    def replay(self, e):
        report_path = e["output"] + "/report.json"
        report = self.evidence.read(report_path)
        self.header(report, e, replay=True)
        verified = self.node("replay", self.evidence.path(report_path))
        c = report["cases"][0]
        data = self.evidence.read(e["output"] + "/" + c["name"] + "-raw.json")
        latencies = {k: [step[k + "Ms"] for step in c["steps"]] for k in
                     ("inputToFirstSubmission", "inputToFirstFenceSignal", "inputToSettledFenceSignal")}
        row = {"scenario": c["name"], "variant": e["variant"], "repetition": e["repetition"], "order": e["order"],
               "report": report_path, "raw_valid": True, "steps": len(c["steps"]), "submissions": c["summary"]["submissions"],
               "task_cpu_ms": c["summary"]["taskCpuMs"], "task_cpu_per_step_ms": c["summary"]["taskCpuPerStepMs"],
               "metric_window_ms": c["summary"]["capture"]["metricWindowMs"],
               "capture_ms": c["summary"]["capture"]["durationMs"],
               "latencies_ms": latencies, "latency_distributions": {k: c["summary"][k] for k in latencies},
               "quiet_barriers_ms": [s["quietForMs"] for s in data["rawSteps"]],
               "per_step_submissions": [len(step["projection"]) for step in c["steps"]],
               "identity": verified["cases"][0], "before_hud": c["beforeHud"], "after_hud": c["afterHud"]}
        self.replay_records.append(row)
        return True

    def compare_replays(self, entries):
        result = {}
        for name in REPLAYS:
            rows = sorted((r for r in self.replay_records if r["scenario"] == name), key=lambda r: r["order"])
            case = result[name] = {"comparable": False, "comparative_timing_withheld": True}
            if len(rows) != 4 or rows[0]["variant"] != "baseline" or rows[0]["repetition"] != 1:
                case["reason"] = "Missing or invalid capture among all four required processes"
                continue
            anchor = self.evidence.path(rows[0]["report"])
            all_valid = True
            for row in rows[1:]:
                label = "replay-identity:" + row["report"]
                comparison = self.checked(label, lambda row=row: self.node("compare", anchor, self.evidence.path(row["report"])))
                self.replay_comparisons.append({"report": row["report"], "anchor": rows[0]["report"], "audit": comparison})
                e = next(e for e in entries if e["output"] + "/report.json" == row["report"])
                if e.get("comparison_exit_code") != 0:
                    self.errors.append({"check": label, "error": "Workflow comparison did not succeed"})
                    all_valid = False
                if comparison is None:
                    all_valid = False
            if not all_valid:
                case["reason"] = "At least one exact ordered workload/runner identity check failed"
                continue
            case["comparable"], case["comparative_timing_withheld"] = True, False
            for variant in ("baseline", "candidate"):
                selected = sorted((r for r in rows if r["variant"] == variant), key=lambda r: r["repetition"])
                primary = {"task_cpu_ms": process_summary([r["task_cpu_ms"] for r in selected]),
                           "task_cpu_per_step_ms": process_summary([r["task_cpu_per_step_ms"] for r in selected])}
                for key in selected[0]["latency_distributions"]:
                    for statistic in ("medianMs", "p95Ms", "maxMs"):
                        primary[key + "_" + statistic] = process_summary([r["latency_distributions"][key][statistic] for r in selected])
                case[variant] = {"primary": primary, "submissions": [r["submissions"] for r in selected],
                    "secondary_pooled_step_distributions": {k: distribution([v for r in selected for v in r["latencies_ms"][k]]) for k in selected[0]["latencies_ms"]}}
            a, b = case["baseline"]["primary"], case["candidate"]["primary"]
            case["primary_changes_percent"] = {k: change(a[k]["median"], b[k]["median"]) for k in a}
            case["paired_by_repetition_changes_percent"] = {k: [change(av, bv) for av, bv in zip(a[k]["values"], b[k]["values"])] for k in a}
            case["observed_adverse_directions"] = [k for k, value in case["primary_changes_percent"].items() if value is not None and value > 0]
        return result

    def run(self):
        valid_metadata = self.checked("artifact-and-environment-provenance", self.metadata)
        # A provenance failure gates every aggregate. Individual files may still
        # be retained elsewhere, but this output never labels them comparable.
        play_cases, replay_cases = {}, {}
        if valid_metadata:
            entries = self.checked("play-execution-order", self.execution)
            if entries:
                for e in entries:
                    self.checked("play:" + e["output"], lambda e=e: self.play(e))
                self.checked("preselected-profile-inventory", self.profile_inventory)
                play_cases = self.aggregate_play()
            if self.args.replay == "required":
                replay_entries = self.checked("replay-execution-order", lambda: self.execution(True))
                if replay_entries:
                    for e in replay_entries:
                        self.checked("replay-raw:" + e["output"], lambda e=e: self.replay(e))
                    replay_cases = self.compare_replays(replay_entries)
            else:
                require("camera_replay_execution" not in self.provenance, "Replay-absent mode cannot discard supplied replay evidence")
        return {"schema": 1, "scope": SCOPE, "valid": not self.errors,
            "expected": {"baseline_source": self.args.baseline_sha, "baseline_run": self.args.baseline_run,
                         "candidate_source": self.args.candidate_sha, "candidate_run": self.args.candidate_run,
                         "replay": self.args.replay, "harness_hashes": self.expected_hashes},
            "audit": {"errors": self.errors, "warnings": self.warnings, "checks": self.checks,
                      "limits": "Artifact metadata and preserved baseline resource identity are checked. Pages binaries are not present here: the workflow's package-byte verification is not independently repeated. These files do not authenticate GitHub API responses or physical display presentation."},
            "source_provenance": self.source, "provenance": self.provenance, "environment": self.environment,
            "held_key": {"records": self.play_records, "scenarios": play_cases},
            "profiles": {"selection": sorted(PROFILE_CASES), "repetition": 1, "expected_count": 14,
                         "scope": "Separate post-timing sampling passes. Self/inclusive top tables retain the harness's top 45 nodes. Raw sample counts and exact delta weights are audited; no cross-package WASM-index matching or extra-case selection is inferred.", "records": self.profiles},
            "camera_replay": {"records": self.replay_records, "identity_comparisons": self.replay_comparisons, "scenarios": replay_cases},
            "audited_input_files": self.evidence.files}


def write_csv(path, rows):
    if not rows:
        path.write_text("")
        return
    with path.open("w", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=list(rows[0]), lineterminator="\n")
        writer.writeheader()
        for row in rows:
            writer.writerow({k: json.dumps(v, sort_keys=True) if isinstance(v, (list, dict)) else v for k, v in row.items()})


def markdown(summary):
    def f(value, digits=2):
        return "—" if value is None else f"{value:.{digits}f}"
    def cell(process):
        return "/".join(f(v) for v in process["values"])
    lines = ["# Paired browser analysis", "", f"Audit: **{'PASS' if summary['valid'] else 'INCOMPLETE / FAILED'}**.", "",
             f"Baseline `{summary['expected']['baseline_source']}` (run {summary['expected']['baseline_run']}); "
             f"candidate `{summary['expected']['candidate_source']}` (run {summary['expected']['candidate_run']}).", "", SCOPE, "",
             "## Held-key and stationary processes", "", "Each cell retains repetition 1/2. Deltas use the median across process values; all signs are retained.", "",
             "| Scenario | Task CPU ms, baseline → candidate | Δ CPU | Submitted maps/s, baseline → candidate | Δ maps/s | rAF/s, baseline → candidate | Δ rAF/s |",
             "|---|---:|---:|---:|---:|---:|---:|"]
    for name, case in summary["held_key"]["scenarios"].items():
        if not case["comparable"]:
            lines.append(f"| {name} | comparison withheld | | | | | |")
            continue
        a, b, d = case["baseline"]["primary"], case["candidate"]["primary"], case["primary_changes_percent"]
        cells = []
        for key in ("cpu_task_ms", "map_submissions_per_second", "raf_per_second"):
            cells += [f"{cell(a[key])} → {cell(b[key])}", f(d[key]) + "%"]
        lines.append("| " + " | ".join([name] + cells) + " |")
    lines += ["", "CPU occupancy, interval/fence p95, per-process ranges, paired repetition deltas, within-variant drift, HUD distance, "
              "resolution counts, and pipeline safeguards are retained in analysis.json and samples.csv.", "",
              "### Secondary aggregates and formulas", "",
              "- Process median: statistics.median(process values); with two processes this is their arithmetic mean.",
              "- Weighted CPU occupancy: 100 × sum(TaskDuration ms) / sum(CDP metric interval ms).",
              "- Weighted map/rAF rate: 1000 × sum(count) / sum(probe capture ms).",
              "- Pooled interval distributions combine only intervals already formed inside each process; no cross-process interval is invented.",
              "- Within-process distributions match the harness: sorted[n//2] median and sorted[min(n−1, floor(0.95n))] p95.",
              "- Percent change: 100 × (candidate/baseline − 1). Occupancy percentage points are reported separately.", "",
              "## Fixed-camera replay", "", "Comparative latency appears only after all four raw captures and ordered identities pass.", "",
              "| Scenario | Comparable | Task CPU/step ms, baseline → candidate | First submission median ms | Observed settled-fence signal median ms |",
              "|---|---|---:|---:|---:|"]
    for name, case in summary["camera_replay"]["scenarios"].items():
        if not case["comparable"]:
            lines.append(f"| {name} | No: {case.get('reason', 'audit failed')} | withheld | withheld | withheld |")
            continue
        a, b = case["baseline"]["primary"], case["candidate"]["primary"]
        cells = [f"{cell(a[key])} → {cell(b[key])}" for key in ("task_cpu_per_step_ms", "inputToFirstSubmission_medianMs", "inputToSettledFenceSignal_medianMs")]
        lines.append("| " + " | ".join([name, "Yes"] + cells) + " |")
    lines += ["", f"Audited profiles: {len(summary['profiles']['records'])}/14, from separate first-repetition passes. "
              "Symbolication requires each exact package's symbols; numeric WASM function indices are not stable cross-package method identities.", "",
              "## Audit limits", "", summary["audit"]["limits"], ""]
    if summary["audit"]["errors"]:
        lines += ["## Audit failures", ""] + ["- " + error["check"] + ": " + error["error"].replace("\n", " ") for error in summary["audit"]["errors"]]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Extracted atlas-play-performance directory, with provenance.json")
    parser.add_argument("output", type=Path, help="Separate directory for derived analysis only")
    parser.add_argument("--baseline-sha", default=BASELINE)
    parser.add_argument("--candidate-sha", default=CANDIDATE)
    parser.add_argument("--baseline-run", type=int, default=42696985047)
    parser.add_argument("--candidate-run", type=int, default=42907891884)
    parser.add_argument("--known-baseline-artifact", type=Path, default=HERE / "known-baseline-artifact.json")
    parser.add_argument("--harness-hash", action="append", default=[], metavar="NAME=SHA256", help="Explicit recorded override, for legacy self-validation only")
    parser.add_argument("--replay", choices=("required", "absent"), default="required")
    parser.add_argument("--node", default="node")
    args = parser.parse_args()
    require(args.input.is_dir(), "Input must be an extracted artifact directory")
    require(not args.output.resolve().is_relative_to(args.input.resolve()), "Derived output must be outside input evidence")
    args.output.mkdir(parents=True, exist_ok=True)
    auditor = Auditor(args)
    summary = auditor.run()
    summary["analyzer"] = {"script_sha256": sha256(Path(__file__).read_bytes()),
        "node_adapter_sha256": sha256((HERE / "verify-raw.cjs").read_bytes()),
        "python": sys.version, "node": subprocess.check_output([args.node, "--version"], text=True).strip(),
        "known_baseline_metadata_sha256": sha256(args.known_baseline_artifact.read_bytes())}
    dump(args.output / "analysis.json", summary)
    write_csv(args.output / "samples.csv", auditor.play_records)
    write_csv(args.output / "replay-samples.csv", auditor.replay_records)
    write_csv(args.output / "profiles.csv", [{k: v for k, v in row.items() if k != "reported_summary"} for row in auditor.profiles])
    (args.output / "README.md").write_text(markdown(summary))
    print(json.dumps({"valid": summary["valid"], "held_key_processes": len(auditor.play_records),
        "profiles": len(auditor.profiles), "replay_processes": len(auditor.replay_records),
        "replay_comparable": {k: v["comparable"] for k, v in summary["camera_replay"]["scenarios"].items()},
        "errors": auditor.errors, "output": str(args.output.resolve())}, indent=2))
    return 0 if summary["valid"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
