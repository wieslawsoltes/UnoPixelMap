#!/usr/bin/env python3
"""Symbolicate and audit all preselected CPU profiles from one paired release run.

Usage: profile_audit.py EVIDENCE BASELINE_SITE CANDIDATE_SITE SYMBOLICATOR OUTPUT
The sites must be the exact unpacked Pages artifacts recorded by EVIDENCE.
Profiles are post-timing diagnostic captures, never throughput measurements.
"""
from __future__ import annotations

import copy
import gzip
import hashlib
import json
from pathlib import Path
import re
import statistics
import subprocess
import sys
from urllib.parse import unquote, urlparse


PROFILE_CASES = {
    'map-idle-relief', 'walk-stationary-firstperson', 'walk-moving-firstperson',
    'walk-moving-chase', 'car-moving-chase', 'car-moving-firstperson',
    'freeflight-moving',
}
WASM_FUNCTION = re.compile(r'\$?wasm-function\[(\d+)\]')
CATEGORIES = {
    'depth_visibility': ('PixelMap.Rendering.GpuSceneDepth.',),
    'label_drawing': ('PixelMap.Rendering.PixelMapRenderer.DrawLabels',
                      'PixelMap.Rendering.PixelMapRenderer.<DrawLabels>'),
    'terrain_texture_baking': ('PixelMap.Rendering.PixelMapRenderer.BakeTerrainTexture',),
    'terrain_integer_copy': ('PixelMap.Rendering.TerrainCache.TryCopyTexture',),
    'gl_state_capture': ('PixelMap.Rendering.GlMapRenderer+GlState..ctor',),
}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read(path: Path) -> bytes:
    if path.is_file():
        return path.read_bytes()
    return gzip.decompress(Path(str(path) + '.gz').read_bytes())


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2) + '\n')


def stack_categories(profile: dict) -> dict:
    nodes = {n['id']: n for n in profile['nodes']}
    assert len(nodes) == len(profile['nodes'])
    parents = {}
    for node in nodes.values():
        for child in node.get('children', []):
            assert child in nodes and child not in parents
            parents[child] = node['id']
    memberships = {}
    for identifier in nodes:
        cursor, seen, categories = identifier, set(), set()
        while cursor is not None:
            assert cursor not in seen
            seen.add(cursor)
            name = nodes[cursor]['callFrame']['functionName']
            categories.update(key for key, patterns in CATEGORIES.items()
                              if any(pattern in name for pattern in patterns))
            cursor = parents.get(cursor)
        memberships[identifier] = categories
    weights = {key: 0 for key in CATEGORIES}
    counts = {key: 0 for key in CATEGORIES}
    samples, deltas = profile['samples'], profile['timeDeltas']
    assert len(samples) == len(deltas) and all(d >= 0 for d in deltas)
    for identifier, delta in zip(samples, deltas):
        for key in memberships[identifier]:
            weights[key] += delta
            counts[key] += 1
    total = sum(deltas)
    return {key: {'inclusive_sample_weight_ms': weights[key] / 1000,
                  'inclusive_sample_count': counts[key],
                  'share_of_complete_profile_percent': weights[key] / total * 100 if total else 0}
            for key in CATEGORIES}


def main() -> None:
    if len(sys.argv) != 6:
        raise SystemExit(__doc__)
    evidence, baseline, candidate, symbolicator, output = map(Path, sys.argv[1:])
    output.mkdir(parents=True, exist_ok=False)
    provenance_bytes = read(evidence / 'provenance.json')
    provenance = json.loads(provenance_bytes)
    (output / 'source-provenance.json').write_bytes(provenance_bytes)
    script_bytes = symbolicator.read_bytes()
    (output / 'symbolicate-browser-profile.py').write_bytes(script_bytes)
    (output / 'profile_audit.py').write_bytes(Path(__file__).read_bytes())
    result = {
        'schema': 1, 'run_id': provenance['candidate']['run_id'],
        'source_provenance_sha256': digest(provenance_bytes),
        'symbolicator_sha256': digest(script_bytes),
        'category_patterns': CATEGORIES,
        'limits': [
            'Every profile is a separately captured first-repetition post-timing window.',
            'Sample weights are not invocation counts, GPU times, or the timed-window CPU metric.',
            'Moving simulation trajectories can differ between variants; these profiles do not isolate a patch.',
            'Only ordinary MethodDef AOT slots are named; runtime/native/generic extras remain anonymous.',
            'Folded aliases are retained. A category matches if any preserved alias matches its pattern.',
            'Each category counts each sampled stack once; categories overlap and must not be added.',
        ], 'variants': {},
    }
    for variant, site in (('baseline', baseline), ('candidate', candidate)):
        identity = provenance[variant]
        framework = site / '_framework'
        assert digest((site / 'index.html').read_bytes()) == identity['index_sha256']
        for resource in identity['resources']:
            name = resource['name']
            assert Path(name).name == name
            assert digest((framework / name).read_bytes()) == resource['sha256'], name
        natives = [r for r in identity['resources'] if r['group'] == 'wasmNative']
        assert len(natives) == 1
        native = natives[0]
        entries = [e for e in provenance['execution']
                   if e['variant'] == variant and e['cpu_profile_requested']]
        assert len(entries) == 7 and {e['scenario'] for e in entries} == PROFILE_CASES
        assert all(e['repetition'] == 1 and e['exit_code'] == 0 and
                   e['source_sha'] == identity['source_sha'] for e in entries)
        variant_dir = output / variant
        input_dir, symbolic_dir = variant_dir / 'inputs', variant_dir / 'symbolicated'
        input_dir.mkdir(parents=True)
        symbolic_dir.mkdir()
        inputs, active, idle = {}, [], []
        for entry in sorted(entries, key=lambda e: e['scenario']):
            scenario = entry['scenario']
            data = read(evidence / entry['output'] / f'{scenario}.cpuprofile')
            profile = json.loads(data)
            path = input_dir / f'{scenario}.cpuprofile'
            path.write_bytes(data)
            inputs[scenario] = (data, profile)
            names = {Path(unquote(urlparse(n['callFrame']['url']).path)).name
                     for n in profile['nodes']
                     if WASM_FUNCTION.fullmatch(n['callFrame']['functionName'])}
            foreign_native = {name for name in names if name.startswith('dotnet.native.')
                              and name != native['name']}
            assert not foreign_native, foreign_native
            if native['name'] in names:
                active.append(path)
            else:
                idle.append(scenario)
                (symbolic_dir / f'{scenario}.unchanged.cpuprofile').write_bytes(data)
        command = [sys.executable, str(symbolicator), '--framework', str(framework),
                   '--expected-native-sha256', native['sha256'], '--output', str(symbolic_dir)]
        for path in active:
            command += ['--profile', str(path)]
        if active:
            completed = subprocess.run(command, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, text=True)
            (variant_dir / 'symbolication.log').write_text(completed.stdout)
            assert completed.returncode == 0, completed.stdout
        summaries = []
        for scenario, (data, original) in inputs.items():
            unchanged = scenario in idle
            suffix = 'unchanged' if unchanged else 'symbolicated'
            named_path = symbolic_dir / f'{scenario}.{suffix}.cpuprofile'
            named_data = named_path.read_bytes()
            named = json.loads(named_data)
            neutralized = copy.deepcopy(named)
            assert len(original['nodes']) == len(neutralized['nodes'])
            renamed = 0
            for old, new in zip(original['nodes'], neutralized['nodes']):
                assert old['id'] == new['id']
                renamed += old['callFrame']['functionName'] != new['callFrame']['functionName']
                new['callFrame']['functionName'] = old['callFrame']['functionName']
            assert neutralized == original, f'Profile fields changed in {variant}/{scenario}'
            if unchanged:
                assert named_data == data
            deltas = original.get('timeDeltas', [])
            summary = {
                'scenario': scenario, 'input_sha256': digest(data),
                'output_sha256': digest(named_data), 'renamed_nodes': renamed,
                'only_function_names_changed': True, 'unchanged_without_native_frames': unchanged,
                'sample_count': len(original.get('samples', [])),
                'sample_weight_ms': sum(deltas) / 1000,
                'profile_duration_ms': (original['endTime'] - original['startTime']) / 1000,
                'median_sample_delta_ms': statistics.median(deltas) / 1000 if deltas else None,
                'max_sample_delta_ms': max(deltas) / 1000 if deltas else None,
                'stack_categories': stack_categories(named),
            }
            if not unchanged:
                function_path = symbolic_dir / f'{scenario}.summary.json'
                functions = json.loads(function_path.read_text())
                functions['source_profile'] = f'{variant}/inputs/{scenario}.cpuprofile'
                write_json(function_path, functions)
                summary.update({k: functions[k] for k in (
                    'referenced_wasm_functions', 'mapped_wasm_functions',
                    'mapped_wasm_self_ms', 'unmapped_wasm_self_ms', 'foreign_wasm_self_ms')})
                summary['top_mapped_self'] = [f for f in functions['functions']
                    if '[wasm-function[' in f['name']][:15]
                summary['top_application_inclusive'] = sorted(
                    (f for f in functions['functions'] if 'PixelMap.' in f['name']),
                    key=lambda f: (-f['inclusive_ms'], f['key']))[:25]
            summaries.append(summary)
        result['variants'][variant] = {
            'source_sha': identity['source_sha'], 'run_id': identity['run_id'],
            'artifact_id': identity['artifact_id'], 'artifact_digest': identity['artifact_digest'],
            'native': native, 'verified_boot_resources': len(identity['resources']),
            'symbolicated_profiles': len(active), 'unchanged_profiles': len(idle), 'profiles': summaries,
        }
    write_json(output / 'profile-analysis.json', result)
    print(json.dumps({v: {'profiles': len(r['profiles']),
                         'named': r['symbolicated_profiles'], 'unchanged': r['unchanged_profiles']}
                      for v, r in result['variants'].items()}))


if __name__ == '__main__':
    main()
