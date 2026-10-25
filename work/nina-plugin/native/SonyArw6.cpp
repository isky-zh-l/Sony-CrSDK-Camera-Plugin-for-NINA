// MIT. Project-owned ABI shim; LibRaw is an external, dynamically linked dependency.
#include <libraw/libraw.h>
#include "SonyTiffBounds.h"
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
    if (result) *result=nullptr;
    if (info) *info={};
    if (!result || !info || !bytes || count < 1024 || count > 512ULL*1024*1024) {
        error(err, size, "Invalid RAW buffer or output pointer"); return -100003;
    }
    *result = nullptr;
    *info = {};
    try {
        auto f = std::make_unique<Frame>();
        int rc = f->raw.open_buffer(bytes, static_cast<size_t>(count));
        if (rc) { error(err, size, libraw_strerror(rc)); return rc; }
        auto& d = f->raw.imgdata;
        if (d.idata.maker_index != LIBRAW_CAMERAMAKER_Sony || d.idata.colors != 3 || !d.idata.filters)
            throw std::runtime_error("Requires a Sony single-plane Bayer RAW supported by the pinned LibRaw");
        libraw_decoder_info_t dec{};
        f->raw.get_decoder_info(&dec);
        if (!dec.decoder_name)
            throw std::runtime_error("LibRaw did not identify a RAW decoder");
        if (!d.idata.dng_version && (!strcmp(d.idata.model,"ILCE-7M5") || !strcmp(d.idata.model,"ILCE-7RM6")))
            sony_tiff::verify(bytes,count);
        rc = f->raw.unpack(); // No thumbnail, raw2image, dcraw_process, WB, gamma, or RGB fallback.
        if (rc) { error(err, size, libraw_strerror(rc)); return rc; }
        if (f->raw.error_count() || !d.rawdata.raw_image || d.rawdata.color3_image || d.rawdata.color4_image)
            throw std::runtime_error("RAW unpack error or non-single-plane output");
        auto& s = d.sizes; auto& i = f->info;
        i.abi = 1; i.rawWidth=s.raw_width; i.rawHeight=s.raw_height; i.pitch=s.raw_pitch;
        i.width=s.width; i.height=s.height; i.left=s.left_margin; i.top=s.top_margin;
        // Pinned LibRaw decodes ILCE-7RM6 but lacks its model-specific active-area table.
        // In particular, lossless files allocate 10240x7168 / 6656x4608 tiled buffers.
        // Use the camera's primary raw inset crop (TIFF DefaultCrop/Sony crop metadata),
        // not JPEG dimensions, an A7M5 fallback, aspect-ratio heuristics, or inset[1].
        // Do this in our bridge; the reviewed third-party source lock remains unchanged.
        if (!strcmp(d.idata.model,"ILCE-7RM6")) {
            const auto& crop=s.raw_inset_crops[0];
            if (crop.cleft==0xffff || crop.ctop==0xffff || !crop.cwidth || !crop.cheight ||
                uint64_t(crop.cleft)+crop.cwidth>s.raw_width ||
                uint64_t(crop.ctop)+crop.cheight>s.raw_height)
                throw std::runtime_error("ILCE-7RM6 requires valid primary RAW crop metadata; no padded-buffer fallback");
            i.left=crop.cleft; i.top=crop.ctop; i.width=crop.cwidth; i.height=crop.cheight;
        }
        i.rawBits=d.color.raw_bps; i.maximum=d.color.maximum; i.black=d.color.black;
        i.errors=f->raw.error_count();
        for(int c=0;c<4;c++) i.cblack[c]=d.color.cblack[c];
        if (!i.width || !i.height || i.width > 20000 || i.height > 20000 ||
            uint64_t(i.width)*i.height > 200000000 ||
            uint64_t(i.left)+i.width>i.rawWidth || uint64_t(i.top)+i.height>i.rawHeight ||
            i.pitch < uint64_t(i.rawWidth)*2 || i.rawBits==0 || i.rawBits>16 || !i.maximum || i.maximum>65535)
            throw std::runtime_error("Unsupported RAW geometry or numeric range");
        // COLOR uses LibRaw's active-image filter pattern, like raw2image's FC(row,col).
        for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
            const int cy=(y+int(i.top%2)+int(s.top_margin%2))%2;
            const int cx=(x+int(i.left%2)+int(s.left_margin%2))%2;
            i.cfa[y*2+x]=d.idata.cdesc[f->raw.COLOR(cy,cx)];
        }
        if (strcmp(i.cfa,"RGGB") && strcmp(i.cfa,"BGGR") && strcmp(i.cfa,"GRBG") && strcmp(i.cfa,"GBRG"))
            throw std::runtime_error("Requires a standard 2x2 Bayer CFA; no RGB, X-Trans or monochrome fallback");
        snprintf(i.model,sizeof(i.model),"%s",d.idata.model);
        snprintf(i.decoder,sizeof(i.decoder),"%s",dec.decoder_name);
        snprintf(i.version,sizeof(i.version),"%s",LibRaw::version());
        *info=i; *result=f.release(); return 0;
    } catch(const std::exception& ex) { error(err,size,ex.what()); return -100001; }
      catch(...) { error(err,size,"Native RAW exception"); return -100002; }
}
extern "C" __declspec(dllexport) int __cdecl arw6_copy(Frame* f, uint16_t* pixels, uint64_t count) {
    if (!f || !pixels || count != uint64_t(f->info.width)*f->info.height) return -1;
    auto& i=f->info;
    for(uint32_t y=0;y<i.height;y++) {
        auto row=reinterpret_cast<const uint16_t*>(reinterpret_cast<const uint8_t*>(f->raw.imgdata.rawdata.raw_image)+(y+i.top)*i.pitch);
        memcpy(pixels+uint64_t(y)*i.width,row+i.left,i.width*2);
    }
    return 0;
}
