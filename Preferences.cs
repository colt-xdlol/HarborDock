using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace HarborDock
{
    internal sealed class PreferencesWindow : Window
    {
        private readonly DockWindow dock;
        private readonly DispatcherTimer saveTimer;
        private bool loading = true;
        private TextBlock status;
        public PreferencesWindow(DockWindow dock)
        {
            this.dock = dock;
            Title = Config.Product + " — настройки";
            Width = 610;
            Height = Math.Min(810, SystemParameters.WorkArea.Height - 24);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            ShowInTaskbar = true;
            Content = XamlReader.Parse(LayoutXaml);
            FrameworkElement root = (FrameworkElement)Content;
            status = (TextBlock)root.FindName("Status");
            ((FrameworkElement)root.FindName("Header")).MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ChangedButton == MouseButton.Left) DragMove();
            };
            ((Button)root.FindName("CloseButton")).Click += delegate { Close(); };
            ((Button)root.FindName("DoneButton")).Click += delegate { Close(); };
            ((Button)root.FindName("AddButton")).Click += delegate { dock.ChooseFiles(); };
            ((Button)root.FindName("RestoreButton")).Click += delegate {
                dock.RestoreTaskbar();
                ((CheckBox)root.FindName("HideTaskbar")).IsChecked = false;
                ((CheckBox)root.FindName("AutoHide")).IsChecked = false;
                status.Text = "панель Windows восстановлена";
            };
            ((Button)root.FindName("ExitButton")).Click += delegate { dock.RequestExit(); };
            ((Border)root.FindName("PreviewHost")).Child = new PreviewSurface(dock);
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            saveTimer.Tick += delegate { saveTimer.Stop(); dock.ApplySettings(); };
            Slider size = (Slider)root.FindName("IconSize");
            Slider zoom = (Slider)root.FindName("Zoom");
            Slider opacity = (Slider)root.FindName("PanelOpacity");
            size.Value = dock.Settings.IconSize;
            zoom.Value = dock.Settings.Magnification;
            opacity.Value = dock.Settings.Opacity * 100;
            Action updateLabels = delegate {
                ((TextBlock)root.FindName("SizeValue")).Text = ((int)size.Value) + " px";
                ((TextBlock)root.FindName("ZoomValue")).Text = zoom.Value.ToString("0.00", CultureInfo.InvariantCulture) + "×";
                ((TextBlock)root.FindName("OpacityValue")).Text = ((int)opacity.Value) + "%";
            };
            size.ValueChanged += delegate { dock.Settings.IconSize = size.Value; updateLabels(); ScheduleSave(); };
            zoom.ValueChanged += delegate { dock.Settings.Magnification = zoom.Value; updateLabels(); ScheduleSave(); };
            opacity.ValueChanged += delegate { dock.Settings.Opacity = opacity.Value / 100; updateLabels(); ScheduleSave(); };
            updateLabels();
            BindCheck(root, "AutoHide", dock.Settings.AutoHide, value => dock.Settings.AutoHide = value);
            BindCheck(root, "AutoCompact", dock.Settings.AutoCompact, value => dock.Settings.AutoCompact = value);
            BindCheck(root, "HideTaskbar", dock.Settings.HideTaskbar, value => dock.Settings.HideTaskbar = value);
            BindCheck(root, "ShowRunning", dock.Settings.ShowRunning, value => dock.Settings.ShowRunning = value);
            BindCheck(root, "ShowStatus", dock.Settings.ShowStatus, value => dock.Settings.ShowStatus = value);
            BindCheck(root, "LightTheme", dock.Settings.LightTheme, value => dock.Settings.LightTheme = value);
            bool startup = false;
            try { startup = Shell.StartupEnabled; } catch { }
            CheckBox startupCheck = (CheckBox)root.FindName("Startup");
            startupCheck.IsChecked = startup;
            startupCheck.Click += delegate {
                try { Shell.StartupEnabled = startupCheck.IsChecked == true; status.Text = "настройки сохранены"; }
                catch (Exception ex) { status.Text = "автозапуск не изменён: " + ex.Message; startupCheck.IsChecked = Shell.StartupEnabled; }
            };
            ComboBox monitor = (ComboBox)root.FindName("Monitor");
            var screens = System.Windows.Forms.Screen.AllScreens;
            monitor.Items.Add("Основной экран");
            foreach (var screen in screens) monitor.Items.Add(screen.DeviceName + "  ·  " + screen.Bounds.Width + " × " + screen.Bounds.Height);
            int selected = Array.FindIndex(screens, s => s.DeviceName == dock.Settings.Monitor);
            monitor.SelectedIndex = selected < 0 ? 0 : selected + 1;
            monitor.SelectionChanged += delegate {
                dock.Settings.Monitor = monitor.SelectedIndex <= 0 ? "" : screens[monitor.SelectedIndex - 1].DeviceName;
                ScheduleSave();
            };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            Closed += delegate { saveTimer.Stop(); dock.ApplySettings(); };
            loading = false;
        }

        private void BindCheck(FrameworkElement root, string name, bool value, Action<bool> changed)
        {
            CheckBox checkbox = (CheckBox)root.FindName(name);
            checkbox.IsChecked = value;
            checkbox.Click += delegate { changed(checkbox.IsChecked == true); ScheduleSave(); };
        }
        private void ScheduleSave()
        {
            if (loading) return;
            status.Text = "настройки сохранены автоматически";
            saveTimer.Stop();
            saveTimer.Start();
        }

        private const string LayoutXaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Background='#151B25' BorderBrush='#354050' BorderThickness='1' CornerRadius='20' Margin='10'>
  <Border.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='3' Opacity='.35'/></Border.Effect>
  <Border.Resources>
    <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#EFF3F8'/></Style>
    <Style x:Key='Sub' TargetType='TextBlock'><Setter Property='Foreground' Value='#97A6BA'/><Setter Property='FontSize' Value='12'/></Style>
    <Style TargetType='Button'>
      <Setter Property='Foreground' Value='#EDF3FA'/><Setter Property='Background' Value='#293547'/>
      <Setter Property='BorderBrush' Value='#39495E'/><Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='14,9'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='9' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border><ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Opacity' Value='.75'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Opacity' Value='.5'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#95DFD2'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='CheckBox'>
      <Setter Property='Foreground' Value='#DCE5F0'/><Setter Property='Margin' Value='0,8'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='48'/></Grid.ColumnDefinitions>
          <ContentPresenter VerticalAlignment='Center'/>
          <Border x:Name='Track' Grid.Column='1' Width='36' Height='21' CornerRadius='11' Background='#3A4658' HorizontalAlignment='Right'>
            <Ellipse x:Name='Thumb' Fill='#B5C3D5' Width='15' Height='15' HorizontalAlignment='Left' Margin='3,0'/>
          </Border>
        </Grid><ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='Track' Property='Background' Value='#81CDBE'/><Setter TargetName='Thumb' Property='Fill' Value='#122A2B'/><Setter TargetName='Thumb' Property='HorizontalAlignment' Value='Right'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Track' Property='BorderBrush' Value='White'/><Setter TargetName='Track' Property='BorderThickness' Value='1'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='Slider'><Setter Property='Foreground' Value='#81CDBE'/><Setter Property='Margin' Value='0,7,0,0'/><Setter Property='Height' Value='21'/><Setter Property='IsMoveToPointEnabled' Value='True'/></Style>
  </Border.Resources>
  <DockPanel Margin='25,20,25,18' LastChildFill='True'>
    <Grid x:Name='Header' DockPanel.Dock='Top' Margin='0,0,0,18'>
      <StackPanel><TextBlock Text='Harbor Dock' FontSize='27' FontWeight='SemiBold'/>
        <TextBlock Text='привычные приложения. плавное движение.' Style='{StaticResource Sub}' Margin='0,6,0,0'/></StackPanel>
      <Button x:Name='CloseButton' Content='&#xE8BB;' FontFamily='Segoe MDL2 Assets' FontSize='12' Width='32' Height='32' Padding='0' HorizontalAlignment='Right' VerticalAlignment='Top' Background='Transparent' BorderThickness='0' AutomationProperties.Name='Закрыть настройки'/>
    </Grid>
    <StackPanel DockPanel.Dock='Bottom' Margin='0,15,0,0'>
      <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition Width='130'/></Grid.ColumnDefinitions>
        <Button x:Name='AddButton' Content='Добавить приложение…'/>
        <Button x:Name='DoneButton' Grid.Column='2' Content='Готово' Background='#81CDBE' Foreground='#10292A' BorderThickness='0' FontWeight='SemiBold'/>
      </Grid>
      <Grid Margin='0,12,0,0'><TextBlock x:Name='Status' Text='Ctrl+Alt+S — настройки · Ctrl+Alt+D — показать док' FontSize='10' Foreground='#8D9CB0' VerticalAlignment='Center'/>
        <Button x:Name='ExitButton' Content='Выйти' HorizontalAlignment='Right' Padding='5,2' Background='Transparent' BorderThickness='0' FontSize='11' Foreground='#A2B0C1'/></Grid>
    </StackPanel>
    <ScrollViewer VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Padding='0,0,8,0'>
      <StackPanel>
        <Border Background='#202B39' CornerRadius='13' Margin='0,0,0,17' Padding='12,10,12,8'>
          <StackPanel><TextBlock Text='наведи курсор на иконки' Foreground='#AEBFD0' FontSize='11' HorizontalAlignment='Center'/>
            <Border x:Name='PreviewHost' Height='104'/></StackPanel>
        </Border>
        <TextBlock Text='Внешний вид' FontWeight='SemiBold' FontSize='15' Margin='0,0,0,10'/>
        <Border Background='#1D2633' CornerRadius='12' Padding='16,12' Margin='0,0,0,17'>
          <StackPanel>
            <Grid><TextBlock Text='Размер иконок'/><TextBlock x:Name='SizeValue' HorizontalAlignment='Right' Foreground='#8DD9CA'/></Grid>
            <Slider x:Name='IconSize' Minimum='28' Maximum='72' TickFrequency='1' IsSnapToTickEnabled='True' AutomationProperties.Name='Размер иконок'/>
            <Grid Margin='0,10,0,0'><TextBlock Text='Увеличение при наведении'/><TextBlock x:Name='ZoomValue' HorizontalAlignment='Right' Foreground='#8DD9CA'/></Grid>
            <Slider x:Name='Zoom' Minimum='1' Maximum='2.2' SmallChange='.05' AutomationProperties.Name='Увеличение при наведении'/>
            <Grid Margin='0,10,0,0'><TextBlock Text='Плотность фона'/><TextBlock x:Name='OpacityValue' HorizontalAlignment='Right' Foreground='#8DD9CA'/></Grid>
            <Slider x:Name='PanelOpacity' Minimum='35' Maximum='95' TickFrequency='1' IsSnapToTickEnabled='True' AutomationProperties.Name='Плотность фона'/>
            <CheckBox x:Name='LightTheme' Content='Светлая панель' Margin='0,12,0,2'/>
          </StackPanel>
        </Border>
        <TextBlock Text='Поведение' FontWeight='SemiBold' FontSize='15' Margin='0,0,0,10'/>
        <Border Background='#1D2633' CornerRadius='12' Padding='16,8' Margin='0,0,0,14'>
          <StackPanel>
            <CheckBox x:Name='AutoHide' Content='Скрывать док автоматически'/>
            <CheckBox x:Name='AutoCompact' Content='Сплющивать поверх развёрнутых окон'/>
            <CheckBox x:Name='HideTaskbar' Content='Заменить панель задач Windows'/>
            <TextBlock Text='Штатная панель вернётся при выходе из дока.' Style='{StaticResource Sub}' FontSize='11' Margin='0,-2,0,3'/>
            <CheckBox x:Name='ShowRunning' Content='Показывать открытые приложения'/>
            <CheckBox x:Name='ShowStatus' Content='Wi-Fi, Bluetooth и язык ввода'/>
            <CheckBox x:Name='Startup' Content='Запускать при входе в Windows'/>
            <Grid Margin='0,10,0,8'><Grid.ColumnDefinitions><ColumnDefinition Width='65'/><ColumnDefinition/></Grid.ColumnDefinitions>
              <TextBlock Text='Экран' VerticalAlignment='Center'/>
              <ComboBox x:Name='Monitor' Grid.Column='1' Padding='7,5' AutomationProperties.Name='Экран для дока'/>
            </Grid>
          </StackPanel>
        </Border>
        <TextBlock Text='Перетащи файл, папку или ярлык в док. Закреплённые иконки можно менять местами. ПКМ — действия и открытые окна.' Style='{StaticResource Sub}' TextWrapping='Wrap' LineHeight='19' Margin='1,0,1,12'/>
        <Button x:Name='RestoreButton' Content='Восстановить панель Windows' Background='Transparent' Margin='0,0,0,4'/>
      </StackPanel>
    </ScrollViewer>
  </DockPanel>
</Border>";
    }

    internal sealed class PreviewSurface : FrameworkElement
    {
        private readonly DockWindow dock;
        private readonly DispatcherTimer timer;
        private readonly double[] scales = { 1, 1, 1, 1, 1 };
        private readonly ImageSource[] icons;
        private Point pointer;
        private bool inside;
        public PreviewSurface(DockWindow dock)
        {
            this.dock = dock;
            icons = dock.Items.Take(scales.Length).Select(i => i.Icon).ToArray();
            MouseMove += delegate(object sender, MouseEventArgs e) { pointer = e.GetPosition(this); inside = true; };
            MouseLeave += delegate { inside = false; };
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Config.FrameIntervalMs) };
            timer.Tick += delegate {
                bool changed = false;
                for (int i = 0; i < scales.Length; i++)
                {
                    double center = ActualWidth / 2 + (i - 2) * 57;
                    double target = 1 + (dock.Settings.Magnification - 1) * (inside ? Layout.Wave(pointer.X - center, 113) : 0);
                    double previous = scales[i];
                    scales[i] = Layout.Ease(previous, target, .016, Config.SpringSpeed);
                    changed |= Math.Abs(previous - scales[i]) > .0001;
                }
                if (changed) InvalidateVisual();
            };
            Loaded += delegate { timer.Start(); };
            Unloaded += delegate { timer.Stop(); };
        }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double total = scales.Sum(s => s * 38 + 12) - 12;
            double x = (ActualWidth - total) / 2;
            Rect panel = new Rect(x - 14, ActualHeight - 59, total + 28, 55);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(100, 91, 111, 133)), new Pen(new SolidColorBrush(Color.FromArgb(55, 210, 226, 241)), 1), panel, 14, 14);
            for (int i = 0; i < icons.Length; i++)
            {
                double size = 38 * scales[i];
                dc.DrawImage(icons[i], new Rect(x, ActualHeight - 15 - size, size, size));
                x += size + 12;
            }
        }
    }
}
