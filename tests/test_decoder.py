"""Synthetic DNG exercises the Sony Bayer ABI only, never camera/ARW compatibility."""
from pathlib import Path
import ctypes as C
import struct
import sys
import hashlib

root=Path(sys.argv[1])
output=root/'artifacts/multicamera/synthetic-raw'
output.mkdir(parents=True,exist_ok=True)
class Info(C.Structure):
    _fields_=[(k,C.c_uint32) for k in ['abi','rawWidth','rawHeight','pitch','width','height','left','top','rawBits','maximum','black','errors']]+[
        ('cblack',C.c_uint32*4),('cfa',C.c_char*5),('model',C.c_char*64),('decoder',C.c_char*128),('version',C.c_char*32)]

def dng(make,model,cfa=(0,1,1,2),white=16383,crop=None):
    w,h=96,80
    pixels=[(i*17+512)%white for i in range(w*h)]
    tags={256:(4,[w]),257:(4,[h]),258:(3,[16]),259:(3,[1]),262:(3,[32803]),
          271:(2,make.encode()+b'\0'),272:(2,model.encode()+b'\0'),273:(4,[0]),
          277:(3,[1]),278:(4,[h]),279:(4,[w*h*2]),284:(3,[1]),
          33421:(3,[2,2]),33422:(1,bytes(cfa)),50706:(1,b'\x01\x04\x00\x00'),
          50707:(1,b'\x01\x01\x00\x00'),50708:(2,(make+' '+model).encode()+b'\0'),
          50710:(1,b'\x00\x01\x02'),50711:(3,[1]),50713:(3,[1,1]),
          50714:(3,[0]),50717:(4,[white]),50778:(3,[21]),
          50721:(10,[(1,1),(0,1),(0,1),(0,1),(1,1),(0,1),(0,1),(0,1),(1,1)])}
    if crop:
        tags[50719]=(4,list(crop[:2]));tags[50720]=(4,list(crop[2:]))
    start=8+2+len(tags)*12+4
    payload=bytearray();entries=[]
    for tag,(typ,value) in sorted(tags.items()):
        if typ in (1,2): b=bytes(value);n=len(b)
        elif typ==3: b=struct.pack('<'+'H'*len(value),*value);n=len(value)
        elif typ==4: b=struct.pack('<'+'I'*len(value),*value);n=len(value)
        elif typ==10: b=b''.join(struct.pack('<ii',*v) for v in value);n=len(value)
        if len(b)<=4: data=b.ljust(4,b'\0')
        else:
            data=struct.pack('<I',start+len(payload));payload+=b
            if len(payload)%2:payload+=b'\0'
        entries.append((tag,typ,n,data))
    raw_offset=start+len(payload)
    entries=[(t,ty,n,struct.pack('<I',raw_offset) if t==273 else d) for t,ty,n,d in entries]
    result=b'II'+struct.pack('<HI',42,8)+struct.pack('<H',len(entries))+b''.join(struct.pack('<HHI',t,ty,n)+d for t,ty,n,d in entries)+struct.pack('<I',0)+payload+struct.pack('<'+'H'*len(pixels),*pixels)
    return result,pixels

lib=C.CDLL(str(root/'artifacts/decoder/SonyArw6.dll'),winmode=0x1100)
lib.arw6_open.argtypes=[C.c_void_p,C.c_uint64,C.POINTER(C.c_void_p),C.POINTER(Info),C.c_char_p,C.c_int]
lib.arw6_copy.argtypes=[C.c_void_p,C.POINTER(C.c_ushort),C.c_uint64]
lib.arw6_close.argtypes=[C.c_void_p]
def decode(data):
    buf=C.create_string_buffer(data);frame=C.c_void_p();info=Info();error=C.create_string_buffer(512)
    rc=lib.arw6_open(buf,len(data),C.byref(frame),C.byref(info),error,len(error))
    return rc,frame,info,error.value

for cfa,pattern in [('RGGB',(0,1,1,2)),('BGGR',(2,1,1,0)),('GRBG',(1,0,2,1)),('GBRG',(1,2,0,1))]:
    data,pixels=dng('Sony','ILCE-6700',pattern)
    (output/(cfa+'.dng')).write_bytes(data)
    rc,frame,info,error=decode(data)
    assert rc==0,(cfa,rc,error)
    try:
        assert info.cfa.decode()==cfa,(info.cfa,cfa)
        assert (info.width,info.height)==(96,80),(info.width,info.height)
        out=(C.c_ushort*(info.width*info.height))()
        assert lib.arw6_copy(frame,out,len(out))==0
        assert list(out)==pixels,'unaltered sensor codes in active plane'
        assert lib.arw6_copy(frame,out,len(out)-1)!=0,'short pixel buffer refused'
        print('PASS synthetic Sony Bayer',cfa,info.model.decode(),info.decoder.decode(),info.version.decode())
    finally:lib.arw6_close(frame)

# Synthetic metadata tests only; pinned LibRaw aligns odd crop origins to the next CFA boundary.
# Actual R6 geometry is independently checked against external RAW metadata.
data,pixels=dng('Sony','ILCE-7RM6',crop=(1,1,92,76))
rc,frame,info,error=decode(data)
assert rc==0,('R6 primary crop',rc,error)
try:
    assert (info.left,info.top,info.width,info.height)==(2,2,91,75),(info.left,info.top,info.width,info.height,info.cfa)
    assert info.cfa.decode()=='RGGB','CFA must follow the decoder-aligned active origin'
    out=(C.c_ushort*(91*75))();assert lib.arw6_copy(frame,out,len(out))==0
    expected=[pixels[y*96+x] for y in range(2,77) for x in range(2,93)]
    assert list(out)==expected,'crop must copy exact original sensor codes'
finally:lib.arw6_close(frame)
for crop in [None,(65535,0,92,76),(5,5,96,80)]:
    data,_=dng('Sony','ILCE-7RM6',crop=crop)
    rc,frame,info,error=decode(data)
    assert rc!=0 and not frame.value,('R6 missing/out-of-bounds crop must be refused',crop,rc,error)
print('PASS synthetic R6 primary crop, decoder-aligned crop CFA, exact pixels and invalid metadata rejection')

data,_=dng('Canon','SYNTHETIC')
rc,frame,info,error=decode(data)
assert rc!=0 and not frame.value,('non-Sony must be rejected',rc,error)
print('PASS non-Sony RAW refused:',error.decode())
for data in [b'x'*2048,b'II*\x00'+b'\x00'*2044,b'x'*16]:
    rc,frame,info,error=decode(data)
    assert rc!=0 and not frame.value,('corrupt RAW refused',rc,error)
print('PASS malformed/truncated RAW and short pixel buffers refused. Synthetic only; no camera/ARW validation.')

short=C.create_string_buffer(b'x'*16);sentinel=C.c_void_p(1);badinfo=Info();badinfo.abi=999;error=C.create_string_buffer(512)
rc=lib.arw6_open(short,16,C.byref(sentinel),C.byref(badinfo),error,len(error))
assert rc!=0 and not sentinel.value and badinfo.abi==0,'failed argument validation must clear output frame/info'
print('PASS invalid-buffer output pointers cleared')
