// Renders one implementation plan (agent-authored markdown, untrusted) inside the plan document
// window. Same sanitizing rules as transcript.js: markdown-it with html:false, then DOMPurify.
// Sanitizing, highlighting, theming and link handling are shared via transcript-common.js.
(function () {
  "use strict";

  var common = window.claudeTranscriptCommon;
  var md = window.markdownit({ html: false, linkify: true, breaks: false });
  var root = document.getElementById("plan");

  function render(markdown) {
    if (!markdown) {
      root.innerHTML = "";
      var empty = document.createElement("p");
      empty.className = "plan-empty";
      empty.textContent = "No plan is waiting for review.";
      root.appendChild(empty);
      return;
    }

    // DOMPurify is the last thing every markup string passes through before an innerHTML
    // assignment on this page; markdown-it already runs with html:false, so this is the second
    // layer, not the only one.
    root.innerHTML = window.DOMPurify.sanitize(md.render(markdown), common.purifyConfig);
    common.highlightWithin(root);

    window.scrollTo(0, 0);
  }

  common.installLinkHandler(root);

  window.claudePlan = { render: render, applyTheme: common.applyTheme };
})();
