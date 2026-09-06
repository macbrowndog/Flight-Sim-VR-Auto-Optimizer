using System.Windows;
using SimVROptimizer.Core;

namespace SimVROptimizer.App;

public partial class DisplaySettingsWindow : Window
{
    public DisplaySettingsWindow(MsfsDisplaySettings settings, NvidiaDlssSettings nvidia)
    {
        InitializeComponent();
        ConfigVersionText.Text = $"USERCFG VERSION {settings.UserConfigVersion}";
        SetValues(settings.Desktop, false);
        SetValues(settings.Vr, true);
        SetNvidiaValues(nvidia);
        ConfigPathText.Text = settings.ConfigPath;
        ConfigPathText.ToolTip = settings.ConfigPath;
    }

    private void SetValues(MsfsGraphicsDisplaySetting setting, bool vr)
    {
        var dlss = setting.AntiAliasing.Equals("DLSS", StringComparison.OrdinalIgnoreCase)
            ? setting.DlssMode
            : $"NOT ACTIVE / SAVED {setting.DlssMode}";
        if (vr)
        {
            VrRenderingText.Text = setting.AntiAliasing;
            VrDlssText.Text = "DLSS MODE  /  " + dlss;
        }
        else
        {
            DesktopRenderingText.Text = setting.AntiAliasing;
            DesktopDlssText.Text = "DLSS MODE  /  " + dlss;
        }
    }

    private void SetNvidiaValues(NvidiaDlssSettings nvidia)
    {
        NvidiaProfileText.Text = "PROFILE  /  " + nvidia.Profile;
        DlssVersionText.Text = "LOADED DLSS LIBRARY  /  " + nvidia.DlssLibraryVersion;
        NvidiaStatusText.Text = nvidia.Status;
        SetPreset(nvidia.FrameGeneration, FgNameText, FgValueText, FgSourceText);
        SetPreset(nvidia.SuperResolution, SrNameText, SrValueText, SrSourceText);
        SetPreset(nvidia.RayReconstruction, RrNameText, RrValueText, RrSourceText);
    }

    private static void SetPreset(NvidiaDlssPresetSetting setting, System.Windows.Controls.TextBlock name,
        System.Windows.Controls.TextBlock value, System.Windows.Controls.TextBlock source)
    {
        name.Text = setting.Name.ToUpperInvariant();
        value.Text = setting.Value;
        source.Text = setting.Source;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
