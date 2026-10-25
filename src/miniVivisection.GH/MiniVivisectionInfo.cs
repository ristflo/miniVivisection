using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace MiniVivisection.GH
{
    /// <summary>The .gha's description for Grasshopper.</summary>
    public sealed class MiniVivisectionInfo : GH_AssemblyInfo
    {
        public override string Name => "miniVivisection";

        public override string Description => GhNames.Text("V Edit: edit Grasshopper geometry in Rhino. V Text: a Panel's text as Rhino text.");

        public override Guid Id => new Guid("6d0f4c1e-6a8b-4f0e-9b5d-2f1b3e7c9a41");

        public override string AuthorName => "miniVivisection Authors";

        public override string AuthorContact => "";

        public override string Version => MiniVivisection.VersionInfo.Full;

        public override string AssemblyVersion => MiniVivisection.VersionInfo.Version;

        public override Bitmap Icon => Icons.EditInRhino;
    }
}
