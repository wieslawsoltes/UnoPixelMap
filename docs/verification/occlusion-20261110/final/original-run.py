from pathlib import Path
import datetime, hashlib, json, os, platform, subprocess
root=Path('/workspace/scratch/c64783d771e9')
experiment=root/'visibility-experiment-v2'
out=root/'visibility-final-20261110'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
expected=json.loads((experiment/'identity.json').read_text())['files']
for variant in ('baseline','candidate'):
    actual={str(p.relative_to(experiment/variant)):sha(p) for p in (experiment/variant).rglob('*') if p.is_file()}
    assert actual==expected[variant], 'Frozen runtime changed: '+variant
cpu='unknown'
for line in Path('/proc/cpuinfo').read_text().splitlines():
    if line.startswith('model name'):
        cpu=line.partition(':')[2].strip();break
env=os.environ.copy()
settings=dict(DOTNET_PROCESSOR_COUNT='2',DOTNET_TieredCompilation='0',COMPlus_TieredCompilation='0',DOTNET_EnableDiagnostics='0')
env.update(settings)
metadata={'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'platform':platform.platform(),'processor':cpu,'available_affinity':sorted(os.sched_getaffinity(0)),'benchmark_affinity':[8],'environment':settings,'order':['baseline','candidate','candidate','baseline'],'warmup':16,'samples':64,'runtime_identities':expected,'dotnet_info':subprocess.check_output([str(root/'toolchain/dotnet/dotnet'),'--info'],text=True),'commands':[]}
for block,variant in enumerate(metadata['order']):
    dest=out/f'{block}-{variant}';dest.mkdir(exist_ok=True)
    cmd=['taskset','-c','8',str(root/'toolchain/dotnet/dotnet'),str(experiment/variant/'PixelMap.Rendering.Tests.dll'),'--depth-benchmark','--depth-baseline=ce82b2c6fccee2d77a9e9489e1c6415e04412e8c','--depth-warmup=16','--depth-samples=64','--depth-output='+str(dest)]
    metadata['commands'].append(cmd)
    with (dest/'run.log').open('w') as log:
        subprocess.run(cmd,cwd=root/'UnoPixelMap',env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
    print('Completed',block,variant,flush=True)
metadata['completed_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
(out/'identity.json').write_text(json.dumps(metadata,indent=2)+'\n')
