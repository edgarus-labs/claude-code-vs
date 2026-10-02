using ClaudeCode.Contracts;
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ClaudeCode.Vsix.VsControl;

internal sealed partial class VsControlPipeServer
{
    private const int _defaultElementDepth = 12;
    private const int _maxElementDepth = 64;
    private const int _defaultElementCount = 500;
    private const int _maxElementCount = 5_000;
    private const int _maxElementValueChars = 200;
    private const int _maxCaptureSide = 1920;

    private const int _maxCaptureBytes = 4 * 1024 * 1024;
    private const int _maxCaptureEncodeAttempts = 3;

    private const int _maxOutstandingPrintWindows = 3;
    private const int _maxOutstandingUiBodies = 3;
    private const int _uiActionTimeoutMs = 5_000;
    private const uint _pumpProbeTimeoutMs = 1_000;
    private const int _maxZOrderWindows = 1_000;
    private const uint _pwRenderFullContent = 0x2;
    private const uint _gwHwndPrev = 3;
    private const uint _gwOwner = 4;
    private const uint _gaRoot = 2;
    private const uint _wmNull = 0x0;
    private const uint _smtoAbortIfHung = 0x2;
    private const int _dwmwaCloaked = 14;
    private const int _dwmwaExtendedFrameBounds = 9;
    private const int _gwlExStyle = -20;
    private const int _wsExTransparent = 0x20;
    private const int _wsExLayered = 0x80000;
    private const int _rgnError = 0;
    private const int _smXVirtualScreen = 76;
    private const int _smYVirtualScreen = 77;
    private const int _smCxVirtualScreen = 78;
    private const int _smCyVirtualScreen = 79;

    private static async Task<JObject> ListAppWindowsAsync(CancellationToken cancellationToken = default)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var processIds = GetDrivableProcessIds(debugger);
        var windows = new JArray();
        if (processIds.Count == 0)
        {
            return new JObject { ["windows"] = windows, ["note"] = "No app this tool may drive is being debugged (a debugged Visual Studio is excluded); use startDebugging first." };
        }

        foreach (var hwnd in EnumerateTopLevelWindows())
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains((int)pid) || !NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var bounds))
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
                ["bounds"] = RectToJson(bounds),
                ["isVisible"] = true,
                ["ownerHwnd"] = owner == IntPtr.Zero ? null : (long?)owner.ToInt64(),
            });
        }

        return new JObject { ["windows"] = windows };
    }

    private static async Task<JObject> GetWindowElementsAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);

        var maxDepth = Math.Min(_maxElementDepth, Math.Max(1, args["maxDepth"]?.Value<int?>() ?? _defaultElementDepth));
        var maxNodes = Math.Min(_maxElementCount, Math.Max(1, args["maxNodes"]?.Value<int?>() ?? _defaultElementCount));

        var budget = new ElementBudget(maxNodes - 1);

        var tree = await RunBoundedUiBodyAsync(() => ElementToJson(AutomationElement.FromHandle(hwnd), 0, maxDepth, budget), cancellationToken);
        if (tree is null)
        {
            return new JObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["pending"] = true,
                ["note"] = "The app has not answered the UI Automation walk yet; an app stopped at a breakpoint cannot answer it, so check getDebuggerState and continueDebugging before retrying.",
            };
        }

        return new JObject { ["hwnd"] = hwnd.ToInt64(), ["root"] = tree, ["truncated"] = budget.Truncated };
    }

    private static readonly HashSet<string> _elementActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "invoke",
        "toggle",
        "select",
        "expand",
        "collapse",
        "focus",
    };

    private static async Task<JObject> InvokeElementAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);
        var action = (args["action"]?.Value<string>() ?? "invoke").ToLowerInvariant();

        if (!_elementActions.Contains(action))
        {
            throw UnknownElementAction(action);
        }

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
                    throw UnknownElementAction(action);
            }

            return DescribeElement(element);
        }, cancellationToken);

        result["action"] = action;
        return result;
    }

    private static InvalidOperationException UnknownElementAction(string action) =>
        new InvalidOperationException($"Unknown action '{action}'; use invoke, toggle, select, expand, collapse or focus.");

    private static async Task<JObject> SetElementValueAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);
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
        }, cancellationToken);
    }

    private static async Task<JObject> RunUiActionAsync(string action, Func<JObject> body, CancellationToken cancellationToken)
    {
        var element = await RunBoundedUiBodyAsync(body, cancellationToken);
        if (element is null)
        {
            return new JObject
            {
                ["pending"] = true,
                ["note"] = $"The app has not finished processing '{action}' yet (it may be stopped at a breakpoint); use waitForBreak or getDebuggerState.",
            };
        }

        return new JObject { ["element"] = element };
    }

    private static async Task<JObject?> RunWithDeadlineAsync(Func<JObject?> body, CancellationToken cancellationToken)
    {
        var work = Task.Run(body);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_uiActionTimeoutMs);
        if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, deadline.Token)) == work)
        {
            return await work;
        }

        ObserveFault(work);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task<JObject> CaptureWindowAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var stoppedAtBreak = debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("The window no longer exists; use listAppWindows.");
        }

        var window = ToScreenRect(rect);
        if (!WindowCaptureRules.HasCapturableSize(window, VirtualScreenRect()))
        {
            return CaptureRefused(hwnd, "offscreen", "The window has no area, or is larger than the whole desktop, so there is nothing to capture; resize it and retry.");
        }

        var printed = stoppedAtBreak ? null : await PrintWindowCaptureAsync(hwnd, window, cancellationToken);

        return printed ?? await Task.Run(() => CopyFromScreenCapture(hwnd, window));
    }

    private static int _outstandingPrintWindows;

    private static int _outstandingUiBodies;

    private static Task<JObject?> RunBoundedUiBodyAsync(Func<JObject?> body, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstandingUiBodies) > _maxOutstandingUiBodies)
        {
            _ = Interlocked.Decrement(ref _outstandingUiBodies);
            return Task.FromResult<JObject?>(null);
        }

        return RunWithDeadlineAsync(
            () =>
            {
                try
                {
                    return body();
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _outstandingUiBodies);
                }
            },
            cancellationToken);
    }

    private static Task<JObject?> PrintWindowCaptureAsync(IntPtr hwnd, ScreenRect window, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstandingPrintWindows) > _maxOutstandingPrintWindows)
        {
            _ = Interlocked.Decrement(ref _outstandingPrintWindows);
            return Task.FromResult<JObject?>(null);
        }

        return RunWithDeadlineAsync(
            () =>
            {
                try
                {
                    return PrintWindowCapture(hwnd, window.Width, window.Height);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _outstandingPrintWindows);
                }
            },
            cancellationToken);
    }

    private static JObject? PrintWindowCapture(IntPtr hwnd, int width, int height)
    {
        if (NativeMethods.SendMessageTimeout(hwnd, _wmNull, IntPtr.Zero, IntPtr.Zero, _smtoAbortIfHung, _pumpProbeTimeoutMs, out _) == IntPtr.Zero)
        {
            return null;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
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
                return null;
            }
        }

        return EncodeCapture(hwnd, bitmap);
    }

    private static JObject CopyFromScreenCapture(IntPtr hwnd, ScreenRect window)
    {
        var refusal = RefuseUnlessExposed(hwnd, window);
        if (refusal is not null)
        {
            return refusal;
        }

        var painted = WindowCaptureRules.PaintedBounds(window, ExtendedFrameBounds(hwnd));
        using var bitmap = new Bitmap(painted.Width, painted.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(painted.Left, painted.Top, 0, 0, new Size(painted.Width, painted.Height));
        }

        return RefuseUnlessExposed(hwnd, window) ?? EncodeCapture(hwnd, bitmap);
    }

    private static JObject? RefuseUnlessExposed(IntPtr hwnd, ScreenRect window)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var current) || ToScreenRect(current) != window)
        {
            return CaptureRefused(hwnd, "moved", "The window moved or closed while it was being read; retry the capture.");
        }

        if (NativeMethods.GetAncestor(hwnd, _gaRoot) != hwnd)
        {
            return CaptureRefused(hwnd, "child", "This is a child window, and the pixels at a child's rectangle cannot be proven to be its own; capture the top-level window listAppWindows reports.");
        }

        var above = WindowsAbove(hwnd);
        if (above is null)
        {
            return CaptureRefused(hwnd, "occluded", "The desktop has too many windows to prove this one is uncovered; close some windows and retry.");
        }

        var exposure = WindowCaptureRules.Classify(
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsIconic(hwnd),
            IsCloaked(hwnd, whenUnknown: true),
            PaintsWholeRectangle(hwnd),
            window,
            VirtualScreenRect(),
            above);

        return exposure switch
        {
            WindowCaptureExposure.Exposed => null,
            WindowCaptureExposure.Hidden => CaptureRefused(hwnd, "hidden", "The window is not visible, so the desktop shows other applications at its rectangle; show it and retry."),
            WindowCaptureExposure.Minimized => CaptureRefused(hwnd, "minimized", "The window is minimized and paints nothing; restore it and retry."),
            WindowCaptureExposure.Cloaked => CaptureRefused(hwnd, "cloaked", "The window is parked on another virtual desktop, or suspended, so it paints nothing where it claims to be and the screen there belongs to other applications; switch to the desktop it is on and retry."),
            WindowCaptureExposure.Translucent => CaptureRefused(hwnd, "translucent", "The window is translucent, or its shape is not its rectangle, so the screen there holds the windows behind it as well; continue execution so the app can render itself instead."),
            WindowCaptureExposure.OffScreen => CaptureRefused(hwnd, "offscreen", "The window is empty or reaches outside the desktop, where nothing is painted; move it fully on screen and retry."),
            _ => CaptureRefused(hwnd, "occluded", "Another window is drawn over it, so reading the screen would return that application's pixels instead. While the app is stopped at a breakpoint Visual Studio itself is normally on top: continue execution and capture again, or bring the app to the front."),
        };
    }

    private static JObject CaptureRefused(IntPtr hwnd, string reason, string note) => new JObject
    {
        ["hwnd"] = hwnd.ToInt64(),
        ["captured"] = false,
        ["reason"] = reason,
        ["note"] = note,
    };

    private static List<ScreenRect>? WindowsAbove(IntPtr hwnd)
    {
        var above = new List<ScreenRect>();
        var visited = 0;
        for (var current = NativeMethods.GetWindow(hwnd, _gwHwndPrev); current != IntPtr.Zero; current = NativeMethods.GetWindow(current, _gwHwndPrev))
        {
            if (++visited > _maxZOrderWindows)
            {
                return null;
            }

            if (!NativeMethods.IsWindowVisible(current) || NativeMethods.IsIconic(current) || IsCloaked(current, whenUnknown: false))
            {
                continue;
            }

            if (NativeMethods.GetWindowRect(current, out var bounds))
            {
                above.Add(ToScreenRect(bounds));
            }
        }

        return above;
    }

    private static bool IsCloaked(IntPtr hwnd, bool whenUnknown) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, _dwmwaCloaked, out int cloaked, sizeof(int)) == 0
            ? cloaked != 0
            : whenUnknown;

    private static bool PaintsWholeRectangle(IntPtr hwnd) =>
        ((NativeMethods.GetWindowLong(hwnd, _gwlExStyle) & (_wsExLayered | _wsExTransparent)) == 0)
        && (NativeMethods.GetWindowRgnBox(hwnd, out _) == _rgnError);

    private static ScreenRect ExtendedFrameBounds(IntPtr hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, _dwmwaExtendedFrameBounds, out NativeMethods.RECT frame, Marshal.SizeOf<NativeMethods.RECT>()) == 0
            ? ToScreenRect(frame)
            : default;

    private static ScreenRect VirtualScreenRect()
    {
        var left = NativeMethods.GetSystemMetrics(_smXVirtualScreen);
        var top = NativeMethods.GetSystemMetrics(_smYVirtualScreen);
        return new ScreenRect(
            left,
            top,
            left + NativeMethods.GetSystemMetrics(_smCxVirtualScreen),
            top + NativeMethods.GetSystemMetrics(_smCyVirtualScreen));
    }

    private static ScreenRect ToScreenRect(NativeMethods.RECT rect) => new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static JObject EncodeCapture(IntPtr hwnd, Bitmap bitmap)
    {
        var scale = WindowCaptureRules.ScaleForLongestSide(bitmap.Width, bitmap.Height, _maxCaptureSide);
        for (var attempt = 1; ; attempt++)
        {
            using var scaled = scale < 1.0 ? Downscale(bitmap, scale) : null;
            var output = scaled ?? bitmap;
            using var stream = new MemoryStream();
            output.Save(stream, ImageFormat.Png);

            if (stream.Length <= _maxCaptureBytes)
            {
                return new JObject
                {
                    ["hwnd"] = hwnd.ToInt64(),
                    ["captured"] = true,
                    ["width"] = output.Width,
                    ["height"] = output.Height,
                    ["scale"] = scale,
                    ["_image"] = new JObject
                    {
                        ["mimeType"] = "image/png",
                        ["data"] = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length),
                    },
                };
            }

            if (attempt >= _maxCaptureEncodeAttempts)
            {
                throw new InvalidOperationException(
                    $"The capture still encodes to {stream.Length} bytes at scale {scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, over the {_maxCaptureBytes}-byte limit; capture a smaller window.");
            }

            scale /= 2.0;
        }
    }

    private static Bitmap Downscale(Bitmap source, double scale)
    {
        var scaled = new Bitmap(
            WindowCaptureRules.ScaleDimension(source.Width, scale),
            WindowCaptureRules.ScaleDimension(source.Height, scale),
            PixelFormat.Format24bppRgb);

        try
        {
            using var graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }
        catch
        {
            scaled.Dispose();
            throw;
        }

        return scaled;
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<IntPtr> ResolveDebuggedWindowAsync(JObject args, CancellationToken cancellationToken)
    {
        var handle = args["hwnd"]?.Value<long?>() ?? throw new InvalidOperationException("Missing required parameter 'hwnd'.");
        var hwnd = new IntPtr(handle);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var processIds = GetDrivableProcessIds(debugger);

        if (!NativeMethods.IsWindow(hwnd))
        {
            throw new InvalidOperationException($"{handle} is not a window handle; use listAppWindows.");
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (!processIds.Contains((int)pid))
        {
            throw new InvalidOperationException("That window does not belong to a process this tool may drive; only apps under the debugger can be driven, and a debugged Visual Studio is excluded.");
        }

        return hwnd;
    }

    private static HashSet<int> GetDrivableProcessIds(EnvDTE.Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var ids = new HashSet<int>();
        try
        {
            foreach (EnvDTE.Process process in debugger.DebuggedProcesses)
            {
                if (!IsVisualStudioImage(process.Name))
                {
                    _ = ids.Add(process.ProcessID);
                }
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidComObjectException)
        {
        }

        return ids;
    }

    private static bool IsVisualStudioImage(string? imagePath) =>
        imagePath is not null
        && (imagePath.EndsWith(@"\devenv.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(imagePath, "devenv.exe", StringComparison.OrdinalIgnoreCase));

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
            element = FindByRuntimeId(root, runtimeId!, 0, new ElementBudget(_maxElementCount - 1));
        }
        else
        {
            var byAutomationId = !string.IsNullOrEmpty(automationId);
            var property = byAutomationId ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty;
            element = root.FindFirst(TreeScope.Element | TreeScope.Descendants, new PropertyCondition(property, byAutomationId ? automationId : name));
        }

        return element ?? throw new InvalidOperationException("No matching element; use getWindowElements to see what the window contains.");
    }

    private static AutomationElement? FindByRuntimeId(AutomationElement element, string runtimeId, int depth, ElementBudget budget)
    {
        if (string.Equals(FormatRuntimeId(element.GetRuntimeId()), runtimeId, StringComparison.Ordinal))
        {
            return element;
        }

        if (depth >= _maxElementDepth)
        {
            return null;
        }

        var walker = TreeWalker.ControlViewWalker;
        for (var child = walker.GetFirstChild(element); child is not null && budget.TryTake(); child = walker.GetNextSibling(child))
        {
            var found = FindByRuntimeId(child, runtimeId, depth + 1, budget);
            if (found is not null)
            {
                return found;
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

    private static JObject ElementToJson(AutomationElement element, int depth, int maxDepth, ElementBudget budget)
    {
        var current = element.Current;
        var node = new JObject
        {
            ["runtimeId"] = FormatRuntimeId(element.GetRuntimeId()),
            ["controlType"] = ControlTypeName(current.ControlType),
            ["name"] = NullIfEmpty(CapValue(current.Name)),
            ["automationId"] = NullIfEmpty(CapValue(current.AutomationId)),
            ["className"] = NullIfEmpty(CapValue(current.ClassName)),
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
            node["value"] = CapValue(((ValuePattern)value).Current.Value);
        }

        if (current.IsKeyboardFocusable) actions.Add("focus");
        node["actions"] = actions;

        var walker = TreeWalker.ControlViewWalker;
        if (depth >= maxDepth)
        {
            if (!budget.Truncated && walker.GetFirstChild(element) is not null)
            {
                budget.MarkTruncated();
            }

            return node;
        }

        var children = new JArray();
        for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
        {
            if (!budget.TryTake())
            {
                break;
            }

            children.Add(ElementToJson(child, depth + 1, maxDepth, budget));
        }

        if (children.Count > 0)
        {
            node["children"] = children;
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
            ["name"] = NullIfEmpty(CapValue(current.Name)),
            ["automationId"] = NullIfEmpty(CapValue(current.AutomationId)),
            ["isEnabled"] = current.IsEnabled,
        };

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) json["toggleState"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) json["value"] = CapValue(((ValuePattern)value).Current.Value);
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) json["isSelected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) json["expandCollapseState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        return json;
    }

    private static string FormatRuntimeId(int[]? runtimeId) => runtimeId is null ? string.Empty : string.Join(".", runtimeId);

    private static string ControlTypeName(ControlType? controlType) =>
        controlType?.ProgrammaticName.Replace("ControlType.", string.Empty) ?? "Unknown";

    private static JValue NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? JValue.CreateNull() : new JValue(value);

    private static string CapValue(string? value) =>
        VsDebuggerChannelRules.TruncateDebuggeeValue(value, _maxElementValueChars);

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int GetWindowRgnBox(IntPtr hWnd, out RECT lprc);
    }
}
