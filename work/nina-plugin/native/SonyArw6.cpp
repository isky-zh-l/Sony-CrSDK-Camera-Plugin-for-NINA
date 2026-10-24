// MIT. Project-owned ABI shim; LibRaw is an external, dynamically linked dependency.
#include <libraw/libraw.h>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <memory>
#include <stdexcept>

struct Info {
    uint32_t abi, rawWidth, rawHeight, pitch, width, height, left, top;
    uint32_t rawBits, maximum, black, errors;
    uint32_t cblack[4];
    char cfa[5], model[64], decoder[128], version[32];
};
struct Frame { LibRaw raw; Info info{}; };
static void error(char* out, int size, const char* msg) { if (out && size > 0) snprintf(out, size, "%s", msg); }
extern "C" __declspec(dllexport) void __cdecl arw6_close(Frame* f) { delete f; }
extern "C" __declspec(dllexport) int __cdecl arw6_open(void* bytes, uint64_t count, Frame** result, Info* info, char* err, int size) {
    *result = nullptr;
    try {
        auto f = std::make_unique<Frame>();
        int rc = f->raw.open_buffer(bytes, static_cast<size_t>(count));
        if (rc) { error(err, size, libraw_strerror(rc)); return rc; }
        auto& d = f->raw.imgdata;
        if (strcmp(d.idata.model, "ILCE-7M5") || d.idata.colors != 3 || !d.idata.filters)
            throw std::runtime_error("Only ILCE-7M5 single-plane Bayer is supported in stage 1");
        libraw_decoder_info_t dec{};
        f->raw.get_decoder_info(&dec);
        if (!dec.decoder_name || !strstr(dec.decoder_name, "arw6"))
            throw std::runtime_error("Not the required ARW6 RAW decoder");
        rc = f->raw.unpack(); // No thumbnail, raw2image, dcraw_process, WB, gamma, or RGB fallback.
        if (rc) { error(err, size, libraw_strerror(rc)); return rc; }
        if (f->raw.error_count() || !d.rawdata.raw_image || d.rawdata.color3_image || d.rawdata.color4_image)
            throw std::runtime_error("RAW unpack error or non-single-plane output");
        auto& s = d.sizes; auto& i = f->info;
        i.abi = 1; i.rawWidth=s.raw_width; i.rawHeight=s.raw_height; i.pitch=s.raw_pitch;
        i.width=s.width; i.height=s.height; i.left=s.left_margin; i.top=s.top_margin;
        i.rawBits=d.color.raw_bps; i.maximum=d.color.maximum; i.black=d.color.black;
        i.errors=f->raw.error_count();
        for(int c=0;c<4;c++) i.cblack[c]=d.color.cblack[c];
        if (!i.width || !i.height || i.width > 12000 || i.height > 12000 ||
            i.left+i.width>i.rawWidth || i.top+i.height>i.rawHeight ||
            i.pitch < i.rawWidth*2 || i.rawBits==0 || i.rawBits>16 || i.maximum>65535)
            throw std::runtime_error("Unsupported RAW geometry or numeric range");
        // COLOR uses LibRaw's active-image filter pattern, like raw2image's FC(row,col).
        for(int y=0;y<2;y++) for(int x=0;x<2;x++) i.cfa[y*2+x]=d.idata.cdesc[f->raw.COLOR(y,x)];
        snprintf(i.model,sizeof(i.model),"%s",d.idata.model);
        snprintf(i.decoder,sizeof(i.decoder),"%s",dec.decoder_name);
        snprintf(i.version,sizeof(i.version),"%s",LibRaw::version());
        *info=i; *result=f.release(); return 0;
    } catch(const std::exception& ex) { error(err,size,ex.what()); return -100001; }
      catch(...) { error(err,size,"Native RAW exception"); return -100002; }
}
extern "C" __declspec(dllexport) int __cdecl arw6_copy(Frame* f, uint16_t* pixels, uint64_t count) {
    if (!f || count != uint64_t(f->info.width)*f->info.height) return -1;
    auto& i=f->info;
    for(uint32_t y=0;y<i.height;y++) {
        auto row=reinterpret_cast<const uint16_t*>(reinterpret_cast<const uint8_t*>(f->raw.imgdata.rawdata.raw_image)+(y+i.top)*i.pitch);
        memcpy(pixels+uint64_t(y)*i.width,row+i.left,i.width*2);
    }
    return 0;
}
