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

  document.addEventListener("click", function (e) {
    var anchor = e.target && e.target.closest ? e.target.closest("a") : null;
    if (!anchor) {
      return;
    }

    e.preventDefault();
    var href = anchor.getAttribute("href");
    if (href) {
      notifyHost("openLink", { url: href });
    }
  });

  window.claudePlan = { render: render, applyTheme: applyTheme };
})();
