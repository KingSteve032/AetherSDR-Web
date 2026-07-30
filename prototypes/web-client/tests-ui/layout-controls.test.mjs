import assert from "node:assert/strict";
import test from "node:test";

import {
  clampAppletRailWidth,
  maximumAppletRailWidth,
  minimumAppletRailWidth,
  shouldCloseToolPanel
} from "../wwwroot/layout-controls.js";

test("right rail resizing stays usable and leaves room for the panadapter", () => {
  assert.equal(
    clampAppletRailWidth(100, 1400),
    minimumAppletRailWidth);
  assert.equal(
    clampAppletRailWidth(900, 1400),
    maximumAppletRailWidth);
  assert.equal(
    clampAppletRailWidth(460, 800),
    384);
});

test("right rail has a safe default width", () => {
  assert.equal(clampAppletRailWidth(Number.NaN, 1400), 300);
});

test("tapping the active tool closes its panel", () => {
  assert.equal(shouldCloseToolPanel(true, "dsp", "dsp"), true);
  assert.equal(shouldCloseToolPanel(true, "dsp", "band"), false);
  assert.equal(shouldCloseToolPanel(false, "dsp", "dsp"), false);
});
