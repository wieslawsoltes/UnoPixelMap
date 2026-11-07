from pathlib import Path
import subprocess, os, json, hashlib, csv
root=Path(__file__).parent
sdk='/tmp/unopixelmap-dotnet/dotnet'
env=os.environ.copy(); env.update(DOTNET_TieredCompilation='0',DOTNET_PROCESSOR_COUNT='2',PIXELMAP_TEST_FONT='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf')
reports=[]
for block,variant in enumerate(['baseline','candidate','candidate','baseline']):
 out=root/f'{block}-{variant}'; out.mkdir(exist_ok=True)
 cmd=['taskset','-c','0',sdk,str(root/f'{variant}-runtime/PixelMap.Rendering.Tests.dll'),'--depth-benchmark','--depth-warmup=16','--depth-samples=64','--depth-baseline=c937ddd192619f48c1a2ec682845b52bc1eebff8',f'--depth-output={out}']
 with (out/'run.log').open('w') as log: subprocess.run(cmd,env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
 report=json.loads((out/'depth-preparation.json').read_text()); reports.append((block,variant,report))
 print(f'Completed block {block}: {variant}',flush=True)
rows=[]; summaries=[]; checks=0
for block,variant,report in reports:
 for workload in report['Results']:
  for sample in workload['Samples']:
   row={'block':block,'variant':variant,'workload':workload['Name'],'coarse_cell_size':workload['CoarseCellSize']}
   for k,v in sample.items():
    if isinstance(v,dict): row.update({k+'_'+a:b for a,b in v.items()})
    else: row[k]=v
   rows.append(row)
for name in dict.fromkeys(row['workload'] for row in rows):
 a=[r for r in rows if r['workload']==name and r['variant']=='baseline']; b=[r for r in rows if r['workload']==name and r['variant']=='candidate']
 for candidate in b:
  baseline=next(r for r in a if r['Frame']==candidate['Frame'])
  for field in ['QueryHash','OverlayHash','MinimumAltitude','MaximumAltitude','TerrainPatches','BuildingTriangles','ConstructedTriangles']:
   assert candidate[field]==baseline[field],(name,candidate['Frame'],field)
   checks+=1
 for r in a+b:
  assert r['FrameIndexCapacityBytes']<=16*1024*1024 and r['IndependentIndexCapacityBytes']<=16*1024*1024
  checks+=2
 def percentile(values,p):
  import math
  ordered=sorted(values); return ordered[max(0,math.ceil(len(ordered)*p)-1)]
 def summarize(v):
  wall=[s['Prepare_WallMs']+s['Navigation_WallMs'] for s in v]; cpu=[s['Prepare_ThreadCpuMs']+s['Navigation_ThreadCpuMs'] for s in v]
  return {'samples':len(v),'median_ms':percentile(wall,.5),'p95_ms':percentile(wall,.95),'thread_cpu_median_ms':percentile(cpu,.5),'allocated_bytes_mean':sum(s['Prepare_Bytes']+s['Navigation_Bytes'] for s in v)/len(v),'depth_query_median_ms':percentile([s['DepthConstruction_WallMs']+s['SparseQueries_WallMs'] for s in v],.5),'prepared_triangles_median':percentile([s['FramePreparedTriangles'] for s in v],.5),'index_capacity_bytes_max':max(s['FrameIndexCapacityBytes'] for s in v),'block_medians_ms':[percentile([s['Prepare_WallMs']+s['Navigation_WallMs'] for s in v if s['block']==block],.5) for block in dict.fromkeys(s['block'] for s in v)]}
 sa,sb=summarize(a),summarize(b); change=(sb['median_ms']/sa['median_ms']-1)*100
 summaries.append({'workload':name,'baseline':sa,'candidate':sb,'median_change_percent':change})
 print(f"{name}: {sa['median_ms']:.3f} -> {sb['median_ms']:.3f} ms ({change:+.1f}%); triangles {sa['prepared_triangles_median']} -> {sb['prepared_triangles_median']}",flush=True)
with (root/'samples.csv').open('w',newline='') as f:
 w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
summary={'protocol':'ABBA fresh processes; identical common harness/dependencies except Rendering.dll; CPU0; tiered compilation disabled; two managed processors; 16 warmup camera phases and64 measured frames per ordinary workload; controls4 warmups/8 samples. Median is lower-middle nearest rank; p95 nearest rank. Total is PrepareGpuFrame including labels plus navigation overlay/picking; GL and presentation excluded.','sample_count':len(rows),'exact_assertions':checks,'identity_sha256':hashlib.sha256((root/'identity.json').read_bytes()).hexdigest(),'samples_sha256':hashlib.sha256((root/'samples.csv').read_bytes()).hexdigest(),'summaries':summaries}
(root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
