"""Compare host-saved FITS sensor samples with each received ARW decoded independently.
Usage: python tests/verify_hardware_fits.py <repo> <autotest-result.json> <output.json>
No SDK initialization or camera connection. External photographs stay local.
"""
from pathlib import Path
import ctypes as C, hashlib, json, sys, struct
import numpy as np
root=Path(sys.argv[1]); report=json.loads(Path(sys.argv[2]).read_text(encoding='utf-8-sig')); output=Path(sys.argv[3])
class Info(C.Structure):
    _fields_=[(k,C.c_uint32) for k in ['abi','rawWidth','rawHeight','pitch','width','height','left','top','rawBits','maximum','black','errors']]+[('cblack',C.c_uint32*4),('cfa',C.c_char*5),('model',C.c_char*64),('decoder',C.c_char*128),('version',C.c_char*32)]
lib=C.CDLL(str(root/'artifacts/decoder/SonyArw6.dll'),winmode=0x1100)
lib.arw6_open.argtypes=[C.c_void_p,C.c_uint64,C.POINTER(C.c_void_p),C.POINTER(Info),C.c_char_p,C.c_int]
lib.arw6_copy.argtypes=[C.c_void_p,C.POINTER(C.c_ushort),C.c_uint64];lib.arw6_close.argtypes=[C.c_void_p]

def exif_metadata(data):
    endian='<' if data[:2]==b'II' else '>'
    def integer(fmt,offset):return struct.unpack_from(endian+fmt,data,offset)[0]
    def directory(offset):
        result={}
        count=integer('H',offset)
        assert count<4096 and offset+2+count*12+4<=len(data),'invalid TIFF directory'
        for n in range(count):
            at=offset+2+n*12;tag=integer('H',at);kind=integer('H',at+2);number=integer('I',at+4)
            size={1:1,2:1,3:2,4:4,5:8,9:4,10:8}.get(kind)
            if size is None or number>1024:continue
            start=at+8 if size*number<=4 else integer('I',at+8)
            if start+size*number>len(data):continue
            if kind==2:value=data[start:start+number].rstrip(b'\0').decode(errors='replace')
            elif number==1 and kind in (3,4,9):value=integer({3:'H',4:'I',9:'i'}[kind],start)
            elif number==1 and kind in (5,10):
                fmt='I' if kind==5 else 'i';den=integer(fmt,start+4);value=integer(fmt,start)/den if den else None
            else:continue
            result[tag]=value
        return result
    main=directory(integer('I',4));extra=directory(main[34665]) if main.get(34665) else {}
    return {'make':main.get(271),'model':main.get(272),'software':main.get(305),'exposureSeconds':extra.get(33434),'iso':extra.get(34855),'lens':extra.get(42036)}

assert report['result']=='PASS' and report.get('frames'),'successful hardware report required'
results=[]
for frame in report['frames']:
    result={'frame':frame['frame'],'rawFormat':frame['rawFormat'],'arwPath':frame['arwPath'],'fitsPath':frame['path']};handle=C.c_void_p()
    try:
        arw=Path(frame['arwPath']).read_bytes();source=C.create_string_buffer(arw);info=Info();err=C.create_string_buffer(512)
        assert lib.arw6_open(source,len(arw),C.byref(handle),C.byref(info),err,len(err))==0,err.value.decode(errors='replace')
        pixels=np.empty((info.height,info.width),dtype=np.uint16)
        assert lib.arw6_copy(handle,pixels.ctypes.data_as(C.POINTER(C.c_ushort)),pixels.size)==0
        data=Path(frame['path']).read_bytes();cards={};position=0
        while True:
            card=data[position:position+80].decode('ascii');position+=80;key=card[:8].strip()
            if key=='END':break
            if card[8:10]=='= ':cards[key]=card[10:].split('/')[0].strip().strip("'").strip()
        assert int(cards['BITPIX'])==16 and (int(cards['NAXIS2']),int(cards['NAXIS1']))==pixels.shape
        assert cards['BAYERPAT']==info.cfa.decode() and cards['ROWORDER']=='TOP-DOWN'
        offset=((position+2879)//2880)*2880
        stored=np.frombuffer(data,dtype='>i2',count=pixels.size,offset=offset).reshape(pixels.shape).astype(np.int32)
        stored=stored*int(cards.get('BSCALE','1'))+int(cards.get('BZERO','0'))
        assert np.array_equal(stored,pixels),'FITS samples differ from original decoded sensor plane'
        assert abs(float(cards['EXPTIME'])-frame['seconds'])<1e-8,'FITS exposure metadata mismatch'
        result.update(passed=True,width=info.width,height=info.height,cfa=info.cfa.decode(),rawBits=info.rawBits,maximum=info.maximum,decodeErrors=info.errors,pixelsCompared=int(pixels.size),exactPixels=True,sourceSha256=hashlib.sha256(arw).hexdigest(),fitsSha256=hashlib.sha256(data).hexdigest(),pixelSha256=hashlib.sha256(pixels.tobytes()).hexdigest(),sampledMin=int(pixels.min()),sampledMax=int(pixels.max()))
        assert info.errors==0
        metadata=exif_metadata(arw);result['arwExif']=metadata
        if metadata['exposureSeconds'] is not None:assert abs(metadata['exposureSeconds']-frame['seconds'])<0.05,'actual ARW exposure differs from request'
    except Exception as ex:result.update(passed=False,error=str(ex))
    finally:
        if handle.value:lib.arw6_close(handle)
    results.append(result);print(json.dumps(result),flush=True)
    output.write_text(json.dumps({'result':'PASS' if all(x['passed'] for x in results) else 'FAIL','results':results},indent=2),encoding='utf-8')
assert all(x['passed'] for x in results)
print('PASS: exact original-decoded sensor pixels and FITS metadata:',len(results),'hardware frames')
