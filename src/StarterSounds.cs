using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
namespace YukisPad
{
    [DataContract] internal sealed class StarterEntry
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string File { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Collection { get; set; }
        [DataMember] public string Source { get; set; }
        [DataMember] public double Gain { get; set; }
    }
    internal static class StarterSounds
    {
        public static void MigratePortable(string oldRoot,string target)
        {
            if(File.Exists(Path.Combine(target,"library.json")) || !File.Exists(Path.Combine(oldRoot,"library.json")))return;
            var old=new Store(oldRoot);if(old.Warning!=null)return;
            Directory.CreateDirectory(Path.Combine(target,"audio"));
            foreach(var sound in old.Data.Sounds)
            {
                string source=old.AudioPath(sound),destination=Path.Combine(target,"audio",sound.File);
                if(File.Exists(source)&&!File.Exists(destination))File.Copy(source,destination);
            }
            File.Copy(Path.Combine(oldRoot,"library.json"),Path.Combine(target,"library.json"),false);
        }
        public static void Ensure(Store store)
        {
            if(store.Data.Settings.StarterPackVersion>=1)return;
            string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"starter-sounds");
            if(!Directory.Exists(source))source=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","starter-sounds"));
            string manifest=Path.Combine(source,"manifest.json");if(!File.Exists(manifest))return;
            StarterEntry[] entries;
            using(var reader=new StreamReader(manifest,true))
            using(var data=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd())))
                entries=(StarterEntry[])new DataContractJsonSerializer(typeof(StarterEntry[])).ReadObject(data);
            foreach(var e in entries)
            {
                string id="starter-"+e.Id;
                if(store.Data.Sounds.Any(s=>s.Id==id))continue;
                if(Path.GetFileName(e.File)!=e.File || String.IsNullOrEmpty(e.Id) || e.Id.Any(c=>!Char.IsLetterOrDigit(c)&&c!='-'))throw new InvalidDataException("Некорректный стартовый набор.");
                string output=Path.Combine(store.AudioRoot,id+".wav");
                PcmInfo info=File.Exists(output)?PcmInfo.Open(output):MediaDecoder.Decode(Path.Combine(source,e.File),output,CancellationToken.None);
                store.Data.Sounds.Add(new Sound { Id=id,Name=e.Name,File=Path.GetFileName(output),Collection=e.Collection,Duration=info.Duration,Start=0,End=info.Duration,Gain=Math.Max(0,Math.Min(1,e.Gain)),Peaks=PcmInfo.Peaks(output,240) });
                store.Save();
            }
            store.Data.Settings.StarterPackVersion=1;store.Save();
        }
    }
}

