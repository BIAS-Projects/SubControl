using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace SubControlMAUI.Services
{
    public static class FFmpegHelper
    {
        public static unsafe void ThrowIfError(this int error)
        {
            if (error < 0)
            {
                const int bufferSize = 1024;
                var buffer = stackalloc byte[bufferSize];

                ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);

                string message = Marshal.PtrToStringAnsi((IntPtr)buffer)!;
                throw new ApplicationException(message);
            }
        }
    }
}