using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace YukisPad
{
    internal static class Ui
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        public static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("YukisPad." + name))
            {
                object result = XamlReader.Load(s); var window = result as Window;
                if (window != null) window.SourceInitialized += delegate { int dark = 0; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 20, ref dark, 4); };
                return result;
            }
        }
        public static T Get<T>(Window w, string name) where T : class { return (T)w.FindName(name); }
        public static void Click(Window w, string name, Action action) { Get<Button>(w, name).Click += delegate { action(); }; }
        public static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        public static void Error(Window owner, Exception ex) { MessageBox.Show(owner, ex.Message, "Yuki’s Pad", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal sealed class WaveView : FrameworkElement
    {
        public float[] Peaks;
        public double Start, End = 1;
        public event Action Changed;
        bool dragStart;
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var selected = Ui.Brush("#245C98"); var muted = Ui.Brush("#A7A7A7");
            dc.DrawRoundedRectangle(Ui.Brush("#DCEAF8"), null, new Rect(Start * w, 0, Math.Max(0, (End - Start) * w), h), 5, 5);
            if (Peaks != null) for (int i = 0; i < Peaks.Length; i++)
            {
                double x = (i + .5) / Peaks.Length * w, amplitude = Math.Max(2, Math.Sqrt(Peaks[i]) * (h - 22));
                dc.DrawLine(new Pen(x >= Start * w && x <= End * w ? selected : muted, Math.Max(1, w / Peaks.Length - 1)), new Point(x, (h - amplitude) / 2), new Point(x, (h + amplitude) / 2));
            }
            foreach (double x in new[] { Start * w, End * w }) { dc.DrawLine(new Pen(selected, 2), new Point(x, 0), new Point(x, h)); dc.DrawRoundedRectangle(selected, null, new Rect(Math.Max(0, Math.Min(w - 8, x - 4)), h / 2 - 13, 8, 26), 3, 3); }
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { double f = e.GetPosition(this).X / Math.Max(1, ActualWidth); dragStart = Math.Abs(f - Start) < Math.Abs(f - End); CaptureMouse(); Move(e); }
        protected override void OnMouseMove(MouseEventArgs e) { Cursor = Cursors.SizeWE; if (IsMouseCaptured) Move(e); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
        void Move(MouseEventArgs e) { double f = Math.Max(0, Math.Min(1, e.GetPosition(this).X / Math.Max(1, ActualWidth))); if (dragStart) Start = Math.Min(End - .001, f); else End = Math.Max(Start + .001, f); InvalidateVisual(); if (Changed != null) Changed(); }
    }

    internal sealed class Editor
    {
        public readonly Window Window;
        public bool Saved, Deleted;
        readonly Sound sound;
        readonly WaveView wave;
        readonly Action<double, double, float> preview;
        readonly Action stop;
        readonly Func<int, uint, string> validateKey;
        int key; uint mods; bool updating;
        public Editor(Window owner, Sound item, bool fresh, string path, Action<double, double, float> preview, Action stop, Func<int, uint, string> validateKey)
        {
            sound = item; this.preview = preview; this.stop = stop; this.validateKey = validateKey;
            Window = (Window)Ui.Load("Editor.xaml"); Window.Owner = owner;
            Ui.Get<TextBox>(Window, "SoundName").Text = sound.Name; Ui.Get<TextBox>(Window, "Collection").Text = sound.Collection;
            Ui.Get<Slider>(Window, "Gain").Value = sound.Gain * 100; Ui.Get<CheckBox>(Window, "Loop").IsChecked = sound.Loop; Ui.Get<CheckBox>(Window, "Favorite").IsChecked = sound.Favorite;
            key = sound.Key; mods = sound.Modifiers; UpdateKey();
            wave = new WaveView { Peaks = sound.Peaks, Start = sound.Start / sound.Duration, End = sound.End / sound.Duration };
            Ui.Get<Grid>(Window, "WaveHost").Children.Add(wave);
            wave.Changed += delegate { updating = true; Ui.Get<TextBox>(Window, "Start").Text = (wave.Start * sound.Duration).ToString("F3", CultureInfo.InvariantCulture); Ui.Get<TextBox>(Window, "End").Text = Math.Min(sound.Duration, wave.End * sound.Duration).ToString("F3", CultureInfo.InvariantCulture); updating = false; UpdateRange(); };
            Ui.Get<TextBox>(Window, "Start").Text = sound.Start.ToString("F3", CultureInfo.InvariantCulture);
            Ui.Get<TextBox>(Window, "End").Text = sound.End.ToString("F3", CultureInfo.InvariantCulture);
            Ui.Get<TextBox>(Window, "Start").TextChanged += delegate { UpdateRange(); }; Ui.Get<TextBox>(Window, "End").TextChanged += delegate { UpdateRange(); };
            Ui.Get<Slider>(Window, "Gain").ValueChanged += delegate { Ui.Get<TextBlock>(Window, "GainLabel").Text = "Громкость: " + Math.Round(Ui.Get<Slider>(Window, "Gain").Value) + "%"; };
            Ui.Get<TextBlock>(Window, "GainLabel").Text = "Громкость: " + Math.Round(sound.Gain * 100) + "%";
            Ui.Get<TextBox>(Window, "Hotkey").PreviewKeyDown += CaptureKey;
            Ui.Click(Window, "ClearHotkey", delegate { key = 0; mods = 0; UpdateKey(); });
            Ui.Click(Window, "Preview", delegate { double a, b; if (ReadRange(out a, out b)) preview(a, b, (float)(Ui.Get<Slider>(Window, "Gain").Value / 100)); else Error("Проверь начало и конец фрагмента."); });
            Ui.Click(Window, "Cancel", delegate { Window.Close(); });
            Ui.Click(Window, "StopPreview", stop);
            Ui.Click(Window, "Save", Save);
            Ui.Click(Window, "Delete", delegate { if (MessageBox.Show(Window, "Удалить «" + sound.Name + "» из библиотеки? Исходный файл останется на месте.", "Удаление звука", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { Deleted = true; Window.Close(); } });
            Ui.Click(Window, "Export", delegate
            {
                double a, b; if (!ReadRange(out a, out b)) { Error("Проверь границы фрагмента."); return; }
                var dialog = new SaveFileDialog { Filter = "WAV|*.wav", FileName = string.Concat(sound.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".wav" };
                if (dialog.ShowDialog(Window) == true) try { stop(); if (Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Выбери файл вне библиотеки."); PcmInfo.Export(path, dialog.FileName, a, b); Error("Фрагмент сохранён в WAV (без изменения громкости)."); } catch (Exception ex) { Error(ex.Message); }
            });
            if (fresh) Ui.Get<Button>(Window, "Delete").Visibility = Visibility.Collapsed;
            Window.Closed += delegate { stop(); }; UpdateRange();
        }
        void UpdateKey() { Ui.Get<TextBox>(Window, "Hotkey").Text = key == 0 ? "Нажми сочетание…" : Hotkeys.Label(mods, key); }
        void CaptureKey(object sender, KeyEventArgs e)
        {
            e.Handled = true; Key k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (k == System.Windows.Input.Key.LeftCtrl || k == System.Windows.Input.Key.RightCtrl || k == System.Windows.Input.Key.LeftAlt || k == System.Windows.Input.Key.RightAlt || k == System.Windows.Input.Key.LeftShift || k == System.Windows.Input.Key.RightShift || k == System.Windows.Input.Key.LWin || k == System.Windows.Input.Key.RWin) return;
            if (k == System.Windows.Input.Key.Escape) { Window.Focus(); return; }
            uint m = 0; var pressed = Keyboard.Modifiers; if ((pressed & ModifierKeys.Control) != 0) m |= 2; if ((pressed & ModifierKeys.Alt) != 0) m |= 1; if ((pressed & ModifierKeys.Shift) != 0) m |= 4; if ((pressed & ModifierKeys.Windows) != 0) m |= 8;
            if ((m & 3) == 0 && !(k >= System.Windows.Input.Key.F1 && k <= System.Windows.Input.Key.F12)) { Error("Используй Ctrl / Alt или клавиши F1–F12."); return; }
            key = KeyInterop.VirtualKeyFromKey(k); mods = m; UpdateKey(); Error("");
        }
        bool ReadRange(out double a, out double b)
        {
            a = b = 0; double end;
            if (!Double.TryParse(Ui.Get<TextBox>(Window, "Start").Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out a) || !Double.TryParse(Ui.Get<TextBox>(Window, "End").Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out end)) return false;
            b = Math.Min(end, sound.Duration);
            return end <= sound.Duration + .001 && Store.ValidRange(a, b, sound.Duration) && b - a >= .01;
        }
        void UpdateRange()
        {
            if (updating) return; double a, b; bool valid = ReadRange(out a, out b);
            Ui.Get<TextBlock>(Window, "SelectionLength").Text = valid ? "Выбрано " + (b - a).ToString("F2") + " сек. из " + sound.Duration.ToString("F2") + " сек." : "Начало должно быть раньше конца · минимум 0,01 сек.";
            if (valid) { wave.Start = a / sound.Duration; wave.End = b / sound.Duration; wave.InvalidateVisual(); }
        }
        void Error(string message) { Ui.Get<TextBlock>(Window, "Error").Text = message; }
        void Save()
        {
            double a, b; if (!ReadRange(out a, out b)) { Error("Проверь границы фрагмента."); return; }
            string name = Ui.Get<TextBox>(Window, "SoundName").Text.Trim(); if (name.Length == 0) { Error("Добавь название звука."); return; }
            string problem = validateKey(key, mods); if (problem != null) { Error(problem); return; }
            sound.Name = name; sound.Collection = Ui.Get<TextBox>(Window, "Collection").Text.Trim(); if (sound.Collection.Length == 0) sound.Collection = "Без коллекции";
            sound.Start = a; sound.End = b; sound.Gain = Ui.Get<Slider>(Window, "Gain").Value / 100; sound.Loop = Ui.Get<CheckBox>(Window, "Loop").IsChecked == true; sound.Favorite = Ui.Get<CheckBox>(Window, "Favorite").IsChecked == true; sound.Key = key; sound.Modifiers = mods;
            Saved = true; Window.Close();
        }
    }

}

