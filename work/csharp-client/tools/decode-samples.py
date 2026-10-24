"""Decode exact files returned by final C# build. No embedded-JPEG fallback."""
from pathlib import Path
import sys, json, subprocess, shutil, hashlib, re, struct, argparse
import numpy as np
from PIL import Image

def token(f):
    while True:
        c=f.read(1)
        if not c: raise ValueError('PGM header EOF')
        if c==b'#': f.readline(); continue
        if not c.isspace(): break
    value=c
    while True:
        c=f.read(1)
        if not c or c.isspace(): return value
        value+=c

def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser(description='Decode exact real C# samples using an externally supplied compatible LibRaw; no embedded JPEG fallback.')
    parser.add_argument('run_directory')
    parser.add_argument('--decoder-dir',required=True,help='Existing LibRaw bin directory with A7 V ARW6 support')
    parser.add_argument('--ui-summary',help='Actual controls verification summary.json; use exact successful paths from this report')
    parser.add_argument('--sample-mode',choices=['ordinary','bulb','both'],default='both')
    options=parser.parse_args()
    run=Path(options.run_directory).resolve()
    if options.ui_summary:
        ui=json.loads(Path(options.ui_summary).read_text(encoding='utf-8-sig'))
        assert ui['Passed'] and ui['BaselineRestored'] and ui['Backend']=='production' and not ui['TestOnly'], 'only successful production controls verification may supply RAW evidence'
        samples=[]
        for capture in ui['Captures']:
            if not capture['IsSuccess']: continue
            source=Path(capture['ArwPath'])
            assert source.stat().st_size==capture['FileBytes']
            with source.open('rb') as f: digest=hashlib.file_digest(f,'sha256').hexdigest().upper()
            samples.append(dict(id=capture['CaptureId'],path=str(source),bytes=capture['FileBytes'],sha256=digest,requestedSeconds=capture['Exposure']['RequestedSeconds'],mode={'Ordinary':0,'Bulb':1}[capture['Exposure']['Mode']]))
    else:
        result=json.loads((run/'result.json').read_text(encoding='utf-8-sig'))
        assert result['success'] and result['backend']=='REAL', 'only successful real-camera run may supply RAW evidence'
        samples=json.loads((run/'samples.json').read_text(encoding='utf-8-sig'))
    modes={'ordinary':(0,),'bulb':(1,),'both':(0,1)}[options.sample_mode]
    chosen=[next(s for s in samples if s['mode']==m) for m in modes]
    decoder=Path(options.decoder_dir).resolve()
    destination=run/'raw-decode'; destination.mkdir(exist_ok=False)
    sha=lambda p: hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper()
    reports=[]
    for sample in chosen:
        source=Path(sample['path']); folder=destination/str(sample['id']);folder.mkdir()
        copy=folder/'SAMPLE.ARW';shutil.copy2(source,copy)
        assert sha(source)==sha(copy)==sample['sha256']
        exits={}
        for name,args in [('raw-identify',['-v','SAMPLE.ARW']),('unprocessed_raw',['SAMPLE.ARW']),('dcraw_emu',['-4','-T','SAMPLE.ARW'])]:
            with (folder/(name+'.stdout.txt')).open('wb') as out,(folder/(name+'.stderr.txt')).open('wb') as err:
                child=subprocess.run([str(decoder/(name+'.exe')),*args],cwd=folder,stdout=out,stderr=err,timeout=120)
            exits[name]=child.returncode
        assert all(x==0 for x in exits.values()), exits
        metadata=(folder/'raw-identify.stdout.txt').read_text(encoding='utf-8',errors='replace')
        assert 'ILCE-7M5' in metadata and 'Cannot decode' not in metadata
        raw_size=tuple(map(int,re.search(r'Full size:\s*(\d+)\s*x\s*(\d+)',metadata).groups()))
        out_size=tuple(map(int,re.search(r'Output size:\s*(\d+)\s*x\s*(\d+)',metadata).groups()))
        pgm=folder/'SAMPLE.ARW.pgm';tiff=folder/'SAMPLE.ARW.tiff'
        with pgm.open('rb') as f:
            assert token(f)==b'P5'
            w,h,maxval=int(token(f)),int(token(f)),int(token(f))
            assert (w,h)==raw_size and maxval==65535 and pgm.stat().st_size-f.tell()==w*h*2
            raw=np.fromfile(f,dtype='>u2')
        with Image.open(tiff) as image:
            assert image.size==out_size and tuple(image.tag_v2[258])==(16,16,16) and image.tag_v2[277]==3 and image.tag_v2[259]==1
            offsets=image.tag_v2[273]; lengths=image.tag_v2[279]
            if isinstance(offsets,int):offsets=(offsets,)
            if isinstance(lengths,int):lengths=(lengths,)
            assert len(offsets)==len(lengths) and sum(lengths)==out_size[0]*out_size[1]*6
            assert all(o>=0 and n>0 and o+n<=tiff.stat().st_size for o,n in zip(offsets,lengths))
        report=dict(sample=sample,raw_size=raw_size,raw_bits=16,raw_pgm_bytes=pgm.stat().st_size,rgb_size=out_size,rgb_bits=[16,16,16],rgb_tiff_bytes=tiff.stat().st_size,raw_min=int(raw.min()),raw_max=int(raw.max()),raw_mean=float(raw.mean()),sha256_matches=True,raw_payload_complete=True,rgb_strip_ranges_valid=True,decoder=str(decoder),exits=exits,result='PASS')
        (folder/'validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        reports.append(report)
    (destination/'validation-summary.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(reports,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
