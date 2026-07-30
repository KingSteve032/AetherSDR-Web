import assert from "node:assert/strict";
import test from "node:test";
import {
  AdaptiveBandwidthController,
  TransportTrafficTracker,
  formatTrafficRate
} from "../wwwroot/network-profile.js";

test("traffic tracker publishes bounded interval rates by stream type", () => {
  const tracker = new TransportTrafficTracker(() => 0);
  tracker.reset(1000);
  tracker.observe("audio", 1200, 1100);
  tracker.observe("spectrum", 600, 1160);
  tracker.observe("text", 200, 1200);

  assert.deepEqual(tracker.takeSnapshot(2000), {
    sampleMilliseconds: 1000,
    receivedBytes: 2000,
    receivedMessages: 3,
    bytesPerSecond: 2000,
    bitsPerSecond: 16000,
    audioBytesPerSecond: 1200,
    spectrumBytesPerSecond: 600,
    textBytesPerSecond: 200,
    messagesPerSecond: 3,
    maximumGapMilliseconds: 60,
    audioPackets: 1,
    spectrumFrames: 1,
    textMessages: 1
  });

  const next = tracker.takeSnapshot(3000);
  assert.equal(next.receivedBytes, 0);
  assert.equal(next.maximumGapMilliseconds, 0);
});

test("adaptive bandwidth waits for sustained foreground degradation", () => {
  const controller = new AdaptiveBandwidthController({
    poorSamplesRequired: 3
  });
  const degraded = {
    traffic: trafficSample(420),
    missingPackets: 0,
    lowBandwidth: false,
    connected: true,
    pageVisible: true
  };

  assert.equal(controller.observe(degraded, 1000), null);
  assert.equal(controller.observe(degraded, 3000), null);
  assert.deepEqual(controller.observe(degraded, 5000), {
    enabled: true,
    reason: "420 ms delivery gap"
  });
  assert.equal(controller.automaticLowBandwidth, true);
});

test("adaptive bandwidth ignores background pauses and manual low mode", () => {
  const controller = new AdaptiveBandwidthController({
    poorSamplesRequired: 1
  });
  assert.equal(controller.observe({
    traffic: trafficSample(900),
    missingPackets: 0,
    lowBandwidth: false,
    connected: true,
    pageVisible: false
  }, 1000), null);

  controller.noteManualSelection(true, 2000);
  assert.equal(controller.observe({
    traffic: trafficSample(20),
    missingPackets: 0,
    lowBandwidth: true,
    connected: true,
    pageVisible: true
  }, 500_000), null);
});

test("automatically reduced traffic restores only after a healthy dwell", () => {
  const controller = new AdaptiveBandwidthController({
    poorSamplesRequired: 1,
    healthySamplesRequired: 2,
    minimumLowDurationMilliseconds: 5000
  });
  assert.equal(
    controller.observe({
      traffic: trafficSample(500),
      missingPackets: 0,
      lowBandwidth: false,
      connected: true,
      pageVisible: true
    }, 1000)?.enabled,
    true);

  const healthy = {
    traffic: trafficSample(40),
    missingPackets: 0,
    lowBandwidth: true,
    connected: true,
    pageVisible: true
  };
  assert.equal(controller.observe(healthy, 4000), null);
  assert.deepEqual(controller.observe(healthy, 6000), {
    enabled: false,
    reason: "sustained healthy delivery"
  });
});

test("traffic rate formatter uses readable network units", () => {
  assert.equal(formatTrafficRate(960), "960 b/s");
  assert.equal(formatTrafficRate(128000), "128 kb/s");
  assert.equal(formatTrafficRate(2_500_000), "2.50 Mb/s");
});

function trafficSample(maximumGapMilliseconds) {
  return {
    sampleMilliseconds: 2000,
    receivedMessages: 100,
    maximumGapMilliseconds
  };
}
