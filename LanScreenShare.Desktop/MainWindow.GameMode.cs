using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Game Mode & Cursor Centering

        private async Task ToggleGameModeAsync()
        {
            if (!_isGameMode)
            {
                bool connected = await EnsureControlConnectedAsync();
                if (!connected)
                {
                    var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                    if (gameModeBtn != null) gameModeBtn.IsChecked = false;
                    return;
                }

                _isGameMode = true;
            }
            else
            {
                _isGameMode = false;
            }

            UpdateGameModeUI();
        }

        private void UpdateGameModeUI()
        {
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsChecked = _isGameMode;
                gameModeBtn.Background = _isGameMode
                    ? new SolidColorBrush(Color.Parse("#2563EB"))
                    : new SolidColorBrush(Color.Parse("#27272A"));
            }

            if (_isGameMode)
            {
                ActivateGameModeUi();
            }
            else
            {
                DeactivateGameModeUi();
            }
        }

        private void ActivateGameModeUi()
        {
            _remoteControlEnabled = true;
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsChecked = true;
                remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            }

            var screenImage = this.FindControl<Image>(ScreenImageControlName);
            if (screenImage != null)
            {
                screenImage.Focus();
                screenImage.Cursor = new Cursor(StandardCursorType.None);
            }

            if (OperatingSystem.IsWindows())
            {
                var center = GetScreenImageCenterOnScreen();
                _isRecenteringMouse = true;
                SetCursorPos(center.X, center.Y);
            }

            _gameModeBannerTimer?.Stop();
            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
            if (banner != null && bannerText != null)
            {
                banner.IsVisible = true;
                if (bannerIcon != null)
                {
                    bannerIcon.Data = (this.FindResource("Icon3dRotation") as StreamGeometry)!;
                    bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                }
                bannerText.Text = "Modo Juego 3D Activo — Presiona Ctrl + Alt + L para liberar el mouse";
                bannerText.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
            }
        }

        private void DeactivateGameModeUi()
        {
            var screenImage = this.FindControl<Image>(ScreenImageControlName);
            if (screenImage != null)
            {
                screenImage.Cursor = Cursor.Default;
            }

            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
            if (banner != null && bannerText != null)
            {
                banner.IsVisible = true;
                if (bannerIcon != null)
                {
                    bannerIcon.Data = (this.FindResource("IconLockOpen") as StreamGeometry)!;
                    bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                }
                bannerText.Text = "Mouse Liberado — Presiona Ctrl + Alt + L para recapturar";
                bannerText.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));

                _gameModeBannerTimer?.Stop();
                _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _gameModeBannerTimer.Tick += (s, e) =>
                {
                    banner.IsVisible = false;
                    _gameModeBannerTimer?.Stop();
                };
                _gameModeBannerTimer.Start();
            }
        }

        private PixelPoint GetScreenImageCenterOnScreen()
        {
            var screenImage = this.FindControl<Image>(ScreenImageControlName);
            if (screenImage != null && RemoteInputHelper.HasPositiveDimensions(screenImage.Bounds))
            {
                int imgCenterX = this.Position.X + (int)Math.Round(screenImage.Bounds.X + (screenImage.Bounds.Width / 2.0));
                int imgCenterY = this.Position.Y + (int)Math.Round(screenImage.Bounds.Y + (screenImage.Bounds.Height / 2.0));
                return new PixelPoint(imgCenterX, imgCenterY);
            }

            int winCenterX = this.Position.X + (int)Math.Round(this.Bounds.Width / 2.0);
            int winCenterY = this.Position.Y + (int)Math.Round(this.Bounds.Height / 2.0);
            return new PixelPoint(winCenterX, winCenterY);
        }

        private void HandleGameModePointerMoved()
        {
            if (_isRecenteringMouse)
            {
                _isRecenteringMouse = false;
                return;
            }

            if (!OperatingSystem.IsWindows())
                return;

            var center = GetScreenImageCenterOnScreen();
            if (GetCursorPos(out NativePoint curPos))
            {
                int dx = curPos.X - center.X;
                int dy = curPos.Y - center.Y;

                if (dx != 0 || dy != 0)
                {
                    _ = _controlClient.SendInputAsync(new RemoteInputMessage
                    {
                        Type = InputEventType.MouseMoveRelative,
                        NormalizedX = dx,
                        NormalizedY = dy
                    });

                    _isRecenteringMouse = true;
                    SetCursorPos(center.X, center.Y);
                }
            }
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetCursorPos(int x, int y);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out NativePoint lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        #endregion
    }
}
