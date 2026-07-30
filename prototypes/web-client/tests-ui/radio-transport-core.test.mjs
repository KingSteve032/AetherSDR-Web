import assert from "node:assert/strict";
import test from "node:test";
import {
  AudioDeliveryTracker,
  ReconnectBackoff,
  decodeRadioAudioFrame
} from "../wwwroot/radio-transport-core.js";

test("transport core separates AETA audio from other binary frames", () => {
  const spectrum = new ArrayBuffer(20);
  new Uint8Array(spectrum).set([0x41, 0x45, 0x54, 0x46], 0);

  assert.equal(decodeRadioAudioFrame(spectrum), null);

  const audio = decodeRadioAudioFrame(audioFrame(7));
  assert.equal(audio.valid, true);
  assert.equal(audio.sequence, 7);
  assert.equal(audio.sampleRate, 24000);
  assert.equal(audio.frameCount, 2);
  assert.equal(audio.samples.length, 4);
});

test("transport core rejects malformed AETA without routing it as spectrum", () => {
  const malformed = audioFrame(1);
  new DataView(malformed).setUint8(5, 1);

  const decoded = decodeRadioAudioFrame(malformed);

  assert.notEqual(decoded, null);
  assert.equal(decoded.valid, false);
  assert.equal(decoded.samples, null);
});

test("worker-side delivery tracking measures gaps and missing packets", () => {
  const tracker = new AudioDeliveryTracker();

  tracker.observe(decodeRadioAudioFrame(audioFrame(10)), 100);
  tracker.observe(decodeRadioAudioFrame(audioFrame(12)), 138.5);

  assert.deepEqual(tracker.snapshot(), {
    receivedPackets: 2,
    receivedFrames: 4,
    malformedPackets: 0,
    missingPackets: 1,
    maximumPacketGapMilliseconds: 38.5
  });
});

test("background audio gaps are re-baselined before foreground delivery", () => {
  const tracker = new AudioDeliveryTracker();

  tracker.observe(decodeRadioAudioFrame(audioFrame(10)), 100);
  tracker.setDeliveryExpected(false);
  tracker.observe(decodeRadioAudioFrame(audioFrame(90)), 2100);
  tracker.setDeliveryExpected(true);
  tracker.observe(decodeRadioAudioFrame(audioFrame(100)), 3000);
  tracker.observe(decodeRadioAudioFrame(audioFrame(102)), 3040);

  assert.deepEqual(tracker.snapshot(), {
    receivedPackets: 4,
    receivedFrames: 8,
    malformedPackets: 0,
    missingPackets: 1,
    maximumPacketGapMilliseconds: 40
  });
});

test("ten connection interruptions use one bounded retry at a time", () => {
  const timers = [];
  const cleared = [];
  const backoff = new ReconnectBackoff(
    (callback, delay) => {
      const timer = { callback, delay };
      timers.push(timer);
      return timer;
    },
    timer => cleared.push(timer));
  const recoveries = [];
  const delays = [];

  for (let interruption = 0; interruption < 10; interruption += 1) {
    const delay = backoff.schedule(() => {
      recoveries.push(interruption);
    });
    delays.push(delay);
    assert.equal(backoff.schedule(() => {}), null);
    timers.at(-1).callback();
  }

  assert.deepEqual(delays, [
    750,
    1500,
    3000,
    6000,
    12000,
    15000,
    15000,
    15000,
    15000,
    15000
  ]);
  assert.equal(recoveries.length, 10);
  assert.equal(cleared.length, 0);

  backoff.reset();
  assert.equal(backoff.schedule(() => {}), 750);
});

function audioFrame(sequence) {
  const buffer = new ArrayBuffer(24);
  new Uint8Array(buffer).set([0x41, 0x45, 0x54, 0x41], 0);
  const view = new DataView(buffer);
  view.setUint8(4, 0);
  view.setUint8(5, 2);
  view.setUint16(6, 24000, true);
  view.setUint32(8, sequence, true);
  view.setUint32(12, 2, true);
  return buffer;
}
