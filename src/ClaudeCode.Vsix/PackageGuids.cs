using System;

namespace ClaudeCode.Vsix;

internal static class PackageGuids
{
    /// <summary>
    /// The claude code package string.
    /// </summary>
    public const string ClaudeCodePackageString = "8f5a6e3b-8f1a-4a6a-9d1a-3c2f7f9d2b40";
    /// <summary>
    /// The claude code command set string.
    /// </summary>
    public const string ClaudeCodeCommandSetString = "3a9b6f1e-3e7c-4f2f-9c2a-2b6f5a7d9e11";
    /// <summary>
    /// The chat tool window persistance string.
    /// </summary>
    public const string ChatToolWindowPersistanceString = "5d2c9a7f-6b3e-4a1d-8f0c-1a9d7e4b2c63";
    /// <summary>
    /// The claude code images string.
    /// </summary>
    public const string ClaudeCodeImagesString = "6a1e4f2b-9c3d-47a8-b5e1-2d8f6c4a9e70";
    /// <summary>
    /// The plan tool window persistance string.
    /// </summary>
    public const string PlanToolWindowPersistanceString = "9c4b2e7d-1f5a-4c8e-a3b6-7e2d9f1c5a48";

    /// <summary>
    /// The claude code images.
    /// </summary>
    public static readonly Guid ClaudeCodeImages = new Guid(ClaudeCodeImagesString);
}
