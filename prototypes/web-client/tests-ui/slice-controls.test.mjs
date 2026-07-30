import assert from "node:assert/strict";
import test from "node:test";

import {
  clampFilterEdgesForMode,
  filterEdgesForMode,
  filterLimitsForMode,
  formatFilterWidth,
  normalizeSpectrumMode,
  rxControlAvailability,
  signalDbmToMeterFraction,
  sliceFlagDirection,
  sliceFlagDirections,
  sliceSignalDbm
} from "../wwwroot/slice-controls.js";

test("receive controls follow Aether mode and radio capability rules", () => {
  assert.deepEqual(
    rxControlAvailability("DIGU", "FLEX-6700"),
    {
      squelch: false,
      nb: true,
      nr: true,
      anf: false,
      nrl: true,
      nrs: false,
      rnn: false,
      nrf: false,
      anfl: false,
      anft: false
    });
  assert.equal(
    rxControlAvailability("USB", "FLEX-6700").squelch,
    true);
  assert.equal(
    rxControlAvailability("USB", "FLEX-6700").anf,
    true);
  assert.equal(
    rxControlAvailability("USB", "FLEX-8600").nrs,
    true);
  assert.equal(
    rxControlAvailability("CW", "FLEX-8600").rnn,
    false);
  assert.equal(
    rxControlAvailability("FM", "FLEX-8600").nr,
    false);
});

test("2D is the safe display default", () => {
  assert.equal(normalizeSpectrumMode(null), "2d");
  assert.equal(normalizeSpectrumMode("anything"), "2d");
  assert.equal(normalizeSpectrumMode("3D"), "3d");
});

test("filter presets follow the demodulation sideband", () => {
  assert.deepEqual(
    filterEdgesForMode("USB", 2700),
    { filterLowHz: 300, filterHighHz: 3000 });
  assert.deepEqual(
    filterEdgesForMode("LSB", 2700),
    { filterLowHz: -3000, filterHighHz: -300 });
  assert.deepEqual(
    filterEdgesForMode("AM", 6000),
    { filterLowHz: -3000, filterHighHz: 3000 });
  assert.deepEqual(
    filterEdgesForMode("CW", 500),
    { filterLowHz: 0, filterHighHz: 500 });
  assert.deepEqual(
    filterEdgesForMode("CWR", 500),
    { filterLowHz: -500, filterHighHz: 0 });
});

test("filter limits follow the radio mode families", () => {
  assert.deepEqual(
    filterLimitsForMode("USB"),
    { minimumHz: 0, maximumHz: 12000, minimumWidthHz: 50 });
  assert.deepEqual(
    filterLimitsForMode("LSB"),
    { minimumHz: -12000, maximumHz: 0, minimumWidthHz: 50 });
  assert.deepEqual(
    filterLimitsForMode("SAM"),
    { minimumHz: -12000, maximumHz: 12000, minimumWidthHz: 50 });
});

test("dragged filter edges are clamped to radio and mode limits", () => {
  assert.deepEqual(
    clampFilterEdgesForMode(
      "USB",
      { filterLowHz: -5000, filterHighHz: 20000 },
      "high"),
    { filterLowHz: 0, filterHighHz: 12000 });
  assert.deepEqual(
    clampFilterEdgesForMode(
      "LSB",
      { filterLowHz: -20000, filterHighHz: 5000 },
      "low"),
    { filterLowHz: -12000, filterHighHz: 0 });
  assert.deepEqual(
    clampFilterEdgesForMode(
      "USB",
      { filterLowHz: 2990, filterHighHz: 3000 },
      "low"),
    { filterLowHz: 2950, filterHighHz: 3000 });
});

test("slice flags open away from the center", () => {
  assert.equal(sliceFlagDirection(.4), "left");
  assert.equal(sliceFlagDirection(.6), "right");
  assert.equal(sliceFlagDirection(.05), "right");
  assert.equal(sliceFlagDirection(.95), "left");
});

test("nearby slice flags alternate sides instead of covering each other", () => {
  assert.deepEqual(
    sliceFlagDirections([.5, .5]),
    ["left", "right"]);
  assert.deepEqual(
    sliceFlagDirections([.4, .6]),
    ["left", "right"]);
  assert.deepEqual(
    sliceFlagDirections([.4, .41, .7]),
    ["left", "right", "right"]);
});

test("filter widths use Aether-style compact labels", () => {
  assert.equal(formatFilterWidth(2700), "2.7K");
  assert.equal(formatFilterWidth(6000), "6K");
  assert.equal(formatFilterWidth(500), "500");
});

test("slice meters use Aether's S0, S9, and S9 plus 60 scale", () => {
  assert.equal(signalDbmToMeterFraction(-127), 0);
  assert.equal(signalDbmToMeterFraction(-73), .6);
  assert.equal(signalDbmToMeterFraction(-13), 1);
  assert.equal(signalDbmToMeterFraction(-200), 0);
  assert.equal(signalDbmToMeterFraction(10), 1);
});

test("each slice meter reads only that slice passband", () => {
  const panadapter = {
    centerFrequencyHz: 14_100_000,
    bandwidthHz: 100_000
  };
  const bins = new Float32Array(100).fill(-115);
  bins[25] = -90;
  bins[75] = -55;

  const sliceA = {
    frequencyHz: 14_075_000,
    filterLowHz: -500,
    filterHighHz: 500
  };
  const sliceB = {
    frequencyHz: 14_125_000,
    filterLowHz: -500,
    filterHighHz: 500
  };

  assert.equal(sliceSignalDbm(sliceA, panadapter, bins), -90);
  assert.equal(sliceSignalDbm(sliceB, panadapter, bins), -55);
  assert.equal(
    sliceSignalDbm(
      { ...sliceB, frequencyHz: 14_200_000 },
      panadapter,
      bins),
    null);
});
