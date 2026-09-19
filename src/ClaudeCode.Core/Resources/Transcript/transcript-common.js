// Shared by transcript.js (index.html) and plan.js (plan.html). Both pages render the same class
// of untrusted agent-authored markdown under the same CSP, so the sanitizing, highlighting, theme
// and link-handling rules live here once: a tightening applied to one page can no longer be
// forgotten on the other. Loaded before the page script; exposes window.claudeTranscriptCommon.
(function () {
  "use strict";

  // The one DOMPurify profile both pages use. Narrowed to what markdown-it and highlight.js can
  // actually emit: the HTML profile only (no SVG, no SVG filters, no MathML), no data-* attributes
  // and no inline style. DOMPurify's default allow-list is html u svg u svgFilters u mathML with
  // ALLOW_DATA_ATTR on, none of which this content needs. The html profile does allow style=, but
  // the pages ship style-src 'self' without 'unsafe-inline', so an inline style attribute would be
  // refused by the browser anyway - dropping it here keeps the sanitizer's output and what the page
  // will actually apply in agreement (see the table-alignment hook below).
  var purifyConfig = {
    USE_PROFILES: { html: true },
    ALLOW_DATA_ATTR: false,
    FORBID_ATTR: ["style"],
  };

  // markdown-it renders table column alignment as style="text-align:left|center|right" on every
  // aligned th/td. With style= forbidden above (and inline styles refused by the CSP either way)
  // that alignment would silently disappear and every numeric column would read left-aligned, so
  // it is translated into a class the stylesheets carry before the attribute is dropped. The
  // alternative - restoring style-src 'unsafe-inline' - would widen exactly the surface a
  // markdown-it or DOMPurify bug reaches, to buy back one piece of table formatting.
  var alignmentClasses = { left: "align-left", center: "align-center", right: "align-right" };

  window.DOMPurify.addHook("beforeSanitizeAttributes", function (node) {
    if (!node.tagName || (node.tagName !== "TH" && node.tagName !== "TD")) {
      return;
    }

    // Only the exact declaration markdown-it emits; anything else is left to be dropped.
    var match = /^\s*text-align\s*:\s*(left|center|right)\s*;?\s*$/i.exec(node.getAttribute("style") || "");
    if (match) {
      node.setAttribute("class", alignmentClasses[match[1].toLowerCase()]);
    }
  });

  // hljs.highlight()'s cost is quadratic in the length of the text handed to one call, and an
  // explicit language does not make it linear - measured on the bundled v11.10.0, one call with
  // language "csharp" over a single line: 4 KB 19 ms, 8 KB 71 ms, 16 KB 285 ms, 20 KB 447 ms,
  // 32 KB 1 139 ms, 64 KB 4 939 ms, 128 KB 21 331 ms. highlightAuto() is worse again because it
  // runs every bundled grammar (106 ms over a 3 000-char sample), which is why hljs.highlightElement()
  // is never used: it falls back to highlightAuto() whenever the element carries no language-* class.
  // Code fences, diff lines and tool output are all untrusted agent text and none of them is
  // length-bounded upstream, and the host rebuilds the streaming message ~5x/s - so an unbounded
  // call site freezes the transcript for minutes with no recovery. A per-call cap is not enough
  // either: ten separate 20 000-char fences measured 5 140 ms together. Every sink therefore shares
  // ONE character budget per message, and text beyond it simply stays the plain text it already was.
  var maxHighlightChars = 20000;

  function newHighlightBudget() {
    return { remaining: maxHighlightChars };
  }

  // The single highlighting sink for both pages: writes hljs markup when the budget still covers
  // `source`, plain text otherwise, and never throws - a highlight failure must not stop the page
  // from rendering. Returns true when the text was highlighted.
  function highlightInto(el, source, language, budget) {
    var text = source || "";
    var affordable = text.length <= budget.remaining;

    // Charged whether or not the text was highlighted: the budget meters how much agent text one
    // message lays out through this sink, so it bounds the node count as well as the highlighting
    // cost. Without that, a tool result of a million over-long lines would render as plain text
    // quickly and still grow the DOM without bound.
    budget.remaining -= text.length;
    if (!language || !affordable) {
      el.textContent = text;
      return false;
    }

    try {
      // hljs escapes the source itself before wrapping tokens in <span class="hljs-*">, but the
      // result still goes through DOMPurify so a grammar escaping flaw can't turn untrusted fence,
      // diff or tool-output text into DOM injection.
      el.innerHTML = window.DOMPurify.sanitize(
        window.hljs.highlight(text, { language: language, ignoreIllegals: true }).value, purifyConfig);
      return true;
    } catch (err) {
      el.textContent = text;
      return false;
    }
  }

  function notifyHost(type, data) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
    }
  }

  // Highlights the declared-language fences of one rendered markdown block against the shared
  // budget. A block that no longer fits is left exactly as markdown-it escaped it - no DOM write,
  // so an oversized fence costs nothing rather than being re-serialized as text.
  function highlightWithin(container, budget) {
    var pass = budget || newHighlightBudget();
    var blocks = container.querySelectorAll("pre code[class*='language-']");
    for (var i = 0; i < blocks.length; i++) {
      var block = blocks[i];
      var source = block.textContent || "";
      if (source.length > pass.remaining) {
        continue;
      }

      var match = /(?:^|\s)language-([\w-]+)/.exec(block.className || "");
      var language = match && window.hljs.getLanguage(match[1]) ? match[1] : null;
      if (language) {
        highlightInto(block, source, language, pass);
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

      // The host opens absolute http/https only, and markdown-it's linkify turns a bare
      // "foo@bar.com" in agent text into a mailto: anchor - forwarding that would show the user
      // "this link cannot be opened" for a link this renderer invented. Refuse it here instead.
      // The host still re-validates everything that does get through.
      if (!/^https?:\/\//i.test(href)) {
        return;
      }

      notifyHost("openLink", { url: href });
    });
  }

  window.claudeTranscriptCommon = {
    purifyConfig: purifyConfig,
    notifyHost: notifyHost,
    newHighlightBudget: newHighlightBudget,
    highlightInto: highlightInto,
    highlightWithin: highlightWithin,
    applyTheme: applyTheme,
    installLinkHandler: installLinkHandler,
  };
})();
