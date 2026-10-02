using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Covers the security invariants of the WebView2 transcript/plan assets, asserted against the asset
/// text: markdown-it runs with raw HTML disabled, DOMPurify guards every markup sink with one narrowed
/// configuration, every highlight.js sink is bounded, and the CSP keeps the page inert and offline.
/// </summary>
public sealed class TranscriptAssetSecurityTests
{
    private static readonly string[] _ownScripts = ["transcript-common.js", "transcript.js", "plan.js"];

    /// <summary>
    /// The shared module.
    /// </summary>
    private const string _sharedModule = "transcript-common.js";

    private static readonly (string Page, string Script)[] _pageScripts =
        [("index.html", "transcript.js"), ("plan.html", "plan.js")];

    private static readonly string[] _vendorBundles =
        ["vendor/markdown-it.min.js", "vendor/purify.min.js", "vendor/highlight.min.js"];

    private static readonly string[] _forbiddenSinks = ["outerHTML", "insertAdjacentHTML", "document.write", "srcdoc"];

    [Theory]
    [InlineData("transcript.js")]
    [InlineData("plan.js")]
    public void MarkdownIsParsedWithRawHtmlDisabled(string fileName)
    {
        string source = ReadScript(fileName);

        MatchCollection constructions = Regex.Matches(source, @"markdownit\(\s*\{(?<options>[^}]*)\}");
        Assert.True(
            constructions.Count > 0,
            $"{fileName} no longer constructs markdown-it with an options object. The 'html: false' option is the "
                + "only thing stopping agent-authored markdown from emitting raw HTML; a bare markdownit() call "
                + "defaults to html: false today but states nothing, so keep it explicit.");

        foreach (Match construction in constructions)
        {
            string options = construction.Groups["options"].Value.Trim();
            Assert.True(
                Regex.IsMatch(options, @"\bhtml\s*:\s*false\b"),
                $"{fileName} constructs markdown-it with {{{options}}}. Raw HTML in agent output would then be "
                    + "parsed instead of escaped, and DOMPurify would become the only barrier to script injection "
                    + "in the transcript. Restore 'html: false'.");
        }
    }

    [Fact]
    public void EveryMarkupSinkIsGuardedByDomPurify()
    {
        var unguarded = new List<string>();
        int assignments = 0;

        foreach (string fileName in _ownScripts)
        {
            string source = ReadScript(fileName);

            MatchCollection writes = Regex.Matches(source, @"\.innerHTML\s*\+?=(?!=)(?<value>[^;]*);");
            assignments += writes.Count;
            foreach (Match write in writes)
            {
                string value = write.Groups["value"].Value.Trim();

                bool safe = value is "\"\"" or "''"
                    || Regex.IsMatch(
                        value,
                        @"^(?:window\.)?DOMPurify\.sanitize\(.*,\s*(?:common\.)?purifyConfig\s*\)$",
                        RegexOptions.Singleline);
                if (!safe)
                {
                    unguarded.Add($"{fileName}: .innerHTML = {Collapse(value)}");
                }
            }

            int mentions = Regex.Count(source, @"\binnerHTML\b");
            if (mentions != writes.Count)
            {
                unguarded.Add(
                    $"{fileName}: {mentions} mentions of innerHTML but only {writes.Count} recognizable "
                        + "'x.innerHTML = <value>;' assignments - an indexed or aliased write would escape this check");
            }

            foreach (string sink in _forbiddenSinks)
            {
                if (source.Contains(sink, StringComparison.Ordinal))
                {
                    unguarded.Add($"{fileName}: uses {sink}, which writes markup without passing through DOMPurify");
                }
            }
        }

        Assert.True(
            assignments > 0,
            "No innerHTML assignment was found in any transcript script. Either the renderers stopped writing "
                + "markup (then drop them from OwnScripts) or this guard's pattern no longer matches the code it "
                + "is supposed to police - which would make the test pass vacuously.");

        Assert.True(
            unguarded.Count == 0,
            "Markup reaches the DOM without DOMPurify in the WebView2 transcript, which renders untrusted agent "
                + "output inside devenv. Every markup string must be produced by DOMPurify.sanitize(..., "
                + "purifyConfig). Offending sinks:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, unguarded));
    }

    /// <summary>
    /// Covers the shared DOMPurify configuration: the html profile only, no <c>data-*</c> attributes,
    /// no <c>style</c> attribute, and no option that widens the allow-list.
    /// </summary>
    [Fact]
    public void SanitizerIsConfiguredWithTheNarrowedHtmlProfile()
    {
        string source = ReadScript(_sharedModule);

        Match declaration = Regex.Match(source, @"var\s+purifyConfig\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(
            declaration.Success,
            $"{_sharedModule} no longer declares the shared 'purifyConfig' object literal. Both pages pass it to "
                + "every DOMPurify.sanitize() call; without it there is no single place that states what agent "
                + "markdown is allowed to contain.");

        string body = Collapse(declaration.Groups["body"].Value);

        foreach (string required in new[]
        {
            @"USE_PROFILES\s*:\s*\{\s*html\s*:\s*true\s*\}",
            @"ALLOW_DATA_ATTR\s*:\s*false",
            @"FORBID_ATTR\s*:\s*\[\s*""style""\s*\]",
        })
        {
            Assert.True(
                Regex.IsMatch(body, required),
                $"purifyConfig is {{{body}}} but must still match /{required}/. The html profile alone (no SVG, no "
                    + "SVG filters, no MathML), no data-* attributes, and no inline style attribute is the whole "
                    + "allow-list for untrusted agent markdown rendered inside devenv.");
        }

        foreach (string widening in new[] { "ADD_TAGS", "ADD_ATTR", "ALLOWED_TAGS", "ALLOWED_ATTR", "ALLOW_UNKNOWN_PROTOCOLS", "WHOLE_DOCUMENT" })
        {
            Assert.False(
                body.Contains(widening, StringComparison.Ordinal),
                $"purifyConfig is {{{body}}}. '{widening}' either re-admits markup the html profile excludes or "
                    + "replaces the profile wholesale; neither is needed by markdown-it or highlight.js output.");
        }
    }

    /// <summary>
    /// Covers highlight.js call sites: <c>hljs.highlight()</c> is called only from the shared module,
    /// <c>highlightElement</c> is never called, and every sink compares its input length with a
    /// character budget.
    /// </summary>
    [Fact]
    public void EveryHighlightSinkIsBoundedByACharacterBudget()
    {
        const string boundCheck = @"\.length\s*(?:>|>=|<|<=)\s*[\w.]*(?:[Mm]ax|remaining)[\w.]*";

        var offenders = new List<string>();
        int sinks = 0;
        var callSiteFiles = new List<string>();

        foreach (string fileName in _ownScripts)
        {
            string source = ReadScript(fileName);

            if (source.Contains("highlightElement", StringComparison.Ordinal))
            {
                offenders.Add(
                    $"{fileName}: calls hljs.highlightElement(), which falls back to unbounded auto-detection "
                        + "when the element carries no language-* class");
            }

            foreach (Match call in Regex.Matches(source, @"hljs\.highlight(?<auto>Auto)?\s*\("))
            {
                sinks++;

                if (!call.Groups["auto"].Success)
                {
                    callSiteFiles.Add(fileName);
                }

                string enclosing = EnclosingFunction(source, call.Index);
                if (!Regex.IsMatch(enclosing, boundCheck))
                {
                    offenders.Add(
                        $"{fileName}: '{call.Value}' is reached from a function that never compares an input "
                            + $"length against a ceiling:{Environment.NewLine}{Collapse(enclosing)}");
                }
            }
        }

        Assert.True(
            sinks > 0,
            "No hljs.highlight()/highlightAuto() call was found in any transcript script. Either highlighting was "
                + "removed (then delete this test) or the pattern no longer matches the call shape it polices, "
                + "which would make this guard pass vacuously.");

        var strays = callSiteFiles.Where(file => file != _sharedModule).Distinct().ToList();
        Assert.True(
            strays.Count == 0,
            $"hljs.highlight() must only be called from {_sharedModule}, which owns the shared character budget "
                + "every page render shares. A per-page call site is how the 20 000-char cap came to bound one of "
                + "three sinks while a single 128 KB tool-output line still froze the renderer for 21 s. "
                + $"Stray call sites in: {string.Join(", ", strays)}");

        Assert.True(
            offenders.Count == 0,
            "Unbounded highlight.js call sites in the WebView2 transcript, which renders untrusted agent output "
                + "inside devenv:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("plan.html")]
    public void ContentSecurityPolicyKeepsThePageInertAndOffline(string fileName)
    {
        Dictionary<string, string> directives = ReadPolicy(fileName);

        AssertDirective(fileName, directives, "default-src", "'none'");
        AssertDirective(fileName, directives, "script-src", "'self'");
        AssertDirective(fileName, directives, "style-src", "'self'");
        AssertDirective(fileName, directives, "img-src", "data:");
        AssertDirective(fileName, directives, "connect-src", "'none'");
        AssertDirective(fileName, directives, "base-uri", "'none'");
        AssertDirective(fileName, directives, "form-action", "'none'");
    }

    [Fact]
    public void BothPagesShareOneContentSecurityPolicy()
    {
        Dictionary<string, string> transcript = ReadPolicy("index.html");
        Dictionary<string, string> plan = ReadPolicy("plan.html");

        IEnumerable<string> difference = transcript.Keys
            .Union(plan.Keys)
            .Where(name => Lookup(transcript, name) != Lookup(plan, name))
            .Select(name => $"{name}: index.html=\"{Lookup(transcript, name)}\" plan.html=\"{Lookup(plan, name)}\"");

        Assert.True(
            !difference.Any(),
            "index.html and plan.html render the same untrusted agent markdown with the same scripts, so their "
                + "policies must not drift - the weaker one becomes the way in. Differences:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, difference));

        static string Lookup(Dictionary<string, string> policy, string name) =>
            policy.TryGetValue(name, out string? sources) ? sources : "<absent>";
    }

    /// <summary>
    /// Covers script order: each page loads the shared module, which its page script reads from
    /// <c>window.claudeTranscriptCommon</c>, before its own script.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("plan.html")]
    public void PageLoadsTheSharedModuleBeforeItsOwnScript(string fileName)
    {
        string page = ReadAsset(fileName);
        string pageScript = _pageScripts.Single(entry => entry.Page == fileName).Script;

        int shared = ScriptTagIndex(page, _sharedModule);
        Assert.True(
            shared >= 0,
            $"{fileName} has no <script src=\"{_sharedModule}\"> tag. Its page script reads the shared sanitizer "
                + "configuration, highlight budget, theme and link handling from window.claudeTranscriptCommon; "
                + "without the tag the page IIFE throws on load and the window renders nothing at all.");

        int own = ScriptTagIndex(page, pageScript);
        Assert.True(
            own >= 0,
            $"{fileName} no longer loads its page script '{pageScript}'. If the page was renamed or retargeted, "
                + "update PageScripts so this pairing keeps being checked.");

        Assert.True(
            shared < own,
            $"{fileName} loads '{pageScript}' before '{_sharedModule}'. Both are classic (non-deferred) scripts "
                + "executed in document order, so the page script would run with window.claudeTranscriptCommon "
                + "still undefined and render nothing.");

        foreach (string bundle in _vendorBundles)
        {
            int vendor = ScriptTagIndex(page, bundle);
            Assert.True(
                vendor >= 0 && vendor < shared,
                $"{fileName} must load '{bundle}' before '{_sharedModule}' (found at index {vendor}, shared module "
                    + $"at {shared}). The shared module touches window.DOMPurify while it is still executing.");
        }
    }

    private static int ScriptTagIndex(string page, string fileName) =>
        page.IndexOf($"<script src=\"{fileName}\">", StringComparison.OrdinalIgnoreCase);

    private static string EnclosingFunction(string source, int index)
    {
        int start = source.LastIndexOf("\n  function ", index, StringComparison.Ordinal);
        if (start < 0)
        {
            return source;
        }

        int end = source.IndexOf("\n  }", index, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..(end + 4)];
    }

    private static void AssertDirective(
        string fileName,
        Dictionary<string, string> directives,
        string name,
        string expected)
    {
        Assert.True(
            directives.TryGetValue(name, out string? actual),
            $"{fileName} has no '{name}' CSP directive. It must be '{name} {expected}'; base-uri and form-action "
                + "in particular do not fall back to default-src, so omitting them leaves them unrestricted.");

        Assert.True(
            actual == expected,
            $"{fileName} CSP directive '{name}' is \"{actual}\" but must be exactly \"{expected}\". The page loads "
                + "no remote resource, has no inline style or script, and posts nowhere - every widening only "
                + "enlarges what a markdown-it or DOMPurify bug could reach inside devenv.");
    }

    private static Dictionary<string, string> ReadPolicy(string fileName)
    {
        string source = ReadAsset(fileName);
        Match meta = Regex.Match(
            source,
            @"<meta\s+http-equiv=""Content-Security-Policy""\s+content=""(?<policy>[^""]*)""",
            RegexOptions.IgnoreCase);

        Assert.True(
            meta.Success,
            $"{fileName} has no Content-Security-Policy meta tag. Without it the page - which renders untrusted "
                + "agent markdown - may load remote script, connect out, and evaluate inline code.");

        return meta.Groups["policy"].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string ReadScript(string fileName)
    {
        string source = Regex.Replace(ReadAsset(fileName), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join(
            Environment.NewLine,
            source.Split('\n').Select(StripLineComment));
    }

    private static string StripLineComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i].TrimEnd();
            }
        }

        return line;
    }

    private static string ReadAsset(string fileName)
    {
        string path = Path.Combine(AssetDirectory(), fileName);
        Assert.True(
            File.Exists(path),
            $"Transcript asset '{fileName}' was not found at '{path}'. These tests read the shipped asset files "
                + "directly from the repository (resolved relative to this test's source path); if the assets moved, "
                + "move this resolution with them.");

        return File.ReadAllText(path);
    }

    private static string AssetDirectory([CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(testFilePath)!,
                "..",
                "..",
                "src",
                "ClaudeCode.Core",
                "Resources",
                "Transcript"));
}
