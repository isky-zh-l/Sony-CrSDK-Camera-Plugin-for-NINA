from pathlib import Path
import json,sys,hashlib
import numpy as np
from PIL import Image
run=Path(sys.argv[1]); manifest=json.loads((run/'returned-file-manifest.json').read_text(encoding='utf-8-sig'))
arw=Path(manifest['returned_arw']); copy=Path(manifest['decode_copy']); pgm=Path(str(copy)+'.pgm'); tiff=Path(str(copy)+'.tiff')
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
with pgm.open('rb') as f:
    assert token(f)==b'P5'
    w,h,maxval=int(token(f)),int(token(f)),int(token(f)); offset=f.tell()
    assert maxval==65535 and pgm.stat().st_size-offset==w*h*2
    raw=np.fromfile(f,dtype='>u2').reshape(h,w)
with Image.open(tiff) as image:
    bits=tuple(image.tag_v2[258]); samples=int(image.tag_v2[277]); compression=int(image.tag_v2[259]); size=image.size
    offsets=image.tag_v2[273]; lengths=image.tag_v2[279]
    if isinstance(offsets,int): offsets=(offsets,)
    if isinstance(lengths,int): lengths=(lengths,)
    assert size==(7028,4688) and bits==(16,16,16) and samples==3 and compression==1
    assert all(o>=0 and n>0 and o+n<=tiff.stat().st_size for o,n in zip(offsets,lengths))
assert (w,h)==(7040,4688)
assert manifest['source_copy_hash_matches'] and hashlib.sha256(copy.read_bytes()).hexdigest().upper()==manifest['sha256']
report=dict(capture_id=manifest['capture_id'],returned_arw=str(arw),arw_bytes=arw.stat().st_size,
    raw_size=[w,h],raw_bits=16,raw_pixels=w*h,raw_min=int(raw.min()),raw_max=int(raw.max()),raw_mean=float(raw.mean()),
    rgb_tiff=str(tiff),rgb_size=list(size),rgb_bits=list(bits),rgb_samples=samples,tiff_compression=compression,
    rgb_tiff_bytes=tiff.stat().st_size,raw_pgm_bytes=pgm.stat().st_size,raw_pgm_payload_complete=True,
    tiff_strips_within_file=True,decode_copy_matches_returned_arw=True,result='PASS')
(run/'decode'/'validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
