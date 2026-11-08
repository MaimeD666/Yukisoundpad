using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace YukisPad
{
    // Windows Media Foundation. Vtable slots follow mfobjects.h and mfreadwrite.h.
    // Only the methods needed for decoding are bound; every COM reference is released.
    internal static class MediaDecoder
    {
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateWaveFormatExFromMFMediaType(IntPtr type, out IntPtr format, out int size, int flags);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetGuid(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetUInt(IntPtr self, ref Guid key, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SelectStream(IntPtr self, int stream, int selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetType(IntPtr self, int stream, IntPtr reserved, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMediaType(IntPtr self, int stream, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReadSample(IntPtr self, int stream, int control, out int actual, out int flags, out long time, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBuffer(IntPtr self, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LockBuffer(IntPtr self, out IntPtr bytes, out int max, out int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnlockBuffer(IntPtr self);
        static T Method<T>(IntPtr obj, int slot) { return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)); }
        static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; } }
        static void Attribute(IntPtr type, string key, string value) { Guid k = new Guid(key), v = new Guid(value); Check(Method<SetGuid>(type, 24)(type, ref k, ref v)); }

        public static PcmInfo Decode(string source, string destination, CancellationToken token)
        {
            IntPtr reader = IntPtr.Zero, type = IntPtr.Zero, current = IntPtr.Zero;
            bool ownsDestination = false;
            Check(MFStartup(0x20070, 1));
            try
            {
                Check(MFCreateSourceReaderFromURL(source, IntPtr.Zero, out reader));
                Check(Method<SelectStream>(reader, 4)(reader, unchecked((int)0xfffffffe), 0));
                const int audio = unchecked((int)0xfffffffd);
                Check(Method<SelectStream>(reader, 4)(reader, audio, 1));
                Check(MFCreateMediaType(out type));
                Attribute(type, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", "73647561-0000-0010-8000-00aa00389b71");
                Attribute(type, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", "00000001-0000-0010-8000-00aa00389b71");
                Guid bitsKey = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
                Check(Method<SetUInt>(type, 21)(type, ref bitsKey, 16));
                Check(Method<SetType>(reader, 7)(reader, audio, IntPtr.Zero, type));
                Check(Method<GetMediaType>(reader, 6)(reader, audio, out current));
                IntPtr wave; int size; Check(MFCreateWaveFormatExFromMFMediaType(current, out wave, out size, 0));
                WaveFormat format;
                try { format = (WaveFormat)Marshal.PtrToStructure(wave, typeof(WaveFormat)); } finally { Marshal.FreeCoTaskMem(wave); }
                if (format.Bits != 16 || format.Channels < 1 || format.Channels > 2 || format.SampleRate < 8000 || format.SampleRate > 192000)
                    throw new InvalidDataException("Нужен моно- или стереофайл с частотой 8–192 кГц.");
                long total = 0;
                using (var writer = new BinaryWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write)))
                {
                    ownsDestination = true;
                    PcmInfo.WriteHeader(writer, format, 0);
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int actual, flags; long timestamp; IntPtr sample;
                        Check(Method<ReadSample>(reader, 9)(reader, audio, 0, out actual, out flags, out timestamp, out sample));
                        try
                        {
                            if ((flags & 0x20) != 0) throw new InvalidDataException("Формат меняется внутри файла.");
                            if (sample != IntPtr.Zero)
                            {
                                IntPtr buffer; Check(Method<GetBuffer>(sample, 41)(sample, out buffer));
                                try
                                {
                                    IntPtr bytes; int max, length;
                                    Check(Method<LockBuffer>(buffer, 3)(buffer, out bytes, out max, out length));
                                    try
                                    {
                                        byte[] block = new byte[length]; Marshal.Copy(bytes, block, 0, length); writer.Write(block); total += length;
                                        if (total > format.BytesPerSecond * 1800L) throw new InvalidDataException("Максимальная длительность звука — 30 минут.");
                                    }
                                    finally { Check(Method<UnlockBuffer>(buffer, 4)(buffer)); }
                                }
                                finally { Release(ref buffer); }
                            }
                        }
                        finally { Release(ref sample); }
                        if ((flags & 2) != 0) break;
                    }
                    if (total < format.BlockAlign) throw new InvalidDataException("В файле нет аудио.");
                    writer.BaseStream.Position = 0; PcmInfo.WriteHeader(writer, format, (int)total);
                }
                return PcmInfo.Open(destination);
            }
            catch { if (ownsDestination && File.Exists(destination)) File.Delete(destination); throw; }
            finally { Release(ref current); Release(ref type); Release(ref reader); MFShutdown(); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SampleRate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
        public static WaveFormat Pcm(int rate, int channels) { return new WaveFormat { Tag = 1, Channels = (ushort)channels, SampleRate = (uint)rate, BytesPerSecond = (uint)(rate * channels * 2), BlockAlign = (ushort)(channels * 2), Bits = 16 }; }
    }

    public sealed class PcmInfo
    {
        public WaveFormat Format;
        public long Offset, Length;
        public double Duration { get { return Length / (double)Format.BytesPerSecond; } }
        public static PcmInfo Open(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Неверный WAV.");
                r.ReadUInt32(); if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException();
                WaveFormat format = new WaveFormat();
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    string id = new string(r.ReadChars(4)); uint len = r.ReadUInt32(); long next = r.BaseStream.Position + len + (len % 2);
                    if (next > r.BaseStream.Length) throw new InvalidDataException("Неполный WAV.");
                    if (id == "fmt ")
                    {
                        if (len < 16) throw new InvalidDataException();
                        format.Tag = r.ReadUInt16(); format.Channels = r.ReadUInt16(); format.SampleRate = r.ReadUInt32(); format.BytesPerSecond = r.ReadUInt32(); format.BlockAlign = r.ReadUInt16(); format.Bits = r.ReadUInt16();
                    }
                    if (id == "data")
                    {
                        if (format.Tag != 1 || format.Bits != 16 || format.Channels < 1 || format.Channels > 2 || format.SampleRate < 8000 || format.SampleRate > 192000 || format.BlockAlign != format.Channels * 2 || format.BytesPerSecond != format.SampleRate * format.BlockAlign || len % format.BlockAlign != 0) throw new InvalidDataException("Неподдерживаемый PCM.");
                        return new PcmInfo { Format = format, Offset = r.BaseStream.Position, Length = len };
                    }
                    r.BaseStream.Position = next;
                }
                throw new InvalidDataException("Аудиоданные не найдены.");
            }
        }
        public static void WriteHeader(BinaryWriter w, WaveFormat f, int bytes)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(bytes + 36); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((ushort)1); w.Write(f.Channels); w.Write(f.SampleRate); w.Write(f.BytesPerSecond); w.Write(f.BlockAlign); w.Write(f.Bits);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        }
        public long FrameAt(double seconds) { return Math.Max(0, Math.Min(Length, (long)(seconds * Format.SampleRate) * Format.BlockAlign)); }
        public static float[] Peaks(string path, int count)
        { return Peaks(path, count, CancellationToken.None); }
        public static float[] Peaks(string path, int count, CancellationToken token)
        {
            if (count < 1) throw new ArgumentOutOfRangeException("count");
            var info = Open(path); var peaks = new float[count]; long frames = info.Length / info.Format.BlockAlign;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                r.BaseStream.Position = info.Offset;
                byte[] chunk = new byte[32768]; long index = 0; int n;
                while ((n = r.Read(chunk, 0, (int)Math.Min(chunk.Length, info.Length - index * info.Format.BlockAlign))) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i + info.Format.BlockAlign <= n; i += info.Format.BlockAlign, index++)
                    {
                        int bucket = (int)Math.Min(count - 1, index * count / Math.Max(1, frames));
                        for (int c = 0; c < info.Format.Channels; c++) peaks[bucket] = Math.Max(peaks[bucket], Math.Abs((int)BitConverter.ToInt16(chunk, i + c * 2)) / 32768f);
                    }
                }
            }
            return peaks;
        }
        public static void Export(string source, string destination, double start, double end)
        {
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Нельзя заменить исходный звук экспортом.");
            var info = Open(source); if (!Store.ValidRange(start, end, info.Duration)) throw new ArgumentException("Неверные границы.");
            long first = info.FrameAt(start), length = info.FrameAt(end) - first;
            using (var input = File.OpenRead(source))
            using (var output = new BinaryWriter(File.Create(destination)))
            {
                WriteHeader(output, info.Format, checked((int)length)); input.Position = info.Offset + first;
                byte[] block = new byte[32768];
                while (length > 0) { int n = input.Read(block, 0, (int)Math.Min(block.Length, length)); if (n == 0) throw new EndOfStreamException(); output.Write(block, 0, n); length -= n; }
            }
        }
    }
}
