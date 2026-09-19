using System.Collections.Generic;

namespace ClaudeCode.VsControl.Mcp;

public static class VsControlToolCatalog
{
    public static IReadOnlyList<VsControlToolDefinition> Tools { get; } = new List<VsControlToolDefinition>
    {
        new(
            "listOpenDocuments",
            "List documents currently open in the Visual Studio editor, including dirty and active state.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "openDocument",
            "Open or activate a file in the Visual Studio editor, optionally moving the caret to a line.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute or solution-relative path of the file to open."},
              "line":{"type":"integer","description":"Optional 1-based line number to move the caret to."}
            },"required":["path"],"additionalProperties":true}
            """),

        new(
            "getActiveDocument",
            "Get the path, full text, and selection range of the document currently active in the Visual Studio editor.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "getSelection",
            "Get the file path, selected text, and line range of the current text selection in Visual Studio.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "replaceSelection",
            "Replace the current selection (or caret position) in the specified document with new text.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Path of the document to edit."},
              "text":{"type":"string","description":"Replacement text."}
            },"required":["path","text"],"additionalProperties":true}
            """),

        new(
            "saveAll",
            "Save all open, modified documents in the solution.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "buildSolution",
            "Build the current solution and wait for completion, returning success and error/warning counts.",
            """
            {"type":"object","properties":{
              "configuration":{"type":"string","description":"Optional build configuration name, e.g. Debug or Release."}
            },"additionalProperties":true}
            """),

        new(
            "getBuildErrors",
            "Read the current contents of the Visual Studio Error List.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "getDiagnostics",
            "Get language-service diagnostics (squiggles) for a specific file.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Path of the file to inspect."}
            },"required":["path"],"additionalProperties":true}
            """),

        new(
            "runCommand",
            "Invoke a Visual Studio DTE command by name. Allow-listed by the extension host to: " +
            "Edit.FormatDocument, Edit.FormatSelection, Debug.StopDebugging, File.SaveAll, View.ErrorList.",
            """
            {"type":"object","properties":{
              "commandName":{"type":"string","description":"DTE command name, e.g. Edit.FormatDocument."},
              "args":{"type":"string","description":"Optional argument string passed to DTE.ExecuteCommand."}
            },"required":["commandName"],"additionalProperties":true}
            """),

        new(
            "getSolutionInfo",
            "Get the path of the currently open solution and the list of projects it contains.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "addFileToProject",
            "Include an existing file from disk in a Visual Studio project (needed for non-SDK-style projects, " +
            "where files are not picked up automatically). The file must already exist inside the workspace.",
            """
            {"type":"object","properties":{
              "projectName":{"type":"string","description":"Name of the target project as listed by getSolutionInfo."},
              "path":{"type":"string","description":"Absolute or solution-relative path of the file to include."}
            },"required":["projectName","path"],"additionalProperties":true}
            """),

        new(
            "addProjectToSolution",
            "Add an existing project file (.csproj, .vbproj, .vcxproj, ...) from inside the workspace to the open solution.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute or solution-relative path of the project file to add."}
            },"required":["path"],"additionalProperties":true}
            """),
    };
}
