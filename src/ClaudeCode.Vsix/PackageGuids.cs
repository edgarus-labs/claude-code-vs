using System;

namespace ClaudeCode.Vsix;

internal static class PackageGuids
{
    public const string ClaudeCodePackageString = "8f5a6e3b-8f1a-4a6a-9d1a-3c2f7f9d2b40";
    public const string ClaudeCodeCommandSetString = "3a9b6f1e-3e7c-4f2f-9c2a-2b6f5a7d9e11";
    public const string ChatToolWindowPersistanceString = "5d2c9a7f-6b3e-4a1d-8f0c-1a9d7e4b2c63";
    public const string ClaudeCodeImagesString = "6a1e4f2b-9c3d-47a8-b5e1-2d8f6c4a9e70";
    public const string PlanToolWindowPersistanceString = "9c4b2e7d-1f5a-4c8e-a3b6-7e2d9f1c5a48";

    // Parsed form only where code actually needs a Guid - attributes take the string constants
    // above, so a companion field per string would be dead weight (ClaudeCodePackage,
    // ClaudeCodeCommandSet, ChatToolWindowPersistance and PlanToolWindowPersistance all had, or
    // would have had, no consumer).
    public static readonly Guid ClaudeCodeImages = new Guid(ClaudeCodeImagesString);
}
