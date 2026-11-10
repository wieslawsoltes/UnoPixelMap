from pathlib import Path
import hashlib, json, os, shutil, subprocess
root=Path('/workspace/scratch/c64783d771e9')
out=root/'visibility-experiment-v2'
base=root/'baseline-ce82b2c/tests/PixelMap.Rendering.Tests/bin/Release/net10.0'
candidate=root/'depth-candidate-20261110/tests/PixelMap.Rendering.Tests/bin/Release/net10.0'
for variant in ('baseline','candidate'):
    runtime=out/variant
    shutil.copytree(base,runtime,dirs_exist_ok=True)
    if variant=='candidate':
        shutil.copy2(candidate/'PixelMap.Rendering.dll',runtime/'PixelMap.Rendering.dll')
        shutil.copy2(candidate/'PixelMap.Rendering.pdb',runtime/'PixelMap.Rendering.pdb')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
identity={v:{str(p.relative_to(out/v)):sha(p) for p in (out/v).rglob('*') if p.is_file()} for v in ('baseline','candidate')}
changed=[p for p in identity['baseline'] if identity['baseline'][p]!=identity['candidate'][p]]
assert changed==['PixelMap.Rendering.dll','PixelMap.Rendering.pdb'] or set(changed)=={'PixelMap.Rendering.dll','PixelMap.Rendering.pdb'},changed
(out/'identity.json').write_text(json.dumps({'files':identity,'changed':changed,'order':['baseline','candidate','candidate','baseline'],'cpu':8,'warmup':16,'samples':32},indent=2)+'\n')
env=os.environ.copy()
env.update(DOTNET_PROCESSOR_COUNT='2',DOTNET_TieredCompilation='0',COMPlus_TieredCompilation='0',DOTNET_EnableDiagnostics='0')
for block,variant in enumerate(('baseline','candidate','candidate','baseline')):
    dest=out/f'{block}-{variant}'
    dest.mkdir(exist_ok=True)
    cmd=['taskset','-c','8',str(root/'toolchain/dotnet/dotnet'),str(out/variant/'PixelMap.Rendering.Tests.dll'),'--depth-benchmark','--depth-baseline=ce82b2c6fccee2d77a9e9489e1c6415e04412e8c','--depth-warmup=16','--depth-samples=32','--depth-output='+str(dest)]
    with (dest/'run.log').open('w') as log:
        subprocess.run(cmd,cwd=root/'UnoPixelMap',env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
    print('Completed',block,variant,flush=True)
