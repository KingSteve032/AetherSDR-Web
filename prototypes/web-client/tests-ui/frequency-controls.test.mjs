import assert from "node:assert/strict";
import test from "node:test";

import {
  clampPanCenter,
  formatFrequency,
  isFrequencyVisible,
  parseFrequency,
  resolveFrequencySliceId
} from "../wwwroot/frequency-controls.js";

test("frequency entry accepts MHz, grouped Hz, raw Hz, and common suffixes", () => {
  assert.equal(parseFrequency("14.100"), 14_100_000);
  assert.equal(parseFrequency("14.100.000"), 14_100_000);
  assert.equal(parseFrequency("14100000"), 14_100_000);
  assert.equal(parseFrequency("7040"), 7_040_000);
  assert.equal(parseFrequency("7.074 MHz"), 7_074_000);
  assert.equal(parseFrequency("14100 kHz"), 14_100_000);
  assert.equal(parseFrequency("not a frequency"), null);
});

test("frequency formatting preserves one-hertz resolution", () => {
  assert.equal(formatFrequency(14_217_214), "14.217.214");
  assert.equal(formatFrequency(7_074_000), "7.074.000");
});

test("pan helpers detect visibility and clamp at radio limits", () => {
  const pan = {
    centerFrequencyHz: 14_280_000,
    bandwidthHz: 200_000
  };
  assert.equal(isFrequencyVisible(14_217_214, pan), true);
  assert.equal(isFrequencyVisible(14_050_000, pan), false);
  assert.equal(clampPanCenter(14_050_000, 200_000), 14_050_000);
  assert.equal(clampPanCenter(1, 200_000), 100_000);
});

test("frequency tuning falls back from a stale mobile slice id", () => {
  const slices = [
    { id: "A", isActive: true },
    { id: "B", isActive: false }
  ];

  assert.equal(resolveFrequencySliceId("stale", slices, "A"), "A");
  assert.equal(resolveFrequencySliceId("", slices, ""), "A");
  assert.equal(resolveFrequencySliceId("B", slices, "A"), "B");
  assert.equal(resolveFrequencySliceId("A", [], "A"), "");
});
