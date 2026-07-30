import assert from "node:assert/strict";
import test from "node:test";

import { WaterfallRenderer } from "../wwwroot/waterfall.js";

function rendererHarness() {
  return Object.assign(
    Object.create(WaterfallRenderer.prototype),
    {
      minDbm: -120,
      maxDbm: -40,
      centerHz: 14_280_000,
      frameCenterHz: 14_280_000,
      frameBandwidthHz: 200_000,
      pendingCenterHz: null,
      pendingBandwidthHz: null,
      pendingSourceCenterHz: 14_280_000,
      pendingSourceBandwidthHz: 200_000,
      acceptNextConfiguredCenter: false,
      bandwidthHz: 200_000,
      activeSliceId: "A",
      spectrum: {
        width: 1000,
        style: {}
      },
      slices: [
        {
          id: "A",
          frequencyHz: 14_263_000,
          mode: "USB",
          filterLowHz: 300,
          filterHighHz: 3000,
          isActive: true
        }
      ]
    });
}

test("peak-preserving resampling retains narrow carriers", () => {
  const renderer = rendererHarness();
  const bins = new Float32Array(1024).fill(-110);
  bins[511] = -45;

  const points = renderer.peakPreservingPoints(bins, 128);

  assert.equal(points.length, 128);
  assert.equal(Math.max(...points), -45);
});

test("slice hit testing prioritizes tuning at a narrow sideband center", () => {
  globalThis.window = { devicePixelRatio: 1 };
  const renderer = rendererHarness();
  const startHz = renderer.centerHz - (renderer.bandwidthHz / 2);
  const slice = renderer.slices[0];
  const lowX =
    ((slice.frequencyHz + slice.filterLowHz - startHz) /
      renderer.bandwidthHz) *
    renderer.spectrum.width;
  const centerX =
    ((slice.frequencyHz - startHz) / renderer.bandwidthHz) *
    renderer.spectrum.width;
  const highX =
    ((slice.frequencyHz + slice.filterHighHz - startHz) /
      renderer.bandwidthHz) *
    renderer.spectrum.width;

  assert.equal(renderer.hitTestSlice(lowX).action, "slice");
  assert.equal(renderer.hitTestSlice(centerX).action, "slice");
  assert.equal(renderer.hitTestSlice(highX).action, "filter-high");
});

test("slice dragging emits live tuning updates and a final commit", () => {
  const renderer = rendererHarness();
  renderer.drawSpectrum = () => {};
  renderer.applyWaterfallPanPreview = () => {};
  renderer.dragPreview = null;
  const tuningEvents = [];
  let previewedFrequency = null;
  renderer.onSliceTune = (_sliceId, frequencyHz, final) => {
    tuningEvents.push({ frequencyHz, final });
  };
  renderer.onSlicePreview = (_sliceId, frequencyHz) => {
    previewedFrequency = frequencyHz;
  };
  const drag = {
    action: "slice",
    sliceId: "A",
    frequencyOffsetHz: 0
  };

  renderer.emitPointerChange(drag, 500, false);

  assert.equal(previewedFrequency, 14_280_000);
  assert.equal(renderer.dragPreview.sliceId, "A");
  assert.equal(renderer.dragPreview.frequencyHz, 14_280_000);
  assert.deepEqual(
    tuningEvents,
    [{ frequencyHz: 14_280_000, final: false }]);

  renderer.emitPointerChange(drag, 500, true);

  assert.deepEqual(
    tuningEvents,
    [
      { frequencyHz: 14_280_000, final: false },
      { frequencyHz: 14_280_000, final: true }
    ]);
});

test("USB filter dragging stops at the carrier and radio edge", () => {
  const renderer = rendererHarness();
  renderer.drawSpectrum = () => {};
  const changes = [];
  renderer.onFilterChange = (sliceId, filterLowHz, filterHighHz) => {
    changes.push({ sliceId, filterLowHz, filterHighHz });
  };

  renderer.emitPointerChange(
    {
      action: "filter-low",
      sliceId: "A",
      mode: "USB",
      startX: 500,
      filterLowHz: 300,
      filterHighHz: 3000
    },
    0,
    true);
  renderer.emitPointerChange(
    {
      action: "filter-high",
      sliceId: "A",
      mode: "USB",
      startX: 500,
      filterLowHz: 300,
      filterHighHz: 3000
    },
    1000,
    true);

  assert.deepEqual(changes, [
    {
      sliceId: "A",
      filterLowHz: 0,
      filterHighHz: 3000
    },
    {
      sliceId: "A",
      filterLowHz: 300,
      filterHighHz: 12000
    }
  ]);
});

test("LSB filter dragging stays below the carrier", () => {
  const renderer = rendererHarness();
  renderer.drawSpectrum = () => {};
  let committed = null;
  renderer.onFilterChange = (_sliceId, filterLowHz, filterHighHz) => {
    committed = { filterLowHz, filterHighHz };
  };

  renderer.emitPointerChange(
    {
      action: "filter-high",
      sliceId: "A",
      mode: "LSB",
      startX: 500,
      filterLowHz: -3000,
      filterHighHz: -300
    },
    1000,
    true);

  assert.deepEqual(
    committed,
    { filterLowHz: -3000, filterHighHz: 0 });
});

test("render mode falls back to 2D and clears stale 3D history", () => {
  const renderer = rendererHarness();
  renderer.renderMode = "3d";
  renderer.traceHistory = [new Float32Array([1])];
  renderer.drawSpectrum = () => {};

  renderer.setRenderMode("unexpected");

  assert.equal(renderer.renderMode, "2d");
  assert.deepEqual(renderer.traceHistory, []);
});

test("background dragging previews and commits a lower pan center", () => {
  const renderer = rendererHarness();
  renderer.drawSpectrum = () => {};
  let previewCenter = null;
  let committedCenter = null;
  renderer.onPanPreview = centerHz => {
    previewCenter = centerHz;
  };
  renderer.onPanCommit = centerHz => {
    committedCenter = centerHz;
  };
  const drag = {
    action: "background",
    centerHz: 14_280_000,
    startX: 500,
    previewCenterHz: 14_280_000
  };

  renderer.emitPointerChange(drag, 600, false);
  assert.equal(previewCenter, 14_260_000);
  assert.equal(drag.previewCenterHz, 14_260_000);
  assert.equal(committedCenter, null);

  renderer.emitPointerChange(drag, 600, true);
  assert.equal(committedCenter, 14_260_000);
  assert.equal(renderer.centerHz, 14_260_000);
  assert.equal(renderer.pendingCenterHz, 14_260_000);
  assert.equal(renderer.pendingBandwidthHz, 200_000);
  assert.equal(renderer.pendingSourceCenterHz, 14_280_000);
  assert.equal(renderer.acceptNextConfiguredCenter, true);
});

test("pan preview reprojects into a fixed full-width frequency viewport", () => {
  const renderer = rendererHarness();
  renderer.pointerState = {
    action: "background",
    moved: true,
    previewCenterHz: 14_260_000
  };
  const bins = new Float32Array(100).fill(-110);
  bins[50] = -45;

  const points = renderer.frequencyProjectedPoints(bins, 1000);

  assert.equal(points.length, 1000);
  assert.equal(points[0], -120);
  assert.equal(Math.max(...points), -45);
  assert.ok(points.indexOf(-45) > 500);
  assert.equal(points.at(-1), -110);
});

test("zoom preview crops and resamples without shrinking the canvas", () => {
  const renderer = rendererHarness();
  renderer.bandwidthHz = 100_000;
  const bins = new Float32Array(100).fill(-110);
  bins[50] = -45;

  const points = renderer.frequencyProjectedPoints(bins, 1000);

  assert.equal(points.length, 1000);
  assert.equal(points[0], -110);
  assert.equal(Math.max(...points), -45);
  assert.equal(points.at(-1), -110);
});

test("right-edge slice dragging pans upward and keeps tuning live", () => {
  const renderer = rendererHarness();
  renderer.drawSpectrum = () => {};
  renderer.applyWaterfallFrequencyPreview = () => {};
  renderer.pointerState = {
    action: "slice",
    sliceId: "A",
    lastX: 1000,
    frequencyOffsetHz: 0,
    edgePanStartedAt: performance.now() - 600
  };
  const panCenters = [];
  const sliceFrequencies = [];
  renderer.onPanPreview = centerHz => panCenters.push(centerHz);
  renderer.onSliceTune = (_sliceId, frequencyHz, final) => {
    sliceFrequencies.push({ frequencyHz, final });
  };

  renderer.stepSliceEdgePan(renderer.pointerState);

  assert.ok(renderer.centerHz > 14_280_000);
  assert.ok(panCenters[0] > 14_280_000);
  assert.ok(sliceFrequencies[0].frequencyHz > 14_280_000);
  assert.equal(sliceFrequencies[0].final, false);
});

test("connection loss cancels pending pan and slice interactions", () => {
  const renderer = rendererHarness();
  const released = [];
  renderer.stopSliceEdgePan = () => {};
  renderer.restoreWaterfallPanPreview = () => {};
  renderer.updatePointerCursor = () => {};
  renderer.drawSpectrum = () => {};
  renderer.waterfall = { style: { cursor: "grabbing" } };
  renderer.spectrum = {
    ...renderer.spectrum,
    hasPointerCapture: () => true,
    releasePointerCapture: pointerId => released.push(pointerId)
  };
  renderer.pointerState = {
    pointerId: 7,
    action: "slice",
    surface: renderer.spectrum
  };
  renderer.dragPreview = { sliceId: "A", frequencyHz: 14_100_000 };
  renderer.pendingCenterHz = 14_100_000;
  renderer.pendingBandwidthHz = 100_000;
  renderer.acceptNextConfiguredCenter = true;

  renderer.cancelUserInteraction();

  assert.deepEqual(released, [7]);
  assert.equal(renderer.pointerState, null);
  assert.equal(renderer.dragPreview, null);
  assert.equal(renderer.pendingCenterHz, null);
  assert.equal(renderer.pendingBandwidthHz, null);
  assert.equal(renderer.acceptNextConfiguredCenter, false);
  assert.equal(renderer.waterfall.style.cursor, "grab");
});

test("version 2 spectrum frames are routed to the selected pan stream", () => {
  globalThis.document = { hidden: false };
  const renderer = rendererHarness();
  renderer.streamId = 0x40000001;
  renderer.sequence = 0;
  renderer.bins = new Float32Array(64).fill(-120);
  renderer.smoothedBins = new Float32Array(64).fill(-120);
  renderer.peakBins = new Float32Array(64).fill(-120);
  renderer.traceHistory = [];
  renderer.pointerState = null;
  renderer.pendingCenterHz = null;
  renderer.pendingBandwidthHz = null;
  renderer.onPanConfirmed = null;
  renderer.renderMode = "2d";
  renderer.peakEnabled = false;
  renderer.waterfallEnabled = false;
  renderer.lastSpectrumDrawAt = Number.POSITIVE_INFINITY;
  renderer.spectrumFrameIntervalMs = 1000;

  const matching = spectrumFrameV2(0x40000001, 7);
  assert.equal(renderer.acceptFrame(matching), true);
  assert.equal(renderer.sequence, 7);
  assert.equal(renderer.centerHz, 7_074_000);
  assert.equal(renderer.frameCenterHz, 7_074_000);
  assert.equal(renderer.frameBandwidthHz, 200_000);
  assert.equal(renderer.bins[0], -100);

  const otherPan = spectrumFrameV2(0x40000002, 8);
  assert.equal(renderer.acceptFrame(otherPan), false);
  assert.equal(renderer.sequence, 7);
  assert.equal(renderer.centerHz, 7_074_000);
});

test("version 3 spectrum frames carry bandwidth for zoom alignment", () => {
  globalThis.document = { hidden: false };
  const renderer = rendererHarness();
  renderer.streamId = 0x40000001;
  renderer.sequence = 0;
  renderer.bins = new Float32Array(64).fill(-120);
  renderer.smoothedBins = new Float32Array(64).fill(-120);
  renderer.peakBins = new Float32Array(64).fill(-120);
  renderer.traceHistory = [];
  renderer.pointerState = null;
  renderer.pendingCenterHz = null;
  renderer.pendingBandwidthHz = null;
  renderer.onPanConfirmed = null;
  renderer.renderMode = "2d";
  renderer.peakEnabled = false;
  renderer.waterfallEnabled = false;
  renderer.lastSpectrumDrawAt = Number.POSITIVE_INFINITY;
  renderer.spectrumFrameIntervalMs = 1000;

  const matching = spectrumFrameV3(0x40000001, 9, 100_000);
  assert.equal(renderer.acceptFrame(matching), true);
  assert.equal(renderer.frameBandwidthHz, 100_000);
  assert.equal(renderer.bandwidthHz, 100_000);
});

test("pan settles on the first changed radio frame then tracks authority", () => {
  globalThis.document = { hidden: false };
  const renderer = rendererHarness();
  renderer.streamId = 0x40000001;
  renderer.sequence = 0;
  renderer.bins = new Float32Array(64).fill(-120);
  renderer.smoothedBins = new Float32Array(64).fill(-120);
  renderer.peakBins = new Float32Array(64).fill(-120);
  renderer.traceHistory = [];
  renderer.pointerState = null;
  renderer.pendingCenterHz = 14_100_000;
  renderer.pendingBandwidthHz = 200_000;
  renderer.pendingSourceCenterHz = 14_280_000;
  renderer.pendingSourceBandwidthHz = 200_000;
  renderer.acceptNextConfiguredCenter = true;
  renderer.renderMode = "2d";
  renderer.peakEnabled = false;
  renderer.waterfallEnabled = false;
  renderer.lastSpectrumDrawAt = Number.POSITIVE_INFINITY;
  renderer.spectrumFrameIntervalMs = 1000;
  const confirmations = [];
  renderer.onPanConfirmed = (centerHz, bandwidthHz) => {
    confirmations.push({ centerHz, bandwidthHz });
  };

  renderer.acceptFrame(
    spectrumFrameV3(0x40000001, 10, 200_000, 14_160_000));

  assert.equal(renderer.pendingCenterHz, null);
  assert.equal(renderer.centerHz, 14_160_000);
  assert.deepEqual(confirmations, [
    { centerHz: 14_160_000, bandwidthHz: 200_000 }
  ]);

  renderer.acceptFrame(
    spectrumFrameV3(0x40000001, 11, 200_000, 14_100_000));

  assert.equal(renderer.centerHz, 14_100_000);
  assert.deepEqual(confirmations.at(-1), {
    centerHz: 14_100_000,
    bandwidthHz: 200_000
  });
});

function spectrumFrameV2(streamId, sequence) {
  const binCount = 64;
  const buffer = new ArrayBuffer(24 + (binCount * 2));
  const view = new DataView(buffer);
  view.setUint8(0, 65);
  view.setUint8(1, 69);
  view.setUint8(2, 84);
  view.setUint8(3, 70);
  view.setUint8(4, 0);
  view.setUint8(5, 2);
  view.setUint16(6, binCount, true);
  view.setUint32(8, sequence, true);
  view.setBigInt64(12, 7_074_000n, true);
  view.setUint32(20, streamId, true);
  for (let index = 0; index < binCount; index += 1) {
    view.setInt16(24 + (index * 2), -1000, true);
  }
  return buffer;
}

function spectrumFrameV3(
  streamId,
  sequence,
  bandwidthHz,
  centerFrequencyHz = 7_074_000) {
  const binCount = 64;
  const buffer = new ArrayBuffer(28 + (binCount * 2));
  const view = new DataView(buffer);
  view.setUint8(0, 65);
  view.setUint8(1, 69);
  view.setUint8(2, 84);
  view.setUint8(3, 70);
  view.setUint8(4, 0);
  view.setUint8(5, 3);
  view.setUint16(6, binCount, true);
  view.setUint32(8, sequence, true);
  view.setBigInt64(12, BigInt(centerFrequencyHz), true);
  view.setUint32(20, streamId, true);
  view.setInt32(24, bandwidthHz, true);
  for (let index = 0; index < binCount; index += 1) {
    view.setInt16(28 + (index * 2), -1000, true);
  }
  return buffer;
}
