using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LanScreenShare.Core.Security;

namespace LanScreenShare.Desktop
{
    public partial class TrustedDevicesWindow : Window
    {
        private const string AccentBlueHex = "#2563EB";
        private static readonly string[] AccessModeOptions =
        [
            "Acceso automático sin preguntar",
            "Preguntar siempre al conectar"
        ];

        private readonly TrustedDeviceManager _deviceManager = null!;
        private readonly Action<X509Certificate2>? _onIdentityRegenerated;
        private bool _confirmRevokeAllPending;
        private bool _confirmRegeneratePending;

        public TrustedDevicesWindow()
        {
            InitializeComponent();
        }

        public TrustedDevicesWindow(
            TrustedDeviceManager deviceManager,
            Action<X509Certificate2>? onIdentityRegenerated = null)
        {
            InitializeComponent();
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _onIdentityRegenerated = onIdentityRegenerated;

            WireHeaderAndFooter();
            _ = ReloadDevicesAsync();
        }

        private void WireHeaderAndFooter()
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
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            WireButton("RefreshDevicesButton", async () => await ReloadDevicesAsync());
            WireButton("CloseWindowButton", Close);
            WireButton("FooterCloseButton", Close);
            WireButton("RevokeAllButton", HandleRevokeAllClickAsync);
            WireButton("RegenerateIdentityButton", HandleRegenerateIdentityClickAsync);
        }

        private void WireButton(string buttonName, Action onClick)
        {
            var btn = this.FindControl<Button>(buttonName);
            if (btn != null) btn.Click += (_, _) => onClick();
        }

        private void WireButton(string buttonName, Func<Task> onClickAsync)
        {
            var btn = this.FindControl<Button>(buttonName);
            if (btn != null) btn.Click += async (_, _) => await onClickAsync();
        }

        private async Task ReloadDevicesAsync()
        {
            var devices = await _deviceManager.GetTrustedDevicesAsync();
            RenderDevices(devices);
        }

        private void RenderDevices(IReadOnlyList<TrustedDeviceViewItem> items)
        {
            var container = this.FindControl<StackPanel>("DevicesStackPanel");
            var emptyState = this.FindControl<Border>("EmptyStatePanel");
            if (container == null) return;

            container.Children.Clear();
            bool hasItems = items.Count > 0;
            if (emptyState != null) emptyState.IsVisible = !hasItems;

            foreach (var item in items)
            {
                container.Children.Add(BuildDeviceCard(item));
            }
        }

        private Border BuildDeviceCard(TrustedDeviceViewItem item)
        {
            var cardContent = new StackPanel { Spacing = 10 };
            cardContent.Children.Add(BuildCardHeader(item));
            cardContent.Children.Add(BuildMetadataRow(item));
            cardContent.Children.Add(BuildPermissionsEditor(item));

            return new Border
            {
                Background = BrushFrom("#171E2B"),
                BorderBrush = BrushFrom(item.Status == TrustedDeviceStatus.Connected ? AccentBlueHex : "#253042"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14),
                Child = cardContent
            };
        }

        private static Grid BuildCardHeader(TrustedDeviceViewItem item)
        {
            var grid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleStack = new StackPanel { Spacing = 2 };
            string subtitleText = string.IsNullOrWhiteSpace(item.Device.Alias)
                ? $"Huella SHA-256: {item.ShortFingerprint}"
                : $"Equipo original: {item.OriginalName} · Huella: {item.ShortFingerprint}";

            titleStack.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = BrushFrom("#F8FAFC")
            });

            var fpText = new TextBlock
            {
                Text = subtitleText,
                FontSize = 11.5,
                Foreground = BrushFrom("#8B9BB0")
            };
            ToolTip.SetTip(fpText, item.Device.PublicKeyFingerprint);
            titleStack.Children.Add(fpText);

            var badge = BuildStatusBadge(item);
            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(titleStack);
            grid.Children.Add(badge);
            return grid;
        }

        private static Border BuildStatusBadge(TrustedDeviceViewItem item)
        {
            (string bg, string border, string fg) = item.Status switch
            {
                TrustedDeviceStatus.Connected => ("#152D4F", AccentBlueHex, "#93C5FD"),
                TrustedDeviceStatus.Revoked => ("#33181D", "#7F1D1D", "#FCA5A5"),
                _ => ("#143026", "#166534", "#86EFAC")
            };

            return new Border
            {
                Background = BrushFrom(bg),
                BorderBrush = BrushFrom(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.StatusLabel,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = BrushFrom(fg)
                }
            };
        }

        private static TextBlock BuildMetadataRow(TrustedDeviceViewItem item) =>
            new()
            {
                Text = $"Emparejado: {item.PairedAtDisplay}   ·   Última conexión: {item.LastSeenAtDisplay}",
                FontSize = 11.5,
                Foreground = BrushFrom("#94A3B8")
            };

        private readonly record struct PermissionControls(
            TextBox AliasBox,
            ComboBox ModeCombo,
            CheckBox ControlCheck,
            CheckBox ClipboardCheck,
            CheckBox FilesCheck);

        private StackPanel BuildPermissionsEditor(TrustedDeviceViewItem item)
        {
            var perms = item.Device.EffectivePermissions;
            var controls = new PermissionControls(
                AliasBox: new TextBox
                {
                    Text = item.Device.Alias ?? string.Empty,
                    PlaceholderText = "Alias personalizado (ej. Laptop Personal)",
                    FontSize = 12,
                    Background = BrushFrom("#111722"),
                    BorderBrush = BrushFrom("#2A364B"),
                    Foreground = BrushFrom("#F8FAFC"),
                    Width = 220
                },
                ModeCombo: new ComboBox
                {
                    FontSize = 12,
                    Background = BrushFrom("#111722"),
                    BorderBrush = BrushFrom("#2A364B"),
                    Foreground = BrushFrom("#F8FAFC"),
                    Width = 225,
                    ItemsSource = AccessModeOptions,
                    SelectedIndex = perms.AccessMode == DeviceAccessMode.AskEveryTime ? 1 : 0
                },
                ControlCheck: CreatePermissionCheckBox("Control mouse y teclado", perms.AllowRemoteControl),
                ClipboardCheck: CreatePermissionCheckBox("Portapapeles", perms.AllowClipboardSync),
                FilesCheck: CreatePermissionCheckBox("Archivos", perms.AllowFileTransfer));

            var topControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };
            topControls.Children.Add(controls.AliasBox);
            topControls.Children.Add(controls.ModeCombo);

            var checkRow = new WrapPanel { Orientation = Orientation.Horizontal };
            checkRow.Children.Add(controls.ControlCheck);
            checkRow.Children.Add(controls.ClipboardCheck);
            checkRow.Children.Add(controls.FilesCheck);

            var actions = BuildDeviceActionButtons(item, controls);

            var wrapper = new StackPanel { Spacing = 8 };
            wrapper.Children.Add(topControls);
            wrapper.Children.Add(checkRow);
            wrapper.Children.Add(actions);
            return wrapper;
        }

        private static CheckBox CreatePermissionCheckBox(string label, bool isChecked) =>
            new()
            {
                Content = label,
                IsChecked = isChecked,
                FontSize = 12,
                Foreground = BrushFrom("#CBD5E1"),
                Margin = new Thickness(0, 0, 14, 2)
            };

        private WrapPanel BuildDeviceActionButtons(TrustedDeviceViewItem item, PermissionControls controls)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            var fingerprint = (DeviceFingerprint)item.Device.PublicKeyFingerprint;

            var saveBtn = CreateActionButton("Guardar permisos", "#1D4ED8", AccentBlueHex, "#FFFFFF");
            saveBtn.Click += async (_, _) =>
            {
                var updatedPerms = new DevicePermissions(
                    controls.ModeCombo.SelectedIndex == 1 ? DeviceAccessMode.AskEveryTime : DeviceAccessMode.Auto,
                    controls.ControlCheck.IsChecked == true,
                    controls.ClipboardCheck.IsChecked == true,
                    controls.FilesCheck.IsChecked == true);

                await _deviceManager.UpdateDeviceSettingsAsync(fingerprint, controls.AliasBox.Text, updatedPerms);
                ShowFeedback($"Permisos y alias guardados para '{item.DisplayName}'.", isWarning: false);
                await ReloadDevicesAsync();
            };
            panel.Children.Add(saveBtn);

            if (item.Status == TrustedDeviceStatus.Connected)
            {
                var dropControlBtn = CreateActionButton("Retirar solo control remoto", "#292524", "#78350F", "#FCD34D");
                dropControlBtn.IsEnabled = item.IsActiveController;
                dropControlBtn.Click += async (_, _) =>
                {
                    _deviceManager.RevokeRemoteControlForDevice(fingerprint);
                    ShowFeedback($"Control remoto retirado para '{item.DisplayName}' manteniendo la visualización.", isWarning: false);
                    await ReloadDevicesAsync();
                };
                panel.Children.Add(dropControlBtn);
            }

            if (item.Device.IsRevoked)
            {
                var unblockBtn = CreateActionButton("Desbloquear", "#143026", "#166534", "#86EFAC");
                unblockBtn.Click += async (_, _) =>
                {
                    await _deviceManager.UnrevokeDeviceAsync(fingerprint);
                    ShowFeedback($"Se desbloqueó '{item.DisplayName}'.", isWarning: false);
                    await ReloadDevicesAsync();
                };
                panel.Children.Add(unblockBtn);
            }
            else
            {
                var blockBtn = CreateActionButton("Bloquear (Revocar)", "#33181D", "#7F1D1D", "#FCA5A5");
                blockBtn.Click += async (_, _) =>
                {
                    await _deviceManager.RevokeDeviceAsync(fingerprint);
                    ShowFeedback($"Se bloqueó '{item.DisplayName}' y se cerró cualquier sesión activa.", isWarning: true);
                    await ReloadDevicesAsync();
                };
                panel.Children.Add(blockBtn);
            }

            var forgetBtn = CreateActionButton("Desvincular (Olvidar)", "#1E293B", "#475569", "#E2E8F0");
            forgetBtn.Click += async (_, _) =>
            {
                await _deviceManager.RemoveDeviceAsync(fingerprint);
                ShowFeedback($"Se desvinculó '{item.DisplayName}' de la lista de confianza.", isWarning: true);
                await ReloadDevicesAsync();
            };
            panel.Children.Add(forgetBtn);

            return panel;
        }

        private static Button CreateActionButton(string text, string bgHex, string borderHex, string fgHex) =>
            new()
            {
                Content = text,
                Background = BrushFrom(bgHex),
                BorderBrush = BrushFrom(borderHex),
                BorderThickness = new Thickness(1),
                Foreground = BrushFrom(fgHex),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 6),
                Margin = new Thickness(0, 4, 8, 0),
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

        private async Task HandleRevokeAllClickAsync()
        {
            var btn = this.FindControl<Button>("RevokeAllButton");
            if (!_confirmRevokeAllPending)
            {
                _confirmRevokeAllPending = true;
                _confirmRegeneratePending = false;
                ResetRegenerateButtonText();
                if (btn != null) btn.Content = "Confirmar: borrar todos y cortar sesiones";
                ShowFeedback("Haz clic nuevamente en confirmar para eliminar todos los dispositivos y desconectar sesiones activas.", isWarning: true);
                return;
            }

            _confirmRevokeAllPending = false;
            if (btn != null) btn.Content = "Revocar todos los dispositivos";
            await _deviceManager.RevokeAllDevicesAsync();
            ShowFeedback("Todos los dispositivos fueron revocados y todas las sesiones activas cerradas.", isWarning: true);
            await ReloadDevicesAsync();
        }

        private async Task HandleRegenerateIdentityClickAsync()
        {
            var btn = this.FindControl<Button>("RegenerateIdentityButton");
            if (!_confirmRegeneratePending)
            {
                _confirmRegeneratePending = true;
                _confirmRevokeAllPending = false;
                ResetRevokeAllButtonText();
                if (btn != null) btn.Content = "Confirmar: rotar certificado mTLS ahora";
                ShowFeedback("Haz clic nuevamente para rotar tu certificado local mTLS y cerrar todas las sesiones activas.", isWarning: true);
                return;
            }

            _confirmRegeneratePending = false;
            ResetRegenerateButtonText();
            if (btn != null) btn.Content = "Regenerar mi identidad mTLS";

            var newCert = await _deviceManager.RegenerateIdentityAsync();
            _onIdentityRegenerated?.Invoke(newCert);
            ShowFeedback("Identidad criptográfica regenerada. Sesiones previas cerradas.", isWarning: false);
            await ReloadDevicesAsync();
        }

        private void ResetRevokeAllButtonText()
        {
            var btn = this.FindControl<Button>("RevokeAllButton");
            if (btn != null) btn.Content = "Revocar todos los dispositivos";
        }

        private void ResetRegenerateButtonText()
        {
            var btn = this.FindControl<Button>("RegenerateIdentityButton");
            if (btn != null) btn.Content = "Regenerar mi identidad mTLS";
        }

        private void ShowFeedback(string message, bool isWarning)
        {
            var banner = this.FindControl<Border>("FeedbackBanner");
            var text = this.FindControl<TextBlock>("FeedbackBannerText");
            if (banner == null || text == null) return;

            text.Text = message;
            banner.Background = BrushFrom(isWarning ? "#311C18" : "#152B43");
            banner.BorderBrush = BrushFrom(isWarning ? "#92400E" : AccentBlueHex);
            banner.IsVisible = true;
        }

        private static SolidColorBrush BrushFrom(string hex) => new(Color.Parse(hex));
    }
}
