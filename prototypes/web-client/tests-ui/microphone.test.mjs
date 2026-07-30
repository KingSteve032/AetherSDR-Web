import assert from "node:assert/strict";
import test from "node:test";

import {
  dbToMeterPercent,
  rmsToDb
} from "../wwwroot/microphone.js";

test("microphone meter converts RMS input to decibels", () => {
  assert.equal(rmsToDb(1), 0);
  assert.equal(Math.round(rmsToDb(.5) * 10) / 10, -6);
  assert.equal(rmsToDb(0), -96);
});

test("microphone meter clamps its visual range", () => {
  assert.equal(dbToMeterPercent(-60), 0);
  assert.equal(dbToMeterPercent(-30), 50);
  assert.equal(dbToMeterPercent(0), 100);
  assert.equal(dbToMeterPercent(-96), 0);
});
