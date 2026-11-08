using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace YukisPad
{
    [DataContract] internal sealed class RegistryValueBackup
    {
        [DataMember] public string Name;
        [DataMember] public int Kind;
        [DataMember] public string Text;
        [DataMember] public string[] Strings;
        [DataMember] public byte[] Bytes;
        [DataMember] public long Number;
        public static RegistryValueBackup Read(RegistryKey key, string name)
        {
            var b = new RegistryValueBackup { Name = name, Kind = -1 };
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return b;
            b.Kind = (int)key.GetValueKind(name); object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is string) b.Text = (string)value; else if (value is string[]) b.Strings = (string[])value; else if (value is byte[]) b.Bytes = (byte[])value; else b.Number = Convert.ToInt64(value);
            return b;
        }
        public void Restore(RegistryKey key)
        {
            if (Kind < 0) { key.DeleteValue(Name, false); return; }
            object value = Text != null ? (object)Text : Strings != null ? (object)Strings : Bytes != null ? (object)Bytes : Kind == (int)RegistryValueKind.DWord ? (object)(int)Number : Number;
            key.SetValue(Name, value, (RegistryValueKind)Kind);
        }
    }
    [DataContract] internal sealed class EndpointBackup
    {
        [DataMember] public string Endpoint;
        [DataMember] public string OwnerSid;
        [DataMember] public bool FxExisted;
        [DataMember] public string Slot;
        [DataMember] public List<RegistryValueBackup> Values;
    }
    internal static class SetupProgram
    {
        const string Disabled = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        const string Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
        const string StreamModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
        const string FxTitle = "{b725f130-47ef-101a-a5f1-02608c9eebac},10";
        static string Root { get { return MicIntegration.SharedRoot; } }
        static SecurityIdentifier AudioServiceSid { get { return (SecurityIdentifier)new NTAccount("NT SERVICE", "Audiosrv").Translate(typeof(SecurityIdentifier)); } }
        static string BackupPath(string id) { return Path.Combine(Root, "backups", id + ".json"); }
        static string BridgePath(string id) { return Path.Combine(Root, "bridges", id + ".bin"); }
        static void Log(string text) { File.AppendAllText(Path.Combine(Root, "setup.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); }

        [STAThread] static int Main(string[] args)
        {
            bool quiet = args.Contains("--quiet");
            try
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Установщику нужны права администратора.");
                EnsureDirectories(); EnablePrivilege("SeBackupPrivilege"); EnablePrivilege("SeRestorePrivilege");
                if (args.Contains("--uninstall")) { Uninstall(); if (!quiet) MessageBox.Show("Компонент отключён. Библиотека звуков сохранена.", "Yuki’s Pad"); return 0; }
                string raw = args.FirstOrDefault(s => s.StartsWith("--endpoint="));
                if (raw == null) return ShowInstaller();
                Guid id; if (!Guid.TryParse(raw.Substring(11), out id)) throw new ArgumentException("Неверный идентификатор микрофона.");
                if (args.Contains("--test-lifecycle")) { TestLifecycle(id.ToString("B")); return 0; }
                if (args.Contains("--disconnect")) Disconnect(id.ToString("B"), true);
                else Install(id.ToString("B"), !args.Contains("--skip-probe"));
                return 0;
            }
            catch (Exception ex)
            {
                try { Log("FAIL " + ex); } catch { }
                if (!quiet) MessageBox.Show(ex.Message, "Yuki’s Pad — установка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        static int ShowInstaller()
        {
            Application.EnableVisualStyles();
            using (var form = new Form { Text = "Установка Yuki’s Pad", ClientSize = new Size(460, 202), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, Font = new Font("Segoe UI", 9) })
            {
                var title = new Label { Text = "Микрофон:", Left = 12, Top = 12, Width = 436, Height = 20 };
                var combo = new ComboBox { Left = 12, Top = 36, Width = 436, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "DisplayName", DataSource = MicIntegration.Endpoints() };
                var text = new Label { Text = "Выберите микрофон, используемый в Discord. Во время установки звук Windows будет кратко перезапущен.", Left = 12, Top = 76, Width = 436, Height = 72 };
                var button = new Button { Text = "Установить и проверить", Left = 274, Top = 162, Width = 174, Height = 28 };
                var cancel = new Button { Text = "Отмена", Left = 188, Top = 162, Width = 80, Height = 28 };
                bool working = false;
                form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
                cancel.Click += delegate { form.Close(); };
                button.Click += async delegate
                {
                    var endpoint = combo.SelectedItem as MicEndpoint; if (endpoint == null) return;
                    working = true; button.Enabled = false; cancel.Enabled = false; combo.Enabled = false; text.Text = "Установка и проверка микрофона…";
                    try { await System.Threading.Tasks.Task.Run(delegate { Install(endpoint.Id, true); }); working = false; MessageBox.Show(form, "Установлено. В Discord выберите этот же микрофон. Приложение доступно в меню «Пуск».", "Yuki’s Pad"); form.Close(); }
                    catch (Exception ex) { Log("FAIL " + ex); working = false; text.Text = "Установка не завершена."; MessageBox.Show(form, ex.Message, "Ошибка установки", MessageBoxButtons.OK, MessageBoxIcon.Error); button.Enabled = cancel.Enabled = combo.Enabled = true; }
                };
                form.Controls.AddRange(new Control[] { title, text, combo, button, cancel }); form.ShowDialog();
            }
            return 0;
        }
        static void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AudioServiceSid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Root, security);
            Directory.CreateDirectory(Path.Combine(Root, "backups")); Directory.CreateDirectory(Path.Combine(Root, "bridges"));

        }
        static void Install(string id, bool probe)
        {
            using (var endpoint = Registry.LocalMachine.OpenSubKey(MicIntegration.CaptureKey + "\\" + id)) if (endpoint == null) throw new IOException("Микрофон не найден.");
            try
            {
                WithAudioStopped(delegate { ConfigureEndpoint(id); });
                if (probe)
                {
                  foreach (string mode in new[] { "default", "communications", "raw", "raw-communications" })
                  {
                    string result = Path.Combine(Root, "probe-" + id + "-" + mode + ".txt");
                    var p = Process.Start(new ProcessStartInfo(Path.Combine(MicIntegration.InstallRoot, "MicProbe.exe"), Quote(id) + " " + Quote(BridgePath(id)) + " " + Quote(result) + " " + mode) { UseShellExecute = false, CreateNoWindow = true });
                    if (!p.WaitForExit(15000)) { p.Kill(); throw new IOException("Проверка микрофона не завершилась вовремя."); }
                    int code = p.ExitCode; p.Dispose(); Log(File.Exists(result) ? File.ReadAllText(result) : "No probe log");
                    if (code != 0) throw new IOException("Тест звука через микрофон не прошёл. Подробности: " + result);
                  }
                }
                Log("PASS installed " + id);
            }
            catch (Exception problem) { try { Disconnect(id, true); Log("Original microphone settings restored"); } catch (Exception restore) { Log("RESTORE FAILED " + restore); throw new AggregateException("Требуется восстановление настроек микрофона.", problem, restore); } throw new IOException(problem.Message + " Исходные настройки микрофона восстановлены.", problem); }
        }
        static void ConfigureEndpoint(string id)
        {
            Log("Install " + id); InstallPayload(); RegisterCom();
            string fxPath = MicIntegration.CaptureKey + "\\" + id + "\\FxProperties";
            EndpointBackup backup;
            if (File.Exists(BackupPath(id))) backup = ReadBackup(id);
            else
            {
                bool existed; using (var before = Registry.LocalMachine.OpenSubKey(fxPath)) existed = before != null;
                using (var fx = OpenPrivileged(fxPath))
                {
                    string slot = MicIntegration.EndpointEffect;
                    backup = new EndpointBackup { Endpoint = id, OwnerSid = WindowsIdentity.GetCurrent().User.Value, FxExisted = existed, Slot = slot, Values = new List<RegistryValueBackup>() };
                    foreach (string name in new[] { slot, MicIntegration.BridgeProperty, MicIntegration.OriginalProperty, Disabled, Modes, FxTitle }) backup.Values.Add(RegistryValueBackup.Read(fx, name));
                    WriteBackup(backup);
                }
            }
            // Move earlier versions from a per-stream slot, which RAW capture bypasses.
            if (backup.Slot != MicIntegration.EndpointEffect)
            {
                using (var fx = OpenPrivileged(fxPath))
                {
                    foreach (string name in new[] { MicIntegration.EndpointEffect, Modes })
                        if (!backup.Values.Any(v => v.Name == name)) backup.Values.Add(RegistryValueBackup.Read(fx, name));
                    if (String.Equals(fx.GetValue(backup.Slot) as string, MicIntegration.Clsid, StringComparison.OrdinalIgnoreCase)) backup.Values.First(v => v.Name == backup.Slot).Restore(fx);
                    if (backup.Slot == MicIntegration.StreamEffect)
                    { var priorModes = backup.Values.FirstOrDefault(v => v.Name == StreamModes); if (priorModes != null) priorModes.Restore(fx); }
                }
                backup.Slot = MicIntegration.EndpointEffect;
                string saved = BackupPath(id);
                using (var file = new FileStream(saved + ".next", FileMode.Create, FileAccess.Write)) { new DataContractJsonSerializer(typeof(EndpointBackup)).WriteObject(file, backup); file.Flush(true); }
                File.Replace(saved + ".next", saved, saved + ".previous");
            }
            PrepareBridge(backup);
            using (var fx = OpenPrivileged(fxPath))
            {
                var original = backup.Values.First(v => v.Name == backup.Slot);
                fx.SetValue(MicIntegration.BridgeProperty, BridgePath(id), RegistryValueKind.String);
                fx.SetValue(MicIntegration.OriginalProperty, original.Text ?? "", RegistryValueKind.String);
                fx.SetValue(backup.Slot, MicIntegration.Clsid, RegistryValueKind.String);
                if (fx.GetValue(FxTitle) == null) fx.SetValue(FxTitle, "Yuki’s Pad", RegistryValueKind.String);
                fx.SetValue(Disabled, 0, RegistryValueKind.DWord);
                if (fx.GetValue(Modes) == null) fx.SetValue(Modes, new[] { "{c18e2f7e-933d-4965-b7d1-1eef228d2af3}", "{98951333-b9cd-48b1-a0a3-ff40682d73f7}", "{9e90ea20-b493-4fd1-a1a8-7e1361a956cf}" }, RegistryValueKind.MultiString);
                fx.Flush();
            }
        }
        static string Quote(string value) { if (value.Contains("\"")) throw new ArgumentException(); return "\"" + value + "\""; }
        static void InstallPayload()
        {
            Directory.CreateDirectory(MicIntegration.InstallRoot);
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("YukisPad.Payload.zip"))
            {
                if (resource == null) throw new IOException("В установщике отсутствуют файлы приложения.");
                using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
                    foreach (var entry in zip.Entries)
                    {
                        string path = Path.GetFullPath(Path.Combine(MicIntegration.InstallRoot, entry.FullName));
                        if (!path.StartsWith(MicIntegration.InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("..")) throw new InvalidDataException("Неверный путь в дистрибутиве.");
                        if (String.IsNullOrEmpty(entry.Name)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var content = entry.Open())
                        using (var memory = new MemoryStream())
                        {
                            content.CopyTo(memory); byte[] bytes = memory.ToArray();
                            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) continue;
                            try { File.WriteAllBytes(path, bytes); } catch (IOException) { throw new IOException("Закрой Yuki’s Pad перед обновлением программы."); }
                        }
                    }
            }
            string setup = Path.Combine(MicIntegration.InstallRoot, "YukisPadSetup.exe"), current = Assembly.GetExecutingAssembly().Location;
            if (!Path.GetFullPath(current).Equals(Path.GetFullPath(setup), StringComparison.OrdinalIgnoreCase)) File.Copy(current, setup, true);
            using (var uninstall = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YukisPad"))
            {
                uninstall.SetValue("DisplayName", "Yuki’s Pad"); uninstall.SetValue("DisplayVersion", "0.2.1"); uninstall.SetValue("Publisher", "Yuki’s Pad"); uninstall.SetValue("InstallLocation", MicIntegration.InstallRoot); uninstall.SetValue("UninstallString", Quote(setup) + " --uninstall"); uninstall.SetValue("DisplayIcon", Path.Combine(MicIntegration.InstallRoot, "YukisPad.exe")); uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
            }
            // A per-machine Start Menu shortcut. No autostart task or persistent service.
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Yuki’s Pad.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { menu });
                try { shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Path.Combine(MicIntegration.InstallRoot, "YukisPad.exe") }); shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { MicIntegration.InstallRoot }); shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null); }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        static void RegisterCom()
        {
            using (var clsid = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\CLSID\" + MicIntegration.Clsid + @"\InprocServer32")) { clsid.SetValue("", Path.Combine(MicIntegration.InstallRoot, "YukiMicFx.dll")); clsid.SetValue("ThreadingModel", "Both"); }
            using (var apo = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + MicIntegration.Clsid))
            {
                apo.SetValue("FriendlyName", "Yuki’s Pad Microphone Mixer"); apo.SetValue("Copyright", "Yuki’s Pad"); apo.SetValue("MajorVersion", 1); apo.SetValue("MinorVersion", 0); apo.SetValue("Flags", 15);
                foreach (string key in new[] { "MinInputConnections", "MaxInputConnections", "MinOutputConnections", "MaxOutputConnections", "NumAPOInterfaces" }) apo.SetValue(key, 1);
                apo.SetValue("MaxInstances", -1, RegistryValueKind.DWord); apo.SetValue("APOInterface0", "{fd7f2b29-24d0-4b5c-b177-592c39f9ca10}");
            }
        }
        static void PrepareBridge(EndpointBackup backup)
        {
            string path = BridgePath(backup.Endpoint);
            if (!File.Exists(path)) using (var writer = new BinaryWriter(File.Create(path))) { writer.BaseStream.SetLength(MicIntegration.SharedSize); writer.Write(0x594B5044); writer.Write(1); writer.Write(MicIntegration.Capacity); writer.Write(MicIntegration.Rate); }
            if (new FileInfo(path).Length != MicIntegration.SharedSize) throw new IOException("Повреждён файл связи с микрофоном.");
            var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
            foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
            foreach (string sid in new[] { backup.OwnerSid, "S-1-5-19", "S-1-5-20" }) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AudioServiceSid, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
            File.SetAccessControl(path, security);
        }
        static void WriteBackup(EndpointBackup backup)
        {
            string path = BackupPath(backup.Endpoint); using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write)) { new DataContractJsonSerializer(typeof(EndpointBackup)).WriteObject(file, backup); file.Flush(true); }
            File.Move(path + ".tmp", path);
        }
        static EndpointBackup ReadBackup(string id) { using (var file = File.OpenRead(BackupPath(id))) { var b = (EndpointBackup)new DataContractJsonSerializer(typeof(EndpointBackup)).ReadObject(file); if (b.Endpoint != id) throw new InvalidDataException("Неверная резервная копия."); return b; } }
        static void Disconnect(string id, bool restart)
        {
            if (!File.Exists(BackupPath(id))) return;
            var backup = ReadBackup(id); string path = MicIntegration.CaptureKey + "\\" + id + "\\FxProperties";
            using (var key = OpenPrivileged(path))
            {
                foreach (var value in backup.Values) value.Restore(key); key.Flush();
                if (!backup.FxExisted && !backup.Values.Any(v => v.Name == FxTitle)) key.DeleteValue(FxTitle, false);
                if (!backup.FxExisted && key.ValueCount == 0 && key.SubKeyCount == 0)
                { int status = NtDeleteKey(key.Handle); if (status != 0) throw new IOException("Не удалось удалить пустой ключ эффекта: 0x" + status.ToString("X8")); }
            }
            if (restart) RestartAudio();
            Log("Disconnected " + id);
        }
        static void Uninstall()
        {
            foreach (string path in Directory.GetFiles(Path.Combine(Root, "backups"), "*.json")) { Guid id; if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out id)) Disconnect(id.ToString("B"), false); }
            RestartAudio();
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Classes\CLSID\" + MicIntegration.Clsid, false);
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + MicIntegration.Clsid, false);
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YukisPad", false);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Yuki’s Pad.lnk"); if (File.Exists(menu)) File.Delete(menu);
            foreach (string name in new[] { "YukisPad.exe", "YukisPad.exe.config", "YukiMicFx.dll", "MicProbe.exe", "YukisPadSetup.exe" })
            {
                string path = Path.Combine(MicIntegration.InstallRoot, name);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException)
                {
                    // A later reinstall must not be deleted by a pending reboot action.
                    string retired = path + ".removed-" + Guid.NewGuid().ToString("N");
                    if (MoveFileEx(path, retired, 0)) MoveFileEx(retired, null, 4);
                    else Log("Retained locked file: " + path);
                }
            }
            Log("Uninstalled; user library and microphone backups retained");
        }
        static void TestLifecycle(string id)
        {
            var report = new List<string>(); Exception failure = null;
            string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukisPad", "library.json");
            byte[] before = File.Exists(library) ? File.ReadAllBytes(library) : null;
            var backup = ReadBackup(id);
            try
            {
                Uninstall();
                using (var key = Registry.LocalMachine.OpenSubKey(MicIntegration.CaptureKey + "\\" + id + "\\FxProperties"))
                {
                    if (backup.FxExisted && key == null) throw new IOException("Original effects key missing.");
                    foreach (var expected in backup.Values)
                    {
                        var actual = key == null ? new RegistryValueBackup { Name = expected.Name, Kind = -1 } : RegistryValueBackup.Read(key, expected.Name);
                        if (actual.Kind != expected.Kind || actual.Text != expected.Text || actual.Number != expected.Number || !(actual.Strings ?? new string[0]).SequenceEqual(expected.Strings ?? new string[0]) || !(actual.Bytes ?? new byte[0]).SequenceEqual(expected.Bytes ?? new byte[0])) throw new IOException("Microphone setting not restored: " + expected.Name);
                    }
                }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + MicIntegration.Clsid)) if (key != null) throw new IOException("COM registration remains.");
                if (before != null && (!File.Exists(library) || !before.SequenceEqual(File.ReadAllBytes(library)))) throw new IOException("User library changed.");
                foreach (string name in new[] { "Audiosrv", "AudioEndpointBuilder" }) using (var service = new ServiceController(name)) if (service.Status != ServiceControllerStatus.Running) throw new IOException("Audio service stopped: " + name);
                report.Add("PASS: uninstall restored original microphone settings, removed COM registration, preserved the library and running audio services.");
            }
            catch (Exception ex) { failure = ex; report.Add("FAIL: " + ex.Message); }
            finally
            {
                try { Install(id, true); report.Add("PASS: clean reinstall and hardware microphone probe."); }
                catch (Exception ex) { failure = ex; report.Add("FAIL reinstall: " + ex.Message); }
                File.WriteAllLines(Path.Combine(Root, "install-lifecycle-test.txt"), report);
            }
            if (failure != null) throw failure;
        }
        static void RestartAudio() { WithAudioStopped(delegate { }); }
        static void WithAudioStopped(Action action)
        {
            Log("Restart Windows Audio");
            var stopped = new List<string>();
            try { StopServiceTree("AudioEndpointBuilder", stopped); action(); }
            finally
            {
                stopped.Reverse();
                foreach (string name in stopped) using (var service = new ServiceController(name))
                {
                    service.Refresh(); if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
            }
            System.Threading.Thread.Sleep(1000);
        }
        static void StopServiceTree(string name, List<string> stopped)
        {
            using (var service = new ServiceController(name))
            {
                if (service.Status == ServiceControllerStatus.Stopped) return;
                var dependents = service.DependentServices;
                try
                {
                    foreach (var dependent in dependents) if (dependent.Status != ServiceControllerStatus.Stopped) StopServiceTree(dependent.ServiceName, stopped);
                    stopped.Add(name); service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
                finally { foreach (var dependent in dependents) dependent.Dispose(); }
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivilege state, uint length, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string from, string to, int flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegCreateKeyEx(IntPtr root, string subkey, int reserved, string cls, int options, int desired, IntPtr security, out SafeRegistryHandle result, out int disposition);
        [DllImport("ntdll.dll")] static extern int NtDeleteKey(SafeRegistryHandle key);
        static void EnablePrivilege(string name)
        {
            IntPtr token; if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out token)) throw new Win32Exception();
            try { Luid luid; if (!LookupPrivilegeValue(null, name, out luid)) throw new Win32Exception(); var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 }; if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0) throw new Win32Exception(); }
            finally { CloseHandle(token); }
        }
        static RegistryKey OpenPrivileged(string path)
        {
            if (!path.StartsWith(MicIntegration.CaptureKey + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unexpected registry path");
            SafeRegistryHandle handle; int disposition; int code = RegCreateKeyEx(new IntPtr(unchecked((int)0x80000002)), path, 0, null, 4, 0, IntPtr.Zero, out handle, out disposition);
            if (code != 0) throw new Win32Exception(code); return RegistryKey.FromHandle(handle, RegistryView.Registry64);
        }
    }
}
