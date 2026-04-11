using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace SubControlMAUI.Services
{
    public sealed class VideoFrame
    {
        public SKBitmap Bitmap { get; }
        public long Pts { get; }

        public VideoFrame(SKBitmap bitmap, long pts)
        {
            Bitmap = bitmap;
            Pts = pts;
        }
    }

    public sealed class CameraStream : IAsyncDisposable
    {
        private const int SWS_FAST_BILINEAR = 1;

        public string CameraId { get; }
        private readonly string _sdpPath;
        private readonly CancellationTokenSource _cts = new();
        private Task? _worker;

        private readonly object _frameLock = new();
        private VideoFrame? _latestFrame;

        // When true, a per-frame min-max histogram stretch is applied to the
        // luma channel of each decoded frame before publishing.  Enables full
        // dynamic range display for FLIR/thermal cameras whose H.264 stream
        // compresses the thermal range into a narrow luma band.
        private readonly bool _applyStretch;

        // Single reusable write buffer — never exposed outside this class.
        // A fresh copy is made for each published VideoFrame so callers own
        // their bitmap independently and can dispose it freely.
        private SKBitmap? _writeBuffer;

        public event Action<VideoFrame>? OnFrame;

        /// <param name="cameraId">Unique identifier for this stream.</param>
        /// <param name="host">RTP source host.</param>
        /// <param name="port">RTP source port.</param>
        /// <param name="applyStretch">
        /// Set true for FLIR/thermal cameras.  Applies a per-frame min-max
        /// histogram stretch to the luma channel of each decoded frame so the
        /// full thermal dynamic range is visible regardless of how compressed
        /// the H.264 stream's luma band is.
        /// </param>
        public CameraStream(string cameraId, string host, int port, bool applyStretch = true)
        {
            CameraId = cameraId;
            _applyStretch = applyStretch;

            string sdp =
                "v=0\r\n" +
                "o=- 0 0 IN IP4 127.0.0.1\r\n" +
                "s=WebCam\r\n" +
                $"c=IN IP4 {host}\r\n" +
                "t=0 0\r\n" +
                $"m=video {port} RTP/AVP 96\r\n" +
                "a=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 packetization-mode=1\r\n";

            _sdpPath = Path.Combine(Path.GetTempPath(), $"webcam_{cameraId}.sdp");
            File.WriteAllText(_sdpPath, sdp);
        }

        public Task StartAsync()
        {
            _worker ??= Task.Run(() => RunAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public VideoFrame? GetLatestFrame()
        {
            lock (_frameLock)
                return _latestFrame;
        }

        // Holds all FFmpeg native pointers so they can be shared between
        // the unsafe helper methods without living in the async method itself.
        private struct FfmpegState
        {
            public unsafe AVFormatContext* FmtCtx;
            public unsafe AVCodecContext* CodecCtx;
            public unsafe AVFrame* Frame;
            public unsafe AVPacket* Packet;
            public unsafe SwsContext* SwsCtx;
            public int VideoStreamIndex;
            public int Width;
            public int Height;
        }

        // ----------------------------------------------------------------
        // The async pump — contains NO unsafe code so await is legal here.
        // ----------------------------------------------------------------
        private async Task RunAsync(CancellationToken token)
        {
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "bin", "x64");
            ffmpeg.avformat_network_init();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);

            var state = new FfmpegState();

            try
            {
                OpenAndInitialise(ref state);

                while (!token.IsCancellationRequested)
                {
                    bool needsDelay = ReadAndDecode(ref state);

                    if (needsDelay)
                        await Task.Delay(1, token);
                }
            }

            finally
            {
                CleanUp(ref state);
                try { File.Delete(_sdpPath); } catch { }
                _writeBuffer?.Dispose();
                _writeBuffer = null;
            }
        }

        // ----------------------------------------------------------------
        // Unsafe helpers — non-async so they can freely use pointers.
        // ----------------------------------------------------------------

        private unsafe void OpenAndInitialise(ref FfmpegState s)
        {
            var inputFmt = ffmpeg.av_find_input_format("sdp");
            if (inputFmt == null)
                throw new Exception("FFmpeg 'sdp' demuxer not found.");

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "protocol_whitelist", "file,udp,rtp,crypto,data", 0);

            var fmtCtx = s.FmtCtx;
            ffmpeg.avformat_open_input(&fmtCtx, _sdpPath, inputFmt, &opts).ThrowIfError();
            s.FmtCtx = fmtCtx;
            ffmpeg.avformat_find_stream_info(s.FmtCtx, null).ThrowIfError();

            s.VideoStreamIndex = -1;
            for (int i = 0; i < (int)s.FmtCtx->nb_streams; i++)
            {
                if (s.FmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    s.VideoStreamIndex = i;
                    break;
                }
            }
            if (s.VideoStreamIndex == -1)
                throw new Exception("No video stream found.");

            var codecPar = s.FmtCtx->streams[s.VideoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new Exception("H.264 decoder not found.");

            s.CodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(s.CodecCtx, codecPar).ThrowIfError();
            ffmpeg.avcodec_open2(s.CodecCtx, codec, null).ThrowIfError();

            s.Frame = ffmpeg.av_frame_alloc();
            s.Packet = ffmpeg.av_packet_alloc();
        }

        /// <summary>
        /// Reads one packet and decodes it.
        /// Returns <c>true</c> when the caller should back off with a short delay.
        /// </summary>
        private unsafe bool ReadAndDecode(ref FfmpegState s)
        {
            int ret = ffmpeg.av_read_frame(s.FmtCtx, s.Packet);
            if (ret < 0)
            {
                ffmpeg.av_packet_unref(s.Packet);
                return true;
            }

            if (s.Packet->stream_index != s.VideoStreamIndex)
            {
                ffmpeg.av_packet_unref(s.Packet);
                return false;
            }

            DecodePacket(s.CodecCtx, s.Frame, s.Packet, ref s.SwsCtx, ref s.Width, ref s.Height);
            ffmpeg.av_packet_unref(s.Packet);
            return false;
        }

        private unsafe void CleanUp(ref FfmpegState s)
        {
            if (s.SwsCtx != null) { ffmpeg.sws_freeContext(s.SwsCtx); s.SwsCtx = null; }

            if (s.Frame != null)
            {
                var p = s.Frame;
                ffmpeg.av_frame_free(&p);
                s.Frame = p;
            }
            if (s.Packet != null)
            {
                var p = s.Packet;
                ffmpeg.av_packet_free(&p);
                s.Packet = p;
            }
            if (s.CodecCtx != null)
            {
                var p = s.CodecCtx;
                ffmpeg.avcodec_free_context(&p);
                s.CodecCtx = p;
            }
            if (s.FmtCtx != null)
            {
                var p = s.FmtCtx;
                ffmpeg.avformat_close_input(&p);
                s.FmtCtx = p;
            }
        }

        private unsafe void DecodePacket(
            AVCodecContext* codecCtx,
            AVFrame* frame,
            AVPacket* pkt,
            ref SwsContext* swsCtx,
            ref int width,
            ref int height)
        {
            ffmpeg.avcodec_send_packet(codecCtx, pkt);

            while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
            {
                int w = frame->width;
                int h = frame->height;
                var fmt = (AVPixelFormat)frame->format;

                if (w == 0 || h == 0 || fmt == AVPixelFormat.AV_PIX_FMT_NONE)
                    continue;

                if (w != width || h != height || swsCtx == null)
                {
                    if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
                    width = w;
                    height = h;

                    swsCtx = ffmpeg.sws_getContext(
                        w, h, fmt,
                        w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                        SWS_FAST_BILINEAR, null, null, null);

                    _writeBuffer?.Dispose();
                    _writeBuffer = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
                }

                // Scale the decoded frame into the reusable write buffer.
                using (var pixmap = _writeBuffer!.PeekPixels())
                {
                    var dstPtr = pixmap.GetPixels();

                    byte_ptrArray4 dstData = new byte_ptrArray4();
                    int_array4 dstLinesize = new int_array4();
                    dstData[0] = (byte*)dstPtr.ToPointer();
                    dstLinesize[0] = _writeBuffer.RowBytes;

                    ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, h, dstData, dstLinesize);
                }

                // For FLIR/thermal streams, stretch the luma range of the decoded
                // BGRA bitmap to 0-255 so the full thermal dynamic range is visible.
                // This is done in-place on _writeBuffer before the Copy() below so
                // there are no extra allocations beyond the one Copy() that was
                // already happening.
                if (_applyStretch)
                    StretchContrast(_writeBuffer!);

                // Publish a copy so the caller owns independent memory and can
                // dispose it freely without touching our internal write buffer.
                var publishedBitmap = _writeBuffer.Copy();

                lock (_frameLock)
                {
                    _latestFrame = new VideoFrame(publishedBitmap, frame->pts);
                    OnFrame?.Invoke(_latestFrame);
                }
            }
        }

        /// <summary>
        /// Performs a per-frame min-max histogram stretch on the BGRA bitmap
        /// in-place.  Finds the darkest and brightest pixel by luma approximation
        /// and linearly remaps all channels to span the full 0-255 range.
        ///
        /// Operating on BGRA rather than YUV means all three channels are
        /// stretched by the same factor, preserving hue — important when a LUT
        /// (e.g. Ironbow) is applied downstream by FlirVideoView.
        ///
        /// Cost: two passes over w*h*4 bytes.  For 640x512 BGRA that is ~2.5MB,
        /// taking ~0.3-0.5ms on a modern CPU — negligible at 30fps.
        /// No heap allocations are made; all work is done on the existing bitmap
        /// pixels via an unsafe pointer.
        /// </summary>
        private static unsafe void StretchContrast(SKBitmap bmp)
        {
            using var pixmap = bmp.PeekPixels();
            byte* px = (byte*)pixmap.GetPixels().ToPointer();
            int pixelCount = bmp.Width * bmp.Height;
            int byteCount = pixelCount * 4; // BGRA — 4 bytes per pixel

            // --- Pass 1: find min and max luma across all pixels ---
            // Luma approximation: Y ≈ (29*B + 150*G + 77*R) >> 8
            // Integer weights avoid float conversion in the hot loop.
            byte lumaMin = 255;
            byte lumaMax = 0;

            for (int i = 0; i < byteCount; i += 4)
            {
                byte luma = (byte)((px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8);
                if (luma < lumaMin) lumaMin = luma;
                if (luma > lumaMax) lumaMax = luma;
            }

            // Flat or near-flat frame — nothing useful to stretch.
            // Guard against division by zero and avoid amplifying pure noise.
            if (lumaMax - lumaMin < 8)
                return;

            // --- Pass 2: stretch each channel by the same scale factor ---
            // Using the same scale for B, G, R preserves the hue ratios so
            // downstream LUT palettes (Ironbow, Rainbow, etc.) map correctly.
            float scale = 255f / (lumaMax - lumaMin);

            for (int i = 0; i < byteCount; i += 4)
            {
                px[i] = ClampByte((px[i] - lumaMin) * scale); // B
                px[i + 1] = ClampByte((px[i + 1] - lumaMin) * scale); // G
                px[i + 2] = ClampByte((px[i + 2] - lumaMin) * scale); // R
                // px[i + 3] = alpha — leave untouched
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte ClampByte(float v)
            => v < 0f ? (byte)0 : v > 255f ? (byte)255 : (byte)v;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_worker != null) await _worker;
            _cts.Dispose();
            // _writeBuffer is disposed inside RunAsync's finally block
        }
    }
}