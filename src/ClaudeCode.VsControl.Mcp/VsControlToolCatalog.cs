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
            "Build, rebuild or clean the current solution and wait for completion, returning success and error/warning counts.",
            """
            {"type":"object","properties":{
              "action":{"type":"string","enum":["build","rebuild","clean"],"description":"What to do; default build."},
              "configuration":{"type":"string","description":"Optional build configuration name. Use the bare name as listed in the solution - Release, not Release|Any CPU. An unrecognized name is ignored and the solution builds in whatever configuration was already active, so compare the result's configuration field against what you asked for."}
            },"additionalProperties":true}
            """),

        new(
            "buildProject",
            "Build, rebuild or clean one project of the open solution and wait for completion.",
            """
            {"type":"object","properties":{
              "projectName":{"type":"string","description":"Name of the project as listed by getSolutionInfo."},
              "action":{"type":"string","enum":["build","rebuild","clean"],"description":"What to do; default build."}
            },"required":["projectName"],"additionalProperties":true}
            """),

        new(
            "getBuildErrors",
            "Read the Visual Studio Error List: errors, warnings and messages, optionally filtered by severity. At most " +
            "1000 rows are returned; truncated: true means the Error List has more (narrow with severity).",
            """
            {"type":"object","properties":{
              "severity":{"type":"string","enum":["error","warning","message"],"description":"Return only rows of this severity; default all."}
            },"additionalProperties":true}
            """),

        new(
            "getOutput",
            "Read the text of a Visual Studio Output window pane (Build, Debug, General, ...). Use clear=true before a run to " +
            "capture only fresh log lines, and read the Debug pane while debugging to see the app's Debug.WriteLine/Console output.",
            """
            {"type":"object","properties":{
              "pane":{"type":"string","description":"Pane name; default Debug. Build, Debug and General resolve the built-in panes even on a localized Visual Studio; any other pane is matched by its displayed name (case-insensitive)."},
              "maxChars":{"type":"integer","description":"Return at most this many trailing characters; default 20000, capped at 200000."},
              "clear":{"type":"boolean","description":"Clear the pane instead of reading it."}
            },"additionalProperties":true}
            """),

        new(
            "startDebugging",
            "Start the startup project (or the named project) under the Visual Studio debugger and wait until it is running " +
            "or stopped at a breakpoint. This executes the solution's code.",
            """
            {"type":"object","properties":{
              "projectName":{"type":"string","description":"Optional project to make the startup project first."},
              "configuration":{"type":"string","description":"Optional solution configuration to activate first. Use the bare name as listed in the solution - Debug, not Debug|Any CPU. An unrecognized name is ignored and the build and launch use whatever configuration was already active."},
              "waitForBreakMs":{"type":"integer","maximum":45000,"description":"How long to wait for a breakpoint hit after start; default 3000, capped at 45000."}
            },"additionalProperties":true}
            """),

        new(
            "stopDebugging",
            "Stop the current debugging session and terminate the debugged processes.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "getDebuggerState",
            "Get the debugger mode (design/run/break), the last break reason, the current stack frame location and the debugged processes.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "setBreakpoint",
            "Set a breakpoint at a file line, optionally with a break-when-true condition.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute or solution-relative source file path."},
              "line":{"type":"integer","description":"1-based line."},
              "condition":{"type":"string","description":"Optional break-when-true expression. It is evaluated inside the debugged process every time the line is reached, so property getters and method calls in it really execute - same caveat as evaluateExpression, but on every hit and without a timeout."}
            },"required":["path","line"],"additionalProperties":true}
            """),

        new(
            "removeBreakpoint",
            "Remove the breakpoints at a file line, all breakpoints in a file, or - when neither path nor line is given - " +
            "every breakpoint in the solution, including ones the user set by hand and cannot restore. Because omitting " +
            "both parameters is the destructive form, this is the one tool that rejects unrecognized parameter names " +
            "instead of ignoring them: send path, not file or filePath.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Source file path; omit path and line together to remove every breakpoint."},
              "line":{"type":"integer","description":"1-based line; requires path."}
            },"additionalProperties":false}
            """),

        new(
            "listBreakpoints",
            "List the breakpoints of the current solution.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "continueDebugging",
            "Resume execution from a breakpoint and wait for the next break, program end or timeout.",
            """
            {"type":"object","properties":{
              "waitForBreakMs":{"type":"integer","maximum":45000,"description":"How long to wait for the next break; default 5000, capped at 45000."}
            },"additionalProperties":true}
            """),

        new(
            "stepOver",
            "Step over the current line and wait for the debugger to break again.",
            """{"type":"object","properties":{"waitForBreakMs":{"type":"integer","maximum":45000,"description":"How long to wait for the next break; default 5000, capped at 45000."}},"additionalProperties":true}"""),

        new(
            "stepInto",
            "Step into the call on the current line and wait for the debugger to break again.",
            """{"type":"object","properties":{"waitForBreakMs":{"type":"integer","maximum":45000,"description":"How long to wait for the next break; default 5000, capped at 45000."}},"additionalProperties":true}"""),

        new(
            "stepOut",
            "Step out of the current function and wait for the debugger to break again.",
            """{"type":"object","properties":{"waitForBreakMs":{"type":"integer","maximum":45000,"description":"How long to wait for the next break; default 5000, capped at 45000."}},"additionalProperties":true}"""),

        new(
            "waitForBreak",
            "Wait until the debugger stops at a breakpoint/exception or the program ends, up to a timeout.",
            """
            {"type":"object","properties":{
              "timeoutMs":{"type":"integer","maximum":45000,"description":"Maximum wait; default 10000, capped at 45000."}
            },"additionalProperties":true}
            """),

        new(
            "getCallStack",
            "Get the call stack of the current thread while the debugger is in break mode.",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "getLocals",
            "Get the local variables (name, type, value) of a stack frame while in break mode.",
            """
            {"type":"object","properties":{
              "frameIndex":{"type":"integer","description":"Stack frame to read, numbered like getCallStack: 0 = innermost frame (default), 1 = its caller, and so on. The frame is selected first, so the result's currentFrame agrees with it."}
            },"additionalProperties":true}
            """),

        new(
            "evaluateExpression",
            "Evaluate an expression in a stack frame while in break mode (like the Watch window). " +
            "Evaluation runs code inside the debugged process: property getters and method calls in the " +
            "expression really execute and can have side effects.",
            """
            {"type":"object","properties":{
              "expression":{"type":"string"},
              "frameIndex":{"type":"integer","description":"Optional stack frame to evaluate in, numbered like getCallStack: 0 = innermost frame, 1 = its caller, and so on. The frame is selected first, so the result's currentFrame agrees with it; omitted, the expression is evaluated in the frame Visual Studio has selected."},
              "timeoutMs":{"type":"integer","maximum":5000,"description":"Evaluation timeout; default 3000, capped at 5000."}
            },"required":["expression"],"additionalProperties":true}
            """),

        new(
            "listAppWindows",
            "List the top-level windows of the processes currently under the debugger (title, class, bounds, handle).",
            """{"type":"object","properties":{},"additionalProperties":true}"""),

        new(
            "getWindowElements",
            "Get the UI Automation element tree of a debugged app window: control types, names, automation ids, values, " +
            "bounds and supported actions. Use it to find what to click or type into. If the app has not answered the " +
            "walk within 5 s - normal for an app stopped at a breakpoint - the result is {hwnd, pending: true, note} with no root.",
            """
            {"type":"object","properties":{
              "hwnd":{"type":"integer","description":"Window handle from listAppWindows."},
              "maxDepth":{"type":"integer","description":"Tree depth limit; default 12, capped at 64."},
              "maxNodes":{"type":"integer","description":"Node count limit; default 500, capped at 5000."}
            },"required":["hwnd"],"additionalProperties":true}
            """),

        new(
            "invokeElement",
            "Act on a UI element of a debugged app window: invoke (click) a button, toggle a checkbox, select an item, " +
            "expand/collapse, or focus it.",
            """
            {"type":"object","properties":{
              "hwnd":{"type":"integer"},
              "runtimeId":{"type":"string","description":"runtimeId from getWindowElements (most precise)."},
              "automationId":{"type":"string"},
              "name":{"type":"string","description":"Element name/label, exact match."},
              "action":{"type":"string","enum":["invoke","toggle","select","expand","collapse","focus"],"description":"Default invoke."}
            },"required":["hwnd"],"oneOf":[{"required":["runtimeId"]},{"required":["automationId"]},{"required":["name"]}],"additionalProperties":true}
            """),

        new(
            "setElementValue",
            "Set the text/value of an editable UI element (text box, combo box) in a debugged app window.",
            """
            {"type":"object","properties":{
              "hwnd":{"type":"integer"},
              "runtimeId":{"type":"string"},
              "automationId":{"type":"string"},
              "name":{"type":"string"},
              "value":{"type":"string"}
            },"required":["hwnd","value"],"oneOf":[{"required":["runtimeId"]},{"required":["automationId"]},{"required":["name"]}],"additionalProperties":true}
            """),

        new(
            "captureWindow",
            "Take a PNG screenshot of a debugged app window and return it as an image. While the app is running the " +
            "window is asked to render itself, so one sitting behind other windows still captures fine. When it cannot " +
            "render - at a breakpoint, or if the render is declined - the desktop is read at the window's rectangle " +
            "instead, and only that path can refuse: it answers {hwnd, captured:false, reason, note} with a reason of " +
            "moved, child, offscreen, hidden, minimized, cloaked, translucent or occluded rather than return pixels it " +
            "cannot prove are the window's own. A stopped app cannot paint itself or answer UI Automation: at a " +
            "breakpoint this refuses (occluded/translucent) and getWindowElements reports pending, so continue execution " +
            "(continueDebugging) first, then capture or walk the window.",
            """
            {"type":"object","properties":{
              "hwnd":{"type":"integer","description":"Window handle from listAppWindows."}
            },"required":["hwnd"],"additionalProperties":true}
            """),

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
            "openSolution",
            "Open a solution file (.sln/.slnx) from inside the workspace in Visual Studio, closing the current one if any. " +
            "Use after creating a new solution on disk (e.g. with dotnet new) so the build and debugger tools can work on it.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute or workspace-relative path of the solution file."}
            },"required":["path"],"additionalProperties":true}
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
