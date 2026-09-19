// Renders the chat transcript inside the WebView2 host. Message content (model output, tool
// output) is untrusted: markdown-it never emits raw HTML (html:false) and everything still goes
// through DOMPurify before it touches the DOM. No inline event handlers are ever used - all
// interaction is wired via addEventListener in this file so CSP's script-src 'self' is enough.
(function () {
  "use strict";

  var md = window.markdownit({ html: false, linkify: true, breaks: false });
  var root = document.getElementById("transcript");

  function notifyHost(type, data) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
    }
  }

  function isAtBottom() {
    var doc = document.scrollingElement || document.documentElement;
    return doc.scrollTop + window.innerHeight >= doc.scrollHeight - 4;
  }

  function highlightWithin(container) {
    var blocks = container.querySelectorAll("pre code");
    for (var i = 0; i < blocks.length; i++) {
      try {
        window.hljs.highlightElement(blocks[i]);
      } catch (err) {
        // Best-effort: a highlight failure must never block the transcript from rendering.
      }
    }
  }

  function renderMarkdown(text) {
    var raw = md.render(text || "");
    // DOMPurify.sanitize() has already run (default allow-list: strips <script>, on*, javascript:
    // URLs, etc.) - this innerHTML assignment is exactly DOMPurify's documented usage pattern, not
    // a raw/unsanitized write.
    var clean = window.DOMPurify.sanitize(raw, { ADD_ATTR: [] });
    var div = document.createElement("div");
    div.className = "content";
    div.innerHTML = clean;
    highlightWithin(div);
    return div;
  }

  var languageByExtension = {
    cs: "csharp", ts: "typescript", tsx: "typescript", js: "javascript", jsx: "javascript",
    py: "python", java: "java", go: "go", rs: "rust", rb: "ruby", php: "php", json: "json",
    yml: "yaml", yaml: "yaml", css: "css", html: "xml", xml: "xml", sql: "sql", sh: "bash",
    ps1: "powershell", md: "markdown", cpp: "cpp", c: "c", h: "cpp", swift: "swift", kt: "kotlin",
  };

  function languageForPath(path) {
    var match = path ? /\.([a-zA-Z0-9]+)$/.exec(path) : null;
    if (!match) {
      return null;
    }

    var language = languageByExtension[match[1].toLowerCase()];
    return language && window.hljs.getLanguage(language) ? language : null;
  }

  function buildDiffBody(content) {
    var wrap = document.createElement("div");
    var language = languageForPath(content.path);
    if (content.path) {
      var pathEl = document.createElement("div");
      pathEl.className = "diff-path";
      pathEl.textContent = content.path;
      wrap.appendChild(pathEl);
    }

    var lines = content.diffLines || [];
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      var lineEl = document.createElement("div");
      if (line.kind === "Hunk") {
        lineEl.className = "diff-line diff-hunk";
        lineEl.textContent = line.text || "";
        wrap.appendChild(lineEl);
        continue;
      }

      lineEl.className = "diff-line" + (line.kind === "Added" ? " diff-added" : line.kind === "Removed" ? " diff-removed" : "");

      var prefixEl = document.createElement("span");
      prefixEl.className = "diff-prefix";
      prefixEl.textContent = line.prefix || " ";
      lineEl.appendChild(prefixEl);

      var codeEl = document.createElement("span");
      var text = line.text || "";
      try {
        var highlighted = language
          ? window.hljs.highlight(text, { language: language, ignoreIllegals: true })
          : window.hljs.highlightAuto(text);
        // hljs.highlight()/.highlightAuto() HTML-escape the source text themselves before wrapping
        // tokens in <span class="hljs-...">, so .value is safe to assign directly - this is the
        // library's own documented output contract, not raw/unescaped diff content.
        codeEl.innerHTML = highlighted.value;
      } catch (err) {
        codeEl.textContent = text;
      }

      lineEl.appendChild(codeEl);
      wrap.appendChild(lineEl);
    }

    return wrap;
  }

  // Raw tool output (build/git/test logs) stays unhighlighted - highlightAuto() mis-colors plain
  // text. Only when the tool call names a source file (Read/Write/Edit <path>) is the output
  // highlighted for that file's language; the Read tool's "  12\t<code>" line-number prefix is
  // split into a gutter so the numbers don't skew the tokenizer.
  function buildPlainBody(content, language) {
    var pre = document.createElement("pre");
    var text = content.text || "";
    if (!language) {
      var code = document.createElement("code");
      code.textContent = text;
      pre.appendChild(code);
      return pre;
    }

    var lines = text.split("\n");
    var numbered = lines.length > 0 && lines.every(function (line) { return line.length === 0 || /^\s*\d+\t/.test(line); });
    for (var i = 0; i < lines.length; i++) {
      var lineEl = document.createElement("div");
      lineEl.className = "src-line";
      var source = lines[i];
      if (numbered) {
        var match = /^(\s*\d+)\t(.*)$/.exec(source);
        var gutter = document.createElement("span");
        gutter.className = "src-gutter";
        gutter.textContent = match ? match[1].trim() : "";
        lineEl.appendChild(gutter);
        source = match ? match[2] : source;
      }

      var codeEl = document.createElement("span");
      try {
        // hljs escapes the source itself before wrapping tokens (see buildDiffBody); the result is
        // still passed through DOMPurify so only its <span class="hljs-*"> markup can reach the DOM.
        codeEl.innerHTML = window.DOMPurify.sanitize(
          window.hljs.highlight(source, { language: language, ignoreIllegals: true }).value, { ADD_ATTR: [] });
      } catch (err) {
        codeEl.textContent = source;
      }

      lineEl.appendChild(codeEl);
      pre.appendChild(lineEl);
    }

    return pre;
  }

  // "Read src\a\File.cs (1 - 80)" -> "csharp"; commands and non-file tools -> null.
  function languageForToolTitle(title) {
    var match = /^(Edit|Write|Read|MultiEdit)\s+(.+?)(\s+\(\d+\s*-\s*\d+\))?$/.exec(title || "");
    return match ? languageForPath(match[2].replace(/[`'"]/g, "")) : null;
  }

  // A tiny always-visible hint of how much is behind a collapsed card - otherwise a collapsed
  // card with content looks identical to an empty one, and there's no way to tell there's more
  // without opening it.
  function summarizeContent(content) {
    var lines = 0;
    var added = 0;
    var removed = 0;
    var hasDiff = false;
    for (var i = 0; i < content.length; i++) {
      var item = content[i];
      if (item.isDiff) {
        hasDiff = true;
        var diffLines = item.diffLines || [];
        for (var j = 0; j < diffLines.length; j++) {
          if (diffLines[j].kind === "Added") added++;
          else if (diffLines[j].kind === "Removed") removed++;
        }
      } else if (item.text) {
        lines += item.text.split("\n").length;
      }
    }

    var parts = [];
    if (hasDiff) {
      parts.push("+" + added + " -" + removed);
    }
    if (lines > 0) {
      parts.push(lines + (lines === 1 ? " line" : " lines"));
    }

    return parts.join(", ");
  }

  // One level, not two: the body is always visible (no separate "nothing shown at all" collapsed
  // state) but truncated to ~6 lines by default with a fade + "Show more" - same mechanic as long
  // markdown code fences, just without a details/summary toggle gating whether you see anything.
  function buildToolCard(toolCall) {
    var card = document.createElement("div");
    // Collapsed by default (only the one-line header shows), matching the VS Code extension: a
    // long transcript reads as a list of what happened, and any row expands on click.
    card.className = "tool-card collapsed";

    var header = document.createElement("div");
    header.className = "tool-header";
    header.setAttribute("role", "button");
    header.tabIndex = 0;
    function toggle() {
      card.classList.toggle("collapsed");
      // Truncation needs real layout, which a collapsed (display:none) body never had at render time.
      if (!card.classList.contains("collapsed")) {
        var body = card.querySelector(":scope > .tool-body-wrapper > .body");
        if (body) applyTruncation(body);
      }
    }

    header.addEventListener("click", toggle);
    header.addEventListener("keydown", function (e) {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        toggle();
      }
    });

    header.appendChild(buildToolTitle(toolCall.title || ""));

    var counts = diffCounts(toolCall.content || []);
    if (counts) {
      var added = document.createElement("span");
      added.className = "diff-added-count";
      added.textContent = "+" + counts.added;
      header.appendChild(added);
      var removed = document.createElement("span");
      removed.className = "diff-removed-count";
      removed.textContent = "−" + counts.removed;
      header.appendChild(removed);
    } else {
      var contentSummary = summarizeContent(toolCall.content || []);
      if (contentSummary) {
        var preview = document.createElement("span");
        preview.className = "preview";
        preview.textContent = contentSummary;
        header.appendChild(preview);
      }
    }

    // Like the Claude desktop app: no status text for completed rows, only running/failed.
    var statusKey = String(toolCall.status || "").toLowerCase();
    if (statusKey === "failed" || statusKey === "pending" || statusKey === "inprogress") {
      var status = document.createElement("span");
      status.className = "status status-" + statusKey;
      status.textContent = statusKey === "failed" ? "Failed" : "Running…";
      header.appendChild(status);
    }

    var chevron = document.createElement("span");
    chevron.className = "chevron";
    header.appendChild(chevron);

    card.appendChild(header);

    var bodyWrapper = document.createElement("div");
    bodyWrapper.className = "tool-body-wrapper";

    var body = document.createElement("div");
    body.className = "body";
    var content = toolCall.content || [];
    var language = languageForToolTitle(toolCall.title);
    for (var i = 0; i < content.length; i++) {
      body.appendChild(content[i].isDiff ? buildDiffBody(content[i]) : buildPlainBody(content[i], language));
    }

    bodyWrapper.appendChild(body);
    card.appendChild(bodyWrapper);
    pendingTruncationChecks.push(body);
    return card;
  }

  // "Edit src\a\b\File.cs" -> "Edited <b>File.cs</b>"; anything else (a shell command, "Approve Plan")
  // is shown as-is, trimmed to one line.
  var toolVerbs = { Edit: "Edited", Write: "Wrote", Read: "Read", Create: "Created", Delete: "Deleted", MultiEdit: "Edited" };

  function buildToolTitle(rawTitle) {
    var title = document.createElement("span");
    title.className = "title";
    var match = /^(Edit|Write|Read|Create|Delete|MultiEdit)\s+(.+?)(\s+\(\d+\s*-\s*\d+\))?$/.exec(rawTitle);
    if (match) {
      var path = match[2].replace(/[`'"]/g, "");
      var name = path.split(/[\\/]/).pop() || path;
      title.appendChild(document.createTextNode(toolVerbs[match[1]] + " "));
      var strong = document.createElement("strong");
      strong.textContent = name;
      strong.title = path;
      title.appendChild(strong);
      if (match[3]) {
        title.appendChild(document.createTextNode(" " + match[3].trim()));
      }
    } else {
      title.textContent = rawTitle;
      title.title = rawTitle;
    }

    return title;
  }

  function diffCounts(content) {
    var added = 0;
    var removed = 0;
    var hasDiff = false;
    for (var i = 0; i < content.length; i++) {
      if (!content[i].isDiff) continue;
      hasDiff = true;
      var lines = content[i].diffLines || [];
      for (var j = 0; j < lines.length; j++) {
        if (lines[j].kind === "Added") added++;
        else if (lines[j].kind === "Removed") removed++;
      }
    }

    return hasDiff ? { added: added, removed: removed } : null;
  }

  // Very long tool output (a big diff, a huge command dump) doesn't collapse the whole card behind
  // one click - it truncates to a fixed height with a fade + "Show more", so short results still
  // read at a glance while long ones don't take over the transcript. Checked after the fragment is
  // in the document (needs real layout) via one rAF per render pass, not per card.
  var pendingTruncationChecks = [];
  var truncateMaxHeight = 140; // roughly 5-6 lines at the default font size

  function flushTruncationChecks() {
    var items = pendingTruncationChecks;
    pendingTruncationChecks = [];
    if (items.length === 0) {
      return;
    }

    requestAnimationFrame(function () {
      for (var i = 0; i < items.length; i++) {
        applyTruncation(items[i]);
      }
    });
  }

  // Idempotent: measures once the body has layout (a collapsed card is measured when expanded).
  function applyTruncation(body) {
    if (body.dataset.truncateChecked === "1" || !body.isConnected || body.scrollHeight === 0) {
      return;
    }

    body.dataset.truncateChecked = "1";
    if (body.scrollHeight <= truncateMaxHeight + 4) {
      return;
    }

    body.classList.add("truncated");
    body.style.maxHeight = truncateMaxHeight + "px";

    var gradient = document.createElement("div");
    gradient.className = "truncate-gradient";
    body.parentNode.insertBefore(gradient, body.nextSibling);

    var toggleButton = document.createElement("button");
    toggleButton.type = "button";
    toggleButton.className = "expand-button";
    toggleButton.textContent = "Show more";
    toggleButton.addEventListener("click", function () {
      var expanded = !body.classList.contains("truncated");
      if (expanded) {
        body.classList.add("truncated");
        body.style.maxHeight = truncateMaxHeight + "px";
        gradient.style.display = "";
        toggleButton.textContent = "Show more";
      } else {
        body.classList.remove("truncated");
        body.style.maxHeight = "";
        gradient.style.display = "none";
        toggleButton.textContent = "Show less";
      }
    });
    gradient.parentNode.parentNode.insertBefore(toggleButton, gradient.parentNode.nextSibling);
  }

  function buildMessage(message) {
    var wrap = document.createElement("div");
    var isUser = message.role === "User" || message.role === "user";
    wrap.className = "msg " + (isUser ? "msg-user" : "msg-assistant");
    var parts = message.parts || [];

    if (isUser) {
      // User replay is always plain text with no tool calls; render the concatenated parts as
      // one bubble rather than one bubble per part.
      var text = "";
      for (var i = 0; i < parts.length; i++) {
        text += parts[i].text || "";
      }

      var bubble = document.createElement("div");
      bubble.className = "bubble";
      var images = message.images || [];
      if (images.length > 0) {
        var strip = document.createElement("div");
        strip.className = "user-images";
        for (var k = 0; k < images.length; k++) {
          var image = images[k];
          var mime = /^image\/[a-z0-9.+-]+$/i.test(image.mimeType || "") ? image.mimeType : "image/png";
          var img = document.createElement("img");
          img.className = "user-image";
          // data: URL built from our own base64 payload only (CSP img-src allows data:, nothing remote).
          img.src = "data:" + mime + ";base64," + (image.data || "");
          img.alt = image.name || "image";
          img.title = image.name || "";
          strip.appendChild(img);
        }
        bubble.appendChild(strip);
      }

      if (text.length > 0) {
        bubble.appendChild(renderUserText(text));
      }

      wrap.appendChild(bubble);
      return wrap;
    }

    // Text and tool calls interleave in the order they actually happened - not "all text, then
    // all tool calls" - so a tool call that ran between two paragraphs renders between them too.
    for (var j = 0; j < parts.length; j++) {
      var part = parts[j];
      wrap.appendChild(part.type === "tool" ? buildToolCard(part) : renderMarkdown(part.text));
    }

    if (typeof message.durationSeconds === "number") {
      var footer = document.createElement("div");
      footer.className = "msg-footer";
      var footerParts = ["Responded in " + formatElapsed(message.durationSeconds)];
      if (typeof message.tokensUsed === "number" && message.tokensUsed > 0) {
        footerParts.push(formatTokens(message.tokensUsed) + " tokens");
      }
      footer.textContent = footerParts.join(" · ");
      wrap.appendChild(footer);
    }

    return wrap;
  }

  // User prompts often carry pasted unified diffs ("=== DIFF: path ===" / "diff --git" headers,
  // "@@" hunks, +/- lines). Those runs are rendered like tool diffs - green/red rows with syntax
  // colors for the file's language - and everything else stays plain text.
  var diffHeaderPatterns = [
    /^=== DIFF: (.+?) ===\s*$/,
    /^diff --git a\/(.+?) b\/.+$/,
    /^\+\+\+ (?:b\/)?(.+?)\s*$/,
  ];

  function renderUserText(text) {
    var container = document.createElement("div");
    container.className = "bubble-text";
    var lines = text.split("\n");
    var plain = [];
    var i = 0;

    function flushPlain() {
      if (plain.length === 0) return;
      var block = document.createElement("div");
      block.className = "bubble-plain";
      block.textContent = plain.join("\n");
      container.appendChild(block);
      plain = [];
    }

    while (i < lines.length) {
      var header = matchDiffHeader(lines[i]);
      if (!header) {
        plain.push(lines[i]);
        i++;
        continue;
      }

      // Skip the rest of a git-style header (index/---/+++ lines) up to the first hunk.
      var j = i + 1;
      while (j < lines.length && /^(index |--- |\+\+\+ |new file|deleted file|similarity|rename )/.test(lines[j])) j++;
      if (j >= lines.length || !/^@@/.test(lines[j])) {
        plain.push(lines[i]);
        i++;
        continue;
      }

      flushPlain();
      var diffLines = [];
      while (j < lines.length) {
        var line = lines[j];
        if (/^@@/.test(line)) {
          diffLines.push({ kind: "Hunk", prefix: "", text: line });
        } else if (line.length === 0 || line === "\r") {
          // A blank line inside a hunk is context; two in a row end the diff block.
          if (j + 1 < lines.length && /^[@+\- ]/.test(lines[j + 1])) diffLines.push({ kind: "Context", prefix: " ", text: "" });
          else break;
        } else if (line[0] === "+") {
          diffLines.push({ kind: "Added", prefix: "+", text: line.substring(1) });
        } else if (line[0] === "-") {
          diffLines.push({ kind: "Removed", prefix: "-", text: line.substring(1) });
        } else if (line[0] === " ") {
          diffLines.push({ kind: "Context", prefix: " ", text: line.substring(1) });
        } else if (/^\\ No newline/.test(line)) {
          j++;
          continue;
        } else if (matchDiffHeader(line)) {
          break; // next file: handled by the outer loop
        } else {
          break;
        }
        j++;
      }

      var block = buildDiffBody({ path: header, diffLines: diffLines });
      block.className = "user-diff";
      container.appendChild(block);
      i = j;
    }

    flushPlain();
    return container;
  }

  function matchDiffHeader(line) {
    for (var k = 0; k < diffHeaderPatterns.length; k++) {
      var match = diffHeaderPatterns[k].exec(line);
      if (match) return match[1];
    }

    return null;
  }

  function formatElapsed(seconds) {
    if (seconds < 60) {
      return seconds + "s";
    }

    var minutes = Math.floor(seconds / 60);
    var remainder = seconds % 60;
    return minutes + "m " + remainder + "s";
  }

  function buildActivity(activity) {
    var wrap = document.createElement("div");
    wrap.className = "activity";

    var dots = document.createElement("span");
    dots.className = "activity-dots";
    dots.appendChild(document.createElement("i"));
    dots.appendChild(document.createElement("i"));
    dots.appendChild(document.createElement("i"));
    wrap.appendChild(dots);

    var text = document.createElement("span");
    text.className = "activity-text";
    // Same shape as the VS Code extension's status line: "4m 36s · 8.3k tokens · Running tools…".
    var parts = [];
    if (typeof activity.elapsedSeconds === "number") {
      parts.push(formatElapsed(activity.elapsedSeconds));
    }
    if (typeof activity.tokens === "number" && activity.tokens > 0) {
      parts.push(formatTokens(activity.tokens) + " tokens");
    }
    parts.push(activity.text || "Working…");
    text.textContent = parts.join(" · ");
    wrap.appendChild(text);

    return wrap;
  }

  function formatTokens(count) {
    if (count < 1000) {
      return String(count);
    }

    if (count < 1000000) {
      return (count / 1000).toFixed(count < 10000 ? 1 : 0) + "k";
    }

    return (count / 1000000).toFixed(1) + "M";
  }

  function render(payload) {
    var wasAtBottom = isAtBottom();
    var fragment = document.createDocumentFragment();
    var messages = (payload && payload.messages) || [];
    for (var i = 0; i < messages.length; i++) {
      fragment.appendChild(buildMessage(messages[i]));
    }

    if (payload && payload.activity) {
      fragment.appendChild(buildActivity(payload.activity));
    }

    root.textContent = "";
    root.appendChild(fragment);
    flushTruncationChecks();

    if (wasAtBottom) {
      var doc = document.scrollingElement || document.documentElement;
      doc.scrollTop = doc.scrollHeight;
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

  function setFontSize(px) {
    document.documentElement.style.setProperty("--chat-font-size", px + "px");
  }

  // The transcript's own font size is controlled by the WPF host (see ChatPanelView.ChatTextFontSize)
  // so it stays in sync with the composer/popups, which are still plain WPF. Ctrl+wheel is
  // forwarded there instead of zooming the page itself.
  window.addEventListener(
    "wheel",
    function (e) {
      if (e.ctrlKey) {
        e.preventDefault();
        notifyHost("zoom", { delta: e.deltaY });
      }
    },
    { passive: false }
  );

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

  window.claudeTranscript = {
    render: render,
    applyTheme: applyTheme,
    setFontSize: setFontSize,
  };
})();
