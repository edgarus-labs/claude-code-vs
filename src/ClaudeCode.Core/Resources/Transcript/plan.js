// Renders one implementation plan (agent-authored markdown, untrusted) inside the plan document
// window. Same sanitizing rules as transcript.js: markdown-it with html:false, then DOMPurify.
(function () {
  "use strict";

  var md = window.markdownit({ html: false, linkify: true, breaks: false });
  var root = document.getElementById("plan");

  function notifyHost(type, data) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
    }
  }

  function render(markdown) {
    if (!markdown) {
      root.innerHTML = "";
      var empty = document.createElement("p");
      empty.className = "plan-empty";
      empty.textContent = "No plan is waiting for review.";
      root.appendChild(empty);
      return;
    }

    var clean = window.DOMPurify.sanitize(md.render(markdown), { ADD_ATTR: [] });
    root.innerHTML = clean;
    var blocks = root.querySelectorAll("pre code");
    for (var i = 0; i < blocks.length; i++) {
      try {
        window.hljs.highlightElement(blocks[i]);
      } catch (err) {
        // Best-effort highlighting only.
      }
    }

    window.scrollTo(0, 0);
  }

  function applyTheme(vars) {
    var style = document.documentElement.style;
    for (var key in vars) {
      if (Object.prototype.hasOwnProperty.call(vars, key)) {
        style.setProperty(key, vars[key]);
      }
    }
  }

  // An in-document fragment ("[Jump](#architecture)") has to be handled here: preventDefault()
  // already suppressed native anchor navigation, and the host rejects a non-absolute URI, so
  // forwarding it would silently drop the click. markdown-it emits no heading ids, hence the
  // slug fallback over the rendered headings.
  function scrollToFragment(href) {
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

    var target = document.getElementById(id) || headingForSlug(id);
    if (target) {
      target.scrollIntoView({ block: "start" });
    }
  }

  // GitHub-style heading slug: lowercase, drop punctuation, one hyphen per whitespace character
  // (so "Design & Rollout" is "#design--rollout", the anchor an agent will have written).
  function slugify(text) {
    return text.toLowerCase().trim().replace(/[^\w\- ]+/g, "").replace(/\s/g, "-");
  }

  function headingForSlug(slug) {
    var wanted = slugify(slug);
    var headings = root.querySelectorAll("h1, h2, h3, h4, h5, h6");
    for (var i = 0; i < headings.length; i++) {
      if (slugify(headings[i].textContent || "") === wanted) {
        return headings[i];
      }
    }

    return null;
  }

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
      scrollToFragment(href);
      return;
    }

    notifyHost("openLink", { url: href });
  });

  window.claudePlan = { render: render, applyTheme: applyTheme };
})();
