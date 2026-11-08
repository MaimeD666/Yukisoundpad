using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace YukisPad
{
    internal sealed class MicEndpoint
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BridgePath { get; set; }
        public bool Installed { get; set; }
        public string DisplayName { get { return Name + (Installed ? "  · подключён" : ""); } }
        public override string ToString() { return DisplayName; }
    }
    internal static class MicIntegration
    {
        public const string Clsid = "{707a2e34-33cf-493d-b753-7271e57091ab}";
        public const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        public const string BridgeProperty = "{e8a3270d-c8aa-4994-8934-41d1e2229835},1";
        public const string OriginalProperty = "{e8a3270d-c8aa-4994-8934-41d1e2229835},2";
        public const string LegacyEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
        public const string StreamEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
        public const string EndpointEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        public const int Rate = 48000, Capacity = 131072, HeaderSize = 4096, SharedSize = HeaderSize + Capacity * 8;
        public static readonly string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YukisPad");
        public static readonly string SharedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YukisPad");
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        public static List<MicEndpoint> Endpoints()
        {
            var result = new List<MicEndpoint>();
            using (var root = Registry.LocalMachine.OpenSubKey(CaptureKey))
            {
                if (root == null) return result;
                foreach (string id in root.GetSubKeyNames())
                    using (var key = root.OpenSubKey(id))
                    {
                        if (Convert.ToInt32(key.GetValue("DeviceState", 0)) != 1) continue;
                        string name = "Микрофон";
                        using (var props = key.OpenSubKey("Properties"))
                        {
                            if (props != null) name = Convert.ToString(props.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2", "Микрофон")) + " (" + Convert.ToString(props.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6", "Audio")) + ")";
                        }
                        var endpoint = new MicEndpoint { Id = id, Name = name };
                        using (var fx = key.OpenSubKey("FxProperties"))
                            if (fx != null)
                            {
                                endpoint.BridgePath = fx.GetValue(BridgeProperty) as string;
                                endpoint.Installed = new[] { LegacyEffect, StreamEffect, EndpointEffect }.Any(slot => String.Equals(fx.GetValue(slot) as string, Clsid, StringComparison.OrdinalIgnoreCase));
                            }
                        result.Add(endpoint);
                    }
            }
            return result.OrderByDescending(e => e.Installed).ThenBy(e => e.Name).ToList();
        }
        public static MicEndpoint Selected(string id)
        {
            var endpoints = Endpoints();
            return String.IsNullOrEmpty(id) ? endpoints.FirstOrDefault(e => e.Installed) ?? endpoints.FirstOrDefault() : endpoints.FirstOrDefault(e => e.Id == id);
        }
        public static string SetupPath
        {
            get { string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YukisPadSetup.exe"); return File.Exists(local) ? local : Path.Combine(InstallRoot, "YukisPadSetup.exe"); }
        }
        public static Process RunSetup(string endpoint, bool remove)
        {
            if (!File.Exists(SetupPath)) throw new FileNotFoundException("Запусти YukisPadSetup.exe из дистрибутива, чтобы подключить микрофон.");
            Guid id; if (!Guid.TryParse(endpoint, out id)) throw new ArgumentException("Выбери микрофон.");
            return Process.Start(new ProcessStartInfo(SetupPath, (remove ? "--disconnect" : "--install") + " --endpoint=" + id.ToString("B") + " --quiet") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
    }

    internal interface IPlayback : IDisposable
    {
        float Volume { get; set; }
        bool Finished { get; }
        Exception Error { get; }
        double Duration { get; }
        double Position { get; }
    }

    internal sealed class MicPlayback : IPlayback
    {
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Thread thread;
        readonly double duration;
        readonly bool loop;
        readonly Stopwatch clock = new Stopwatch();
        volatile float volume;
        volatile bool finished;
        Exception error;
        public float Volume { get { return volume; } set { volume = value; } }
        public bool Finished { get { return finished; } }
        public Exception Error { get { return error; } }
        public double Duration { get { return duration; } }
        public double Position { get { double t = clock.Elapsed.TotalSeconds; return loop ? t % duration : Math.Min(t, duration); } }
        public MicPlayback(string path, MicEndpoint endpoint, double start, double end, bool repeat, float gain)
        {
            if (endpoint == null || !endpoint.Installed || String.IsNullOrEmpty(endpoint.BridgePath)) throw new IOException("Подключи микрофон в настройках Yuki’s Pad.");
            duration = end - start; loop = repeat; volume = gain;
            thread = new Thread(delegate()
            {
                bool ownsMutex = false; Mutex writer = null;
                try
                {
                    writer = new Mutex(false, "Local\\YukisPadMicWriter-" + endpoint.Id);
                    try { ownsMutex = writer.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) throw new IOException("Этот микрофон уже используется другим экземпляром Yuki’s Pad.");
                    using (var file = new FileStream(endpoint.BridgePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    using (var map = MemoryMappedFile.CreateFromFile(file, null, MicIntegration.SharedSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false))
                    using (var view = map.CreateViewAccessor(0, MicIntegration.SharedSize, MemoryMappedFileAccess.ReadWrite))
                    using (var clip = new ClipFrames(path, start, end, repeat))
                    {
                        if (view.ReadUInt32(0) != 0x594B5044 || view.ReadInt32(4) != 1 || view.ReadInt32(8) != MicIntegration.Capacity) throw new InvalidDataException("Неверный формат аудиокомпонента. Повтори установку.");
                        view.Write(20, 0); Thread.MemoryBarrier();
                        int generation = unchecked(view.ReadInt32(16) + 1);
                        view.Write(16, generation); view.Write(24, Process.GetCurrentProcess().Id); view.Write(32, 0L); view.Write(48, Stopwatch.Frequency); view.Write(64, volume);
                        long written = 0, startQpc = Stopwatch.GetTimestamp(); bool active = false, ended = false;
                        float[] block = new float[960];
                        try
                        {
                            clock.Start();
                            while (!stop.WaitOne(0))
                            {
                                long current = (long)(clock.Elapsed.TotalSeconds * MicIntegration.Rate);
                                long target = current + 12000;
                                while (!ended && written < target && !stop.WaitOne(0))
                                {
                                    int frames = clip.Read(block, 480); if (frames == 0) { ended = true; break; }
                                    int at = (int)(written % MicIntegration.Capacity), first = Math.Min(frames, MicIntegration.Capacity - at);
                                    view.WriteArray(MicIntegration.HeaderSize + at * 8, block, 0, first * 2);
                                    if (first < frames) view.WriteArray(MicIntegration.HeaderSize, block, first * 2, (frames - first) * 2);
                                    Thread.MemoryBarrier(); written += frames; view.Write(32, written);
                                }
                                view.Write(56, (long)MicIntegration.GetTickCount64()); view.Write(64, volume);
                                if (!active)
                                {
                                    startQpc = Stopwatch.GetTimestamp(); view.Write(40, startQpc); clock.Restart(); Thread.MemoryBarrier(); view.Write(20, 1); active = true;
                                }
                                if (ended && current >= written + 4800) break;
                                stop.WaitOne(10);
                            }
                        }
                        finally { view.Write(20, 0); Thread.MemoryBarrier(); clock.Stop(); }
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { if (ownsMutex) writer.ReleaseMutex(); if (writer != null) writer.Dispose(); finished = true; }
            });
            thread.IsBackground = true; thread.Name = "Microphone sound writer"; thread.Start();
        }
        public void Dispose() { stop.Set(); thread.Join(); stop.Dispose(); }
    }

    // Constant-memory linear conversion to the native mixer's 48 kHz stereo stream.
    internal sealed class ClipFrames : IDisposable
    {
        readonly FileStream stream;
        readonly BinaryReader reader;
        readonly PcmInfo info;
        readonly long first, frameCount;
        readonly bool loop;
        readonly double step;
        long currentFrame;
        double position;
        float left, right, nextLeft, nextRight;
        public ClipFrames(string path, double start, double end, bool repeat)
        {
            info = PcmInfo.Open(path); if (!Store.ValidRange(start, end, info.Duration)) throw new InvalidDataException("Неверные границы звука.");
            first = info.FrameAt(start); frameCount = (info.FrameAt(end) - first) / info.Format.BlockAlign;
            if (frameCount < 1) throw new InvalidDataException("Пустой фрагмент.");
            loop = repeat; step = info.Format.SampleRate / (double)MicIntegration.Rate;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768); reader = new BinaryReader(stream); stream.Position = info.Offset + first;
            ReadFrame(out left, out right); ReadFrame(out nextLeft, out nextRight);
        }
        void ReadFrame(out float l, out float r)
        {
            if (stream.Position >= info.Offset + first + frameCount * info.Format.BlockAlign)
            {
                if (loop) stream.Position = info.Offset + first;
                else { l = r = 0; return; }
            }
            l = reader.ReadInt16() / 32768f; r = info.Format.Channels == 2 ? reader.ReadInt16() / 32768f : l;
        }
        public int Read(float[] buffer, int frames)
        {
            int i = 0;
            for (; i < frames && (loop || position < frameCount); i++, position += step)
            {
                long desired = (long)position;
                while (currentFrame < desired) { left = nextLeft; right = nextRight; ReadFrame(out nextLeft, out nextRight); currentFrame++; }
                double fraction = position - currentFrame;
                buffer[i * 2] = (float)(left + (nextLeft - left) * fraction); buffer[i * 2 + 1] = (float)(right + (nextRight - right) * fraction);
            }
            return i;
        }
        public void Dispose() { reader.Dispose(); }
    }
}
