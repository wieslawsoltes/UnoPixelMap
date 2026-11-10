#!/usr/bin/env python3
"""Extract algorithm counters from an instrumented diagnostic; ignore every timing."""
import argparse
import collections
import csv
import gzip
import io
import json
from pathlib import Path
import statistics


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report',type=Path)
    parser.add_argument('output',type=Path)
    args=parser.parse_args()
    raw=args.report.read_bytes()
    if args.report.suffix=='.gz':raw=gzip.decompress(raw)
    data=json.loads(raw)
    stream=io.StringIO(newline='')
    writer=csv.writer(stream,lineterminator='\n')
    writer.writerow(['workload','art_width','art_height','cell','coarse_model_candidates','point_model_candidates','unprepared_point_models','large_model_candidates','prepared_triangles_before','early_return','prepared_triangles_after'])
    groups=[]
    for workload in data['Results']:
        rows=[list(map(int,value.split(','))) for value in workload['Diagnostics']]
        assert all(len(row)==10 for row in rows)
        writer.writerows([[workload['Name'],*row] for row in rows])
        summary={
            'workload':workload['Name'],'camera_frames':workload['MeasuredSamples'],
            'traversals':len(rows),'early_returns':sum(row[8] for row in rows),
        }
        for index,name in ((3,'coarse_models'),(4,'point_models'),(5,'unprepared_point_models')):
            values=[row[index] for row in rows]
            summary[name]={'median':statistics.median(values) if values else None,
                           'max':max(values) if values else None,
                           'zero':values.count(0),'one':values.count(1),'at_least_two':sum(v>=2 for v in values)}
        summary['new_prepared_triangles_in_traversals']=sum(row[9]-row[7] for row in rows)
        groups.append(summary)
    args.output.mkdir(parents=True,exist_ok=True)
    (args.output/'summary.json').write_text(json.dumps({
        'scope':'Diagnostic-only instrumentation of CoversBeforeComplete executions; no diagnostic timings are used. Entries count all label/navigation invocations reaching that traversal; cached samples and near-singular plane early rejections are excluded. All ordinary workloads cover16camera phases with zero warmup; short controls cover8initial phases.',
        'workloads':groups,
    },indent=2)+'\n')
    with (args.output/'queries.csv.gz').open('wb') as file:
        with gzip.GzipFile(filename='',fileobj=file,mode='wb',mtime=0) as out:out.write(stream.getvalue().encode())


if __name__=='__main__':main()
