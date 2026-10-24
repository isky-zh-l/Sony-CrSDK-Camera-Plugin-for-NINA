"""External acceptance tool, never linked into DLL. Uses the existing compatible LibRaw."""
from pathlib import Path
import argparse,json,sys,struct,re,subprocess,hashlib,shutil,os
import numpy as np
from PIL import Image

def exif_exposure(path):
    with Path(path).open('rb') as f:
        size=Path(path).stat().st_size
        def at(offset,n):
            if offset<0 or n<0 or offset+n>size: raise ValueError('TIFF offset outside file')
            f.seek(offset);b=f.read(n)
            if len(b)!=n:raise ValueError('TIFF short read')
            return b
        header=at(0,8);endian='<' if header[:2]==b'II' else '>'
        assert header[:2] in (b'II',b'MM') and struct.unpack(endian+'H',header[2:4])[0]==42
        def ifd(offset):
            count=struct.unpack(endian+'H',at(offset,2))[0]
            assert count<4096
            tags={}
            for i in range(count):
                row=at(offset+2+i*12,12);tag,kind,n=struct.unpack(endian+'HHI',row[:8]);tags[tag]=(kind,n,row[8:12])
            return tags
        first=ifd(struct.unpack(endian+'I',header[4:8])[0])
        kind,n,raw=first[34665];assert kind==4 and n==1
        tags=ifd(struct.unpack(endian+'I',raw)[0])
        kind,n,raw=tags[33434];assert kind==5 and n==1
        numerator,denominator=struct.unpack(endian+'II',at(struct.unpack(endian+'I',raw)[0],8))
        assert denominator
        return {'numerator':numerator,'denominator':denominator,'seconds':numerator/denominator}

def pgm_token(f):
    while True:
        c=f.read(1)
        if not c:raise ValueError('PGM header EOF')
        if c==b'#':f.readline();continue
        if not c.isspace():break
    value=c
    while True:
        c=f.read(1)
        if not c or c.isspace():return value
        value+=c

def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run_directory',type=Path)
    parser.add_argument('--decoder-dir',default=os.environ.get('SONY_LIBRAW_TOOLS'),help='Compatible LibRaw executable directory; alternatively SONY_LIBRAW_TOOLS')
    args=parser.parse_args()
    if not args.decoder_dir: parser.error('Supply --decoder-dir or SONY_LIBRAW_TOOLS; there is no developer-machine default.')
    decoder=Path(args.decoder_dir).resolve()
    for name in ['raw-identify.exe','unprocessed_raw.exe','dcraw_emu.exe']:
        if not (decoder/name).is_file(): parser.error(f'Missing decoder executable: {decoder/name}')
    run=args.run_directory.resolve();samples=json.loads((run/'samples.json').read_text(encoding='utf-8-sig'))
    decode=run/'bulb-decode';decode.mkdir(exist_ok=False)
    reports=[]
    for item in samples['bulb_samples']:
        requested=item['requested_seconds'];source=Path(item['path'])
        folder=decode/str(int(requested));folder.mkdir()
        copy=folder/'SAMPLE.ARW';shutil.copy2(source,copy)
        sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
        assert sha(copy)==sha(source)
        exits={}
        for name,args in [('raw-identify',['-v','SAMPLE.ARW']),('unprocessed_raw',['SAMPLE.ARW']),('dcraw_emu',['-4','-T','SAMPLE.ARW'])]:
            with (folder/(name+'.stdout.txt')).open('wb') as output,(folder/(name+'.stderr.txt')).open('wb') as errors:
                child=subprocess.run([str(decoder/(name+'.exe')),*args],cwd=folder,stdout=output,stderr=errors,timeout=120)
            exits[name]=child.returncode
        assert all(v==0 for v in exits.values()),exits
        metadata=(folder/'raw-identify.stdout.txt').read_text(encoding='utf8',errors='replace')
        assert 'Cannot decode' not in metadata and 'ILCE-7M5' in metadata
        raw_size=tuple(map(int,re.search(r'Full size:\s*(\d+)\s*x\s*(\d+)',metadata).groups()))
        output_size=tuple(map(int,re.search(r'Output size:\s*(\d+)\s*x\s*(\d+)',metadata).groups()))
        pgm=folder/'SAMPLE.ARW.pgm';tiff=folder/'SAMPLE.ARW.tiff'
        with pgm.open('rb') as f:
            assert pgm_token(f)==b'P5'
            width,height,bits=int(pgm_token(f)),int(pgm_token(f)),int(pgm_token(f));offset=f.tell()
            assert bits==65535 and (width,height)==raw_size and pgm.stat().st_size-offset==width*height*2
            raw=np.fromfile(f,dtype='>u2').reshape(height,width)
        with Image.open(tiff) as image:
            rgb_size=image.size;rgb_bits=tuple(image.tag_v2[258]);channels=int(image.tag_v2[277]);compression=int(image.tag_v2[259])
            assert rgb_size==output_size and rgb_bits==(16,16,16) and channels==3 and compression==1
            offsets=image.tag_v2[273];lengths=image.tag_v2[279]
            if isinstance(offsets,int):offsets=(offsets,)
            if isinstance(lengths,int):lengths=(lengths,)
            assert all(o>=0 and n>0 and o+n<=tiff.stat().st_size for o,n in zip(offsets,lengths))
        exif=exif_exposure(source)
        assert abs(exif['seconds']-requested)<=0.2,('EXIF does not agree with requested duration',exif,requested)
        report=dict(capture_id=item['capture_id'],requested_seconds=requested,returned_arw=str(source),arw_bytes=source.stat().st_size,sha256=sha(source),
            wrapper_sha256=samples['wrapper_sha256'],exif_exposure=exif,exif_difference_seconds=exif['seconds']-requested,
            raw_size=raw_size,raw_bits=16,raw_pgm_bytes=pgm.stat().st_size,raw_min=int(raw.min()),raw_max=int(raw.max()),raw_mean=float(raw.mean()),
            rgb_size=rgb_size,rgb_bits=rgb_bits,rgb_tiff_bytes=tiff.stat().st_size,raw_payload_complete=True,rgb_strips_within_file=True,
            decode_copy_matches_returned_file=True,decoder=str(decoder),tool_exit_codes=exits,result='PASS')
        (folder/'validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
        print(json.dumps(report,ensure_ascii=False,indent=2));reports.append(report)
    assert len(reports)==2 and {r['requested_seconds'] for r in reports}=={2,60}
    (decode/'validation-summary.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf8')

if __name__=='__main__':main()
