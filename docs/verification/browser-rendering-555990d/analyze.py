#!/usr/bin/env python3
"""Audit the workflow's own paired browser evidence without combining CI runners."""
import csv
import gzip
import hashlib
import json
import math
import statistics
import sys
from collections import Counter
from pathlib import Path

root = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=True)
def stored_path(path):
    candidate = path if path.is_file() else Path(str(path) + '.gz')
    if not candidate.resolve().is_relative_to(root):
        raise ValueError('Evidence paths must remain inside the input directory')
    return candidate

def evidence_bytes(path):
    stored = stored_path(path)
    data = stored.read_bytes()
    return gzip.decompress(data) if stored.suffix == '.gz' else data

provenance = json.loads(evidence_bytes(root / 'provenance.json'))
environment = json.loads(evidence_bytes(root / 'environment.json'))
expected_scenarios = (
    'map-idle-default', 'map-idle-relief', 'walk-stationary-firstperson',
    'walk-moving-firstperson', 'walk-moving-chase', 'car-stationary-chase',
    'car-moving-chase', 'car-moving-firstperson', 'freeflight-stationary',
    'freeflight-moving')
expected_execution = []
for name in expected_scenarios:
    order = [(1, 'baseline'), (1, 'candidate')]
    if 'moving' in name:
        order += [(2, 'candidate'), (2, 'baseline')]
    expected_execution.extend((name, variant, repetition) for repetition, variant in order)
assert [(e['scenario'], e['variant'], e['repetition']) for e in provenance['execution']] == expected_execution
assert [e['order'] for e in provenance['execution']] == list(range(1, 31))

def digest(path):
    return hashlib.sha256(evidence_bytes(path)).hexdigest()

def distribution(values):
    values = sorted(values)
    if not values:
        return {'samples': 0}
    return {'samples': len(values), 'median_ms': values[len(values) // 2],
            'p95_ms': values[min(len(values) - 1, math.floor(len(values) * .95))],
            'max_ms': values[-1], 'mean_ms': statistics.mean(values)}

def close(a, b):
    return math.isclose(a, b, rel_tol=1e-9, abs_tol=1e-6)

records = []
errors = []
raw_cases = {}
for execution in provenance['execution']:
    report_path = root / execution['output'] / 'report.json'
    if not stored_path(report_path).is_file():
        errors.append(f'Missing {report_path.relative_to(root)}')
        continue
    report = json.loads(evidence_bytes(report_path))
    if execution.get('exit_code') != 0 or report.get('error'):
        errors.append({'execution': execution, 'error': report.get('error')})
    if not report['cases']:
        errors.append(f'No measured case in {report_path.relative_to(root)}')
        continue
    assert len(report['cases']) == 1
    c = report['cases'][0]
    assert report['revision'] == execution['source_sha'] == provenance[execution['variant']]['source_sha']
    assert c['name'] == execution['scenario']
    assert report['harnessSha256'] == environment['play-performance.cjs_sha256']
    assert report['explorationHarnessSha256'] == environment['explore.cjs_sha256']
    assert report['productionPerfHarnessSha256'] == environment['performance.cjs_sha256']
    raw_path = report_path.with_name(c['name'] + '-raw.json')
    data = json.loads(evidence_bytes(raw_path))
    raw, before, after = data['raw'], data['before'], data['after']
    task_ms = (after['TaskDuration'] - before['TaskDuration']) * 1000
    metric_ms = (after['Timestamp'] - before['Timestamp']) * 1000
    assert close(task_ms, c['cpu']['TaskDurationMs'])
    assert close(metric_ms, c['metricWindowMs'])
    assert close(task_ms / metric_ms * 100, c['cpuTaskPercent'])
    assert close(len(raw['resolves']) / raw['durationMs'] * 1000, c['mapResolves']['submissionsPerSecond'])
    assert close(len(raw['frames']) / raw['durationMs'] * 1000, c['rafPerSecond'])
    raf_intervals = [b[0] - a[0] for a, b in zip(raw['frames'], raw['frames'][1:])]
    resolve_intervals = [b['time'] - a['time'] for a, b in zip(raw['resolves'], raw['resolves'][1:])]
    signal_latencies = [s['resolveSignalLatencyMs'] for s in raw['syncCalls'] if s.get('resolveSignalLatencyMs') is not None]
    if raf_intervals:
        assert close(distribution(raf_intervals)['p95_ms'], c['raf']['p95Ms'])
    if resolve_intervals:
        assert close(distribution(resolve_intervals)['p95_ms'], c['mapResolves']['intervals']['p95Ms'])
    key = (execution['scenario'], execution['variant'], execution['repetition'])
    assert key not in raw_cases
    raw_cases[key] = {'raf': raf_intervals, 'resolves': resolve_intervals, 'signals': signal_latencies}
    direct = c['directGpuFrames'] or {}
    packs = c['pixelPackReadback']
    records.append({
        'scenario': c['name'], 'variant': execution['variant'], 'repetition': execution['repetition'], 'order': execution['order'],
        'source_sha': report['revision'], 'report': str(report_path.relative_to(root)), 'report_sha256': digest(report_path),
        'raw_sha256': digest(raw_path), 'capture_ms': c['durationMs'], 'metric_window_ms': metric_ms,
        'cpu_task_ms': task_ms, 'cpu_occupancy_percent': c['cpuTaskPercent'],
        'map_submissions': c['mapResolves']['submissions'], 'map_submissions_per_second': c['mapResolves']['submissionsPerSecond'],
        'map_submit_median_ms': c['mapResolves']['intervals'].get('medianMs'),
        'map_submit_p95_ms': c['mapResolves']['intervals'].get('p95Ms'),
        'signal_median_ms': c['mapResolves']['submitToSignal'].get('medianMs'),
        'signal_p95_ms': c['mapResolves']['submitToSignal'].get('p95Ms'),
        'raf_count': len(raw['frames']), 'raf_per_second': c['rafPerSecond'], 'raf_p95_ms': c['raf'].get('p95Ms'),
        'compositor_submissions': c['mapResolves']['compositorSubmissions'],
        'detached_submissions': c['mapResolves']['detachedSubmissions'], 'pixel_sizes': c['mapResolves']['byPixelSize'],
        'direct_pending_start': direct.get('atStart'), 'direct_pending_end': direct.get('atEnd'),
        'direct_maximum_pending': direct.get('maximumPending'), 'direct_released_before_signal': direct.get('releasedBeforeSignal'),
        'read_pixels_calls': c['readPixels']['samples'], 'read_pixels_ms': c['readPixels']['totalMs'],
        'pixel_pack_submissions': packs['submissions'], 'pixel_pack_maximum_pending': packs['maximumPending'],
        'pixel_pack_overwritten': packs['overwritten'], 'pixel_pack_abandoned': packs['abandoned'],
        'pixel_pack_released_without_copy': packs['releasedWithoutCopy'], 'positive_timeout_polls': packs['positiveTimeoutPolls'],
        'pixel_pack_copies': packs['getBufferSubData']['samples'], 'long_tasks': c['longTasks'], 'long_task_ms': c['longTaskDurationMs'],
        'before_hud': c['beforeHud'], 'after_hud': c['afterHud'],
        'distance_delta': None if c['beforeHud'] is None else c['afterHud']['distance'] - c['beforeHud']['distance'],
    })

scenarios = {}
for scenario in dict.fromkeys(r['scenario'] for r in records):
    values = scenarios[scenario] = {}
    for variant in ('baseline', 'candidate'):
        rows = [r for r in records if r['scenario'] == scenario and r['variant'] == variant]
        if not rows:
            errors.append(f'Missing {variant} {scenario}')
            continue
        expected = 2 if 'moving' in scenario else 1
        assert len(rows) == expected, (scenario, variant, len(rows))
        capture = sum(r['capture_ms'] for r in rows)
        metric = sum(r['metric_window_ms'] for r in rows)
        pixels = Counter()
        for row in rows:
            pixels.update(row['pixel_sizes'])
        subset = [raw_cases[(scenario, variant, r['repetition'])] for r in rows]
        values[variant] = {
            'repeats': len(rows),
            'cpu_task_ms': [r['cpu_task_ms'] for r in rows], 'cpu_task_mean_ms': statistics.mean(r['cpu_task_ms'] for r in rows),
            'cpu_occupancy_percent': [r['cpu_occupancy_percent'] for r in rows],
            'weighted_cpu_occupancy_percent': sum(r['cpu_task_ms'] for r in rows) / metric * 100,
            'map_submissions_per_second': [r['map_submissions_per_second'] for r in rows],
            'weighted_map_submissions_per_second': sum(r['map_submissions'] for r in rows) / capture * 1000,
            'raf_per_second': [r['raf_per_second'] for r in rows],
            'weighted_raf_per_second': sum(r['raf_count'] for r in rows) / capture * 1000,
            'raf_p95_ms': [r['raf_p95_ms'] for r in rows], 'map_submit_p95_ms': [r['map_submit_p95_ms'] for r in rows],
            'signal_p95_ms': [r['signal_p95_ms'] for r in rows],
            'pooled_raf_distribution': distribution([x for raw in subset for x in raw['raf']]),
            'pooled_map_submission_distribution': distribution([x for raw in subset for x in raw['resolves']]),
            'pooled_signal_distribution': distribution([x for raw in subset for x in raw['signals']]),
            'pixel_size_submission_counts': dict(pixels), 'distance_delta': [r['distance_delta'] for r in rows],
            'compositor_submissions': sum(r['compositor_submissions'] for r in rows),
            'read_pixels_calls': sum(r['read_pixels_calls'] for r in rows),
            'max_direct_pending': max(r['direct_maximum_pending'] or 0 for r in rows),
            'released_before_signal': sum(r['direct_released_before_signal'] or 0 for r in rows),
        }
    if 'baseline' in values and 'candidate' in values:
        a, b = values['baseline'], values['candidate']
        values['changes'] = {k + '_percent': (b[k] / a[k] - 1) * 100 if a[k] else None for k in (
            'cpu_task_mean_ms', 'weighted_cpu_occupancy_percent', 'weighted_map_submissions_per_second', 'weighted_raf_per_second')}
        values['changes']['occupancy_percentage_points'] = b['weighted_cpu_occupancy_percent'] - a['weighted_cpu_occupancy_percent']
        values['changes']['per_repeat_cpu_percent'] = [(b / a - 1) * 100 for a, b in zip(a['cpu_task_ms'], b['cpu_task_ms'])]
        values['changes']['per_repeat_submission_percent'] = [(b / a - 1) * 100 if a else None for a, b in
            zip(a['map_submissions_per_second'], b['map_submissions_per_second'])]

summary = {'provenance': provenance, 'environment': environment, 'errors': errors, 'records': records, 'scenarios': scenarios,
           'scope': 'Per-run paired comparison only. CPU occupancy uses the Performance metric interval; map submission rates use the capture interval. rAF is browser scheduling, map submissions are submitted images, and neither measures physical display presentation. These runs use SwiftShader. Moving trajectories can diverge, so retain HUD distances. Two repeats characterize observed spread; they do not establish statistical confidence.'}
(output / 'analysis.json').write_text(json.dumps(summary, indent=2) + '\n')
with (output / 'samples.csv').open('w', newline='') as f:
    writer = csv.DictWriter(f, fieldnames=list(records[0]), lineterminator='\n')
    writer.writeheader()
    for row in records:
        writer.writerow({k: json.dumps(v, sort_keys=True) if isinstance(v, (dict, list)) else v for k, v in row.items()})
print('Errors:', errors)
print('Identity:', provenance['baseline']['source_sha'], '->', provenance['candidate']['source_sha'])
for name, s in scenarios.items():
    a, b, d = s['baseline'], s['candidate'], s['changes']
    def values(items):
        return '/'.join('-' if v is None else f'{v:.2f}' for v in items)
    print(name, 'CPU ms', values(a['cpu_task_ms']), '->', values(b['cpu_task_ms']),
          f"({d['cpu_task_mean_ms_percent']:+.1f}%)", 'occupancy', values(a['cpu_occupancy_percent']), '->', values(b['cpu_occupancy_percent']),
          'map/s', values(a['map_submissions_per_second']), '->', values(b['map_submissions_per_second']),
          'rAF/s', values(a['raf_per_second']), '->', values(b['raf_per_second']),
          'rAF p95', values(a['raf_p95_ms']), '->', values(b['raf_p95_ms']))
print('Saved', output)

if errors:
    raise SystemExit(1)
