using System.Runtime.InteropServices;

namespace OpenDriverUpdater;

/// <summary>Ajustes de ventana de Windows 11: barra de título oscura/clara y del mismo color que la interfaz.</summary>
internal static class Native
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyWindowStyle(IntPtr hwnd, bool dark, Color caption)
    {
        try
        {
            int on = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));

            int round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            // COLORREF = 0x00BBGGRR. En Windows 10 estos atributos se ignoran sin error.
            int colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
        }
        catch
        {
            // dwmapi no disponible: se usa la barra de título estándar
        }
    }
}
