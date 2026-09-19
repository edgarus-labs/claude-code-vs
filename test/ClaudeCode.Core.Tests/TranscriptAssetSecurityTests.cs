using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Pins the security invariants of the WebView2 transcript/plan assets
/// (src/ClaudeCode.Core/Resources/Transcript). Those pages render untrusted agent output inside
/// the devenv process, and their whole defence is a handful of wiring decisions that live only in
/// JavaScript and HTML attributes: markdown-it runs with raw HTML disabled, DOMPurify guards every
/// markup sink with one narrowed configuration, every highlight.js sink is bounded, and the CSP
/// keeps the page inert and offline. There is no JavaScript test runner in this repository (and
/// adding one is out of proportion for six asset files), so the invariants are asserted against
/// the shipped asset text from here - each of them is one deleted argument away from script
/// execution or a frozen renderer inside Visual Studio, and nothing else in CI would notice.
/// </summary>
public sealed class TranscriptAssetSecurityTests
{
    /// <summary>Every script in the asset folder that is authored here (vendor bundles excluded).</summary>
    private static readonly string[] OwnScripts = ["transcript-common.js", "transcript.js", "plan.js"];

    /// <summary>The shared module both pages must load before their own page script.</summary>
    private const string SharedModule = "transcript-common.js";

    /// <summary>Each page and the page script it is the host for.</summary>
    private static readonly (string Page, string Script)[] PageScripts =
        [("index.html", "transcript.js"), ("plan.html", "plan.js")];

    /// <summary>The pinned vendor bundles every page must load before the shared module.</summary>
    private static readonly string[] VendorBundles =
        ["vendor/markdown-it.min.js", "vendor/purify.min.js", "vendor/highlight.min.js"];

    /// <summary>Markup sinks other than <c>innerHTML</c> that would bypass the DOMPurify guard entirely.</summary>
    private static readonly string[] ForbiddenSinks = ["outerHTML", "insertAdjacentHTML", "document.write", "srcdoc"];

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

        foreach (string fileName in OwnScripts)
        {
            string source = ReadScript(fileName);

            // Left-hand side "x.innerHTML =" / "+=" but not "==="; value is the rest of the statement.
            MatchCollection writes = Regex.Matches(source, @"\.innerHTML\s*\+?=(?!=)(?<value>[^;]*);");
            assignments += writes.Count;
            foreach (Match write in writes)
            {
                string value = write.Groups["value"].Value.Trim();

                // The sanitizer call has to be the whole value and it has to be handed the shared
                // purifyConfig: DOMPurify.sanitize(html, {ADD_TAGS: ['script']}) sanitizes nothing
                // worth having, and "sanitize(x) + rawHtml" launders raw markup through a call that
                // merely *mentions* the sanitizer. Requiring the value to end at the shared config
                // rejects both.
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

            // A property write the pattern above cannot see is just as dangerous, so the count of the
            // bare identifier has to match the count of recognized assignments.
            int mentions = Regex.Count(source, @"\binnerHTML\b");
            if (mentions != writes.Count)
            {
                unguarded.Add(
                    $"{fileName}: {mentions} mentions of innerHTML but only {writes.Count} recognizable "
                        + "'x.innerHTML = <value>;' assignments - an indexed or aliased write would escape this check");
            }

            foreach (string sink in ForbiddenSinks)
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
    /// The sanitizer's allow-list is a single shared object literal, so widening it is a one-word
    /// edit in one place with no other visible effect. The html profile excludes SVG, SVG filters
    /// and MathML; <c>ALLOW_DATA_ATTR: false</c> drops <c>data-*</c>; and forbidding <c>style</c>
    /// keeps inline CSS (which the html profile otherwise permits) out of the DOM, which is what
    /// lets the pages ship <c>style-src 'self'</c> without <c>'unsafe-inline'</c>.
    /// </summary>
    [Fact]
    public void SanitizerIsConfiguredWithTheNarrowedHtmlProfile()
    {
        string source = ReadScript(SharedModule);

        Match declaration = Regex.Match(source, @"var\s+purifyConfig\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(
            declaration.Success,
            $"{SharedModule} no longer declares the shared 'purifyConfig' object literal. Both pages pass it to "
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
    /// highlight.js is the transcript's only super-linear cost: measured on the bundled v11.10.0,
    /// one <c>hljs.highlight()</c> call with an <em>explicit</em> language over a single line costs
    /// 447 ms at 20 KB, 1 139 ms at 32 KB, 4 939 ms at 64 KB and 21 331 ms at 128 KB, and
    /// <c>highlightAuto()</c> is worse still because it runs every bundled grammar. Tool output,
    /// diff lines and code fences all come from the agent and none of them is length-bounded
    /// upstream, and the host rebuilds the streaming message about five times a second - so an
    /// unbounded call site freezes the panel for minutes. The previous remediation bounded one of
    /// the three call sites and left two unbounded, which is exactly the regression this pins:
    /// there is now a single sink in the shared module and every sink checks a bound.
    /// </summary>
    [Fact]
    public void EveryHighlightSinkIsBoundedByACharacterBudget()
    {
        // A length test against a declared ceiling: "<anything>.length > budget.remaining",
        // "sample.length > maxHighlightChars", etc. Name-agnostic within the max*/remaining
        // convention so the assertion is about the bound existing, not about an identifier.
        const string boundCheck = @"\.length\s*(?:>|>=|<|<=)\s*[\w.]*(?:[Mm]ax|remaining)[\w.]*";

        var offenders = new List<string>();
        int sinks = 0;
        var callSiteFiles = new List<string>();

        foreach (string fileName in OwnScripts)
        {
            string source = ReadScript(fileName);

            // highlightElement() picks the language off the element and silently falls back to
            // highlightAuto() when there is no language-* class, so it cannot be bounded by its
            // caller at all - it is banned rather than budgeted.
            if (source.Contains("highlightElement", StringComparison.Ordinal))
            {
                offenders.Add(
                    $"{fileName}: calls hljs.highlightElement(), which falls back to unbounded auto-detection "
                        + "when the element carries no language-* class");
            }

            foreach (Match call in Regex.Matches(source, @"hljs\.highlight(?<auto>Auto)?\s*\("))
            {
                sinks++;

                // highlightAuto() is permitted only for language *detection* over a sample the
                // caller has already bounded; actual highlighting has one chokepoint, checked below.
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

        // One chokepoint, in the shared module: the previous regression was a second and third call
        // site in transcript.js that the cap in transcript-common.js never saw. Any new syntax
        // highlighting has to go through the shared, budgeted sink.
        var strays = callSiteFiles.Where(file => file != SharedModule).Distinct().ToList();
        Assert.True(
            strays.Count == 0,
            $"hljs.highlight() must only be called from {SharedModule}, which owns the shared character budget "
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

        // Exact source lists, not "contains": every extra source is a capability the page does not
        // need and a sanitizer bug could use. 'unsafe-inline'/'unsafe-eval' are rejected implicitly.
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
    /// Every sanitizing, highlighting, theming and link rule both pages rely on lives in the shared
    /// module, and each page script reads it from <c>window.claudeTranscriptCommon</c> at load time.
    /// Dropping or reordering the script tag leaves <c>common</c> undefined, throws out of the page
    /// IIFE, and produces a permanently blank window - with every other assertion here still green.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("plan.html")]
    public void PageLoadsTheSharedModuleBeforeItsOwnScript(string fileName)
    {
        string page = ReadAsset(fileName);
        string pageScript = PageScripts.Single(entry => entry.Page == fileName).Script;

        int shared = ScriptTagIndex(page, SharedModule);
        Assert.True(
            shared >= 0,
            $"{fileName} has no <script src=\"{SharedModule}\"> tag. Its page script reads the shared sanitizer "
                + "configuration, highlight budget, theme and link handling from window.claudeTranscriptCommon; "
                + "without the tag the page IIFE throws on load and the window renders nothing at all.");

        int own = ScriptTagIndex(page, pageScript);
        Assert.True(
            own >= 0,
            $"{fileName} no longer loads its page script '{pageScript}'. If the page was renamed or retargeted, "
                + "update PageScripts so this pairing keeps being checked.");

        Assert.True(
            shared < own,
            $"{fileName} loads '{pageScript}' before '{SharedModule}'. Both are classic (non-deferred) scripts "
                + "executed in document order, so the page script would run with window.claudeTranscriptCommon "
                + "still undefined and render nothing.");

        // The shared module registers its DOMPurify table-alignment hook at load time, so the
        // vendor bundles are a load-time dependency too, not just a call-time one: loading them
        // after it throws on the very first statement and blanks the window.
        foreach (string bundle in VendorBundles)
        {
            int vendor = ScriptTagIndex(page, bundle);
            Assert.True(
                vendor >= 0 && vendor < shared,
                $"{fileName} must load '{bundle}' before '{SharedModule}' (found at index {vendor}, shared module "
                    + $"at {shared}). The shared module touches window.DOMPurify while it is still executing.");
        }
    }

    private static int ScriptTagIndex(string page, string fileName) =>
        page.IndexOf($"<script src=\"{fileName}\">", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The body of the smallest <c>function</c> enclosing <paramref name="index"/>, located from the
    /// two-space indentation every top-level function in these IIFE modules uses. Returns the whole
    /// script when no enclosing function can be identified, which fails the bound check loudly
    /// rather than silently passing it.
    /// </summary>
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

    /// <summary>
    /// Script text with comments removed, so a comment that merely names a banned API (these files
    /// explain at length why they avoid innerHTML and highlightElement) cannot fail the guards, and a
    /// commented-out sink cannot pass them. Trailing comments are stripped as well as whole-line
    /// ones: the innerHTML mention counter above compares a raw identifier count against a count of
    /// assignments, so a trailing "// ... innerHTML ..." would otherwise fail CI for a comment.
    /// </summary>
    private static string ReadScript(string fileName)
    {
        string source = Regex.Replace(ReadAsset(fileName), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join(
            Environment.NewLine,
            source.Split('\n').Select(StripLineComment));
    }

    /// <summary>
    /// Drops a <c>//</c> comment from one line of JavaScript, honouring string and template
    /// literals (so <c>"https://host"</c> survives) and backslash escapes (so an escaped
    /// <c>\/\/</c> inside a regular expression literal is not mistaken for a comment).
    /// </summary>
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
