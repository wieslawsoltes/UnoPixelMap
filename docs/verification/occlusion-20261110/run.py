#!/usr/bin/env python3
"""Run one fixed-camera native ABBA sequence with a shared compiled harness."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def identity(directory):
    return {p.relative_to(directory).as_posix(): sha256(p)
            for p in sorted(directory.rglob('*')) if p.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--baseline-runtime', type=Path, required=True)
    parser.add_argument('--candidate-renderer', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cpu', type=int, default=8)
    parser.add_argument('--warmup', type=int, default=16)
    parser.add_argument('--samples', type=int, default=64)
    parser.add_argument('--filter')
    args = parser.parse_args()
    for field in ('dotnet', 'baseline_runtime', 'candidate_renderer', 'output'):
        setattr(args, field, getattr(args, field).resolve())
    if args.output.exists():
        parser.error('Output must be a new directory; previous evidence is never overwritten.')
    if args.cpu not in os.sched_getaffinity(0):
        parser.error('Requested CPU is outside the process affinity.')
    if not (args.baseline_runtime / 'PixelMap.Rendering.Tests.dll').is_file():
        parser.error('Baseline runtime does not contain the compiled test harness.')
    args.output.mkdir(parents=True)
    runtimes = args.output / 'runtimes'
    for variant in ('baseline', 'candidate'):
        shutil.copytree(args.baseline_runtime, runtimes / variant)
    shutil.copy2(args.candidate_renderer, runtimes / 'candidate/PixelMap.Rendering.dll')
    pdb = args.candidate_renderer.with_suffix('.pdb')
    if pdb.exists():
        shutil.copy2(pdb, runtimes / 'candidate/PixelMap.Rendering.pdb')
    files = {v: identity(runtimes / v) for v in ('baseline', 'candidate')}
    assert files['baseline'].keys() == files['candidate'].keys()
    changed = [name for name in files['baseline'] if files['baseline'][name] != files['candidate'][name]]
    assert set(changed) <= {'PixelMap.Rendering.dll', 'PixelMap.Rendering.pdb'}, changed
    assert 'PixelMap.Rendering.dll' in changed, 'Candidate renderer is identical to baseline.'
    settings = dict(DOTNET_PROCESSOR_COUNT='2', DOTNET_TieredCompilation='0',
                    COMPlus_TieredCompilation='0', DOTNET_EnableDiagnostics='0')
    env = os.environ.copy()
    env.update(settings)
    cpu = next((line.partition(':')[2].strip() for line in Path('/proc/cpuinfo').read_text().splitlines()
                if line.startswith('model name')), 'unknown')
    report = dict(started_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  platform=platform.platform(), processor=cpu,
                  benchmark_affinity=[args.cpu], environment=settings,
                  inherited_test_font=env.get('PIXELMAP_TEST_FONT'),
                  warmup=args.warmup, samples=args.samples, filter=args.filter,
                  order=['baseline', 'candidate', 'candidate', 'baseline'],
                  runtime_identities=files, changed_files=changed,
                  dotnet_info=subprocess.check_output([str(args.dotnet), '--info'], text=True),
                  commands=[])
    for block, variant in enumerate(report['order']):
        output = args.output / f'{block}-{variant}'
        output.mkdir()
        command = ['taskset', '-c', str(args.cpu), str(args.dotnet),
                   str(runtimes / variant / 'PixelMap.Rendering.Tests.dll'), '--depth-benchmark',
                   '--depth-baseline=ce82b2c6fccee2d77a9e9489e1c6415e04412e8c',
                   f'--depth-warmup={args.warmup}', f'--depth-samples={args.samples}',
                   f'--depth-output={output}']
        if args.filter:
            command.append('--depth-filter=' + args.filter)
        report['commands'].append(command)
        with (output / 'run.log').open('w') as log:
            subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        print(f'Completed {block}: {variant}', flush=True)
    report['completed_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    (args.output / 'identity.json').write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    main()
