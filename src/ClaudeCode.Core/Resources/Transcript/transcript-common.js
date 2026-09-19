// Shared by transcript.js (index.html) and plan.js (plan.html). Both pages render the same class
// of untrusted agent-authored markdown under the same CSP, so the sanitizing, highlighting, theme
// and link-handling rules live here once: a tightening applied to one page can no longer be
// forgotten on the other. Loaded before the page script; exposes window.claudeTranscriptCommon.
(function () {
  "use strict";

  // The one DOMPurify profile both pages use. Narrowed to what markdown-it and highlight.js can
  // actually emit: the HTML profile only (no SVG, no SVG filters, no MathML) and no data-*
  // attributes. DOMPurify's default allow-list is html u svg u svgFilters u mathML with
  // ALLOW_DATA_ATTR on, none of which this content needs.
  var purifyConfig = { USE_PROFILES: { html: true }, ALLOW_DATA_ATTR: false };

  // highlight.js is only ever asked to highlight a fence that declares its own language, and only
  // up to this many characters. hljs.highlightElement() falls back to highlightAuto() when no
  // language-* class is present, and highlightAuto() runs every bundled grammar with a cost that
  // is quadratic in the length of a single line - measured on the bundled v11.10.0: a 4 KB
  // one-liner takes 149 ms, 8 KB 518 ms, 16 KB 1955 ms, 32 KB 8275 ms, and 200 KB (the
  // MarkdownSafetyLimits.MaxMarkdownLength cap) does not finish. The host re-renders the streaming
  // message ~5x/s, so one unlabeled agent code fence would pin the renderer with no recovery.
  // Highlighting with an explicit language is linear (<1 ms at 32 KB) and the cap bounds it anyway;
  // anything skipped simply stays the plain text markdown-it already escaped.
  var maxHighlightChars = 20000;

  function notifyHost(type, data) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
    }
  }

  function highlightWithin(container) {
    var blocks = container.querySelectorAll("pre code[class*='language-']");
    for (var i = 0; i < blocks.length; i++) {
      var block = blocks[i];
      var source = block.textContent || "";
      if (source.length > maxHighlightChars) {
        continue;
      }

      var match = /(?:^|\s)language-([\w-]+)/.exec(block.className || "");
      var language = match && window.hljs.getLanguage(match[1]) ? match[1] : null;
      if (!language) {
        continue;
      }

      try {
        // hljs escapes the source itself before wrapping tokens in <span class="hljs-*">, but the
        // result still goes through DOMPurify so a grammar escaping flaw can't turn untrusted
        // fence text into DOM injection - the same second layer the diff/tool-output sinks use.
        block.innerHTML = window.DOMPurify.sanitize(
          window.hljs.highlight(source, { language: language, ignoreIllegals: true }).value, purifyConfig);
      } catch (err) {
        // Best-effort: a highlight failure must never block the page from rendering.
      }
    }
  }

  function applyTheme(vars) {
    var style = document.documentElement.style;
    for (var key in vars) {
      if (Object.prototype.hasOwnProperty.call(vars, key)) {
        style.setProperty(key, vars[key]);
      }
    }
  }

  // GitHub-style heading slug: lowercase, drop punctuation, one hyphen per whitespace character
  // (so "Design & Rollout" is "#design--rollout", the anchor an agent will have written).
  function slugify(text) {
    return text.toLowerCase().trim().replace(/[^\w\- ]+/g, "").replace(/\s/g, "-");
  }

  function headingForSlug(root, slug) {
    var wanted = slugify(slug);
    var headings = root.querySelectorAll("h1, h2, h3, h4, h5, h6");
    for (var i = 0; i < headings.length; i++) {
      if (slugify(headings[i].textContent || "") === wanted) {
        return headings[i];
      }
    }

    return null;
  }

  // An in-document fragment ("[Jump](#architecture)") has to be handled here: preventDefault()
  // already suppressed native anchor navigation, and the host rejects a non-absolute URI, so
  // forwarding it would silently drop the click. markdown-it emits no heading ids, hence the
  // slug fallback over the rendered headings.
  function scrollToFragment(root, href) {
    var raw = href.slice(1);
    if (!raw) {
      window.scrollTo(0, 0);
      return;
    }

    var id = raw;
    try {
      id = decodeURIComponent(raw);
    } catch (err) {
      // Malformed escape - match against the literal fragment instead.
    }

    var target = document.getElementById(id) || headingForSlug(root, id);
    if (target) {
      target.scrollIntoView({ block: "start" });
    }
  }

  // Every external link leaves through the host, which re-validates it (absolute http/https,
  // non-loopback) before launching a browser; in-document fragments never reach the host.
  function installLinkHandler(root) {
    document.addEventListener("click", function (e) {
      var anchor = e.target && e.target.closest ? e.target.closest("a") : null;
      if (!anchor) {
        return;
      }

      e.preventDefault();
      var href = anchor.getAttribute("href");
      if (!href) {
        return;
      }

      if (href.charAt(0) === "#") {
        scrollToFragment(root, href);
        return;
      }

      notifyHost("openLink", { url: href });
    });
  }

  window.claudeTranscriptCommon = {
    purifyConfig: purifyConfig,
    notifyHost: notifyHost,
    highlightWithin: highlightWithin,
    applyTheme: applyTheme,
    installLinkHandler: installLinkHandler,
  };
})();
