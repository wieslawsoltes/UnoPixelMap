#!/usr/bin/env python3
"""Compatibility and concrete corruption checks; never a performance benchmark."""
import argparse
import copy
import gzip
import importlib.util
import json
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("paired_analyzer", HERE / "analyze.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def read(path):
    data = path.read_bytes()
    return json.loads(gzip.decompress(data) if path.suffix == ".gz" else data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("repo", type=Path)
    args = parser.parse_args()
    validation = HERE / "validation"
    validation.mkdir(exist_ok=True)
    old = args.repo / "docs/verification/browser-rendering-555990d"
    preflight = args.repo / "docs/verification/camera-replay-preflight-20261110"
    checks = []

    def node(action, *paths, succeeds=True):
        result = subprocess.run(["node", str(HERE / "verify-raw.cjs"), action, *map(str, paths)], capture_output=True, text=True, timeout=60)
        output = json.loads(result.stdout)
        assert (result.returncode == 0 and output.get("valid")) == succeeds, output
        return output

    baseline = read(old / "provenance.json")["baseline"]
    module.dump(validation / "old-baseline-artifact.json", baseline)
    command = ["python3", str(HERE / "analyze.py"), str(old), str(validation / "legacy-paired"),
        "--baseline-sha", baseline["source_sha"], "--baseline-run", str(baseline["run_id"]),
        "--candidate-sha", module.BASELINE, "--candidate-run", "42696985047",
        "--known-baseline-artifact", str(validation / "old-baseline-artifact.json"),
        "--harness-hash", "explore.cjs=83d83491198362e499b67a07d1ddcc5ed2f3daaf451dd9493017c48153bae49b", "--replay", "absent"]
    subprocess.run(command, check=True, capture_output=True, text=True)
    subprocess.run(["python3", str(old / "analyze.py"), str(old), str(validation / "prior-analyzer-reference")],
                   check=True, capture_output=True, text=True)
    previous = read(validation / "prior-analyzer-reference/analysis.json")
    current = read(validation / "legacy-paired/analysis.json")
    assert current["valid"] and len(current["held_key"]["records"]) == 30 and len(current["profiles"]["records"]) == 14
    comparisons = 0
    for before, after in zip(previous["records"], current["held_key"]["records"]):
        for key in set(before) & set(after):
            assert before[key] == after[key], (key, before[key], after[key])
            comparisons += 1
    for name, old_case in previous["scenarios"].items():
        for variant in ("baseline", "candidate"):
            old_values, new_values = old_case[variant], current["held_key"]["scenarios"][name][variant]["secondary"]
            for key in ("cpu_task_mean_ms", "weighted_cpu_occupancy_percent", "weighted_map_submissions_per_second", "weighted_raf_per_second"):
                assert old_values[key] == new_values[key], (name, key)
                comparisons += 1
            for old_key, new_key in (("pooled_raf_distribution", "raf"), ("pooled_map_submission_distribution", "submissions"), ("pooled_signal_distribution", "signals")):
                mapping = {"samples": "samples", "median_ms": "medianMs", "p95_ms": "p95Ms", "max_ms": "maxMs", "mean_ms": "meanMs"}
                assert {mapping[k]: v for k, v in old_values[old_key].items()} == new_values["pooled_within_process_distributions"][new_key]
                comparisons += 1
    checks.append({"check": "legacy-30-process-14-profile-compatibility", "passed": True, "exact_field_comparisons": comparisons})
    flight = next(r for r in current["held_key"]["records"] if (r["scenario"], r["variant"], r["repetition"]) == ("freeflight-moving", "baseline", 2))
    counts = flight["observed_fence_correlations"]
    assert [counts[k] for k in ("submitted_ids", "observed_signal_records", "in_capture_submissions_with_observed_signal", "signals_for_ids_outside_capture", "submitted_ids_without_observed_signal")] == [37, 36, 35, 1, 2]
    checks.append({"check": "retain-observed-fence-censoring", "passed": True, "result": counts})
    identity_auditor = module.Auditor(argparse.Namespace(input=old, node="node", harness_hash=[]))
    identity_auditor.environment = read(old / "environment.json")
    execution = read(old / "provenance.json")["execution"][0]
    identity_report = read(old / execution["output"] / "report.json.gz")
    for field, bad_value in (("browser", ""), ("cpu", "AMD")):
        altered = copy.deepcopy(identity_report)
        altered[field] = bad_value
        try:
            identity_auditor.header(altered, execution)
        except ValueError as error:
            checks.append({"check": "reject-partial-" + field + "-identity", "passed": True, "error": str(error)})
        else:
            raise AssertionError("Accepted partial identity: " + field)
    first, second = (preflight / f"repetition-{i}/report.json.gz" for i in (1, 2))
    for file in (first, second):
        result = node("replay", file)
        checks.append({"check": "preflight-raw-" + file.parent.name, "passed": True, "result": result})
    checks.append({"check": "preflight-exact-cross-process-identity", "passed": True, "result": node("compare", first, second)})

    with tempfile.TemporaryDirectory(prefix="audit-corruption-", dir=validation) as temp:
        root = Path(temp)
        play_dir = old / "candidate/walk-moving-firstperson/repetition-1"
        play_report = read(play_dir / "report.json.gz")
        play_raw = read(play_dir / "walk-moving-firstperson-raw.json.gz")
        profile = read(play_dir / "walk-moving-firstperson.cpuprofile.gz")
        report_path, raw_path, profile_path = (root / name for name in ("play-report.json", "play-raw.json", "play.cpuprofile"))
        module.dump(raw_path, play_raw)
        module.dump(profile_path, profile)
        altered = copy.deepcopy(play_report)
        altered["cases"][0]["cpu"]["TaskDurationMs"] += 1
        module.dump(report_path, altered)
        checks.append({"check": "reject-summary-tampering", "passed": True, "error": node("play", report_path, raw_path, profile_path, succeeds=False)["error"]})
        module.dump(report_path, play_report)
        altered_profile = copy.deepcopy(profile)
        altered_profile["nodes"][0].setdefault("children", []).append(altered_profile["nodes"][0]["id"])
        module.dump(profile_path, altered_profile)
        checks.append({"check": "reject-profile-parent-cycle", "passed": True, "error": node("play", report_path, raw_path, profile_path, succeeds=False)["error"]})

        replay_report = read(first)
        replay_report["cases"] = replay_report["cases"][:1]
        replay_raw = read(preflight / "repetition-1/map-pan-relief-raw.json.gz")
        replay_path, replay_raw_path = root / "report.json", root / "map-pan-relief-raw.json"
        module.dump(replay_path, replay_report)
        corruptions = {
            "reject-quiet-barrier-shortening": lambda d: d["rawSteps"][0].__setitem__("quietForMs", 249),
            "reject-dropped-ordered-submission": lambda d: d["rawSteps"][0]["resolves"].pop(),
            "reject-extra-unscoped-submission": lambda d: d["raw"]["resolves"].append(copy.deepcopy(d["raw"]["resolves"][-1])),
        }
        for name, mutate in corruptions.items():
            altered_raw = copy.deepcopy(replay_raw)
            mutate(altered_raw)
            module.dump(replay_raw_path, altered_raw)
            checks.append({"check": name, "passed": True, "error": node("replay", replay_path, succeeds=False)["error"]})
        module.dump(replay_raw_path, replay_raw)
        changed_report = copy.deepcopy(replay_report)
        changed_report["cases"][0]["steps"][0]["projection"][0]["draws"][0]["uniforms"]["uGround"][0] += 1
        changed_path = root / "changed-report.json"
        module.dump(changed_path, changed_report)
        checks.append({"check": "reject-changed-projection", "passed": True, "error": node("compare", replay_path, changed_path, succeeds=False)["error"]})

        config = argparse.Namespace(input=root, node="node", harness_hash=[])
        auditor = module.Auditor(config)
        entries = []
        for order, (variant, repetition) in enumerate((("baseline", 1), ("candidate", 1), ("candidate", 2), ("baseline", 2)), 1):
            output = f"{variant}/repetition-{repetition}"
            destination = root / output / "report.json"
            destination.parent.mkdir(parents=True)
            module.dump(destination, changed_report if order == 3 else replay_report)
            auditor.replay_records.append({"scenario": "map-pan-relief", "variant": variant, "repetition": repetition, "order": order, "report": output + "/report.json"})
            entries.append({"output": output, "comparison_exit_code": 0})
        gated = auditor.compare_replays(entries)["map-pan-relief"]
        assert not gated["comparable"] and gated["comparative_timing_withheld"] and "primary_changes_percent" not in gated
        checks.append({"check": "withhold-whole-scenario-comparison-after-one-identity-failure", "passed": True, "result": gated})

    result = {"all_passed": True, "checks": checks, "legacy_command": command,
              "scope": "Analyzer compatibility and corruption checks only; no new performance data or candidate browser measurements."}
    module.dump(validation / "selfcheck.json", result)
    print(json.dumps({"all_passed": True, "checks": len(checks), "exact_legacy_fields": comparisons}, indent=2))


if __name__ == "__main__":
    main()
