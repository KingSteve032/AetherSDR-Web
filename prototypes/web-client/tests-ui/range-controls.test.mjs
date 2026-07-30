import test from "node:test";
import assert from "node:assert/strict";
import {
  audioPanToSlider,
  rangeFillPercent,
  sliderToAudioPan
} from "../wwwroot/range-controls.js";

test("range fill follows the input value across arbitrary ranges", () => {
  assert.equal(rangeFillPercent(0, 100, 0), 0);
  assert.equal(rangeFillPercent(0, 100, 73), 73);
  assert.equal(rangeFillPercent(-140, -60, -120), 25);
  assert.equal(rangeFillPercent(5, 60, 30), 45.45454545454545);
});

test("range fill clamps invalid and out-of-range values", () => {
  assert.equal(rangeFillPercent(0, 100, 150), 100);
  assert.equal(rangeFillPercent(0, 100, -10), 0);
  assert.equal(rangeFillPercent(10, 10, 10), 0);
  assert.equal(rangeFillPercent("bad", 100, 50), 0);
});

test("audio balance maps between the centered UI and FLEX pan", () => {
  assert.equal(sliderToAudioPan(-50), 0);
  assert.equal(sliderToAudioPan(0), 50);
  assert.equal(sliderToAudioPan(50), 100);
  assert.equal(audioPanToSlider(0), -50);
  assert.equal(audioPanToSlider(50), 0);
  assert.equal(audioPanToSlider(100), 50);
});
