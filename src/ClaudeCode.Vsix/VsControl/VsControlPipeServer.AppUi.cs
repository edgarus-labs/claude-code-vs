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

/// <summary>
/// Lets the agent look at and drive the windows of the application under the debugger through UI
/// Automation. Every method first resolves the requested window handle against the set of processes the
/// VS debugger currently owns; a window of any other process (the user's browser, password manager,
/// Visual Studio itself) is rejected before any UIA call is made. A debugged Visual Studio - the
/// experimental instance an F5 on a VSIX project launches - is rejected too: driving a full IDE would
/// hand the agent File > Open and the Command Window.
/// <para>
/// Screenshots never return pixels that are not provably the window's own. <c>PrintWindow</c> asks the
/// app to render itself and is unaffected by what is on top of it; the desktop-reading fallback is
/// allowed only while the window is visible, restored, entirely on screen, unmoved and uncovered, and
/// refuses with <c>captured: false</c> otherwise.
/// </para>
/// </summary>
internal sealed partial class VsControlPipeServer
{
    private const int _defaultElementDepth = 12;
    private const int _maxElementDepth = 64;
    private const int _defaultElementCount = 500;
    private const int _maxElementCount = 5_000;
    private const int _maxElementValueChars = 200;
    private const int _maxCaptureSide = 1920;

    // The MCP sidecar derives its own attachment ceiling from this number (McpServer._maxImageBytes);
    // keep the two equal so every payload this server can produce is one the sidecar will forward.
    private const int _maxCaptureBytes = 4 * 1024 * 1024;
    private const int _maxCaptureEncodeAttempts = 3;
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
    private const int _smXVirtualScreen = 76;
    private const int _smYVirtualScreen = 77;
    private const int _smCxVirtualScreen = 78;
    private const int _smCyVirtualScreen = 79;

    private static async Task<JObject> ListAppWindowsAsync(CancellationToken cancellationToken = default)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); // the analyzer needs the switch visible in this method
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

            // The window can be destroyed between EnumWindows and this read, and a failed
            // GetWindowRect leaves the struct zeroed; skip it rather than report a phantom 0x0 window.
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

        // Both caps are hard ceilings on agent input: ElementToJson recurses one managed frame per
        // level, so an unclamped depth would overflow the walker thread's stack, and a stack overflow
        // cannot be caught - it takes devenv.exe and the user's unsaved work with it.
        var maxDepth = Math.Min(_maxElementDepth, Math.Max(1, args["maxDepth"]?.Value<int?>() ?? _defaultElementDepth));
        var maxNodes = Math.Min(_maxElementCount, Math.Max(1, args["maxNodes"]?.Value<int?>() ?? _defaultElementCount));

        // The root always fits; the budget counts the descendants that may still be emitted.
        var budget = new ElementBudget(maxNodes - 1);

        // UIA calls are cross-process and can stall while the target's UI thread is busy or stopped
        // at a breakpoint, so the walk runs off the VS UI thread under a deadline.
        var tree = await RunWithDeadlineAsync(() => ElementToJson(AutomationElement.FromHandle(hwnd), 0, maxDepth, budget), cancellationToken);
        if (tree is null)
        {
            return new JObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["pending"] = true,
                ["note"] = "The app has not answered the UI Automation walk yet (it may be stopped at a breakpoint); use waitForBreak or getDebuggerState.",
            };
        }

        return new JObject { ["hwnd"] = hwnd.ToInt64(), ["root"] = tree, ["truncated"] = budget.Truncated };
    }

    private static async Task<JObject> InvokeElementAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);
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
        }, cancellationToken);

        result["action"] = action;
        return result;
    }

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

    /// <summary>
    /// Runs a UIA action on a background thread. WPF's providers block Invoke/SetValue until the app's
    /// handler returns, so a click that lands on a breakpoint would otherwise hang this call (and, if
    /// it ran on the VS UI thread, Visual Studio itself). After <see cref="_uiActionTimeoutMs"/> the
    /// call reports <c>pending: true</c> instead; the action itself keeps running in the app.
    /// </summary>
    private static async Task<JObject> RunUiActionAsync(string action, Func<JObject> body, CancellationToken cancellationToken)
    {
        var element = await RunWithDeadlineAsync(body, cancellationToken);
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

    /// <summary>
    /// Runs <paramref name="body"/> on a background thread and waits <see cref="_uiActionTimeoutMs"/>
    /// for it, returning <c>null</c> when the deadline wins. The deadline is a linked token rather
    /// than a bare <c>Task.Delay</c> so the timer dies with the call instead of outliving it, and so
    /// tearing down the pipe session ends the wait at once. The abandoned work keeps running inside
    /// the target, so its eventual failure is observed here rather than resurfacing as an unobserved
    /// task exception.
    /// </summary>
    private static async Task<JObject?> RunWithDeadlineAsync(Func<JObject?> body, CancellationToken cancellationToken)
    {
        var work = Task.Run(body);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_uiActionTimeoutMs);
        if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, deadline.Token)) == work)
        {
            return await work; // rethrows the body's own error
        }

        ObserveFault(work);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task<JObject> CaptureWindowAsync(JObject args, CancellationToken cancellationToken = default)
    {
        var hwnd = await ResolveDebuggedWindowAsync(args, cancellationToken);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); // the analyzer needs the switch visible in this method
        var stoppedAtBreak = debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode;

        // The window can be destroyed between the IsWindow check and this read; GetWindowRect leaves
        // the struct zeroed on failure, which would otherwise be captured as a 1x1 "screenshot" of the
        // top-left pixel of the primary monitor and reported as a success.
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("The window no longer exists; use listAppWindows.");
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        // PrintWindow sends WM_PRINT synchronously and returns only once the target's message pump
        // answers it. An app stopped at a breakpoint - the normal case for this tool - never will, so
        // calling it on the VS UI thread freezes devenv outright. Skip it at break mode, and run it
        // off the UI thread with a deadline everywhere else.
        var printed = stoppedAtBreak ? null : await PrintWindowCaptureAsync(hwnd, width, height, cancellationToken);

        // Everything below is pixel work - a GDI blit, a bicubic resample, PNG compression, base64 -
        // hundreds of milliseconds for a 4K window, and none of it needs the UI thread.
        return printed ?? await Task.Run(() => CopyFromScreenCapture(hwnd, rect, width, height));
    }

    /// <summary>Runs <c>PrintWindow</c> on a background thread and gives up after
    /// <see cref="_uiActionTimeoutMs"/>. The abandoned thread keeps sole ownership of its bitmap and
    /// device context - no caller may touch GDI objects a stuck WM_PRINT is still drawing into - which
    /// is exactly why <see cref="PrintWindowCapture"/> proves the target answers messages before it
    /// allocates anything.</summary>
    private static Task<JObject?> PrintWindowCaptureAsync(IntPtr hwnd, int width, int height, CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(() => PrintWindowCapture(hwnd, width, height), cancellationToken);

    /// <summary>Returns <c>null</c> when the window cannot render itself: either its message pump does
    /// not answer, or <c>PrintWindow</c> declines.</summary>
    private static JObject? PrintWindowCapture(IntPtr hwnd, int width, int height)
    {
        // WM_PRINT is delivered synchronously, so a target that never pumps messages would strand this
        // thread - and every GDI object it holds - for the life of devenv.exe. WM_NULL with
        // SMTO_ABORTIFHUNG asks the same question before anything is allocated and always returns.
        if (NativeMethods.SendMessageTimeout(hwnd, _wmNull, IntPtr.Zero, IntPtr.Zero, _smtoAbortIfHung, _pumpProbeTimeoutMs, out _) == IntPtr.Zero)
        {
            return null;
        }

        // 24bpp, not 32bpp: neither PrintWindow nor BitBlt fills the alpha byte for GDI-rendered
        // content, so a 32bpp surface saved as PNG carries a real - and entirely zero - alpha channel
        // and renders blank wherever it is honoured.
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

    /// <summary>
    /// Reads the desktop at the window's rectangle. Those pixels are the window's own only while
    /// nothing covers it, so the exposure gate runs immediately before and immediately after the blit;
    /// on any doubt the call returns <c>captured: false</c> rather than another application's picture.
    /// </summary>
    private static JObject CopyFromScreenCapture(IntPtr hwnd, NativeMethods.RECT rect, int width, int height)
    {
        var refusal = RefuseUnlessExposed(hwnd, rect);
        if (refusal is not null)
        {
            return refusal;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        }

        // A window raised, or the target moved, while the blit ran would already be in the bitmap.
        return RefuseUnlessExposed(hwnd, rect) ?? EncodeCapture(hwnd, bitmap);
    }

    /// <summary>
    /// Returns <c>null</c> when every pixel at <paramref name="rect"/> is provably the window's own,
    /// and the refusal to send back otherwise. The reason never names the covering window: its title
    /// would disclose what the user has open to the agent.
    /// </summary>
    private static JObject? RefuseUnlessExposed(IntPtr hwnd, NativeMethods.RECT rect)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var current) || !SameRect(current, rect))
        {
            return CaptureRefused(hwnd, "moved", "The window moved or closed while it was being read; retry the capture.");
        }

        var root = NativeMethods.GetAncestor(hwnd, _gaRoot);
        var above = WindowsAbove(root == IntPtr.Zero ? hwnd : root);
        if (above is null)
        {
            return CaptureRefused(hwnd, "occluded", "The desktop has too many windows to prove this one is uncovered; close some windows and retry.");
        }

        var exposure = WindowCaptureRules.Classify(
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsIconic(root == IntPtr.Zero ? hwnd : root),
            ToScreenRect(rect),
            VirtualScreenRect(),
            above);

        return exposure switch
        {
            WindowCaptureExposure.Exposed => null,
            WindowCaptureExposure.Hidden => CaptureRefused(hwnd, "hidden", "The window is not visible, so the desktop shows other applications at its rectangle; show it and retry."),
            WindowCaptureExposure.Minimized => CaptureRefused(hwnd, "minimized", "The window is minimized and paints nothing; restore it and retry."),
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

    /// <summary>
    /// The rectangles of the top-level windows painted above <paramref name="hwnd"/>. Windows that
    /// paint nothing are left out: hidden ones, minimized ones, and DWM-cloaked ones - a suspended UWP
    /// app or a window parked on another virtual desktop keeps a full-size rectangle it never draws in.
    /// Returns <c>null</c> when the z-order is longer than <see cref="_maxZOrderWindows"/>, which the
    /// caller must read as "cannot prove the window is uncovered".
    /// </summary>
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

            if (!NativeMethods.IsWindowVisible(current) || NativeMethods.IsIconic(current) || IsCloaked(current))
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

    /// <summary>A failed query counts the window as drawn, which only ever refuses a capture.</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, _dwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

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

    private static bool SameRect(NativeMethods.RECT left, NativeMethods.RECT right) => ToScreenRect(left) == ToScreenRect(right);

    private static JObject EncodeCapture(IntPtr hwnd, Bitmap bitmap)
    {
        var scale = WindowCaptureRules.ScaleForLongestSide(bitmap.Width, bitmap.Height, _maxCaptureSide);
        for (var attempt = 1; ; attempt++)
        {
            using var scaled = scale < 1.0 ? Downscale(bitmap, scale) : null;
            var output = scaled ?? bitmap;
            using var stream = new MemoryStream();
            output.Save(stream, ImageFormat.Png);

            // PNG size does not follow from the pixel count, so the byte ceiling can only be applied
            // after encoding. The payload exists several times over on its way to the model (stream,
            // base64 string, JObject, response line), and the MCP sidecar drops anything larger.
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

        // Ownership only reaches the caller's using once this returns; GDI+ raises OutOfMemoryException
        // for an exhausted handle table, and the HBITMAP would then survive until a finalizer run.
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

    /// <summary>Keeps an abandoned background call from surfacing as an unobserved task exception.</summary>
    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The trust check shared by every window-scoped method.</summary>
    private static async Task<IntPtr> ResolveDebuggedWindowAsync(JObject args, CancellationToken cancellationToken)
    {
        var handle = args["hwnd"]?.Value<long?>() ?? throw new InvalidOperationException("Missing required parameter 'hwnd'.");
        var hwnd = new IntPtr(handle);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); // the analyzer needs the switch visible in this method
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

    /// <summary>
    /// The debugged processes whose windows the agent may look at and drive. Visual Studio is excluded:
    /// an F5 on a VSIX project launches a second devenv.exe, and UI-automating a full IDE would give
    /// the agent File > Open and the Command Window - arbitrary file access and code execution outside
    /// both <see cref="WorkspacePathGuard"/> and the runCommand allow-list.
    /// </summary>
    private static HashSet<int> GetDrivableProcessIds(EnvDTE.Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var ids = GetDebuggedProcessIds(debugger);
        foreach (EnvDTE.Process process in debugger.DebuggedProcesses)
        {
            if (IsVisualStudioImage(process.Name))
            {
                _ = ids.Remove(process.ProcessID);
            }
        }

        return ids;
    }

    /// <summary>The debugger reports a process by its full image path.</summary>
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

    /// <summary>
    /// The node budget for one <see cref="ElementToJson"/> walk. <see cref="Truncated"/> records that a
    /// child was actually left out, which an exhausted budget alone does not prove: a tree that fits in
    /// exactly <c>maxNodes</c> nodes ends at zero with nothing dropped.
    /// </summary>
    private sealed class ElementBudget
    {
        public ElementBudget(int nodes) => Remaining = nodes;

        public int Remaining { get; private set; }

        public bool Truncated { get; private set; }

        public bool TryTake()
        {
            if (Remaining <= 0)
            {
                Truncated = true;
                return false;
            }

            Remaining--;
            return true;
        }
    }

    // Live (Current) reads rather than a CacheRequest: TreeWalker.GetFirstChild/GetNextSibling do not
    // populate the active request for the elements they return, so cached reads throw on every child.
    private static JObject ElementToJson(AutomationElement element, int depth, int maxDepth, ElementBudget budget)
    {
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

        if (depth < maxDepth)
        {
            var children = new JArray();
            var walker = TreeWalker.ControlViewWalker;
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
            {
                if (!budget.TryTake())
                {
                    break;
                }

                children.Add(ElementToJson(child, depth + 1, maxDepth, budget));
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
    }
}
