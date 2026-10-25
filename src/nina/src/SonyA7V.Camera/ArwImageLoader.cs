using System.IO;
using System.Security.Cryptography;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace SonyA7V.Camera;

/// <summary>One owned frame. No camera, profile, diagnostic directory, queue or startup-hook dependency.</summary>
public sealed class ArwImageFrame : IExposureData {
    public IImageData ImageData { get; }
    public RawInfo RawInfo { get; }
    public string SourcePath { get; }
    public string SourceSha256 { get; }
    public int BitDepth => ImageData.Properties.BitDepth;
    public ImageMetaData MetaData => ImageData.MetaData;
    internal ArwImageFrame(IImageData image, RawInfo info, string path, string sha256) {
        ImageData=image; RawInfo=info; SourcePath=path; SourceSha256=sha256;
    }
    public Task<IImageData> ToImageData(IProgress<ApplicationStatus>? progress=default, CancellationToken cancelToken=default) {
        cancelToken.ThrowIfCancellationRequested();
        return Task.FromResult(ImageData);
    }
}

/// <summary>Reusable by a future DownloadExposure; caller must first verify its own successful CaptureSnapshot.</summary>
public sealed class ArwImageLoader(IImageDataFactory factory) {
    private readonly Lazy<PrivateDecoder> decoder=new(()=>new PrivateDecoder());
    public async Task<ArwImageFrame> LoadAsync(string input, CancellationToken token=default, bool attachOriginalRaw=true) {
        token.ThrowIfCancellationRequested();
        var path=Path.GetFullPath(input);
        using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(source.Length<1024 || source.Length>512L*1024*1024) throw new InvalidDataException("Unsupported input length");
        var bytes=new byte[(int)source.Length];
        await source.ReadExactlyAsync(bytes,token);
        var sha256=Convert.ToHexString(SHA256.HashData(bytes));
        var decoded=await Task.Run(()=>decoder.Value.Decode(bytes,token),token);
        token.ThrowIfCancellationRequested();
        var info=decoded.Info;
        if(!Enum.TryParse<BayerPatternEnum>(info.Cfa,out var bayer) || bayer==BayerPatternEnum.Auto || bayer==BayerPatternEnum.None)
            throw new InvalidDataException("Unsupported CFA: "+info.Cfa);
        if(decoded.Pixels.Max()>info.Maximum) throw new InvalidDataException("Pixel exceeds decoder maximum");
        // A fresh metadata graph per call. Future adapter adds only this capture's snapshot/times.
        var meta=new ImageMetaData();
        meta.Camera.Name=info.Model;
        meta.Camera.BayerPattern=bayer;
        meta.Camera.SensorType=Enum.Parse<SensorType>(info.Cfa);
        // Keep codec-expanded codes (up to 39002) in 16 bits; raw_bps=14 is recorded separately.
        // attachOriginalRaw=false lets NINA save its native FITS instead of the camera container.
        var array=attachOriginalRaw ? new ImageArray(decoded.Pixels,bytes,"arw") : new ImageArray(decoded.Pixels);
        token.ThrowIfCancellationRequested();
        IImageData data=factory.CreateBaseImageData(array,(int)info.Width,(int)info.Height,16,true,meta);
        token.ThrowIfCancellationRequested();
        if(data.Data.FlatArray.Length!=(long)info.Width*info.Height)
            throw new InvalidDataException("NINA image contract failed");
        if(attachOriginalRaw && (data.Data.RAWData is null || data.Data.RAWData.Length!=bytes.Length || Convert.ToHexString(SHA256.HashData(data.Data.RAWData))!=sha256))
            throw new InvalidDataException("NINA kept different original RAW bytes");
        return new ArwImageFrame(data,info,path,sha256);
    }
}

