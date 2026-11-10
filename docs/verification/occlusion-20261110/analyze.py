#!/usr/bin/env python3
"""Audit original reports, then derive summary JSON and exact-camera paired samples."""
import argparse
import csv
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import statistics

ORDER = ('baseline', 'candidate', 'candidate', 'baseline')
PHASES = {
    'prepare_navigation': ('Prepare', 'Navigation'),
    'prepare': ('Prepare',),
    'navigation': ('Navigation',),
    'depth_construction': ('DepthConstruction',),
    'sparse_queries': ('SparseQueries',),
    'construction_queries': ('DepthConstruction', 'SparseQueries'),
    'repeated_queries': ('RepeatedQueries',),
}
COUNTERS = ('FramePreparedTriangles', 'PreparedTriangles', 'PreparedEdges', 'PreparedGroups',
            'PreparedCells', 'FrameIndexCapacityBytes', 'IndependentIndexCapacityBytes')

def read_report(folder):
    path = folder / 'depth-preparation.json'
    data = path.read_bytes() if path.exists() else gzip.decompress(path.with_suffix('.json.gz').read_bytes())
    return json.loads(data)

def median(values):
    return statistics.median(values)

def p95(values):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * .95) - 1)]

def change(before, after):
    return None if before == 0 else (after / before - 1) * 100

def stats(values):
    return {'median': median(values), 'p95': p95(values), 'min': min(values), 'max': max(values),
            'mean': statistics.mean(values)}

def audit(source, output):
    blocks = [read_report(source / f'{i}-{variant}') for i, variant in enumerate(ORDER)]
    for key in ('Runtime', 'Platform', 'Processors', 'LabelFont', 'HarnessSha256', 'Width', 'Height', 'RequestedPixelSize', 'Warmup', 'Samples', 'Scope'):
        assert len({json.dumps(b[key], sort_keys=True) for b in blocks}) == 1, ('Protocol differs', key)
    assert blocks[0]['RendererSha256'] == blocks[3]['RendererSha256']
    assert blocks[1]['RendererSha256'] == blocks[2]['RendererSha256']
    assert blocks[0]['RendererSha256'] != blocks[1]['RendererSha256']
    assert all(len(b['Results']) == len(blocks[0]['Results']) for b in blocks)
    rows, workloads = [], []
    comparisons = 0
    for index, first in enumerate(blocks[0]['Results']):
        groups = [b['Results'][index] for b in blocks]
        for key in ('Name', 'Features', 'Style', 'Probes', 'CoarseCellSize', 'QueryCells', 'Warmup', 'MeasuredSamples'):
            assert len({g[key] for g in groups}) == 1, (first['Name'], key)
        size = len(first['Samples'])
        assert all(len(g['Samples']) == size == g['MeasuredSamples'] for g in groups)
        for group in groups[1:]:
            for before, after in zip(first['Samples'], group['Samples']):
                for key in ('Frame', 'QueryHash', 'OverlayHash', 'MinimumAltitude', 'MaximumAltitude', 'TerrainPatches', 'BuildingTriangles'):
                    assert before[key] == after[key], (first['Name'], before['Frame'], key)
                    comparisons += 1
        summary = {key: first[key] for key in ('Name', 'Features', 'Style', 'Probes', 'CoarseCellSize', 'QueryCells', 'Warmup', 'MeasuredSamples')}
        summary['metrics'] = {}
        for name, parts in PHASES.items():
            by_field = {}
            for field in ('WallMs', 'ThreadCpuMs', 'Bytes'):
                values = [[sum(s[p][field] for p in parts) for s in g['Samples']] for g in groups]
                baseline, candidate = values[0] + values[3], values[1] + values[2]
                paired = [(a, b) for bi, ci in ((0, 1), (3, 2)) for a, b in zip(values[bi], values[ci])]
                by_field[field] = {
                    'baseline': stats(baseline), 'candidate': stats(candidate),
                    'median_change_percent': change(median(baseline), median(candidate)),
                    'block_medians': [median(v) for v in values],
                    'block_p95': [p95(v) for v in values],
                    'matched_sample_delta_median': median([b - a for a, b in paired]),
                    'matched_sample_percent_median': median([change(a, b) for a, b in paired if a != 0]) if any(a != 0 for a, b in paired) else None,
                }
                phase_changes = []
                for phase in sorted({(s['Frame'] + first['Warmup']) % 16 for s in first['Samples']}):
                    before = [sum(s[p][field] for p in parts) for block in (0, 3) for s in groups[block]['Samples'] if (s['Frame'] + first['Warmup']) % 16 == phase]
                    after = [sum(s[p][field] for p in parts) for block in (1, 2) for s in groups[block]['Samples'] if (s['Frame'] + first['Warmup']) % 16 == phase]
                    phase_changes.append({'camera_phase': phase, 'baseline_median': median(before), 'candidate_median': median(after), 'change_percent': change(median(before), median(after))})
                phase_percent = [v['change_percent'] for v in phase_changes if v['change_percent'] is not None]
                by_field[field]['camera_phase_change_percent_median'] = median(phase_percent) if phase_percent else None
                by_field[field]['camera_phase_change_percent_range'] = [min(phase_percent), max(phase_percent)] if phase_percent else None
                by_field[field]['improved_camera_phases'] = [v['camera_phase'] for v in phase_changes if v['candidate_median'] < v['baseline_median']]
            summary['metrics'][name] = by_field
            for pair, (bi, ci) in enumerate(((0, 1), (3, 2))):
                for before, after in zip(groups[bi]['Samples'], groups[ci]['Samples']):
                    row = [first['Name'], pair, before['Frame'], (before['Frame'] + first['Warmup']) % 16, name]
                    for field in ('WallMs', 'ThreadCpuMs', 'Bytes'):
                        a, b = sum(before[p][field] for p in parts), sum(after[p][field] for p in parts)
                        row += [a, b, b - a]
                    rows.append(row)
        summary['counters'] = {}
        for counter in COUNTERS:
            values = [[s[counter] for s in g['Samples']] for g in groups]
            if any(v is None for part in values for v in part):
                summary['counters'][counter] = {'unavailable': True}
                continue
            baseline, candidate = values[0] + values[3], values[1] + values[2]
            summary['counters'][counter] = {
                'baseline': stats(baseline), 'candidate': stats(candidate),
                'total_change_percent': change(sum(baseline), sum(candidate)),
                'block_medians': [median(v) for v in values],
            }
        for flag in ('FrameCoarseIndex', 'FrameExactIndex', 'IndependentCoarseIndex', 'IndependentExactIndex'):
            summary[flag] = sorted({s[flag] for g in groups for s in g['Samples']}, key=str)
        workloads.append(summary)
    summary = {
        'scope': blocks[0]['Scope'],
        'protocol': {k: blocks[0][k] for k in ('Runtime', 'Platform', 'Processors', 'LabelFont', 'Width', 'Height', 'RequestedPixelSize', 'Warmup', 'Samples', 'HarnessSha256')},
        'renderers': {'baseline': blocks[0]['RendererSha256'], 'candidate': blocks[1]['RendererSha256']},
        'order': ORDER,
        'statistics': 'Median averages the middle two samples; p95 is nearest rank. Matched samples pair equal frame/camera indices in adjacent AB and BA blocks. Camera-phase medians pool each phase across both corresponding variant processes. Timing samples are not excluded.',
        'measured_frames': sum(len(g['Samples']) for b in blocks for g in b['Results']),
        'exact_comparisons': comparisons,
        'workloads': workloads,
    }
    output.mkdir(parents=True, exist_ok=True)
    (output/'summary.json').write_text(json.dumps(summary, indent=2)+'\n')
    stream = io.StringIO(newline='')
    writer = csv.writer(stream, lineterminator='\n')
    writer.writerow(['workload','pair','frame','camera_phase','metric','baseline_wall_ms','candidate_wall_ms','wall_delta_ms','baseline_thread_cpu_ms','candidate_thread_cpu_ms','thread_cpu_delta_ms','baseline_allocated_bytes','candidate_allocated_bytes','allocated_delta_bytes'])
    writer.writerows(rows)
    with (output/'phase-pairs.csv.gz').open('wb') as raw:
        with gzip.GzipFile(filename='', fileobj=raw, mode='wb', mtime=0) as zipped:
            zipped.write(stream.getvalue().encode())
    for w in workloads:
        m=w['metrics']['prepare_navigation']['WallMs']
        print(w['Name'], f"{m['baseline']['median']:.4f} -> {m['candidate']['median']:.4f} ms ({m['median_change_percent']:+.2f}%)", 'blocks', m['block_medians'])
    return summary

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args=parser.parse_args()
    audit(args.source,args.output)
