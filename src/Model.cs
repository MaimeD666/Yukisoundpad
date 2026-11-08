using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace YukisPad
{
    [DataContract]
    public sealed class Sound : INotifyPropertyChanged
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string File { get; set; }
        [DataMember] public string Collection { get; set; }
        [DataMember] public double Duration { get; set; }
        [DataMember] public double Start { get; set; }
        [DataMember] public double End { get; set; }
        [DataMember] public double Gain { get; set; }
        [DataMember] public bool Favorite { get; set; }
        [DataMember] public bool Loop { get; set; }
        [DataMember] public int Key { get; set; }
        [DataMember] public uint Modifiers { get; set; }
        [DataMember] public float[] Peaks { get; set; }
        public string LengthText { get { return FormatTime(End - Start); } }
        public string FavoriteIcon { get { return Favorite ? "★" : "☆"; } }
        public string LoopText { get { return Loop ? "↻ ПОВТОР" : "АУДИО"; } }
        public string HotkeyText { get { return Key == 0 ? "—" : Hotkeys.Label(Modifiers, Key); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(null)); }
        public static string FormatTime(double value) { return TimeSpan.FromSeconds(Math.Max(0, value)).ToString(value >= 3600 ? @"h\:mm\:ss" : @"m\:ss"); }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string Output { get; set; }
        [DataMember] public string Monitor { get; set; }
        [DataMember] public string Microphone { get; set; }
        [DataMember] public bool Monitoring { get; set; }
        [DataMember] public double Volume { get; set; }
        [DataMember] public double MicVolume { get; set; }
        [DataMember] public bool MinimizeToTray { get; set; }
        [DataMember] public string Endpoint { get; set; }
        [DataMember] public bool SendToMicrophone { get; set; }
        [DataMember] public int RoutingVersion { get; set; }
        [DataMember] public int StarterPackVersion { get; set; }
        public static Settings Defaults() { return new Settings { Volume = 0.8, MicVolume = 0.8, Monitoring = true, MinimizeToTray = true, SendToMicrophone = true, RoutingVersion = 1 }; }
    }

    [DataContract]
    public sealed class Library
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<Sound> Sounds { get; set; }
        [DataMember] public Settings Settings { get; set; }
        public Library() { Version = 1; Sounds = new List<Sound>(); Settings = Settings.Defaults(); }
    }

    public sealed class Store
    {
        public readonly string Root;
        public readonly string AudioRoot;
        public Library Data { get; private set; }
        public string Warning { get; private set; }
        public Store(string root)
        {
            Root = root; AudioRoot = Path.Combine(root, "audio"); Directory.CreateDirectory(AudioRoot);
            Data = new Library();
            string path = Path.Combine(Root, "library.json");
            if (System.IO.File.Exists(path))
            {
                try { Data = Read(path); }
                catch
                {
                    // Never overwrite a damaged library without keeping its original bytes.
                    System.IO.File.Copy(path, path + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                    try { Data = Read(path + ".bak"); Warning = "Библиотека восстановлена из резервной копии."; }
                    catch { Data = new Library(); Warning = "Не удалось прочитать библиотеку. Исходный файл сохранён рядом с library.json."; }
                }
            }
        }
        static Library Read(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Библиотека слишком велика.");
            using (var stream = System.IO.File.OpenRead(path))
            {
                var data = (Library)new DataContractJsonSerializer(typeof(Library)).ReadObject(stream);
                if (data == null || data.Version != 1 || data.Sounds == null || data.Settings == null) throw new InvalidDataException("Неверный формат библиотеки.");
                foreach (var s in data.Sounds)
                    if (s == null || String.IsNullOrEmpty(s.Id) || String.IsNullOrEmpty(s.Name) || s.Collection == null || !SafeFile(s.File) || !ValidRange(s.Start, s.End, s.Duration) || Double.IsNaN(s.Gain) || s.Gain < 0 || s.Gain > 1 || s.Key < 0 || s.Key > 255 || s.Modifiers > 15 || (s.Peaks != null && (s.Peaks.Length > 1024 || s.Peaks.Any(p => Single.IsNaN(p) || p < 0 || p > 1)))) throw new InvalidDataException("Повреждённая запись.");
                if (data.Sounds.Select(s => s.Id).Distinct().Count() != data.Sounds.Count) throw new InvalidDataException("Повторяющиеся записи.");
                data.Settings.Volume = Clamp(data.Settings.Volume);
                data.Settings.MicVolume = Clamp(data.Settings.MicVolume);
                if (data.Settings.RoutingVersion == 0) { data.Settings.RoutingVersion = 1; data.Settings.SendToMicrophone = true; }
                return data;
            }
        }
        static double Clamp(double v) { return Double.IsNaN(v) || Double.IsInfinity(v) ? 0.8 : Math.Max(0, Math.Min(1, v)); }
        public static bool ValidRange(double start, double end, double duration) { return !Double.IsNaN(start + end + duration) && !Double.IsInfinity(start + end + duration) && start >= 0 && end > start && end <= duration + 0.001; }
        static bool SafeFile(string file) { return !String.IsNullOrEmpty(file) && Path.GetFileName(file) == file && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0; }
        public string AudioPath(Sound sound) { if (!SafeFile(sound.File)) throw new InvalidDataException(); return Path.Combine(AudioRoot, sound.File); }
        public void Save()
        {
            string path = Path.Combine(Root, "library.json"), temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(Library)).WriteObject(stream, Data); stream.Flush(true); }
            if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, path + ".bak");
            else System.IO.File.Move(temp, path);
        }
    }
}

