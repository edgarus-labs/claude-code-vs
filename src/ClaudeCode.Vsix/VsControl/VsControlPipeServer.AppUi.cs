using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ClaudeCode.Vsix.VsControl;

/// <summary>
/// Lets the agent look at and drive the windows of the application under the debugger through UI
/// Automation. Every method first resolves the requested window handle against the set of processes the
/// VS debugger currently owns; a window of any other process (the user's browser, password manager,
/// Visual Studio itself) is rejected before any UIA call is made.
/// </summary>
internal sealed partial class VsControlPipeServer
{
    private const int _defaultElementDepth = 12;
    private const int _defaultElementCount = 500;
    private const int _maxElementCount = 5_000;
    private const int _maxElementValueChars = 200;
    private const int _maxCaptureSide = 1920;
    private const int _uiActionTimeoutMs = 5_000;
    private const uint _pwRenderFullContent = 0x2;
    private const uint _gwOwner = 4;

    private static async Task<JObject> ListAppWindowsAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        var processIds = GetDebuggedProcessIds(debugger);
        var windows = new JArray();
        if (processIds.Count == 0)
        {
            return new JObject { ["windows"] = windows, ["note"] = "No process is being debugged; use startDebugging first." };
        }

        foreach (var hwnd in EnumerateTopLevelWindows())
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains((int)pid) || !NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }

            var owner = NativeMethods.GetWindow(hwnd, _gwOwner);
            windows.Add(new JObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["processId"] = (int)pid,
                ["title"] = GetWindowText(hwnd),
                ["className"] = GetClassName(hwnd),
                ["bounds"] = RectToJson(GetWindowRect(hwnd)),
                ["isVisible"] = true,
                ["ownerHwnd"] = owner == IntPtr.Zero ? null : (long?)owner.ToInt64(),
            });
        }

        return new JObject { ["windows"] = windows };
    }

    private static async Task<JObject> GetWindowElementsAsync(JObject args)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args);
        var maxDepth = Math.Max(1, args["maxDepth"]?.Value<int?>() ?? _defaultElementDepth);
        var budget = Math.Min(_maxElementCount, Math.Max(1, args["maxNodes"]?.Value<int?>() ?? _defaultElementCount));

        // UIA calls are cross-process and can stall while the target's UI thread is busy or stopped
        // at a breakpoint; none of them may run on the VS UI thread.
        var (tree, remaining) = await Task.Run(() =>
        {
            var root = AutomationElement.FromHandle(hwnd);
            var json = ElementToJson(root, 0, maxDepth, ref budget);
            return (json, budget);
        });

        return new JObject { ["hwnd"] = hwnd.ToInt64(), ["root"] = tree, ["truncated"] = remaining <= 0 };
    }

    private static async Task<JObject> InvokeElementAsync(JObject args)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args);
        var action = (args["action"]?.Value<string>() ?? "invoke").ToLowerInvariant();

        var result = await RunUiActionAsync(action, () =>
        {
            var element = FindElement(hwnd, args);
            switch (action)
            {
                case "invoke":
                    RequirePattern<InvokePattern>(element, InvokePattern.Pattern, action).Invoke();
                    break;
                case "toggle":
                    RequirePattern<TogglePattern>(element, TogglePattern.Pattern, action).Toggle();
                    break;
                case "select":
                    RequirePattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern, action).Select();
                    break;
                case "expand":
                    RequirePattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, action).Expand();
                    break;
                case "collapse":
                    RequirePattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, action).Collapse();
                    break;
                case "focus":
                    element.SetFocus();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown action '{action}'; use invoke, toggle, select, expand, collapse or focus.");
            }

            return DescribeElement(element);
        });

        result["action"] = action;
        return result;
    }

    private static async Task<JObject> SetElementValueAsync(JObject args)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args);
        var value = args["value"]?.Value<string>() ?? throw new InvalidOperationException("Missing required parameter 'value'.");

        return await RunUiActionAsync("setValue", () =>
        {
            var element = FindElement(hwnd, args);
            var pattern = RequirePattern<ValuePattern>(element, ValuePattern.Pattern, "setValue");
            if (pattern.Current.IsReadOnly)
            {
                throw new InvalidOperationException("The element is read-only.");
            }

            pattern.SetValue(value);
            return DescribeElement(element);
        });
    }

    /// <summary>
    /// Runs a UIA action on a background thread. WPF's providers block Invoke/SetValue until the app's
    /// handler returns, so a click that lands on a breakpoint would otherwise hang this call (and, if
    /// it ran on the VS UI thread, Visual Studio itself). After <see cref="_uiActionTimeoutMs"/> the
    /// call reports <c>pending: true</c> instead; the action itself keeps running in the app.
    /// </summary>
    private static async Task<JObject> RunUiActionAsync(string action, Func<JObject> body)
    {
        var work = Task.Run(body);
        var finished = await Task.WhenAny(work, Task.Delay(_uiActionTimeoutMs));
        if (finished != work)
        {
            return new JObject
            {
                ["pending"] = true,
                ["note"] = $"The app has not finished processing '{action}' yet (it may be stopped at a breakpoint); use waitForBreak or getDebuggerState.",
            };
        }

        var element = await work; // rethrows the action's own error
        return new JObject { ["element"] = element };
    }

    private static async Task<JObject> CaptureWindowAsync(JObject args)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args);
        var rect = GetWindowRect(hwnd);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            bool printed;
            try
            {
                printed = NativeMethods.PrintWindow(hwnd, hdc, _pwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (!printed)
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
            }
        }

        var scale = Math.Min(1.0, (double)_maxCaptureSide / Math.Max(width, height));
        using var output = scale < 1.0 ? Downscale(bitmap, scale) : bitmap;
        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);

        return new JObject
        {
            ["hwnd"] = hwnd.ToInt64(),
            ["width"] = output.Width,
            ["height"] = output.Height,
            ["scale"] = scale,
            ["_image"] = new JObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(stream.ToArray()) },
        };
    }

    private static Bitmap Downscale(Bitmap source, double scale)
    {
        var scaled = new Bitmap((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        return scaled;
    }

    /// <summary>The trust check shared by every window-scoped method.</summary>
    private static async Task<IntPtr> ResolveDebuggedWindowAsync(JObject args)
    {
        var handle = args["hwnd"]?.Value<long?>() ?? throw new InvalidOperationException("Missing required parameter 'hwnd'.");
        var hwnd = new IntPtr(handle);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        var processIds = GetDebuggedProcessIds(debugger);

        if (!NativeMethods.IsWindow(hwnd))
        {
            throw new InvalidOperationException($"{handle} is not a window handle; use listAppWindows.");
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (!processIds.Contains((int)pid))
        {
            throw new InvalidOperationException("That window does not belong to a process under the debugger; only debugged apps can be driven.");
        }

        return hwnd;
    }

    private static AutomationElement FindElement(IntPtr hwnd, JObject args)
    {
        var runtimeId = args["runtimeId"]?.Value<string>();
        var automationId = args["automationId"]?.Value<string>();
        var name = args["name"]?.Value<string>();
        var selectors = new[] { runtimeId, automationId, name }.Count(s => !string.IsNullOrEmpty(s));
        if (selectors != 1)
        {
            throw new InvalidOperationException("Specify exactly one of runtimeId, automationId or name.");
        }

        var root = AutomationElement.FromHandle(hwnd);
        AutomationElement? element;
        if (!string.IsNullOrEmpty(runtimeId))
        {
            element = FindByRuntimeId(root, runtimeId!);
        }
        else
        {
            var property = !string.IsNullOrEmpty(automationId) ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty;
            element = root.FindFirst(TreeScope.Element | TreeScope.Descendants, new PropertyCondition(property, automationId ?? name));
        }

        return element ?? throw new InvalidOperationException("No matching element; use getWindowElements to see what the window contains.");
    }

    private static AutomationElement? FindByRuntimeId(AutomationElement root, string runtimeId)
    {
        var budget = _maxElementCount;
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0 && budget-- > 0)
        {
            var element = stack.Pop();
            if (string.Equals(FormatRuntimeId(element.GetRuntimeId()), runtimeId, StringComparison.Ordinal))
            {
                return element;
            }

            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
            {
                stack.Push(child);
            }
        }

        return null;
    }

    private static T RequirePattern<T>(AutomationElement element, AutomationPattern pattern, string action) where T : BasePattern
    {
        if (element.TryGetCurrentPattern(pattern, out var found) && found is T typed)
        {
            return typed;
        }

        var supported = string.Join(", ", element.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", string.Empty)));
        throw new InvalidOperationException($"The element does not support '{action}'. Supported patterns: {(supported.Length == 0 ? "none" : supported)}.");
    }

    // Live (Current) reads rather than a CacheRequest: TreeWalker.GetFirstChild/GetNextSibling do not
    // populate the active request for the elements they return, so cached reads throw on every child.
    private static JObject ElementToJson(AutomationElement element, int depth, int maxDepth, ref int budget)
    {
        budget--;
        var current = element.Current;
        var node = new JObject
        {
            ["runtimeId"] = FormatRuntimeId(element.GetRuntimeId()),
            ["controlType"] = ControlTypeName(current.ControlType),
            ["name"] = NullIfEmpty(current.Name),
            ["automationId"] = NullIfEmpty(current.AutomationId),
            ["className"] = NullIfEmpty(current.ClassName),
            ["bounds"] = RectToJson(current.BoundingRectangle),
            ["isEnabled"] = current.IsEnabled,
            ["isOffscreen"] = current.IsOffscreen,
        };

        var actions = new JArray();
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) actions.Add("invoke");
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            actions.Add("toggle");
            node["toggleState"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            actions.Add("select");
            node["isSelected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        }

        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            actions.Add("expand");
            actions.Add("collapse");
            node["expandCollapseState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
        {
            actions.Add("setValue");
            var text = ((ValuePattern)value).Current.Value ?? string.Empty;
            node["value"] = text.Length > _maxElementValueChars ? text.Substring(0, _maxElementValueChars) + "…" : text;
        }

        if (current.IsKeyboardFocusable) actions.Add("focus");
        node["actions"] = actions;

        if (depth < maxDepth && budget > 0)
        {
            var children = new JArray();
            var walker = TreeWalker.ControlViewWalker;
            for (var child = walker.GetFirstChild(element); child is not null && budget > 0; child = walker.GetNextSibling(child))
            {
                children.Add(ElementToJson(child, depth + 1, maxDepth, ref budget));
            }

            if (children.Count > 0) node["children"] = children;
        }

        return node;
    }

    private static JObject DescribeElement(AutomationElement element)
    {
        var current = element.Current;
        var json = new JObject
        {
            ["runtimeId"] = FormatRuntimeId(element.GetRuntimeId()),
            ["controlType"] = ControlTypeName(current.ControlType),
            ["name"] = NullIfEmpty(current.Name),
            ["automationId"] = NullIfEmpty(current.AutomationId),
            ["isEnabled"] = current.IsEnabled,
        };

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) json["toggleState"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) json["value"] = ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) json["isSelected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) json["expandCollapseState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        return json;
    }

    private static string FormatRuntimeId(int[]? runtimeId) => runtimeId is null ? string.Empty : string.Join(".", runtimeId);

    private static string ControlTypeName(ControlType? controlType) =>
        controlType?.ProgrammaticName.Replace("ControlType.", string.Empty) ?? "Unknown";

    private static JValue NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? JValue.CreateNull() : new JValue(value);

    private static JObject RectToJson(System.Windows.Rect rect) => rect.IsEmpty
        ? new JObject()
        : new JObject { ["x"] = (int)rect.X, ["y"] = (int)rect.Y, ["width"] = (int)rect.Width, ["height"] = (int)rect.Height };

    private static JObject RectToJson(NativeMethods.RECT rect) => new JObject
    {
        ["x"] = rect.Left,
        ["y"] = rect.Top,
        ["width"] = rect.Right - rect.Left,
        ["height"] = rect.Bottom - rect.Top,
    };

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        var handles = new List<IntPtr>();
        _ = NativeMethods.EnumWindows((hwnd, _) => { handles.Add(hwnd); return true; }, IntPtr.Zero);
        return handles;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new char[512];
        var length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static NativeMethods.RECT GetWindowRect(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowRect(hwnd, out var rect);
        return rect;
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    }
}
