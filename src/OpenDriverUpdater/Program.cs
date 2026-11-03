using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace OpenDriverUpdater
{
    static class Program
    {
        // carpeta de trabajo en %LOCALAPPDATA%\OpenDriverUpdater
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDriverUpdater");

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        [STAThread]
        static void Main()
        {
            // las dll de WebView2 van embebidas en el exe (ver .csproj)
            AppDomain.CurrentDomain.AssemblyResolve += LoadEmbeddedAssembly;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Run();
        }

        // separado de Main para que el JIT no intente cargar WebView2 antes de tener el AssemblyResolve
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run()
        {
            Application.Run(new MainForm());
        }

        static Assembly LoadEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib/" + name + ".dll"))
            {
                if (stream == null) return null;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return Assembly.Load(ms.ToArray());
                }
            }
        }

        /// <summary>Saca un recurso embebido a disco (solo si no existe ya esa version).</summary>
        public static void ExtractResource(string resource, string path)
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var dst = File.Create(path))
                src.CopyTo(dst);
        }
    }
}
