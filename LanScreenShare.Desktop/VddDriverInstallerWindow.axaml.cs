using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LanScreenShare.Media.Display;

namespace LanScreenShare.Desktop
{
    public partial class VddDriverInstallerWindow : Window
    {
        private readonly VirtualDisplayDriverInstallerService _installerService;
        private bool _isInstalling;

        public VddDriverInstallerWindow()
            : this(new VirtualDisplayDriverInstallerService())
        {
        }

        public VddDriverInstallerWindow(VirtualDisplayDriverInstallerService installerService)
        {
            InitializeComponent();
            _installerService = installerService ?? throw new ArgumentNullException(nameof(installerService));

            LoadInstallPlanPreview();
            WireEvents();
        }

        private void LoadInstallPlanPreview()
        {
            var plan = _installerService.BuildInstallPlan();
            var methodTitle = this.FindControl<TextBlock>("InstallMethodTitle");
            if (methodTitle != null)
            {
                methodTitle.Text = plan.Source == VddInstallSource.LocalInfPackage
                    ? "Método detectado: Paquete firmado offline (Drivers\\VDD\\MttVDD.inf)"
                    : "Método de respaldo: Descarga e instalación vía winget";
            }

            var previewText = this.FindControl<TextBlock>("CommandPreviewText");
            if (previewText != null)
            {
                previewText.Text = $"{plan.Executable} {plan.Arguments}";
            }
        }

        private void WireEvents()
        {
            var headerDrag = this.FindControl<Grid>("HeaderDragGrid");
            if (headerDrag != null)
            {
                headerDrag.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    {
                        BeginMoveDrag(e);
                    }
                };
            }

            this.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && !_isInstalling)
                {
                    Close(false);
                    e.Handled = true;
                }
            };

            WireButton("CloseWindowButton", () => { if (!_isInstalling) Close(false); });
            WireButton("CancelInstallButton", () => { if (!_isInstalling) Close(false); });

            var installBtn = this.FindControl<Button>("InstallDriverButton");
            if (installBtn != null)
            {
                installBtn.Click += async (_, _) => await RunInstallAsync();
            }
        }

        private void WireButton(string name, Action handler)
        {
            var btn = this.FindControl<Button>(name);
            if (btn != null) btn.Click += (_, _) => handler();
        }

        private async Task RunInstallAsync()
        {
            if (_isInstalling)
                return;

            _isInstalling = true;
            SetButtonsEnabled(false, "Instalando...");

            var result = await _installerService.InstallDriverAsync();
            _isInstalling = false;

            ShowResultBanner(result);

            if (result.Success && VirtualDisplayDriverInstallerService.IsAnyVirtualDisplayDriverInstalled())
            {
                Close(true);
                return;
            }

            SetButtonsEnabled(true, "Reintentar instalación");
        }

        private void SetButtonsEnabled(bool enabled, string installText)
        {
            var installBtn = this.FindControl<Button>("InstallDriverButton");
            if (installBtn != null)
            {
                installBtn.IsEnabled = enabled;
                installBtn.Content = installText;
            }

            var cancelBtn = this.FindControl<Button>("CancelInstallButton");
            if (cancelBtn != null) cancelBtn.IsEnabled = enabled;
        }

        private void ShowResultBanner(VddInstallResult result)
        {
            var banner = this.FindControl<Border>("ResultBanner");
            var bannerText = this.FindControl<TextBlock>("ResultBannerText");
            if (banner == null || bannerText == null)
                return;

            banner.IsVisible = true;
            bannerText.Text = result.Message;

            if (result.Success)
            {
                banner.Background = new SolidColorBrush(Color.Parse("#14291F"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#22C55E"));
            }
            else
            {
                banner.Background = new SolidColorBrush(Color.Parse("#2B1618"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#EF4444"));
            }
        }
    }
}
