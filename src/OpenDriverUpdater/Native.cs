using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenDriverUpdater
{
    // barra de titulo del mismo color que la app (Windows 11; en Windows 10 se ignora)
    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void StyleWindow(IntPtr hwnd, bool dark, Color caption)
        {
            try
            {
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);      // DWMWA_WINDOW_CORNER_PREFERENCE
                int color = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref color, 4);      // DWMWA_CAPTION_COLOR
            }
            catch { }
        }
    }
}
