using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace YukisPad
{
    internal static class WinAudio
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Caps { public ushort Mid, Pid; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
        [StructLayout(LayoutKind.Sequential)] internal struct Header { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] internal static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")] internal static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern int waveOutGetDevCapsW(UIntPtr id, out Caps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern int waveInGetDevCapsW(UIntPtr id, out Caps caps, uint size);
        [DllImport("winmm.dll")] internal static extern int waveOutOpen(out IntPtr handle, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] internal static extern int waveOutPrepareHeader(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveOutUnprepareHeader(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveOutWrite(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] internal static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutMessage(IntPtr h, uint message, out uint device, out uint flags);
        [DllImport("winmm.dll")] internal static extern int waveInOpen(out IntPtr handle, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] internal static extern int waveInPrepareHeader(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInUnprepareHeader(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInAddBuffer(IntPtr h, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] internal static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] internal static extern int waveInClose(IntPtr h);
        internal static readonly int HeaderSize = Marshal.SizeOf(typeof(Header));
        internal static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(Header), "Flags");
        internal static void Check(int result) { if (result != 0) throw new IOException("Аудиоустройство недоступно (код " + result + "). Проверьте подключение и выбранный выход."); }
        public static List<Device> Devices(bool input)
        {
            var list = new List<Device>(); uint count = input ? waveInGetNumDevs() : waveOutGetNumDevs();
            if (!input) list.Add(new Device { Id = -1, Name = "По умолчанию Windows", Token = "" });
            var occurrences = new Dictionary<string, int>();
            for (uint i = 0; i < count; i++)
            {
                Caps c; int result = input ? waveInGetDevCapsW(new UIntPtr(i), out c, (uint)Marshal.SizeOf(typeof(Caps))) : waveOutGetDevCapsW(new UIntPtr(i), out c, (uint)Marshal.SizeOf(typeof(Caps)));
                if (result != 0) continue;
                int n; occurrences.TryGetValue(c.Name, out n); occurrences[c.Name] = n + 1;
                list.Add(new Device { Id = (int)i, Name = c.Name + (n > 0 ? " #" + (n + 1) : ""), Token = c.Name + "|" + n });
            }
            return list;
        }
        public static int Resolve(string token, bool input)
        {
            if (!input && String.IsNullOrEmpty(token)) { uint id, flags; return waveOutMessage(new IntPtr(-1), 0x2015, out id, out flags) == 0 ? unchecked((int)id) : -1; }
            foreach (var d in Devices(input)) if (d.Token == token) return d.Id;
            throw new IOException(input ? "Выберите доступный микрофон в настройках." : "Выбранный аудиовыход отключён. Откройте настройки.");
        }
        internal static void ApplyGain(byte[] data, int count, float gain)
        {
            for (int i = 0; i < count - 1; i += 2) { short s = (short)((short)(data[i] | (data[i + 1] << 8)) * Math.Max(0, Math.Min(1, gain))); data[i] = (byte)s; data[i + 1] = (byte)(s >> 8); }
        }
    }
    public sealed class Device { public int Id; public string Name { get; set; } public string Token; public override string ToString() { return Name; } }

    internal sealed class AudioBuffer : IDisposable
    {
        public IntPtr Header, Data;
        public bool Busy;
        public AudioBuffer(int size)
        {
            Data = Marshal.AllocHGlobal(size); Header = Marshal.AllocHGlobal(WinAudio.HeaderSize);
            Marshal.StructureToPtr(new WinAudio.Header { Data = Data, Length = (uint)size }, Header, false);
        }
        public bool Done { get { return (Marshal.ReadInt32(Header, WinAudio.FlagsOffset) & 1) != 0; } }
        public void Dispose() { if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header); if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data); Header = Data = IntPtr.Zero; }
    }

    internal sealed class Output : IDisposable
    {
        IntPtr handle;
        readonly List<AudioBuffer> buffers = new List<AudioBuffer>();
        public readonly AutoResetEvent Ready = new AutoResetEvent(false);
        public Output(int device, WaveFormat format, int size)
        {
            try
            {
                WinAudio.Check(WinAudio.waveOutOpen(out handle, unchecked((uint)device), ref format, Ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000));
                for (int i = 0; i < 4; i++)
                {
                    var b = new AudioBuffer(size);
                    try { WinAudio.Check(WinAudio.waveOutPrepareHeader(handle, b.Header, WinAudio.HeaderSize)); buffers.Add(b); }
                    catch { b.Dispose(); throw; }
                }
            }
            catch { Dispose(); throw; }
        }
        public bool Write(byte[] data, int count)
        {
            foreach (var b in buffers)
            {
                if (b.Busy && !b.Done) continue;
                Marshal.Copy(data, 0, b.Data, count); Marshal.WriteInt32(b.Header, IntPtr.Size, count);
                WinAudio.Check(WinAudio.waveOutWrite(handle, b.Header, WinAudio.HeaderSize)); b.Busy = true; return true;
            }
            return false;
        }
        public bool IsEmpty { get { foreach (var b in buffers) if (b.Busy && !b.Done) return false; return true; } }
        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                WinAudio.waveOutReset(handle);
                foreach (var b in buffers) { WinAudio.waveOutUnprepareHeader(handle, b.Header, WinAudio.HeaderSize); b.Dispose(); }
                WinAudio.waveOutClose(handle); handle = IntPtr.Zero;
            }
            buffers.Clear(); Ready.Dispose();
        }
    }

    internal sealed class Playback : IPlayback
    {
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Thread thread;
        readonly Stopwatch clock = new Stopwatch();
        public volatile float Volume;
        public volatile bool Finished;
        public Exception Error;
        public readonly double Duration;
        readonly bool loop;
        float IPlayback.Volume { get { return Volume; } set { Volume = value; } }
        bool IPlayback.Finished { get { return Finished; } }
        Exception IPlayback.Error { get { return Error; } }
        double IPlayback.Duration { get { return Duration; } }
        public double Position { get { double t = Math.Max(0, clock.Elapsed.TotalSeconds - 0.08); return loop && Duration > 0 ? t % Duration : Math.Min(t, Duration); } }
        public Playback(string path, int device, double start, double end, bool repeat, float volume)
        {
            Duration = end - start; loop = repeat; Volume = volume;
            thread = new Thread(delegate()
            {
                try
                {
                    var info = PcmInfo.Open(path);
                    if (!Store.ValidRange(start, end, info.Duration)) throw new InvalidDataException("Неверная обрезка.");
                    long first = info.FrameAt(start), last = info.FrameAt(end);
                    int size = (int)(info.Format.SampleRate / 50) * info.Format.BlockAlign;
                    using (var output = new Output(device, info.Format, size))
                    using (var input = File.OpenRead(path))
                    {
                        byte[] bytes = new byte[size]; var waits = new WaitHandle[] { stop, output.Ready };
                        input.Position = info.Offset + first; clock.Start();
                        while (!stop.WaitOne(0))
                        {
                            long remaining = info.Offset + last - input.Position;
                            if (remaining <= 0)
                            {
                                if (repeat) { input.Position = info.Offset + first; continue; }
                                while (!output.IsEmpty && WaitHandle.WaitAny(waits, 1000) != 0) { }
                                break;
                            }
                            int n = input.Read(bytes, 0, (int)Math.Min(size, remaining)); if (n == 0) break;
                            WinAudio.ApplyGain(bytes, n, Volume);
                            while (!output.Write(bytes, n)) if (WaitHandle.WaitAny(waits, 1000) == 0) return;
                        }
                    }
                }
                catch (Exception ex) { Error = ex; }
                finally { clock.Stop(); Finished = true; }
            });
            thread.IsBackground = true; thread.Name = "Sound playback"; thread.Start();
        }
        public void Dispose() { stop.Set(); if (Thread.CurrentThread != thread) thread.Join(); stop.Dispose(); }
    }

}
