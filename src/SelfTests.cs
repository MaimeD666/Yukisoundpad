using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace YukisPad
{
    internal static class SelfTests
    {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static int Run(string parent)
        {
            string root = Path.Combine(parent, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(root);
            var log = new List<string>(); int failed = 0;
            Action<string, Action> test = delegate(string name, Action action) { try { action(); log.Add("PASS " + name); } catch (Exception ex) { failed++; log.Add("FAIL " + name + ": " + ex); } };
            string fixture = Path.Combine(root, "fixture.wav");
            using (var writer = new BinaryWriter(File.Create(fixture)))
            {
                var format = WaveFormat.Pcm(44100, 2); PcmInfo.WriteHeader(writer, format, 44100 * 4);
                for (int i = 0; i < 44100; i++) { short sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 16000); writer.Write(sample); writer.Write(sample); }
            }
            test("WAV metadata and sample alignment", delegate { var i = PcmInfo.Open(fixture); Assert(i.Duration == 1 && i.Length == 176400 && i.Offset == 44, "Wrong WAV metadata"); Assert(i.FrameAt(.12345) % 4 == 0, "Unaligned trim"); });
            test("Exact trim, including PCM payload", delegate
            {
                string target = Path.Combine(root, "trim.wav"); PcmInfo.Export(fixture, target, .25, .75); var info = PcmInfo.Open(target); Assert(Math.Abs(info.Duration - .5) < .0001, "Wrong trim duration");
                byte[] input = File.ReadAllBytes(fixture), output = File.ReadAllBytes(target); Assert(output.Skip(44).SequenceEqual(input.Skip(44 + 44100).Take(88200)), "Trim data mismatch");
            });
            test("Waveform peaks are bounded and nonzero", delegate { var p = PcmInfo.Peaks(fixture, 240); Assert(p.Length == 240 && p.All(v => v > .4f && v < .5f), "Incorrect peaks"); });
            test("PCM gain preserves signed samples", delegate { byte[] b = { 0, 128, 255, 127, 0, 0 }; WinAudio.ApplyGain(b, b.Length, .5f); Assert(BitConverter.ToInt16(b, 0) == -16384 && BitConverter.ToInt16(b, 2) == 16383 && BitConverter.ToInt16(b, 4) == 0, "Gain failed"); });
            test("Invalid ranges rejected", delegate { Assert(!Store.ValidRange(-1, 1, 2) && !Store.ValidRange(1, 1, 2) && !Store.ValidRange(0, 4, 2) && !Store.ValidRange(Double.NaN, 1, 2) && !Store.ValidRange(0, Double.PositiveInfinity, 2), "Range accepted"); });
            test("Media Foundation decoding of PCM WAV", delegate
            {
                string decoded = Path.Combine(root, "decoded.wav"); var info = MediaDecoder.Decode(fixture, decoded, CancellationToken.None); Assert(Math.Abs(info.Duration - 1) < .001 && info.Format.Bits == 16, "Decoder format mismatch"); Assert(PcmInfo.Peaks(decoded, 80).Max() > .4f, "Decoder produced silence");
            });
            test("Cancelled decoding removes temporary file", delegate
            {
                string path = Path.Combine(root, "cancelled.wav"); bool cancelled = false;
                try { MediaDecoder.Decode(fixture, path, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !File.Exists(path), "Cancellation leaked output");
            });
            test("Media Foundation decodes MPEG-1 Layer III frames", delegate
            {
                // Valid silent MP3 frames: 128 kbps, 44.1 kHz, stereo, no reservoir.
                string mp3 = Path.Combine(root, "silence.mp3"); byte[] frame = new byte[417]; frame[0] = 0xff; frame[1] = 0xfb; frame[2] = 0x90;
                using (var f = File.Create(mp3)) for (int n = 0; n < 40; n++) f.Write(frame, 0, frame.Length);
                var decoded = System.Threading.Tasks.Task.Run(delegate { return MediaDecoder.Decode(mp3, Path.Combine(root, "mp3-decoded.wav"), CancellationToken.None); }).GetAwaiter().GetResult(); Assert(decoded.Duration > .9 && decoded.Duration < 1.2, "MP3 decoder duration");
            });
            test("Decoder never deletes a preexisting destination", delegate
            {
                string path = Path.Combine(root, "existing.wav"); File.WriteAllText(path, "keep me"); bool rejected = false;
                try { MediaDecoder.Decode(fixture, path, CancellationToken.None); } catch (IOException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == "keep me", "Existing destination modified");
            });
            test("Corrupt audio fails cleanly", delegate
            {
                string broken = Path.Combine(root, "broken.mp3"), decoded = Path.Combine(root, "broken.wav"); File.WriteAllText(broken, "not audio"); bool rejected = false;
                try { MediaDecoder.Decode(broken, decoded, CancellationToken.None); } catch { rejected = true; } Assert(rejected && !File.Exists(decoded), "Invalid media accepted");
            });
            test("Library persists Cyrillic, trim and hotkeys", delegate
            {
                string data = Path.Combine(root, "library"); var store = new Store(data); store.Data.Sounds.Add(new Sound { Id = "test", Name = "Привет, кенты", File = "test.wav", Collection = "Мемы", Start = .2, End = .8, Duration = 1, Gain = .75, Key = 65, Modifiers = 3 }); store.Save();
                var reloaded = new Store(data); var s = reloaded.Data.Sounds.Single(); Assert(s.Name == "Привет, кенты" && s.Start == .2 && s.Key == 65 && s.Gain == .75, "Serialization mismatch");
                reloaded.Save(); File.WriteAllText(Path.Combine(data, "library.json"), "broken"); var recovered = new Store(data); Assert(recovered.Data.Sounds.Count == 1 && recovered.Warning != null && Directory.GetFiles(data, "*.damaged-*").Length == 1, "Recovery failed");
            });
            test("Library path traversal rejected", delegate { var s = new Store(Path.Combine(root, "path")); bool rejected = false; try { s.AudioPath(new Sound { File = "..\\outside.wav" }); } catch (InvalidDataException) { rejected = true; } Assert(rejected, "Unsafe path accepted"); });
            test("Device enumeration", delegate { var outputs = WinAudio.Devices(false); var inputs = WinAudio.Devices(true); log.Add("INFO Outputs: " + string.Join(", ", outputs.Select(d => d.Name))); log.Add("INFO Inputs: " + string.Join(", ", inputs.Select(d => d.Name))); Assert(outputs.Count > 0, "No default entry"); if (outputs.Count > 1) Assert(WinAudio.Resolve("", false) >= 0, "Default output resolution failed"); });
            test("PCM playback buffer lifecycle (silence)", delegate
            {
                if (WinAudio.waveOutGetNumDevs() == 0) { log.Add("SKIP No output hardware"); return; }
                string silence = Path.Combine(root, "silence.wav"); using (var w = new BinaryWriter(File.Create(silence))) { var f = WaveFormat.Pcm(48000, 2); PcmInfo.WriteHeader(w, f, 38400); w.Write(new byte[38400]); }
                using (var p = new Playback(silence, -1, 0, .2, false, 0)) { var timeout = System.Diagnostics.Stopwatch.StartNew(); while (!p.Finished && timeout.ElapsedMilliseconds < 5000) Thread.Sleep(20); Assert(p.Finished, "Playback stalled"); if (p.Error != null) throw p.Error; }
                using (var p = new Playback(silence, -1, 0, .1, true, 0)) { Thread.Sleep(160); Assert(!p.Finished, "Loop ended early"); }
            });
            test("Microphone resampler preserves trim, pitch, stereo and looping", delegate
            {
                float[] samples = new float[48000];
                using (var clip = new ClipFrames(fixture, .25, .75, false))
                {
                    Assert(clip.Read(samples, 24000) == 24000 && clip.Read(new float[2], 1) == 0, "Wrong resampled duration");
                    for (int i = 20; i < 23980; i += 137)
                    {
                        float expected = (float)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 16000 / 32768);
                        Assert(Math.Abs(samples[i * 2] - expected) < .001 && samples[i * 2] == samples[i * 2 + 1], "Pitch or channel mismatch");
                    }
                }
                using (var clip = new ClipFrames(fixture, .25, .75, true))
                { clip.Read(samples, 24000); float[] again = new float[48000]; Assert(clip.Read(again, 24000) == 24000 && Math.Abs(again[200] - samples[200]) < .001, "Loop discontinuity"); }
            });
            test("Microphone transport publishes samples and stops cleanly", delegate
            {
                string path = Path.Combine(root, "bridge.bin");
                using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    f.SetLength(MicIntegration.SharedSize);
                    using (var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(f, null, MicIntegration.SharedSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
                    using (var view = map.CreateViewAccessor())
                    {
                        view.Write(0, 0x594B5044); view.Write(4, 1); view.Write(8, MicIntegration.Capacity); view.Write(12, MicIntegration.Rate);
                        var endpoint = new MicEndpoint { Id = Guid.NewGuid().ToString("B"), Installed = true, BridgePath = path };
                        using (var player = new MicPlayback(fixture, endpoint, .25, .75, false, .5f))
                        {
                            var timeout = System.Diagnostics.Stopwatch.StartNew();
                            while (view.ReadInt32(20) == 0 && !player.Finished && timeout.ElapsedMilliseconds < 2000) Thread.Sleep(5);
                            Assert(view.ReadInt32(20) == 1 && view.ReadInt64(32) >= 12000 && view.ReadSingle(64) == .5f, "Writer did not publish");
                            Assert(view.ReadInt64(48) == System.Diagnostics.Stopwatch.Frequency, "Clock mismatch");
                            Assert(Math.Abs(view.ReadSingle(4096 + 100 * 8)) > .1, "Published silence");
                        }
                        Assert(view.ReadInt32(20) == 0, "Dispose left injection enabled");
                    }
                }
            });
            test("Starter pack decodes, persists and respects deleted presets", delegate
            {
                var store = new Store(Path.Combine(root, "starters")); StarterSounds.Ensure(store);
                Assert(store.Data.Sounds.Count >= 5 && store.Data.Settings.StarterPackVersion == 1, "Starter pack missing");
                foreach (var sound in store.Data.Sounds) Assert(PcmInfo.Peaks(store.AudioPath(sound), 30).Max() > .01 && sound.Duration > .1, "Broken starter sound");
                store.Data.Sounds.RemoveAt(0); store.Save(); int count = store.Data.Sounds.Count;
                var reload = new Store(store.Root); StarterSounds.Ensure(reload); Assert(reload.Data.Sounds.Count == count, "Deleted preset restored unexpectedly");
                log.Add("INFO Starter durations: " + String.Join(", ", store.Data.Sounds.Select(s => s.Name + "=" + s.Duration.ToString("F2") + "s")));
            });
            log.Add("RESULT " + (failed == 0 ? "PASS" : "FAIL") + "; failures=" + failed);
            Directory.CreateDirectory(parent); File.WriteAllLines(Path.Combine(parent, "latest.txt"), log, Encoding.UTF8); return failed == 0 ? 0 : 1;
        }
    }
}
