using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace YukisPad
{
    internal static class RouteTests
    {
        public static int Run(string root)
        {
            Directory.CreateDirectory(root);
            try
            {
                var endpoint=MicIntegration.Selected(null);
                if(endpoint==null||!endpoint.Installed)throw new IOException("Microphone component is not installed.");
                string fixture=Path.Combine(root,"route-tone.wav");
                using(var w=new BinaryWriter(File.Create(fixture)))
                {
                    PcmInfo.WriteHeader(w,WaveFormat.Pcm(44100,2),44100*4*2);
                    for(int i=0;i<44100*2;i++){short value=(short)(.125*32767*Math.Sin(i*2*Math.PI*997/44100));w.Write(value);w.Write(value);}
                }
                foreach(string mode in new[]{"observe","observe-communications","observe-raw","observe-raw-communications"})
                {
                    string report=Path.Combine(root,mode+".txt");if(File.Exists(report))File.Delete(report);
                    string probe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MicProbe.exe");
                    using(var process=Process.Start(new ProcessStartInfo(probe,"\""+endpoint.Id+"\" \""+endpoint.BridgePath+"\" \""+report+"\" "+mode){UseShellExecute=false,CreateNoWindow=true}))
                    {
                        try
                        {
                            var timeout=Stopwatch.StartNew();bool ready=false;
                            while(!process.HasExited&&timeout.ElapsedMilliseconds<8000)
                            {
                                if(File.Exists(report))using(var f=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var r=new StreamReader(f))ready=r.ReadToEnd().Contains("READY");
                                if(ready)break;Thread.Sleep(20);
                            }
                            if(!ready)throw new IOException("Capture did not become ready: "+report);
                            using(var player=new MicPlayback(fixture,endpoint,.25,1.75,false,1))
                            {
                                if(!process.WaitForExit(12000))throw new IOException("Capture timed out.");
                                if(player.Error!=null)throw player.Error;
                                if(process.ExitCode!=0)throw new IOException("Capture test failed: "+report);
                            }
                        }
                        finally{if(!process.HasExited){process.Kill();process.WaitForExit();}}
                    }
                }
                File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: Application PCM playback (44.1 kHz, stereo, trimmed) reached the physical microphone capture in Default, Communications, RAW and RAW Communications modes. No voice recordings saved.");return 0;
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.txt"),"FAIL: "+ex);return 1;}
        }
    }
}
