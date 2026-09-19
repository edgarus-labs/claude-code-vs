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
/// the devenv process, and their whole defence is three wiring decisions that live only in
/// JavaScript and HTML attributes: markdown-it runs with raw HTML disabled, DOMPurify guards every
/// markup sink, and the CSP keeps the page inert and offline. There is no JavaScript test runner in
/// this repository (and adding one is out of proportion for six asset files), so the invariants are
/// asserted against the shipped asset text from here - each of them is one deleted argument away
/// from script execution inside Visual Studio, and nothing else in CI would notice.
/// </summary>
public sealed class TranscriptAssetSecurityTests
{
    /// <summary>Every script in the asset folder that is authored here (vendor bundles excluded).</summary>
    private static readonly string[] OwnScripts = ["transcript-common.js", "transcript.js", "plan.js"];

    private static readonly string[] Pages = ["index.html", "plan.html"];

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
                bool safe = value is "\"\"" or "''"
                    || value.Contains("DOMPurify.sanitize(", StringComparison.Ordinal);
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

    [Fact]
    public void HighlightingNeverAutoDetectsUntrustedCodeFences()
    {
        var offenders = OwnScripts
            .Where(fileName => ReadScript(fileName).Contains("highlightElement", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "hljs.highlightElement() silently falls back to highlightAuto() when the element carries no "
                + "language-* class, and highlightAuto()'s cost is quadratic in the length of a single line "
                + "(8.3 s for one 32 KB line on the bundled v11.10.0, unbounded at the 200 KB message cap). The "
                + "host re-renders the streaming message about five times a second, so one unlabeled agent code "
                + "fence pins the renderer permanently. Highlight only declared languages, through hljs.highlight() "
                + "with an explicit language and a size cap. Offending files: "
                + string.Join(", ", offenders));
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
    /// commented-out sink cannot pass them.
    /// </summary>
    private static string ReadScript(string fileName)
    {
        string source = Regex.Replace(ReadAsset(fileName), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join(
            Environment.NewLine,
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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
