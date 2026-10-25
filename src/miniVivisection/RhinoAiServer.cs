using System;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

namespace MiniVivisection
{
    /// <summary>
    /// RhinoAI's MCP server in this Rhino, for the Claude tab's "rhino" tools. RhinoAI
    /// starts it only when asked -- its MCPStart command; at startup only with its
    /// AutoLoadMCP setting on and an agent enabled. Without a running server, RhinoAI's
    /// router answers a tool call by launching a new Rhino (/runscript=_MCPSpawn) and
    /// waiting up to 120 s for it. So before each message the tab makes sure this
    /// Rhino's server runs: <c>Rhino.AI.RhinoAIHost.HasStarted(doc)</c> (internal, by
    /// reflection), and if it doesn't, RhinoAI's own <c>-MCPStart</c> with its default
    /// port. The router adopts this Rhino through the announcement RhinoAI then writes.
    /// </summary>
    internal static class RhinoAiServer
    {
        private static readonly Guid PlugInId = new Guid("2668d7ed-f507-4a68-8295-8172147a0e39");
        private const string HostTypeName = "Rhino.AI.RhinoAIHost";

        /// <summary>On the UI thread. A note for the transcript, or null when the server
        /// already runs.</summary>
        public static string EnsureRunning(RhinoDoc doc)
        {
            if (doc == null)
                return null;
            if (!PlugIn.PlugInExists(PlugInId, out var loaded, out _))
                return "RhinoAI isn't installed in this Rhino -- Claude can't work in the Rhino model.";
            if (loaded && Port(doc) != null)
                return null;
            if (Command.InCommand())
                return "a command is running -- RhinoAI's MCP server can't be started now; the router may start another Rhino.";
            RhinoApp.RunScript("_-MCPStart _Enter", false);
            var port = Port(doc);
            return port != null
                ? string.Format("started RhinoAI's MCP server in this Rhino (http://localhost:{0}/).", port)
                : "couldn't start RhinoAI's MCP server (MCPStart) -- see Rhino's command line; the router may start another Rhino.";
        }

        /// <summary>With "Start RhinoAI's server for Claude" off: a note when the server doesn't
        /// run (null when it does, or RhinoAI isn't loaded).</summary>
        public static string NotRunningNote(RhinoDoc doc)
        {
            if (doc == null || !PlugIn.PlugInExists(PlugInId, out var loaded, out _) || !loaded || Port(doc) != null)
                return null;
            return VNames.Text("its MCP server isn't running, so Claude can't work in the model -- start it with RhinoAI's MCPStart, or turn on V Settings > Claude > \"Start RhinoAI's server for Claude\" (it listens on localhost without a token).");
        }

        /// <summary>The port of this document's RhinoAI MCP server, or null when none runs
        /// (or RhinoAI isn't loaded).</summary>
        private static int? Port(RhinoDoc doc)
        {
            try
            {
                var host = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "RhinoAI")
                    ?.GetType(HostTypeName);
                var tryGetPort = host?.GetMethod("TryGetPortFor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (tryGetPort == null)
                    return null;
                var arguments = new object[] { doc, 0 };
                return (bool)tryGetPort.Invoke(null, arguments) ? (int?)(int)arguments[1] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
