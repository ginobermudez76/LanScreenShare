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

        private bool CanToggleGameModeOn()
        {
            if (_isGameMode) return true;
            if (!_isViewing) return false;
            return _viewerPermissions.AllowRemoteControl;
        }

        private async Task ToggleGameModeAsync()
        {
            if (!CanToggleGameModeOn())
                return;

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
            ApplyGameModeButtonAndCursor(_isGameMode);

            if (_isGameMode)
            {
                ActivateGameModeUi();
            }
            else
            {
                DeactivateGameModeUi();
            }
        }

        private void ApplyGameModeButtonAndCursor(bool isGameMode)
        {
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsChecked = isGameMode;
                gameModeBtn.Background = new SolidColorBrush(Color.Parse(isGameMode ? "#2563EB" : "#27272A"));
            }

            var screenImage = this.FindControl<Image>(ScreenImageControlName);
            if (screenImage != null)
            {
                screenImage.Cursor = isGameMode ? new Cursor(StandardCursorType.None) : Cursor.Default;
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

            this.FindControl<Image>(ScreenImageControlName)?.Focus();

            if (OperatingSystem.IsWindows())
            {
                var center = GetScreenImageCenterOnScreen();
                _isRecenteringMouse = true;
                SetCursorPos(center.X, center.Y);
            }

            _gameModeBannerTimer?.Stop();
            ShowGameModeBanner(new GameModeBannerStyle(
                "Icon3dRotation",
                "#38BDF8",
                "Modo Juego 3D Activo — Presiona Ctrl + Alt + L para liberar el mouse"));
        }

        private void ExitGameModeWithoutRecaptureHint()
        {
            _isGameMode = false;
            ApplyGameModeButtonAndCursor(false);

            _gameModeBannerTimer?.Stop();
            var banner = this.FindControl<Border>("GameModeBanner");
            if (banner != null)
            {
                banner.IsVisible = false;
            }

            _popoutWindow?.ExitGameModeSilently();
        }

        private void DeactivateGameModeUi()
        {
            ApplyGameModeButtonAndCursor(false);

            ShowGameModeBanner(new GameModeBannerStyle(
                "IconLockOpen",
                "#FBBF24",
                "Mouse Liberado — Presiona Ctrl + Alt + L para recapturar",
                AutoHide: true));
        }

        private readonly record struct GameModeBannerStyle(
            string IconResourceKey,
            string HexColor,
            string Message,
            bool AutoHide = false);

        private void ShowGameModeBanner(in GameModeBannerStyle style)
        {
            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
            if (banner == null || bannerText == null)
            {
                return;
            }

            var brush = new SolidColorBrush(Color.Parse(style.HexColor));
            banner.IsVisible = true;
            if (bannerIcon != null)
            {
                bannerIcon.Data = (this.FindResource(style.IconResourceKey) as StreamGeometry)!;
                bannerIcon.Foreground = brush;
            }
            bannerText.Text = style.Message;
            bannerText.Foreground = brush;

            if (!style.AutoHide)
            {
                return;
            }

            _gameModeBannerTimer?.Stop();
            _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _gameModeBannerTimer.Tick += (s, e) =>
            {
                banner.IsVisible = false;
                _gameModeBannerTimer?.Stop();
            };
            _gameModeBannerTimer.Start();
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
