import test from "node:test";
import assert from "node:assert/strict";
import { RadioAudioPlayer } from "../wwwroot/audio.js";

test("audio player recognizes a valid AETA PCM frame while disabled", () => {
  const buffer = new ArrayBuffer(24);
  const bytes = new Uint8Array(buffer);
  bytes.set([0x41, 0x45, 0x54, 0x41], 0);
  const view = new DataView(buffer);
  view.setUint8(4, 0);
  view.setUint8(5, 2);
  view.setUint16(6, 24000, true);
  view.setUint32(8, 1, true);
  view.setUint32(12, 2, true);

  const player = new RadioAudioPlayer();
  assert.equal(player.acceptFrame(buffer), true);
});

test("audio player leaves non-audio binary frames for the spectrum renderer", () => {
  const buffer = new ArrayBuffer(20);
  new Uint8Array(buffer).set([0x41, 0x45, 0x54, 0x46], 0);

  const player = new RadioAudioPlayer();
  assert.equal(player.acceptFrame(buffer), false);
});

test("audio player detects missing AETA sequence numbers", () => {
  const player = new RadioAudioPlayer();

  assert.equal(player.acceptFrame(audioFrame(10)), true);
  assert.equal(player.acceptFrame(audioFrame(12)), true);

  assert.equal(player.missingPackets, 1);
  assert.ok(player.maximumPacketGapMilliseconds >= 0);
});

test("audio player clears queued audio and rejects frames without a slice", () => {
  const messages = [];
  const player = new RadioAudioPlayer();
  player.enabled = true;
  player.sliceAvailable = true;
  player.node = {
    port: {
      postMessage: message => messages.push(message)
    }
  };

  player.setSliceAvailable(false);

  assert.deepEqual(messages, [{ type: "clear" }]);

  const buffer = new ArrayBuffer(24);
  const bytes = new Uint8Array(buffer);
  bytes.set([0x41, 0x45, 0x54, 0x41], 0);
  const view = new DataView(buffer);
  view.setUint8(4, 0);
  view.setUint8(5, 2);
  view.setUint16(6, 24000, true);
  view.setUint32(12, 2, true);

  assert.equal(player.acceptFrame(buffer), true);
  assert.equal(messages.length, 1);
});

test("retuning clears audio queued for the previous frequency", () => {
  const messages = [];
  const player = new RadioAudioPlayer();
  player.node = {
    port: {
      postMessage: message => messages.push(message)
    }
  };

  player.reset();

  assert.deepEqual(messages, [{ type: "clear" }]);
});

test("audio player reports browser and worklet latency diagnostics", () => {
  const player = new RadioAudioPlayer();
  player.enabled = true;
  player.sliceAvailable = true;
  player.receivedPackets = 12;
  player.receivedFrames = 3072;
  player.missingPackets = 2;
  player.maximumPacketGapMilliseconds = 38;
  player.context = {
    state: "running",
    sampleRate: 48000,
    baseLatency: .005,
    outputLatency: .01
  };
  player.workletDiagnostics = {
    sourceSampleRate: 24000,
    outputSampleRate: 48000,
    queueFrames: 480,
    queueMilliseconds: 20,
    started: true,
    playedFrames: 2880,
    underruns: 1,
    trimmedFrames: 64,
    clearedFrames: 128
  };
  player.workletReportedAt = Date.now();

  const diagnostics = player.getDiagnostics("B");

  assert.equal(diagnostics.activeSliceId, "B");
  assert.equal(diagnostics.queueMilliseconds, 20);
  assert.equal(diagnostics.baseLatencyMilliseconds, 5);
  assert.equal(diagnostics.outputLatencyMilliseconds, 10);
  assert.equal(diagnostics.estimatedLatencyMilliseconds, 35);
  assert.equal(diagnostics.underruns, 1);
  assert.equal(diagnostics.missingPackets, 2);
  assert.equal(diagnostics.maximumPacketGapMilliseconds, 38);
  assert.ok(diagnostics.workletReportAgeMilliseconds >= 0);
});

test("audio player prefers worker-side delivery diagnostics", () => {
  const player = new RadioAudioPlayer();
  player.receivedPackets = 99;
  player.updateTransportDiagnostics({
    receivedPackets: 12,
    receivedFrames: 1536,
    malformedPackets: 0,
    missingPackets: 0,
    maximumPacketGapMilliseconds: 8.5
  });

  const diagnostics = player.getDiagnostics("A");

  assert.equal(diagnostics.receivedPackets, 12);
  assert.equal(diagnostics.receivedFrames, 1536);
  assert.equal(diagnostics.maximumPacketGapMilliseconds, 8.5);
});

test("audio player reports its active delivery path", () => {
  const player = new RadioAudioPlayer();

  player.setDeliveryPath("worker");

  assert.equal(player.getDiagnostics("A").deliveryPath, "worker");
});

test("backgrounding pauses delivery and clears queued audio without disabling PC audio", async () => {
  const messages = [];
  const playbackStates = [];
  const player = lifecyclePlayer(messages, playbackStates);

  assert.equal(await player.setPageVisible(false), true);

  assert.equal(player.enabled, true);
  assert.equal(player.context.state, "suspended");
  assert.deepEqual(messages, [{ type: "clear" }]);
  assert.deepEqual(playbackStates.at(-1), [false, true]);
  assert.deepEqual(
    lifecycleDiagnostics(player),
    {
      pageVisible: false,
      playbackSuppressed: true,
      recoveryPending: true,
      backgroundTransitions: 1,
      foregroundRecoveries: 0
    });
});

test("foreground recovery clears stale audio and re-primes before delivery resumes", async () => {
  const messages = [];
  const playbackStates = [];
  const player = lifecyclePlayer(messages, playbackStates);

  await player.setPageVisible(false);
  assert.equal(await player.setPageVisible(true), true);

  assert.equal(player.context.state, "running");
  assert.deepEqual(
    messages,
    [{ type: "clear" }, { type: "clear" }]);
  assert.deepEqual(playbackStates.at(-1), [true, true]);
  assert.deepEqual(
    lifecycleDiagnostics(player),
    {
      pageVisible: true,
      playbackSuppressed: false,
      recoveryPending: false,
      backgroundTransitions: 1,
      foregroundRecoveries: 1
    });
  assert.equal(await player.setPageVisible(true), false);
  assert.equal(player.foregroundRecoveries, 1);
});

test("foreground recovery stays paused until the audio clock really advances", async () => {
  const messages = [];
  const playbackStates = [];
  const player = lifecyclePlayer(
    messages,
    playbackStates,
    async () => {});

  await player.setPageVisible(false);
  await assert.rejects(
    player.setPageVisible(true),
    /waiting for a browser interaction/);

  assert.equal(player.recoveryPending, true);
  assert.deepEqual(playbackStates.at(-1), [false, true]);

  player.delay = async milliseconds => {
    if (player.context.state === "running") {
      player.context.currentTime += milliseconds / 1000;
    }
  };
  assert.equal(await player.resumeFromUserGesture(), true);
  assert.equal(player.recoveryPending, false);
  assert.deepEqual(playbackStates.at(-1), [true, true]);
});

function lifecyclePlayer(
  messages,
  playbackStates,
  delay
) {
  let player;
  player = new RadioAudioPlayer(delay || (async milliseconds => {
    if (player.context?.state === "running") {
      player.context.currentTime += milliseconds / 1000;
    }
  }));
  player.enabled = true;
  player.sliceAvailable = true;
  player.context = {
    state: "running",
    currentTime: 0,
    async suspend() {
      this.state = "suspended";
    },
    async resume() {
      this.state = "running";
    }
  };
  player.node = {
    port: {
      postMessage: message => messages.push(message)
    }
  };
  player.playbackStateHandler = (enabled, sliceAvailable) => {
    playbackStates.push([enabled, sliceAvailable]);
  };
  return player;
}

function lifecycleDiagnostics(player) {
  const diagnostics = player.getDiagnostics("A");
  return {
    pageVisible: diagnostics.pageVisible,
    playbackSuppressed: diagnostics.playbackSuppressed,
    recoveryPending: diagnostics.recoveryPending,
    backgroundTransitions: diagnostics.backgroundTransitions,
    foregroundRecoveries: diagnostics.foregroundRecoveries
  };
}

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
