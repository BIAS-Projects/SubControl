using System.Buffers;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SubControlMAUI.Services;

/// <summary>
/// Decoded frame payload — raw BGRA pixels ready to blit into a SkiaSharp bitmap.
/// Rented from ArrayPool — MUST call Release() when the consumer is finished with it.
/// </summary>
public sealed class VideoFrame
{
    // Rented buffer — may be larger than Width * Height * 4
    public byte[] Data { get; init; } = [];

    // Actual number of valid bytes in Data (use this, not Data.Length)
    public int DataLength { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }

    // Return the rented buffer back to the pool when done rendering
    public void Release() => ArrayPool<byte>.Shared.Return(Data);
}

/// <summary>
/// Service interface for the RTSP frame decoder.
/// </summary>
public interface IRtspFrameDecoder : IDisposable
{
    event Action<VideoFrame>? FrameReady;
    event Action<string>? StatusChanged;
    event Action<string>? ErrorOccurred;
    bool IsRunning { get; }
    void Start(string rtspUrl);
    void Stop();
}

/// <summary>
/// Connects to an RTSP stream, decodes H.264 frames with FFmpeg and
/// raises FrameReady on every decoded frame.
///
/// GC pressure minimised by:
///   - Renting byte[] from ArrayPool instead of allocating per frame
///   - Single persistent unmanaged frameBuffer for sws_scale output
///   - No per-frame heap allocations in the hot path
/// </summary>
public sealed unsafe class RtspFrameDecoder : IRtspFrameDecoder
{
    // -----------------------------------------------------------------------
    // Public surface
    // -----------------------------------------------------------------------
    public event Action<VideoFrame>? FrameReady;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _running;

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------
    private Thread? _decodeThread;
    private volatile bool _running;
    private CancellationTokenSource _cts = new();

    // -----------------------------------------------------------------------
    // FFmpeg path initialisation — call once at startup from MauiProgram.cs
    // -----------------------------------------------------------------------
    public static void InitialiseFfmpeg(string? ffmpegPath = null)
    {
        if (ffmpegPath != null)
        {
            ffmpeg.RootPath = ffmpegPath;
            return;
        }

        var baseDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                   ?? AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "FFmpeg", "bin", "x64"),
            Path.Combine(baseDir, "FFmpeg", "bin"),
            Path.Combine(baseDir, "FFmpeg"),
            Path.Combine(baseDir, "ffmpeg", "bin", "x64"),
            Path.Combine(baseDir, "ffmpeg", "bin"),
            Path.Combine(baseDir, "ffmpeg"),
            baseDir,
        };

        System.Diagnostics.Debug.WriteLine("=== FFmpeg DLL search ===");
        foreach (var candidate in candidates)
        {
            var files = Directory.Exists(candidate)
                ? Directory.GetFiles(candidate, "av*.dll")
                : [];

            System.Diagnostics.Debug.WriteLine(
                $"  {candidate} — exists:{Directory.Exists(candidate)} av*.dll count:{files.Length}");

            if (files.Length > 0)
            {
                ffmpeg.RootPath = candidate;
                System.Diagnostics.Debug.WriteLine("  ^^^ SELECTED");
                return;
            }
        }

        System.Diagnostics.Debug.WriteLine("  No FFmpeg DLLs found in any candidate path.");
        ffmpeg.RootPath = baseDir;
    }

    // -----------------------------------------------------------------------
    // Start / Stop
    // -----------------------------------------------------------------------
    public void Start(string rtspUrl)
    {
        if (_running) Stop();

        _cts = new CancellationTokenSource();
        _running = true;

        _decodeThread = new Thread(() => DecodeLoop(rtspUrl, _cts.Token))
        {
            IsBackground = true,
            Name = "FFmpeg-RTSP-Decode",
            Priority = ThreadPriority.AboveNormal
        };
        _decodeThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        _decodeThread?.Join(TimeSpan.FromSeconds(3));
        _decodeThread = null;
    }

    // -----------------------------------------------------------------------
    // Core decode loop
    // -----------------------------------------------------------------------
    private void DecodeLoop(string url, CancellationToken ct)
    {
        const int SWS_FAST_BILINEAR = 1;

        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* swsCtx = null;
        byte_ptrArray4 dstData = default;
        int_array4 dstLinesize = default;
        IntPtr frameBuffer = IntPtr.Zero;
        int bufSize = 0;
        AVPixelFormat lastPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;

        try
        {
            RaiseStatus("Initialising FFmpeg…");

            // Verify DLLs loaded — throws "not supported" if missing
            try
            {
                uint ver = ffmpeg.avcodec_version();
                System.Diagnostics.Debug.WriteLine(
                    $"FFmpeg avcodec version: {ver >> 16}.{(ver >> 8) & 0xff}.{ver & 0xff}");
            }
            catch (Exception ex)
            {
                RaiseError(
                    $"FFmpeg native DLLs could not be loaded from '{ffmpeg.RootPath}'. " +
                    $"Details: {ex.Message}");
                return;
            }

            // ------------------------------------------------------------------
            // 1. Open input
            // ------------------------------------------------------------------
            fmtCtx = ffmpeg.avformat_alloc_context();
            if (fmtCtx == null) { RaiseError("Failed to allocate AVFormatContext."); return; }

            fmtCtx->probesize = 32 * 1024;
            fmtCtx->max_analyze_duration = 0;

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "buffer_size", "1048576", 0);
            ffmpeg.av_dict_set(&opts, "max_delay", "0", 0);
            ffmpeg.av_dict_set(&opts, "fflags", "nobuffer+discardcorrupt", 0);
            ffmpeg.av_dict_set(&opts, "flags", "low_delay", 0);
            ffmpeg.av_dict_set(&opts, "analyzeduration", "0", 0);
            ffmpeg.av_dict_set(&opts, "probesize", "32768", 0);
            ffmpeg.av_dict_set(&opts, "reorder_queue_size", "0", 0);
            ffmpeg.av_dict_set(&opts, "timeout", "3000000", 0);
            ffmpeg.av_dict_set(&opts, "stimeout", "3000000", 0);

            int ret = ffmpeg.avformat_open_input(&fmtCtx, url, null, &opts);
            ffmpeg.av_dict_free(&opts);

            if (ret < 0) { RaiseError($"Cannot open stream '{url}': {AvError(ret)}"); return; }

            // ------------------------------------------------------------------
            // 2. Stream info
            // ------------------------------------------------------------------
            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (ret < 0) { RaiseError($"Cannot find stream info: {AvError(ret)}"); return; }

            // ------------------------------------------------------------------
            // 3. Find video stream
            // ------------------------------------------------------------------
            int videoStreamIndex = -1;
            AVCodec* codec = null;

            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    codec = ffmpeg.avcodec_find_decoder(
                        fmtCtx->streams[i]->codecpar->codec_id);
                    break;
                }
            }

            if (videoStreamIndex == -1 || codec == null)
            {
                RaiseError("No video stream or decoder found.");
                return;
            }

            // ------------------------------------------------------------------
            // 4. Open codec
            // ------------------------------------------------------------------
            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) { RaiseError("Failed to allocate AVCodecContext."); return; }

            ret = ffmpeg.avcodec_parameters_to_context(
                codecCtx, fmtCtx->streams[videoStreamIndex]->codecpar);
            if (ret < 0) { RaiseError($"Failed to copy codec parameters: {AvError(ret)}"); return; }

            codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            codecCtx->skip_loop_filter = AVDiscard.AVDISCARD_NONREF;
            codecCtx->skip_frame = AVDiscard.AVDISCARD_DEFAULT;
            codecCtx->skip_idct = AVDiscard.AVDISCARD_NONREF;
            codecCtx->thread_count = Math.Max(1, Environment.ProcessorCount / 2);
            codecCtx->thread_type = ffmpeg.FF_THREAD_FRAME;

            AVDictionary* codecOpts = null;
            ret = ffmpeg.avcodec_open2(codecCtx, codec, &codecOpts);
            ffmpeg.av_dict_free(&codecOpts);
            if (ret < 0) { RaiseError($"Cannot open codec: {AvError(ret)}"); return; }

            // ------------------------------------------------------------------
            // 5. Allocate frames and packet
            // ------------------------------------------------------------------
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            if (frame == null || packet == null)
            {
                RaiseError("Failed to allocate AVFrame or AVPacket.");
                return;
            }

            int w = codecCtx->width;
            int h = codecCtx->height;

            if (w <= 0 || h <= 0) { RaiseError($"Invalid frame dimensions: {w}x{h}"); return; }

            // Single persistent unmanaged buffer — sws_scale writes here,
            // then we copy into a rented managed buffer for the consumer
            bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);
            frameBuffer = Marshal.AllocHGlobal(bufSize);

            ffmpeg.av_image_fill_arrays(
                ref dstData, ref dstLinesize,
                (byte*)frameBuffer,
                AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);

            // ------------------------------------------------------------------
            // 6. Colour-space converter
            // ------------------------------------------------------------------
            lastPixFmt = codecCtx->pix_fmt;
            swsCtx = ffmpeg.sws_getContext(
                w, h, lastPixFmt,
                w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                SWS_FAST_BILINEAR, null, null, null);

            if (swsCtx == null) { RaiseError("Cannot create sws context."); return; }

            RaiseStatus("● Live");

            // ------------------------------------------------------------------
            // 7. Read / decode loop — zero heap allocations except ArrayPool rent
            // ------------------------------------------------------------------
            while (!ct.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, packet);

                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    RaiseStatus("Reconnecting…");
                    break;
                }

                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                ret = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);

                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;

                while (!ct.IsCancellationRequested)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecCtx, frame);

                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;

                    // Rebuild sws context if pixel format changed.
                    // Create new context first — only free old one on success
                    // so we can keep streaming if sws_getContext fails.
                    var framePix = (AVPixelFormat)frame->format;
                    if (framePix != lastPixFmt && framePix != AVPixelFormat.AV_PIX_FMT_NONE)
                    {
                        var newSwsCtx = ffmpeg.sws_getContext(
                            w, h, framePix,
                            w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                            SWS_FAST_BILINEAR, null, null, null);

                        if (newSwsCtx != null)
                        {
                            ffmpeg.sws_freeContext(swsCtx);
                            swsCtx = newSwsCtx;
                            lastPixFmt = framePix;
                        }
                        // If null: keep old swsCtx, don't update lastPixFmt — retries next frame
                    }

                    // Scale into the persistent unmanaged buffer
                    ffmpeg.sws_scale(
                        swsCtx,
                        frame->data, frame->linesize, 0, h,
                        dstData, dstLinesize);

                    // Rent from pool — replaces 'new byte[bufSize]'
                    var rented = ArrayPool<byte>.Shared.Rent(bufSize);
                    Marshal.Copy(frameBuffer, rented, 0, bufSize);

                    // Fix: if no subscribers, return buffer immediately rather than leaking it
                    if (FrameReady is { } handler)
                    {
                        try
                        {
                            handler.Invoke(new VideoFrame
                            {
                                Data = rented,
                                DataLength = bufSize,
                                Width = w,
                                Height = h,
                                Stride = dstLinesize[0]
                            });
                        }
                        catch
                        {
                            // Fix: if consumer throws before storing the frame, return buffer
                            ArrayPool<byte>.Shared.Return(rented);
                            throw;
                        }
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RaiseError($"Decoder exception: {ex.Message}");
        }
        finally
        {
            if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; ffmpeg.avcodec_free_context(&c); }
            if (fmtCtx != null) { var f = fmtCtx; ffmpeg.avformat_close_input(&f); }
            if (frameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(frameBuffer);

            _running = false;
            RaiseStatus("Disconnected");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private void RaiseStatus(string msg) => StatusChanged?.Invoke(msg);
    private void RaiseError(string msg) => ErrorOccurred?.Invoke(msg);

    private static string AvError(int code)
    {
        var buf = new byte[1024];
        fixed (byte* b = buf)
            ffmpeg.av_strerror(code, b, (ulong)buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    public void Dispose() => Stop();
}