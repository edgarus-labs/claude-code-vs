using System;

namespace ClaudeCode.Vsix
{
    /// <summary>GUIDs shared between the managed code and <c>ClaudeCode.vsct</c>. Keep in sync manually -
    /// this project does not use a source generator for the command table.</summary>
    internal static class PackageGuids
    {
        public const string ClaudeCodePackageString = "8f5a6e3b-8f1a-4a6a-9d1a-3c2f7f9d2b40";
        public const string ClaudeCodeCommandSetString = "3a9b6f1e-3e7c-4f2f-9c2a-2b6f5a7d9e11";
        public const string ChatToolWindowPersistanceString = "5d2c9a7f-6b3e-4a1d-8f0c-1a9d7e4b2c63";

        public static readonly Guid ClaudeCodePackage = new Guid(ClaudeCodePackageString);
        public static readonly Guid ClaudeCodeCommandSet = new Guid(ClaudeCodeCommandSetString);
        public static readonly Guid ChatToolWindowPersistance = new Guid(ChatToolWindowPersistanceString);
    }
}
