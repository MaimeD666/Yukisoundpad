using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace YukisPad
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Contains("--self-test")) return SelfTests.Run(Path.Combine(root, "test-results"));
            if (args.Contains("--route-test")) return RouteTests.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukisPad", "route-test"));
            bool smoke = args.Contains("--smoke");
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Ui.Load("Theme.xaml"));
                string dataPath = smoke ? Path.Combine(root, "smoke-data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukisPad");
                Directory.CreateDirectory(dataPath);
                if (!smoke) StarterSounds.MigratePortable(Path.Combine(root, "data"), dataPath);
                bool created;
                using (var instance = new Mutex(true, "Local\\YukisPad-" + StableHash(dataPath), out created))
                {
                    if (!created) { MessageBox.Show("Yuki’s Pad уже запущен. Найди значок в трее.", "Yuki’s Pad"); return 0; }
                    var main = new MainController(new Store(dataPath), smoke);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukisPad", "error.log"), DateTime.Now + " " + e.Exception + Environment.NewLine); Ui.Error(main.Window, e.Exception); e.Handled = true; };
                    app.Run(main.Window);
                }
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukisPad", "error.log"), ex.ToString()); if (!smoke) MessageBox.Show(ex.Message, "Yuki’s Pad — ошибка запуска"); return 1; }
        }
        static string StableHash(string value) { uint hash = 2166136261; foreach (char c in value.ToLowerInvariant()) { hash ^= c; hash *= 16777619; } return hash.ToString("X8"); }
    }

    internal sealed class MainController
    {
        public readonly Window Window;
        readonly Store store;
        readonly bool smoke;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        Hotkeys hotkeys;
        Forms.NotifyIcon tray;
        IPlayback primary;
        Playback monitor;
        Sound playing;
        float playbackGain = 1;
        string filter = "all";
        string collection;
        int page;
        const int PageSize = 30;
        bool closing, importing, modal, volumeDirty;
        CancellationTokenSource importCancel;

        public MainController(Store data, bool smoke)
        {
            store = data; this.smoke = smoke; Window = (Window)Ui.Load("Main.xaml");
            Ui.Get<Slider>(Window, "Volume").Value = store.Data.Settings.Volume * 100;
            Ui.Get<TextBlock>(Window, "VolumeLabel").Text = Math.Round(store.Data.Settings.Volume * 100) + "%";
            Ui.Click(Window, "Import", ChooseFiles); Ui.Click(Window, "EmptyImport", ChooseFiles);
            Ui.Click(Window, "Stop", StopSound); Ui.Click(Window, "Settings", OpenSettings); Ui.Click(Window, "Guide", OpenSettings);
            Ui.Click(Window, "Mic", ToggleMic);
            Ui.Click(Window, "EditSelected", delegate { var selected = Ui.Get<ListBox>(Window, "Sounds").SelectedItem as Sound; if (selected != null) Edit(selected, false); else Status("Выберите звук в списке."); });
            Ui.Click(Window, "AllSounds", delegate { filter = "all"; Refresh(); });
            Ui.Click(Window, "Favorites", delegate { filter = "favorites"; Refresh(); });
            Ui.Get<TextBox>(Window, "Search").TextChanged += delegate { page = 0; RefreshSounds(); };
            Ui.Get<ComboBox>(Window, "Sort").SelectionChanged += delegate { page = 0; RefreshSounds(); };
            Ui.Click(Window, "PreviousPage", delegate { page--; RefreshSounds(); });
            Ui.Click(Window, "NextPage", delegate { page++; RefreshSounds(); });
            Ui.Get<ListBox>(Window, "Sounds").AddHandler(Button.ClickEvent, new RoutedEventHandler(CardClick));
            Ui.Get<ListBox>(Window, "Sounds").MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock) { var selected = Ui.Get<ListBox>(Window, "Sounds").SelectedItem as Sound; if (selected != null) { Play(selected); e.Handled = true; } } };
            Ui.Get<ListBox>(Window, "Sounds").PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { var selected = Ui.Get<ListBox>(Window, "Sounds").SelectedItem as Sound; if (selected != null) Play(selected); e.Handled = true; } };
            Ui.Get<Slider>(Window, "Volume").ValueChanged += delegate
            {
                store.Data.Settings.Volume = Ui.Get<Slider>(Window, "Volume").Value / 100;
                Ui.Get<TextBlock>(Window, "VolumeLabel").Text = Math.Round(store.Data.Settings.Volume * 100) + "%";
                if (primary != null) primary.Volume = (float)store.Data.Settings.Volume * playbackGain;
                if (monitor != null) monitor.Volume = (float)store.Data.Settings.Volume * playbackGain;
                volumeDirty = true;
            };
            Ui.Get<Slider>(Window, "Volume").PreviewMouseUp += delegate { SaveVolume(); };
            Ui.Get<Slider>(Window, "Volume").LostKeyboardFocus += delegate { SaveVolume(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { StopSound(); e.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { ChooseFiles(); e.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { Ui.Get<TextBox>(Window, "Search").Focus(); e.Handled = true; }
            };
            Window.DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !importing ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Window.Drop += async delegate(object sender, DragEventArgs e) { if (!importing && e.Data.GetDataPresent(DataFormats.FileDrop)) await ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
            Window.SourceInitialized += delegate
            {
                if (!smoke) { hotkeys = new Hotkeys(new WindowInteropHelper(Window).Handle); RegisterKeys(); CreateTray(); }
            };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (importing) { if (importCancel != null) importCancel.Cancel(); e.Cancel = true; Status("Отмена импорта… После завершения можно закрыть окно."); return; }
                if (!closing && store.Data.Settings.MinimizeToTray && tray != null) { e.Cancel = true; Window.Hide(); tray.ShowBalloonTip(1800, "Yuki’s Pad работает в трее", "Горячие клавиши доступны. Для выхода — правый клик по значку.", Forms.ToolTipIcon.Info); return; }
                Shutdown();
            };
            timer.Tick += delegate { Tick(); };
            Window.Loaded += async delegate
            {
                if (smoke) { RunSmoke(); return; }
                importing = true;
                try
                {
                    Status("Готовлю библиотеку…");
                    await Task.Run(delegate { StarterSounds.Ensure(store); });
                    Refresh(); RegisterKeys(); Status(store.Warning ?? "Готово");
                }
                catch (Exception ex) { Status("Стартовые звуки: " + ex.Message); }
                finally { importing = false; }
            };
            Refresh();
        }
        void Status(string text) { Ui.Get<TextBlock>(Window, "Status").Text = text; }
        void SaveVolume() { if (volumeDirty && Save()) volumeDirty = false; }
        bool Save() { try { store.Save(); return true; } catch (Exception ex) { Ui.Error(Window, ex); return false; } }
        void Refresh()
        {
            page = 0;
            Ui.Get<TextBlock>(Window, "PageTitle").Text = filter == "favorites" ? "Избранное" : filter == "collection" ? collection : "Все звуки";
            Ui.Get<Button>(Window, "AllSounds").Background = Ui.Brush(filter == "all" ? "#DCEAF8" : "#E8E8E8");
            Ui.Get<Button>(Window, "Favorites").Foreground = Ui.Brush(filter == "favorites" ? "#245C98" : "#202020");
            var groups = Ui.Get<StackPanel>(Window, "Collections"); groups.Children.Clear();
            foreach (string group in store.Data.Sounds.Select(s => s.Collection).Distinct().OrderBy(s => s))
            {
                string selected = group;
                var b = new Button { Content = group, Style = (Style)Application.Current.FindResource("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0), MaxWidth = 140, ToolTip = group };
                if (filter == "collection" && collection == group) b.Foreground = Ui.Brush("#245C98");
                b.Click += delegate { filter = "collection"; collection = selected; Refresh(); }; groups.Children.Add(b);
            }
            var endpoint = MicIntegration.Selected(store.Data.Settings.Endpoint);
            Ui.Get<TextBlock>(Window, "Route").Text = endpoint == null ? "Микрофон не найден" : endpoint.Name;
            UpdateMicButton();
            Ui.Get<TextBlock>(Window, "Route").ToolTip = Ui.Get<TextBlock>(Window, "Route").Text;
            RefreshSounds();
        }
        void RefreshSounds()
        {
            string query = Ui.Get<TextBox>(Window, "Search").Text.Trim();
            Ui.Get<TextBlock>(Window, "SearchHint").Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            IEnumerable<Sound> items = store.Data.Sounds.AsEnumerable().Reverse();
            if (filter == "favorites") items = items.Where(s => s.Favorite);
            if (filter == "collection") items = items.Where(s => s.Collection == collection);
            if (query.Length > 0) items = items.Where(s => s.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || s.Collection.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            int sort = Ui.Get<ComboBox>(Window, "Sort").SelectedIndex;
            if (sort == 1) items = items.OrderBy(s => s.Name); else if (sort == 2) items = items.OrderBy(s => s.End - s.Start);
            var list = items.ToList(); int pages = Math.Max(1, (list.Count + PageSize - 1) / PageSize); page = Math.Max(0, Math.Min(pages - 1, page));
            Ui.Get<ListBox>(Window, "Sounds").ItemsSource = list.Skip(page * PageSize).Take(PageSize).ToList();
            Ui.Get<TextBlock>(Window, "PageNumber").Text = (page + 1) + " / " + pages;
            Ui.Get<Button>(Window, "PreviousPage").IsEnabled = page > 0; Ui.Get<Button>(Window, "NextPage").IsEnabled = page + 1 < pages;
            Ui.Get<StackPanel>(Window, "Pagination").Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            Ui.Get<TextBlock>(Window, "Count").Text = store.Data.Sounds.Count == 0 ? "Звуков: 0" : "Звуков: " + list.Count + "  /  " + store.Data.Sounds.Count + " в библиотеке";
            Ui.Get<System.Windows.Controls.Border>(Window, "Empty").Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Ui.Get<TextBlock>(Window, "EmptyTitle").Text = store.Data.Sounds.Count == 0 ? "Нет звуков" : "Ничего не найдено";
            Ui.Get<TextBlock>(Window, "EmptyText").Text = store.Data.Sounds.Count == 0 ? "Добавьте или перетащите аудиофайлы." : "Попробуй другой запрос или добавь звуки в эту коллекцию.";
        }
        void CardClick(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button; if (button == null) return; var sound = button.DataContext as Sound; if (sound == null) return;
            e.Handled = true;
            switch (button.Tag as string)
            {
                case "play": Play(sound); break;
                case "favorite": sound.Favorite = !sound.Favorite; sound.Refresh(); Save(); RefreshSounds(); break;
                case "edit": Edit(sound, false); break;
            }
        }
        void Play(Sound sound)
        {
            if (importing || modal) return;
            if (playing == sound && primary != null) { StopSound(); return; }
            try { StartSound(sound, sound.Start, sound.End, (float)sound.Gain, false); }
            catch (Exception ex) { StopSound(); Status(ex.Message); }
        }
        void StartSound(Sound sound, double start, double end, float gain, bool preview)
        {
            StopSound(); var settings = store.Data.Settings;
            string path = store.AudioPath(sound); if (!File.Exists(path)) throw new FileNotFoundException("Файл звука отсутствует. Добавь его заново.");
            int output = WinAudio.Resolve(settings.Monitor, false);
            if (preview && WinAudio.Devices(false).Any(d => d.Id == output && IsVirtual(d.Token))) throw new InvalidOperationException("Для предпрослушивания выбери физические наушники в настройках.");
            playbackGain = gain; playing = sound;
            if (!preview && settings.SendToMicrophone)
            {
                primary = new MicPlayback(path, MicIntegration.Selected(settings.Endpoint), start, end, sound.Loop, gain * (float)settings.Volume);
                if (settings.Monitoring) monitor = new Playback(path, output, start, end, sound.Loop, gain * (float)settings.Volume);
            }
            else primary = new Playback(path, output, start, end, !preview && sound.Loop, gain * (float)settings.Volume);
            Ui.Get<TextBlock>(Window, "PlayingName").Text = (preview ? "Прослушивание · " : "") + sound.Name;
            Status(preview ? "Предпрослушивание на выбранном устройстве прослушивания" : "Играет: " + sound.Name + (sound.Loop ? " · повтор включён" : "")); timer.Start();
        }
        public void StopSound()
        {
            if (primary != null) { primary.Dispose(); primary = null; }
            if (monitor != null) { monitor.Dispose(); monitor = null; }
            playing = null;
            Ui.Get<TextBlock>(Window, "PlayingName").Text = "Не воспроизводится"; Ui.Get<TextBlock>(Window, "PlayingTime").Text = "—"; Ui.Get<ProgressBar>(Window, "Progress").Value = 0;
            timer.Stop();
        }
        void Tick()
        {
            if (primary != null)
            {
                if (primary.Finished) { var error = primary.Error; StopSound(); Status(error == null ? "Готово" : error.Message); }
                else { Ui.Get<TextBlock>(Window, "PlayingTime").Text = Sound.FormatTime(primary.Position) + " / " + Sound.FormatTime(primary.Duration); Ui.Get<ProgressBar>(Window, "Progress").Value = primary.Position / Math.Max(.01, primary.Duration); }
            }
            if (monitor != null && monitor.Finished && monitor.Error != null) { var error = monitor.Error; monitor.Dispose(); monitor = null; Status("Прослушивание: " + error.Message); }
        }
        static bool IsVirtual(string token) { string t = (token ?? "").ToLowerInvariant(); return t.Contains("cable") || t.Contains("voicemeeter") || t.Contains("virtual"); }
        void ToggleMic()
        {
            var settings = store.Data.Settings;
            var endpoint = MicIntegration.Selected(settings.Endpoint);
            if (!settings.SendToMicrophone && (endpoint == null || !endpoint.Installed)) { OpenSettings(); return; }
            StopSound(); settings.SendToMicrophone = !settings.SendToMicrophone; Save(); UpdateMicButton();
            Status(settings.SendToMicrophone ? "Звуки пойдут в микрофон вместе с голосом" : "Звуки слышны только тебе");
        }
        void UpdateMicButton()
        {
            bool send = store.Data.Settings.SendToMicrophone;
            Ui.Get<Button>(Window, "Mic").Content = send ? "В микрофон: вкл." : "Только мне";
            Ui.Get<Button>(Window, "Mic").Foreground = Ui.Brush(send ? "#245C98" : "#202020");
        }
        async void ChooseFiles()
        {
            if (importing) { if (importCancel != null) importCancel.Cancel(); return; }
            if (modal) return;
            var dialog = new OpenFileDialog { Title = "Добавить звуки", Multiselect = true, Filter = "Аудио|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma;*.mp4|Все файлы|*.*" };
            if (dialog.ShowDialog(Window) == true) await ImportFiles(dialog.FileNames);
        }
        async Task ImportFiles(string[] files)
        {
            if (importing || modal) return;
            importing = true; StopSound(); importCancel = new CancellationTokenSource(); Ui.Get<Button>(Window, "Import").Content = "Отменить импорт"; Ui.Get<Button>(Window, "EmptyImport").IsEnabled = false;
            try
            {
                foreach (string path in files)
                {
                    if (importCancel.IsCancellationRequested) break;
                    string id = Guid.NewGuid().ToString("N"), destination = Path.Combine(store.AudioRoot, Guid.NewGuid().ToString("N") + ".wav"); bool retained = false;
                    try
                    {
                        if (!File.Exists(path)) continue;
                        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new IOException("Максимальный размер исходного файла — 512 МБ.");
                        Status("Импорт: " + Path.GetFileName(path) + " · декодирование…");
                        Sound sound = await Task.Run(delegate
                        {
                            var info = MediaDecoder.Decode(path, destination, importCancel.Token);
                            var peaks = PcmInfo.Peaks(destination, 240, importCancel.Token);
                            return new Sound { Id = id, File = Path.GetFileName(destination), Name = Path.GetFileNameWithoutExtension(path), Collection = "Без коллекции", Duration = info.Duration, Start = 0, End = info.Duration, Gain = 1, Peaks = peaks };
                        });
                        importCancel.Token.ThrowIfCancellationRequested();
                        retained = Edit(sound, true);
                    }
                    catch (OperationCanceledException) { Status("Импорт отменён"); break; }
                    catch (Exception ex) { Status("Не удалось импортировать " + Path.GetFileName(path)); Ui.Error(Window, new IOException("Не удалось открыть «" + Path.GetFileName(path) + "». " + ex.Message + "\nПопробуй WAV или MP3, если кодек не установлен в Windows.")); }
                    finally { if (!retained && File.Exists(destination)) File.Delete(destination); }
                }
            }
            finally { importing = false; importCancel.Dispose(); importCancel = null; Ui.Get<Button>(Window, "Import").Content = "＋  Добавить звук"; Ui.Get<Button>(Window, "EmptyImport").IsEnabled = true; Refresh(); }
        }
        bool Edit(Sound sound, bool fresh)
        {
            StopSound(); modal = true; if (hotkeys != null) hotkeys.Clear();
            try
            {
                var editor = new Editor(Window, sound, fresh, store.AudioPath(sound), delegate(double a, double b, float gain) { try { StartSound(sound, a, b, gain, true); } catch (Exception ex) { Ui.Error(Window, ex); } }, StopSound, delegate(int key, uint mods)
                {
                    if (key == 0) return null;
                    if (key == 32 && mods == 3) return "Ctrl + Alt + Space зарезервировано для остановки.";
                    if (store.Data.Sounds.Any(s => s.Id != sound.Id && s.Key == key && s.Modifiers == mods)) return "Это сочетание уже назначено другому звуку.";
                    return hotkeys != null && !hotkeys.Available(mods, key) ? "Сочетание занято Windows или другим приложением." : null;
                });
                editor.Window.ShowDialog();
                if (editor.Deleted)
                {
                    int index = store.Data.Sounds.IndexOf(sound); store.Data.Sounds.Remove(sound);
                    if (Save()) { try { File.Delete(store.AudioPath(sound)); } catch (IOException ex) { Status("Запись удалена, но файл занят: " + ex.Message); } }
                    else store.Data.Sounds.Insert(index, sound);
                }
                if (editor.Saved)
                {
                    if (fresh) store.Data.Sounds.Add(sound);
                    if (!Save()) { if (fresh) store.Data.Sounds.Remove(sound); return !fresh; }
                    sound.Refresh(); Status("Сохранено: " + sound.Name); return true;
                }
                return !fresh;
            }
            finally { modal = false; RegisterKeys(); Refresh(); }
        }
        void OpenSettings()
        {
            if (importing || modal) return;
            StopSound(); modal = true; if (hotkeys != null) hotkeys.Clear();
            try { var editor = new SettingsEditor(Window, store.Data.Settings); editor.Window.ShowDialog(); if (editor.Result != null) { store.Data.Settings = editor.Result; Save(); Refresh(); Status("Настройки сохранены. В Discord выбери тот же микрофон."); } }
            finally { modal = false; RegisterKeys(); }
        }
        void RegisterKeys()
        {
            if (hotkeys == null) return; hotkeys.Clear(); var failed = new List<string>();
            if (!hotkeys.Add(1, 3, 32, StopSound)) failed.Add("остановка");
            int id = 2;
            foreach (var s in store.Data.Sounds)
            {
                Sound sound = s; if (s.Key != 0 && !hotkeys.Add(id++, s.Modifiers, s.Key, delegate { Play(sound); })) failed.Add(s.Name);
            }
            if (failed.Count > 0) Status("Горячая клавиша занята: " + string.Join(", ", failed));
        }
        void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "Yuki’s Pad", Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location), Visible = true };
            Action show = delegate { Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); };
            tray.DoubleClick += delegate { Window.Dispatcher.Invoke(show); };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть Yuki’s Pad", null, delegate { Window.Dispatcher.Invoke(show); });
            menu.Items.Add("Остановить звук", null, delegate { Window.Dispatcher.Invoke(new Action(StopSound)); });

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выйти", null, delegate { Window.Dispatcher.Invoke(new Action(delegate { closing = true; Window.Close(); })); });
            tray.ContextMenuStrip = menu;
        }
        void Shutdown()
        {
            StopSound(); SaveVolume(); timer.Stop(); if (hotkeys != null) hotkeys.Dispose(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Application.Current.Shutdown();
        }
        void RunSmoke()
        {
            try
            {
                store.Data.Sounds.Clear(); Refresh();
                Window.UpdateLayout(); Capture(Window, "smoke-main.png");
                var sound = new Sound { Id = "smoke", File = "test.wav", Name = "Проверка интерфейса", Collection = "Тест", Duration = 5, End = 5, Gain = 1, Peaks = Enumerable.Range(0, 240).Select(i => (float)(.15 + .7 * Math.Abs(Math.Sin(i * .19)))).ToArray() };
                var edit = new Editor(Window, sound, true, "unused.wav", delegate { }, delegate { }, delegate { return null; }); edit.Window.Show(); edit.Window.UpdateLayout(); Capture(edit.Window, "smoke-editor.png");
                Ui.Get<TextBox>(edit.Window, "Start").Text = "1.250"; Ui.Get<TextBox>(edit.Window, "End").Text = "3.750";
                Ui.Get<Button>(edit.Window, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!edit.Saved || sound.Start != 1.25 || sound.End != 3.75) throw new Exception("Editor did not save exact trim.");
                var settings = new SettingsEditor(Window, store.Data.Settings); settings.Window.Show(); settings.Window.UpdateLayout(); Capture(settings.Window, "smoke-settings.png"); settings.Window.Close();
                string[] names = { "Тот самый момент", "Барабанная дробь", "Аплодисменты", "Неожиданный поворот", "Победа!", "Пауза для смеха" };
                for (int i = 0; i < 36; i++) store.Data.Sounds.Add(new Sound { Id = "smoke-" + i, File = "test.wav", Name = names[i % names.Length] + (i < 6 ? "" : " " + i), Collection = i % 2 == 0 ? "Реакции" : "Для игры", Duration = 5 + i, Start = 0, End = 5 + i, Gain = 1, Favorite = i % 3 == 0, Key = i < 6 ? 112 + i : 0 });
                Refresh(); Window.UpdateLayout(); Capture(Window, "smoke-library.png");
                if (Ui.Get<ListBox>(Window, "Sounds").Items.Count != 30) throw new Exception("Pagination did not bound UI items.");
                Ui.Get<Button>(Window, "NextPage").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (Ui.Get<ListBox>(Window, "Sounds").Items.Count != 6) throw new Exception("Second page failed.");
                Ui.Get<TextBox>(Window, "Search").Text = "Победа";
                if (Ui.Get<ListBox>(Window, "Sounds").Items.Count != 6) throw new Exception("Search failed.");
                Ui.Get<TextBox>(Window, "Search").Clear();
                Ui.Get<Button>(Window, "Favorites").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (Ui.Get<ListBox>(Window, "Sounds").Items.Count != 12) throw new Exception("Favorites failed.");
                Ui.Get<Button>(Window, "AllSounds").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Ui.Get<Button>(Window, "Stop").Focus();
                if (timer.IsEnabled) throw new Exception("Playback timer runs while idle.");
                using (var keys = new Hotkeys(new WindowInteropHelper(Window).Handle)) keys.VerifyDispatch();
                CreateTray(); if (tray.Icon == null || !tray.Visible) throw new Exception("Tray initialization failed.");
                tray.Visible = false; tray.Dispose(); tray = null;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-result.txt"), "PASS: Main, Editor, Settings rendered; editor trim, 36-item pagination, search, favorites, native hotkey registration/dispatch and tray verified. Output devices: " + WinAudio.Devices(false).Count + "; input devices: " + WinAudio.Devices(true).Count);
                MeasureIdle();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-result.txt"), "FAIL: " + ex); closing = true; Window.Close(); }
        }
        void MeasureIdle()
        {
            var probe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; int phase = 0;
            TimeSpan cpu = TimeSpan.Zero; var clock = new System.Diagnostics.Stopwatch();
            probe.Tick += delegate
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                if (phase++ == 0) { cpu = process.TotalProcessorTime; clock.Start(); return; }
                probe.Stop(); process.Refresh();
                string report = "Idle with 36 sounds (30 visible), microphone off; sample " + clock.Elapsed.TotalSeconds.ToString("F1") + " s. CPU (one core): " + ((process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds * 100).ToString("F2") + "%; working set: " + (process.WorkingSet64 / 1048576.0).ToString("F1") + " MiB; private bytes: " + (process.PrivateMemorySize64 / 1048576.0).ToString("F1") + " MiB. Playback timer disabled: " + !timer.IsEnabled;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "idle-profile.txt"), report);
                closing = true; Window.Close();
            };
            probe.Start();
        }
        static void Capture(Window window, string name)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var f = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(f);
        }
    }
}



