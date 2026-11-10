from pathlib import Path
import os,subprocess
root=Path('/workspace/scratch/c64783d771e9')
out=root/'visibility-experiment-v2'
env=os.environ.copy();env.update(DOTNET_PROCESSOR_COUNT='2',DOTNET_TieredCompilation='0',COMPlus_TieredCompilation='0',DOTNET_EnableDiagnostics='0')
for block,variant in enumerate(('baseline','candidate','candidate','baseline')):
    dest=out/'minecraft'/f'{block}-{variant}'
    dest.mkdir(parents=True,exist_ok=True)
    cmd=['taskset','-c','8',str(root/'toolchain/dotnet/dotnet'),str(out/variant/'PixelMap.Rendering.Tests.dll'),'--depth-benchmark','--depth-filter=hills-minecraft','--depth-warmup=32','--depth-samples=64','--depth-output='+str(dest)]
    with (dest/'run.log').open('w') as log:
        subprocess.run(cmd,cwd=root/'UnoPixelMap',env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
    print('Completed',block,variant,flush=True)
