using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
namespace YukisPad
{
    internal sealed class SettingsEditor
    {
        public readonly Window Window;
        public Settings Result;
        readonly Settings original;
        bool busy;
        public SettingsEditor(Window owner, Settings settings)
        {
            original=settings; Window=(Window)Ui.Load("Settings.xaml"); Window.Owner=owner;
            Ui.Get<CheckBox>(Window,"Monitoring").IsChecked=settings.Monitoring;
            Ui.Get<CheckBox>(Window,"Send").IsChecked=settings.SendToMicrophone;
            Ui.Get<CheckBox>(Window,"Tray").IsChecked=settings.MinimizeToTray;
            Ui.Get<ComboBox>(Window,"Microphone").SelectionChanged+=delegate { UpdateConnection(); };
            Reload(settings.Monitor,settings.Endpoint);
            Ui.Click(Window,"Refresh",delegate { Reload(MonitorToken(),EndpointId()); });
            Ui.Click(Window,"Cancel",delegate { Window.Close(); });
            Ui.Click(Window,"Save",Save);
            Ui.Click(Window,"Connect",delegate { Configure(false); });
            Ui.Click(Window,"Disconnect",delegate { Configure(true); });
            Window.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e) { if(busy)e.Cancel=true; };
        }
        string MonitorToken() { var d=Ui.Get<ComboBox>(Window,"Monitor").SelectedItem as Device; return d==null?null:d.Token; }
        string EndpointId() { var d=Ui.Get<ComboBox>(Window,"Microphone").SelectedItem as MicEndpoint; return d==null?null:d.Id; }
        void Reload(string monitor,string endpoint)
        {
            var devices=WinAudio.Devices(false); var output=Ui.Get<ComboBox>(Window,"Monitor");output.ItemsSource=devices;
            output.SelectedItem=devices.FirstOrDefault(d=>d.Token==(monitor??""));
            var endpoints=MicIntegration.Endpoints();var input=Ui.Get<ComboBox>(Window,"Microphone");input.ItemsSource=endpoints;
            input.SelectedItem=endpoints.FirstOrDefault(d=>d.Id==endpoint) ?? endpoints.FirstOrDefault();
            UpdateConnection();
        }
        void UpdateConnection()
        {
            var e=Ui.Get<ComboBox>(Window,"Microphone").SelectedItem as MicEndpoint;
            Ui.Get<TextBlock>(Window,"Connection").Text=e==null?"Подключи микрофон к компьютеру.":e.Installed?"Подключён. В Discord выбери этот же микрофон.":"Не подключён. Подключение требует прав администратора и перезапустит звук Windows.";
            Ui.Get<Button>(Window,"Disconnect").IsEnabled=!busy && e!=null && e.Installed;
            Ui.Get<Button>(Window,"Connect").IsEnabled=!busy && e!=null;
        }
        async void Configure(bool remove)
        {
            string endpoint=EndpointId();if(endpoint==null||busy)return;
            busy=true; UpdateConnection();
            foreach(string n in new[]{"Save","Cancel","Refresh"})Ui.Get<Button>(Window,n).IsEnabled=false;
            Ui.Get<ComboBox>(Window,"Microphone").IsEnabled=false;
            Ui.Get<TextBlock>(Window,"Error").Text="Ожидаю Windows и проверяю подключение…";
            try
            {
                using(var process=MicIntegration.RunSetup(endpoint,remove))
                {
                    await Task.Run(delegate { process.WaitForExit(); });
                    if(process.ExitCode!=0)throw new InvalidOperationException("Подключение не прошло проверку. Исходные настройки восстановлены. Подробности: "+System.IO.Path.Combine(MicIntegration.SharedRoot,"setup.log"));
                }
                Ui.Get<TextBlock>(Window,"Error").Text=remove?"Компонент отключён от микрофона.":"Готово: тестовый звук прошёл через микрофон.";
                if(!remove)Ui.Get<CheckBox>(Window,"Send").IsChecked=true;
            }
            catch(Exception ex){Ui.Get<TextBlock>(Window,"Error").Text=ex.Message;}
            finally
            {
                busy=false;
                foreach(string n in new[]{"Save","Cancel","Refresh"})Ui.Get<Button>(Window,n).IsEnabled=true;
                Ui.Get<ComboBox>(Window,"Microphone").IsEnabled=true;
                Reload(MonitorToken(),endpoint);
            }
        }
        void Save()
        {
            if(MonitorToken()==null){Ui.Get<TextBlock>(Window,"Error").Text="Выбери устройство для прослушивания.";return;}
            Result=new Settings { Monitor=MonitorToken(), Endpoint=EndpointId(), SendToMicrophone=Ui.Get<CheckBox>(Window,"Send").IsChecked==true, Monitoring=Ui.Get<CheckBox>(Window,"Monitoring").IsChecked==true, MinimizeToTray=Ui.Get<CheckBox>(Window,"Tray").IsChecked==true,Volume=original.Volume,RoutingVersion=1,StarterPackVersion=original.StarterPackVersion };
            Window.Close();
        }
    }
}


