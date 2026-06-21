using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace InstallationWizard.Helpers
{
    /// <summary>
    /// 帮助实现 Popup 高斯模糊（Aero / Acrylic 模糊）的辅助类
    /// </summary>
    public static class PopupBlurHelper
    {
        public static readonly DependencyProperty IsBlurEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsBlurEnabled",
                typeof(bool),
                typeof(PopupBlurHelper),
                new PropertyMetadata(false, OnIsBlurEnabledChanged));

        public static bool GetIsBlurEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsBlurEnabledProperty);
        }

        public static void SetIsBlurEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsBlurEnabledProperty, value);
        }

        private static void OnIsBlurEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Popup popup)
            {
                if ((bool)e.NewValue)
                {
                    popup.Opened += Popup_Opened;
                    // 如果在设置属性时 Popup 已经是打开状态，则直接应用模糊
                    if (popup.IsOpen)
                    {
                        ApplyBlurToPopup(popup);
                    }
                }
                else
                {
                    popup.Opened -= Popup_Opened;
                }
            }
        }

        private static void Popup_Opened(object sender, EventArgs e)
        {
            if (sender is Popup popup)
            {
                ApplyBlurToPopup(popup);
            }
        }

        private static void ApplyBlurToPopup(Popup popup)
        {
            popup.Dispatcher.BeginInvoke(new Action(() =>
            {
                var child = popup.Child as FrameworkElement;
                if (child != null)
                {
                    if (child.IsLoaded)
                    {
                        TryApply(child);
                    }
                    else
                    {
                        child.Loaded += (s, ev) => TryApply(child);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private static void TryApply(Visual child)
        {
            var hwndSource = PresentationSource.FromVisual(child) as HwndSource;
            if (hwndSource != null)
            {
                EnableBlur(hwndSource.Handle);
            }
        }

        // Win32 Interop 定义
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_INVALID_STATE = 5
        }

        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private static void EnableBlur(IntPtr hwnd)
        {
            var accent = new AccentPolicy
            {
                // 使用 Windows 10/11 的模糊效果
                AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 0,
                GradientColor = 0,
                AnimationId = 0
            };

            var accentStructSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);

            Marshal.FreeHGlobal(accentPtr);
        }
    }
}
