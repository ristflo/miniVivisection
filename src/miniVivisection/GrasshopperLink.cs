using System;
using System.IO;
using System.Linq;

namespace MiniVivisection
{
    /// <summary>
    /// The Grasshopper component (Edit in Rhino) is miniVivisection.GH.gha, next to the .rhp.
    /// Grasshopper loads .gha files from its Libraries folder, from Rhino's plug-in folders
    /// (HostUtils.GetAssemblySearchPaths), and from the folders named in .ghlink files in
    /// Libraries. So at load, when the plug-in's folder isn't one Grasshopper looks in, a
    /// miniVivisection.ghlink there names it -- taking effect when Grasshopper starts. It
    /// follows the plug-in: a Debug build's folder, then a Release one's, whichever loaded.
    /// Installed by the Package Manager (…\McNeel\Rhinoceros\packages\…), Grasshopper loads the
    /// package's .gha itself: no link then, and an old one (from a zip copy) is removed, or
    /// Grasshopper would find the component twice.
    /// </summary>
    internal static class GrasshopperLink
    {
        private const string GhaName = "miniVivisection.GH.gha";

        public static void Ensure()
        {
            try
            {
                var folder = Path.GetDirectoryName(typeof(GrasshopperLink).Assembly.Location);
                if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, GhaName)))
                    return; // not built with it
                var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grasshopper", "Libraries", "miniVivisection.ghlink");
                if (InPackage(folder) || Rhino.Runtime.HostUtils.GetAssemblySearchPaths().Any(p => SameFolder(p, folder)))
                {
                    if (File.Exists(link))
                    {
                        File.Delete(link); // the package or Rhino's plug-in folders cover it: no second copy
                        Log.Info(LogArea.Startup, "Grasshopper: removed {0} ({1} is loaded by Grasshopper itself)", link, folder);
                    }
                    return;
                }
                var content = "# miniVivisection's Grasshopper component (written by the plug-in at load)\n" + folder + "\n";
                if (File.Exists(link) && File.ReadAllText(link) == content)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                File.WriteAllText(link, content);
                Log.Info(LogArea.Startup, "Grasshopper: {0} links {1} (Edit in Rhino; from Grasshopper's next start)", link, folder);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Startup, "Grasshopper link for Edit in Rhino: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Whether the folder is a Package Manager install: %APPDATA%\McNeel\Rhinoceros\packages\8.0\miniVivisection\&lt;version&gt;.</summary>
        internal static bool InPackage(string folder)
        {
            try
            {
                var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McNeel", "Rhinoceros", "packages") + Path.DirectorySeparatorChar;
                return Path.GetFullPath(folder).StartsWith(packages, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
