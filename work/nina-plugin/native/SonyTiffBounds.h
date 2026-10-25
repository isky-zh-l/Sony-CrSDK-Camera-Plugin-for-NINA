// MIT. Project-owned bounds check for the TIFF container used by tested Sony ARW6 bodies.
// LibRaw's tiled Sony LJPEG decoder can stop at a missing tile without an unpack error.
// Validate referenced ranges before unpack; this does not authenticate or repair camera files.
#pragma once
#include <cstdint>
#include <vector>
#include <unordered_set>
#include <stdexcept>

namespace sony_tiff {
class Bounds {
    const uint8_t* data; uint64_t size; bool little;
    [[noreturn]] static void bad() {throw std::runtime_error("Incomplete or malformed Sony RAW TIFF: referenced data exceeds the supplied file or the bounded directory contract");}
    void span(uint64_t offset,uint64_t bytes)const {if(offset>size||bytes>size-offset)bad();}
    uint64_t number(uint64_t offset,unsigned width)const {
        span(offset,width);uint64_t value=0;
        for(unsigned n=0;n<width;n++)value|=uint64_t(data[offset+n])<<(8*(little?n:width-n-1));return value;
    }
    struct Field {uint64_t count=0,offset=0;unsigned width=0,type=0;};
    uint64_t item(const Field& field,uint64_t index)const {
        if(index>=field.count||(field.type!=1&&field.type!=3&&field.type!=4&&field.type!=13&&field.type!=16&&field.type!=18))bad();
        return number(field.offset+index*field.width,field.width);
    }
    void ranges(const Field& offsets,const Field& lengths)const {
        if(!offsets.count&&!lengths.count)return;
        if(!offsets.count||offsets.count!=lengths.count||offsets.count>1000000)bad();
        for(uint64_t i=0;i<offsets.count;i++){auto n=item(lengths,i);if(n)span(item(offsets,i),n);}
    }
public:
    Bounds(const void* bytes,uint64_t count):data(static_cast<const uint8_t*>(bytes)),size(count),little(false) {
        span(0,8);little=data[0]=='I'&&data[1]=='I';
        if(!little&&!(data[0]=='M'&&data[1]=='M'))bad();if(number(2,2)!=42)bad();
    }
    void verify()const {
        std::vector<uint64_t> pending{number(4,4)};std::unordered_set<uint64_t> seen;uint64_t total=0;
        while(!pending.empty()) {
            auto directory=pending.back();pending.pop_back();if(!directory)continue;
            if(seen.count(directory))continue;
            if(seen.size()>=64)bad();seen.insert(directory);
            const uint64_t entries=number(directory,2);total+=entries;
            if(entries>4096||total>16384)bad();span(directory,2+entries*12+4);
            Field strips,stripBytes,tiles,tileBytes,jpeg,jpegBytes;
            for(uint64_t i=0;i<entries;i++) {
                const uint64_t at=directory+2+i*12;const auto tag=number(at,2),type=number(at+2,2),count=number(at+4,4);
                unsigned width=0;
                switch(type) {
                    case 1:case 2:case 6:case 7:width=1;break;
                    case 3:case 8:width=2;break;
                    case 4:case 9:case 11:case 13:width=4;break;
                    case 5:case 10:case 12:case 16:case 17:case 18:width=8;break;
                    default:bad();
                }
                if(count>size/width)bad();const uint64_t bytes=count*width;
                Field field{count,bytes<=4?at+8:number(at+8,4),width,unsigned(type)};
                span(field.offset,bytes);
                switch(tag) {
                    case 273:strips=field;break;case 279:stripBytes=field;break;
                    case 324:tiles=field;break;case 325:tileBytes=field;break;
                    case 513:jpeg=field;break;case 514:jpegBytes=field;break;
                    case 330:case 34665:case 34853:case 40965:
                        if(count>64)bad();
                        for(uint64_t j=0;j<count;j++)pending.push_back(item(field,j));break;
                    default:break;
                }
                if(pending.size()>256)bad();
            }
            ranges(strips,stripBytes);ranges(tiles,tileBytes);ranges(jpeg,jpegBytes);
            pending.push_back(number(directory+2+entries*12,4));
        }
    }
};
inline void verify(const void* data,uint64_t bytes){Bounds(data,bytes).verify();}
}
