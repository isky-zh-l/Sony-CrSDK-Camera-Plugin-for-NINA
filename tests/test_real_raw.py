"""Offline real-RAW corpus check. Files are external inputs, never redistributed.
Usage: python tests/test_real_raw.py <repo> <sample-index.json> <result.json>
Index entries require model, path, sha256 and verified=true. No camera is opened.
"""
from pathlib import Path
import ctypes as C, hashlib, json, sys, time
root=Path(sys.argv[1]).resolve(); index=json.loads(Path(sys.argv[2]).read_text(encoding='utf-8')); output=Path(sys.argv[3])
class Info(C.Structure):
    _fields_=[(k,C.c_uint32) for k in ['abi','rawWidth','rawHeight','pitch','width','height','left','top','rawBits','maximum','black','errors']]+[('cblack',C.c_uint32*4),('cfa',C.c_char*5),('model',C.c_char*64),('decoder',C.c_char*128),('version',C.c_char*32)]
lib=C.CDLL(str(root/'artifacts/decoder/SonyArw6.dll'),winmode=0x1100)
lib.arw6_open.argtypes=[C.c_void_p,C.c_uint64,C.POINTER(C.c_void_p),C.POINTER(Info),C.c_char_p,C.c_int]
lib.arw6_copy.argtypes=[C.c_void_p,C.POINTER(C.c_ushort),C.c_uint64];lib.arw6_close.argtypes=[C.c_void_p]
assert index and all(x.get("verified") for x in index),"corpus contains missing/unverified inputs; refusing partial success"
results=[]
for sample in index:
    if not sample.get('verified'):continue
    path=Path(sample['path']); data=path.read_bytes(); result={'path':str(path),'source':sample.get('source'),'expectedModel':sample['model'],'sourceSha256':hashlib.sha256(data).hexdigest()}
    start=time.monotonic();frame=C.c_void_p()
    try:
        assert result['sourceSha256']==sample['sha256'],'source hash mismatch'
        buf=C.create_string_buffer(data);info=Info();error=C.create_string_buffer(512)
        rc=lib.arw6_open(buf,len(data),C.byref(frame),C.byref(info),error,len(error));result['returnCode']=rc
        assert rc==0,error.value.decode(errors='replace')
        meta={k:getattr(info,k) for k in ['rawWidth','rawHeight','pitch','width','height','left','top','rawBits','maximum','black','errors']}
        meta.update({k:getattr(info,k).decode() for k in ['cfa','model','decoder','version']});result['info']=meta
        assert meta['model']==sample['model'],'wrong model'
        for key,value in sample.get('expectedGeometry',{}).items():assert meta[key]==value,('geometry differs from independent camera metadata',key,meta[key],value)
        assert info.width*info.height>1000000 and info.errors==0,'not a complete sensor plane'
        assert meta['cfa'] in ['RGGB','BGGR','GRBG','GBRG'],'unsupported CFA'
        pixels=(C.c_ushort*(info.width*info.height))();assert lib.arw6_copy(frame,pixels,len(pixels))==0,'copy failed'
        raw=bytes(pixels);result['pixelSha256']=hashlib.sha256(raw).hexdigest()
        # Sparse numeric checks avoid slow Python iteration over >60 million samples.
        values=pixels[::max(1,len(pixels)//20000)];result['sampledMin']=min(values);result['sampledMax']=max(values)
        assert result['sampledMax']>result['sampledMin'],'constant/empty image'
        assert result['sampledMax']<=info.maximum,'samples exceed declared white level'
        assert lib.arw6_copy(frame,pixels,len(pixels)-1)!=0,'short output buffer accepted'
        if sample.get('expectedPixelSha256'):assert result['pixelSha256']==sample['expectedPixelSha256'],'reviewed regression fingerprint changed'
        # Real-file truncation must never be accepted as a complete image.
        if '/raw_modes/' in str(path).replace('\\','/'):
            truncated=C.create_string_buffer(data[:len(data)//2]);bad=C.c_void_p();badinfo=Info();baderror=C.create_string_buffer(512)
            badrc=lib.arw6_open(truncated,len(data)//2,C.byref(bad),C.byref(badinfo),baderror,len(baderror))
            if bad.value:lib.arw6_close(bad)
            assert badrc!=0 and not bad.value,'truncated real RAW accepted'
            result['truncatedRejected']=True
        result['passed']=True
    except Exception as ex:result['passed']=False;result['error']=str(ex)
    finally:
        if frame.value:lib.arw6_close(frame)
    result['seconds']=round(time.monotonic()-start,3);results.append(result)
    output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps({'hardwareValidated':False,'results':results},indent=2),encoding='utf-8')
    print(json.dumps(result),flush=True)
assert results,'no verified real RAW inputs'
assert all(x['passed'] for x in results),'real RAW failures; see result JSON'
print('PASS real RAW corpus:',len(results),'files. Camera connection/exposure not tested.')
