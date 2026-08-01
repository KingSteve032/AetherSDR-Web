import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const indexHtml = await readFile(
  new URL("../wwwroot/index.html", import.meta.url),
  "utf8");
const applicationSource = await readFile(
  new URL("../wwwroot/app.js", import.meta.url),
  "utf8");
const stylesheetSource = await readFile(
  new URL("../wwwroot/styles.css", import.meta.url),
  "utf8");

test("receiver header filter follows the active radio slice", () => {
  assert.match(indexHtml, /id="rx-filter-label"/);
  assert.match(
    applicationSource,
    /rxFilterLabel:\s*document\.querySelector\("#rx-filter-label"\)/);
  assert.match(
    applicationSource,
    /rxFilterLabel\.textContent\s*=\s*formatFilterWidth\(width\)/);
});

test("radio page uses TX intent validation asset revisions", () => {
  assert.match(
    indexHtml,
    /src="\/app\.js\?v=m7-tx-intent-validation-1"/);
  assert.match(
    indexHtml,
    /href="\/styles\.css\?v=m7-tx-intent-validation-1"/);
  assert.match(
    applicationSource,
    /\.\/tx-controls\.js\?v=tx-intent-validation-1/);
});

test("TX intent validation UI cannot expose a production command by default", () => {
  assert.match(
    indexHtml,
    /id="tx-authority-panel" hidden/);
  assert.match(
    indexHtml,
    /id="tx-mox" class="mox" hidden disabled/);
  assert.match(
    indexHtml,
    /id="tx-tune" hidden disabled/);
  assert.match(
    indexHtml,
    /id="tx-cwx" hidden disabled/);
  assert.match(
    indexHtml,
    /It has no radio command or microphone-audio transport\./);
});

test("a recovered radio connection replaces its stale failure notice", () => {
  assert.match(
    applicationSource,
    /const recoveredFromRadioError\s*=\s*[\s\S]*?Boolean\(state\.lastRadioConnectionError\)/);
  assert.match(
    applicationSource,
    /if \(recoveredFromRadioError\) \{\s*showToast\("Radio connection restored\."\);\s*\}/);
});

test("mobile pan zoom remains tappable above the receiver sheet", () => {
  assert.match(
    stylesheetSource,
    /@media \(max-width:\s*760px\)[\s\S]*?\.pan-zoom-controls\s*\{[\s\S]*?position:\s*fixed;[\s\S]*?left:\s*auto;[\s\S]*?z-index:\s*25;/);
  assert.match(
    stylesheetSource,
    /\.radio-workspace:not\(\.applet-rail-hidden\) \.pan-zoom-controls\s*\{[\s\S]*?bottom:\s*calc\(min\(58dvh,\s*470px\) \+ 5px\);/);
});

test("mobile tool panels stay above the receiver sheet", () => {
  assert.match(
    stylesheetSource,
    /@media \(max-width:\s*760px\)[\s\S]*?\.tool-flyout\s*\{[\s\S]*?z-index:\s*28;/);
});
