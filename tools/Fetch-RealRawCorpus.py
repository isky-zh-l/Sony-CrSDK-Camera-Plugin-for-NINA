"""Verify the pinned external RAW corpus; --download explicitly permits fetching public CC0 originals.
python tools/Fetch-RealRawCorpus.py <sample-directory> <verified-index.json> [--download]
Photos remain external/ignored and must never be added to a source or binary release.
"""
from pathlib import Path
import hashlib,json,sys,time,urllib.request
from concurrent.futures import ThreadPoolExecutor,as_completed
root=Path(__file__).resolve().parent.parent
base=Path(sys.argv[1]).resolve();output=Path(sys.argv[2]).resolve();download='--download' in sys.argv[3:]
catalog=json.loads((root/'config/real-raw-corpus.json').read_text(encoding='utf-8'))
def process(sample):
    result={**sample,'verified':False};path=(base/sample['file']).resolve();result['path']=str(path)
    try:
        if not path.is_relative_to(base):raise ValueError('unsafe corpus path')
        good=path.is_file() and path.stat().st_size==sample['bytes'] and hashlib.sha256(path.read_bytes()).hexdigest()==sample['sha256']
        if not good and download:
            path.parent.mkdir(parents=True,exist_ok=True)
            for attempt in range(3):
                try:
                    partial=path.stat().st_size if path.exists() and path.stat().st_size<sample['bytes'] else 0
                    headers={'User-Agent':'SonyCrSDK-offline-corpus/0.5.1'}
                    if partial:headers['Range']='bytes='+str(partial)+'-'
                    start=time.monotonic()
                    with urllib.request.urlopen(urllib.request.Request(sample['source'],headers=headers),timeout=35) as response,path.open('ab' if partial and response.status==206 else 'wb') as target:
                        while chunk:=response.read(65536):
                            target.write(chunk)
                            if time.monotonic()-start>1800:raise TimeoutError('download exceeded 30 minutes')
                    if path.stat().st_size!=sample['bytes'] or hashlib.sha256(path.read_bytes()).hexdigest()!=sample['sha256']:raise ValueError('corpus size/hash mismatch')
                    break
                except Exception:
                    if attempt==2:raise
                    time.sleep(2*(attempt+1))
        if not path.is_file() or path.stat().st_size!=sample['bytes'] or hashlib.sha256(path.read_bytes()).hexdigest()!=sample['sha256']:raise ValueError('missing or unverified corpus input; use --download explicitly to fetch')
        result['verified']=True
    except Exception as ex:result['error']=str(ex)
    return result
results=[]
with ThreadPoolExecutor(max_workers=2) as pool:
    for future in as_completed([pool.submit(process,sample) for sample in catalog['samples']]):
        result=future.result();results.append(result);print(result['model'],result['file'],result['verified'],result.get('error',''),flush=True)
results.sort(key=lambda item:item['file']);output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(results,indent=2),encoding='utf-8')
if not all(item['verified'] for item in results):raise SystemExit(1)
print('Verified',len(results),'real RAW samples; no camera operations')
