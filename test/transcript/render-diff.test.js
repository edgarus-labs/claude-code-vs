// Tests the transcript renderer's incremental-render decision, which is pure logic over the JSON
// payload the WPF host pushes in: given the previous and next version of one message, may the page
// patch just the growing tail, or must it rebuild the whole message?
//
// Getting that wrong is not cosmetic. Rebuilding replaces every node in the message ~5x/s while a
// turn streams, which is what made file-reference links unclickable mid-response; patching when the
// message is *not* a simple text append silently replaces the wrong node.
//
// transcript.js runs in WebView2 against markdown-it/DOMPurify/highlight.js and a live DOM, none of
// which these two functions touch - so the file is loaded once into a stub context and only the pure
// functions are exercised. Run with: node --test test/transcript/render-diff.test.js
"use strict";

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const transcriptSource = path.join(
  __dirname, "..", "..", "src", "ClaudeCode.Core", "Resources", "Transcript", "transcript.js");

// Just enough of the browser for transcript.js to finish loading: it reads the transcript root, the
// vendored libraries and the shared module at load time, then registers listeners. None of it is
// reachable from the functions under test.
function loadTranscript() {
  function fakeElement() {
    const element = {
      style: { setProperty() {} },
      className: "",
      textContent: "",
      appendChild(child) { return child; },
      removeChild(child) { return child; },
      replaceChild(child) { return child; },
      insertBefore(child) { return child; },
      setAttribute() {},
      addEventListener() {},
      querySelector() { return null; },
      lastElementChild: null,
      parentNode: null,
    };
    return element;
  }

  const context = {
    document: {
      getElementById() { return fakeElement(); },
      createElement() { return fakeElement(); },
      addEventListener() {},
      documentElement: fakeElement(),
    },
    addEventListener() {},
    requestAnimationFrame() { return 0; },
    cancelAnimationFrame() {},
    markdownit() { return { render: (text) => String(text == null ? "" : text) }; },
    DOMPurify: { sanitize: (html) => html },
    hljs: { getLanguage: () => null, highlight: () => ({ value: "" }), highlightAuto: () => ({ language: null }) },
    claudeTranscriptCommon: {
      purifyConfig: {},
      notifyHost() {},
      newHighlightBudget() { return { remaining: 20000 }; },
      highlightInto() {},
      highlightWithin() {},
      applyTheme() {},
      installLinkHandler() {},
    },
  };
  context.window = context;
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(transcriptSource, "utf8"), context, { filename: transcriptSource });
  return context.claudeTranscript;
}

const transcript = loadTranscript();

function streamingMessage(parts) {
  return { role: "Assistant", parts: parts, images: [], pending: false };
}

function text(value) {
  return { type: "text", text: value };
}

test("a streamed text part that grew is patched instead of rebuilt", () => {
  const before = streamingMessage([text("Open ")]);
  const after = streamingMessage([text("Open src/Foo.cs")]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), true);
});

test("earlier parts are left alone only when they are untouched", () => {
  const before = streamingMessage([text("intro"), text("tail ")]);
  const after = streamingMessage([text("intro EDITED"), text("tail grew")]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("text that was rewritten rather than appended to forces a rebuild", () => {
  const before = streamingMessage([text("hello world")]);
  const after = streamingMessage([text("goodbye world")]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("a newly arrived part forces a rebuild", () => {
  const before = streamingMessage([text("before the tool call")]);
  const after = streamingMessage([text("before the tool call"), { type: "tool", id: "t1", status: "InProgress" }]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("a trailing tool call is never patched as text", () => {
  const before = streamingMessage([{ type: "tool", id: "t1", status: "InProgress" }]);
  const after = streamingMessage([{ type: "tool", id: "t1", status: "Completed" }]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("a message with no parts yet is never patched", () => {
  // buildMessage gives such a message no children at all, so there is no node to replace.
  const before = streamingMessage([]);
  const after = streamingMessage([]);

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

// The patch replaces lastElementChild, assuming it is the last part. buildMessage appends a
// "Responded in ..." footer after the parts as soon as durationSeconds is a number, so for a
// finished message lastElementChild is that footer - patching one would overwrite the footer with a
// duplicate copy of the text.
test("a finished message is never patched, because its last node is the footer", () => {
  const before = { role: "Assistant", parts: [text("done")], images: [], pending: false, durationSeconds: 3 };
  const after = { role: "Assistant", parts: [text("done, with more")], images: [], pending: false, durationSeconds: 3 };

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("a message that just finished is rebuilt so it gains its footer", () => {
  const before = streamingMessage([text("done")]);
  const after = { role: "Assistant", parts: [text("done")], images: [], pending: false, durationSeconds: 3 };

  assert.strictEqual(transcript.isTrailingTextGrowth(before, after), false);
});

test("a change to anything the payload also renders forces a rebuild", () => {
  const before = streamingMessage([text("same")]);

  assert.strictEqual(transcript.isTrailingTextGrowth(
    before, { role: "Assistant", parts: [text("same")], images: [], pending: true }), false,
    "the pending style is rendered on the wrapper, not the text node");
  assert.strictEqual(transcript.isTrailingTextGrowth(
    before, { role: "Assistant", parts: [text("same")], images: [{ name: "a.png" }], pending: false }), false,
    "images render as their own strip inside the bubble");
  assert.strictEqual(transcript.isTrailingTextGrowth(
    before, { role: "User", parts: [text("same")], images: [], pending: false }), false,
    "a user bubble is built by a different branch entirely");
});

test("partsEqual compares payload parts by value", () => {
  assert.strictEqual(transcript.partsEqual(text("a"), text("a")), true);
  assert.strictEqual(transcript.partsEqual(text("a"), text("b")), false);
  assert.strictEqual(
    transcript.partsEqual({ type: "tool", id: "t1", status: "InProgress" }, { type: "tool", id: "t1", status: "Completed" }),
    false);
});
