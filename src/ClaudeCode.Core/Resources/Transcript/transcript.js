// Renders the chat transcript inside the WebView2 host. Message content (model output, tool
// output) is untrusted: markdown-it never emits raw HTML (html:false) and every markup string
// goes through DOMPurify before it touches the DOM. No inline event handlers are ever used - all
// interaction is wired via addEventListener so CSP's script-src 'self' is enough. Sanitizing,
// highlighting, theming and link handling are shared with plan.js via transcript-common.js.
(function () {
  "use strict";

  var common = window.claudeTranscriptCommon;
  var md = window.markdownit({ html: false, linkify: true, breaks: false });
  var root = document.getElementById("transcript");

  function isAtBottom() {
    var doc = document.scrollingElement || document.documentElement;
    return doc.scrollTop + window.innerHeight >= doc.scrollHeight - 4;
  }

  function scrollToBottom() {
    var doc = document.scrollingElement || document.documentElement;
    doc.scrollTop = doc.scrollHeight;
  }

  function renderMarkdown(text, budget) {
    var div = document.createElement("div");
    div.className = "content";
    // DOMPurify (html profile, no data attributes, no inline style) is the last thing this markup
    // passes through before the assignment below - markdown-it already ran with html:false, so this
    // is the second layer rather than the only one. Exactly DOMPurify's documented usage pattern.
    div.innerHTML = window.DOMPurify.sanitize(md.render(text || ""), common.purifyConfig);
    common.highlightWithin(div, budget);
    return div;
  }

  // Only grammars the bundled highlight.js common build registers (see vendor/highlight.min.js);
  // languageForPath checks hljs.getLanguage() anyway, but an entry the bundle cannot honour would
  // only promise highlighting that never happens.
  var languageByExtension = {
    cs: "csharp", ts: "typescript", tsx: "typescript", js: "javascript", jsx: "javascript",
    py: "python", java: "java", go: "go", rs: "rust", rb: "ruby", php: "php", json: "json",
    yml: "yaml", yaml: "yaml", css: "css", html: "xml", xml: "xml", sql: "sql", sh: "bash",
    md: "markdown", cpp: "cpp", c: "c", h: "cpp", swift: "swift", kt: "kotlin",
  };

  function languageForPath(path) {
    var match = path ? /\.([a-zA-Z0-9]+)$/.exec(path) : null;
    if (!match) {
      return null;
    }

    var key = match[1].toLowerCase();
    // Own-property lookup only: ".constructor" (and friends) would otherwise resolve to the
    // inherited Object.prototype member, and hljs.getLanguage() throws TypeError on a non-string
    // - blanking the whole transcript. Tool-output paths are untrusted agent input.
    var language = Object.prototype.hasOwnProperty.call(languageByExtension, key)
      ? languageByExtension[key]
      : null;
    return typeof language === "string" && window.hljs.getLanguage(language) ? language : null;
  }

  // Auto-detection is resolved once per diff instead of once per line: highlightAuto() runs every
  // candidate grammar, so detecting per line costs orders of magnitude more on a large diff and
  // can even settle on a different language for each line. The sample is bounded in characters as
  // well as lines because highlightAuto()'s cost is quadratic in the length of the text it is given
  // (see maxHighlightChars in transcript-common.js) and diff text is untrusted agent output - a
  // 40-line cap alone does not stop one minified/base64 line from hanging the renderer. One
  // detection over a full sample measures 106 ms, so the sample is charged to the message's shared
  // highlight budget as well: a message made of many diffs gets at most a budget's worth of
  // detections (20 000 / 3 000, about six) rather than 106 ms per diff on every rebuild.
  var maxDetectChars = 3000;

  function detectDiffLanguage(lines, budget) {
    var sample = [];
    var sampleBudget = maxDetectChars;
    for (var i = 0; i < lines.length && sample.length < 40 && sampleBudget > 0; i++) {
      if (lines[i].kind !== "Hunk" && lines[i].text) {
        sample.push(lines[i].text.slice(0, sampleBudget));
        sampleBudget -= lines[i].text.length;
      }
    }

    var text = sample.join("\n");
    if (text.length > budget.remaining) {
      return null;
    }

    try {
      var detected = window.hljs.highlightAuto(text).language || null;
      budget.remaining -= text.length;
      return detected;
    } catch (err) {
      return null;
    }
  }

  function buildDiffBody(content, budget) {
    var wrap = document.createElement("div");
    var lines = content.diffLines || [];
    if (content.path) {
      var pathEl = document.createElement("div");
      pathEl.className = "diff-path";
      pathEl.textContent = content.path;
      wrap.appendChild(pathEl);
    }

    var language = languageForPath(content.path) || detectDiffLanguage(lines, budget);
    for (var i = 0; i < lines.length; i++) {
      // Diff lines are capped nowhere upstream either - DiffBuilder emits every old and new line
      // of a large rewrite - so once the message's budget is spent the remainder goes in as one
      // text node, the same rule as buildPlainBody: three elements per line would otherwise grow
      // the DOM without bound long after highlighting had already stopped.
      if (budget.remaining <= 0) {
        var rest = document.createElement("div");
        rest.className = "diff-line";
        var restLines = [];
        for (var r = i; r < lines.length; r++) {
          restLines.push(lines[r].kind === "Hunk" ? lines[r].text || "" : (lines[r].prefix || " ") + (lines[r].text || ""));
        }
        rest.textContent = restLines.join("\n");
        wrap.appendChild(rest);
        break;
      }

      var line = lines[i];
      var lineEl = document.createElement("div");
      if (line.kind === "Hunk") {
        lineEl.className = "diff-line diff-hunk";
        lineEl.textContent = line.text || "";
        // Not a highlighting sink, but a rendered line all the same: charged so that a diff made
        // of hunk headers alone runs into the check above like every other line.
        budget.remaining -= Math.max(1, lineEl.textContent.length);
        wrap.appendChild(lineEl);
        continue;
      }

      lineEl.className = "diff-line" + (line.kind === "Added" ? " diff-added" : line.kind === "Removed" ? " diff-removed" : "");

      var prefixEl = document.createElement("span");
      prefixEl.className = "diff-prefix";
      prefixEl.textContent = line.prefix || " ";
      lineEl.appendChild(prefixEl);

      var codeEl = document.createElement("span");
      // The one highlighting sink for both pages: it decides against the message's shared
      // character budget and falls back to plain text, so neither a single huge diff line nor a
      // diff made of many ordinary ones can pin the renderer.
      common.highlightInto(codeEl, line.text || "", language, budget);

      lineEl.appendChild(codeEl);
      wrap.appendChild(lineEl);
    }

    return wrap;
  }

  // Raw tool output (build/git/test logs) stays unhighlighted - highlightAuto() mis-colors plain
  // text. Only when the tool call names a source file (Read/Write/Edit <path>) is the output
  // highlighted for that file's language; the Read tool's "  12\t<code>" line-number prefix is
  // split into a gutter so the numbers don't skew the tokenizer.
  function buildPlainBody(content, language, budget) {
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
      // Tool output is capped nowhere upstream - not in ToolCallContentViewModel and not on the
      // wire - so once the message's budget is spent the remainder goes in as one text node: two
      // elements per line over a multi-megabyte result would grow the DOM without bound on its
      // own, quite apart from the highlighting cost.
      if (budget.remaining <= 0) {
        var rest = document.createElement("code");
        rest.textContent = lines.slice(i).join("\n");
        pre.appendChild(rest);
        break;
      }

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
      common.highlightInto(codeEl, source, language, budget);

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
  // The message being streamed is rebuilt on every host tick (see render), so per-card UI state
  // (expanded, "Show more" pressed) lives here, keyed by tool call id, and is re-applied when its
  // card is rebuilt instead of being lost with the old DOM.
  //
  // Null-prototype maps: the key is toolCall.id, which arrives verbatim from the agent, and an id
  // of "constructor" or "toString" would otherwise resolve through Object.prototype and read
  // truthy - permanently expanded, permanently untruncated, and impossible to collapse because
  // delete on an inherited key does nothing. Same reason languageForPath does an own-property
  // lookup above.
  var expandedToolCalls = Object.create(null);
  var untruncatedToolCalls = Object.create(null);

  function buildToolCard(toolCall, budget) {
    var card = document.createElement("div");
    var toolId = toolCall.id || "";
    // Collapsed by default (only the one-line header shows), matching the VS Code extension: a
    // long transcript reads as a list of what happened, and any row expands on click.
    card.className = "tool-card" + (toolId && expandedToolCalls[toolId] ? "" : " collapsed");

    var header = document.createElement("div");
    header.className = "tool-header";
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", String(!card.classList.contains("collapsed")));
    header.tabIndex = 0;
    function toggle() {
      card.classList.toggle("collapsed");
      header.setAttribute("aria-expanded", String(!card.classList.contains("collapsed")));
      if (toolId) {
        if (card.classList.contains("collapsed")) delete expandedToolCalls[toolId];
        else expandedToolCalls[toolId] = true;
      }

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
    body.dataset.toolId = toolId;
    var content = toolCall.content || [];
    var language = languageForToolTitle(toolCall.title);
    for (var i = 0; i < content.length; i++) {
      body.appendChild(content[i].isDiff
        ? buildDiffBody(content[i], budget)
        : buildPlainBody(content[i], language, budget));
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

    var toolId = body.dataset.toolId || "";
    var gradient = document.createElement("div");
    gradient.className = "truncate-gradient";
    body.parentNode.insertBefore(gradient, body.nextSibling);

    var toggleButton = document.createElement("button");
    toggleButton.type = "button";
    toggleButton.className = "expand-button";
    gradient.parentNode.parentNode.insertBefore(toggleButton, gradient.parentNode.nextSibling);

    function setTruncated(truncated) {
      if (truncated) {
        body.classList.add("truncated");
        body.style.maxHeight = truncateMaxHeight + "px";
        gradient.style.display = "";
        toggleButton.textContent = "Show more";
        if (toolId) delete untruncatedToolCalls[toolId];
      } else {
        body.classList.remove("truncated");
        body.style.maxHeight = "";
        gradient.style.display = "none";
        toggleButton.textContent = "Show less";
        if (toolId) untruncatedToolCalls[toolId] = true;
      }
    }

    setTruncated(!(toolId && untruncatedToolCalls[toolId]));
    toggleButton.addEventListener("click", function () {
      setTruncated(!body.classList.contains("truncated"));
    });
  }

  function buildMessage(message) {
    // One highlight budget per message, not per render pass: a message is the unit the host caps
    // at MarkdownSafetyLimits.MaxMarkdownLength and the unit it rebuilds while streaming, so a
    // budget here bounds the cost of a rebuild without leaving later messages unhighlighted when
    // a long transcript is loaded in one pass.
    var budget = common.newHighlightBudget();
    var wrap = document.createElement("div");
    var isUser = message.role === "User" || message.role === "user";
    wrap.className = "msg " + (isUser ? "msg-user" : "msg-assistant");
    // #transcript is a polite live region, and the incremental renderer replaces the in-flight
    // message node wholesale ~5x/s - a removal plus an addition, which makes assistive technology
    // restart the announcement of the whole growing message on every tick. An unfinished message
    // opts out; the rebuild that carries durationSeconds opts back in, so it is announced once,
    // complete. User messages have no duration and never need announcing - the user wrote them.
    wrap.setAttribute("aria-live", typeof message.durationSeconds === "number" ? "polite" : "off");
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
        bubble.appendChild(renderUserText(text, budget));
      }

      wrap.appendChild(bubble);
      return wrap;
    }

    // Text and tool calls interleave in the order they actually happened - not "all text, then
    // all tool calls" - so a tool call that ran between two paragraphs renders between them too.
    for (var j = 0; j < parts.length; j++) {
      var part = parts[j];
      wrap.appendChild(part.type === "tool"
        ? buildToolCard(part, budget)
        : renderMarkdown(part.text, budget));
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
  // colors for the file's language - and everything else stays plain text. These run over every
  // line of every user message, and a user message can be agent-authored on session load, so
  // each one has to be linear: a lazy capture followed by \s* over the same characters (the
  // former "+++ " form) backtracked quadratically - 13 s on "+++ " plus 200 000 spaces and one
  // letter, the host's MaxMarkdownLength - where a greedy capture ending on a mandatory
  // non-space finds the same path in one pass.
  var diffHeaderPatterns = [
    /^=== DIFF: (.+?) ===\s*$/,
    /^diff --git a\/(.+?) b\/.+$/,
    /^\+\+\+ (?:b\/)?(.*\S)\s*$/,
  ];

  function renderUserText(text, budget) {
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

      var block = buildDiffBody({ path: header, diffLines: diffLines }, budget);
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

  // Same shape as the VS Code extension's status line: "4m 36s · 8.3k tokens · Running tools…".
  function activityLabel(activity) {
    var parts = [];
    if (typeof activity.elapsedSeconds === "number") {
      parts.push(formatElapsed(activity.elapsedSeconds));
    }
    if (typeof activity.tokens === "number" && activity.tokens > 0) {
      parts.push(formatTokens(activity.tokens) + " tokens");
    }
    parts.push(activity.text || "Working…");
    return parts.join(" · ");
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
    text.textContent = activityLabel(activity);
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

  // Incremental: the host sends the whole transcript every few hundred ms during a turn, but only
  // the message being streamed actually changes. Each rendered message keeps a signature of its
  // payload; unchanged ones keep their DOM (and thus selection, expansion, scroll of inner code).
  var rendered = []; // [{ signature, node }] parallel to payload.messages
  var activityNode = null;

  // The activity indicator is the only thing that changes on most host ticks, so the host updates
  // it through this entry point without re-posting the messages payload (which carries every
  // attachment's base64). render() drives it through the same function, so the two paths cannot
  // drift; a falsy activity removes the indicator.
  function setActivity(activity) {
    var wasAtBottom = isAtBottom();
    if (activity) {
      // Update in place: rebuilding the node on every host tick (100-200ms while streaming)
      // restarts the activity-pulse animation on .activity-dots i from 0%, so the dots never
      // visibly pulse. New messages are inserted before it, so it stays last without being moved.
      if (activityNode) {
        var label = activityNode.querySelector(".activity-text");
        var next = activityLabel(activity);
        if (label.textContent !== next) {
          label.textContent = next;
        }
      } else {
        activityNode = buildActivity(activity);
        root.appendChild(activityNode);
      }
    } else if (activityNode) {
      root.removeChild(activityNode);
      activityNode = null;
    }

    if (wasAtBottom) {
      scrollToBottom();
    }
  }

  function render(payload) {
    var wasAtBottom = isAtBottom();
    var messages = (payload && payload.messages) || [];

    for (var i = 0; i < messages.length; i++) {
      var signature = JSON.stringify(messages[i]);
      var existing = rendered[i];
      if (existing && existing.signature === signature) {
        continue;
      }

      var node = buildMessage(messages[i]);
      if (existing) {
        root.replaceChild(node, existing.node);
      } else {
        root.insertBefore(node, activityNode);
      }
      rendered[i] = { signature: signature, node: node };
    }

    while (rendered.length > messages.length) {
      var stale = rendered.pop();
      if (stale.node.parentNode === root) root.removeChild(stale.node);
    }

    setActivity(payload && payload.activity);

    flushTruncationChecks();

    if (wasAtBottom) {
      scrollToBottom();
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
        common.notifyHost("zoom", { delta: e.deltaY });
      }
    },
    { passive: false }
  );

  common.installLinkHandler(root);

  window.claudeTranscript = {
    render: render,
    setActivity: setActivity,
    applyTheme: common.applyTheme,
    setFontSize: setFontSize,
  };
})();
