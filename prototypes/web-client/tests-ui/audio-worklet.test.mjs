import assert from "node:assert/strict";
import test from "node:test";

let ProcessorClass;
globalThis.AudioWorkletProcessor = class {
  constructor() {
    this.port = {};
  }
};
globalThis.registerProcessor = (_name, processorClass) => {
  ProcessorClass = processorClass;
};
globalThis.sampleRate = 48000;

await import("../wwwroot/audio-worklet.js");

function stereoFrames(frameCount, value = 1000) {
  return new Int16Array(frameCount * 2).fill(value);
}

test("audio worklet trims backlog toward a low-latency target", () => {
  const processor = new ProcessorClass();

  processor.push(stereoFrames(2000), 24000);
  processor.push(stereoFrames(1500), 24000);

  assert.equal(processor.available, 2580);
  assert.equal(processor.trimmedFrames, 920);
  assert.ok(
    processor.available <= processor.maximumBufferedFrames);

  processor.push(stereoFrames(4000), 24000);

  assert.equal(processor.available, processor.maximumBufferedFrames);
});

test("audio worklet begins after a short jitter cushion", () => {
  const processor = new ProcessorClass();
  const output = [[new Float32Array(128), new Float32Array(128)]];

  processor.push(
    stereoFrames(processor.startThresholdFrames - 1),
    24000);
  processor.process([], output);
  assert.equal(processor.started, false);

  processor.push(stereoFrames(1), 24000);
  processor.process([], output);
  assert.equal(processor.started, true);
  assert.notEqual(output[0][0][0], 0);
});

test("audio worklet records a real starvation without counting startup silence", () => {
  const processor = new ProcessorClass();
  const output = [[new Float32Array(128), new Float32Array(128)]];

  processor.process([], output);
  assert.equal(processor.underruns, 0);

  processor.push(stereoFrames(processor.startThresholdFrames), 24000);
  for (let index = 0; index < 20; index += 1) {
    processor.process([], output);
  }

  assert.equal(processor.underruns, 1);
  assert.ok(processor.playedFrames > 0);
});

test("audio worklet distinguishes deliberate clears from latency trims", () => {
  const processor = new ProcessorClass();

  processor.push(stereoFrames(600), 24000);
  processor.clear();

  assert.equal(processor.clearedFrames, 600);
  assert.equal(processor.trimmedFrames, 0);
  assert.equal(processor.available, 0);
});

test("a deliberate clear requires a fresh jitter cushion before playback resumes", () => {
  const processor = new ProcessorClass();
  const output = [[new Float32Array(128), new Float32Array(128)]];

  processor.push(
    stereoFrames(processor.startThresholdFrames),
    24000);
  processor.process([], output);
  assert.equal(processor.started, true);

  processor.clear();
  processor.push(
    stereoFrames(processor.startThresholdFrames - 1),
    24000);
  processor.process([], output);
  assert.equal(processor.started, false);

  processor.push(stereoFrames(1), 24000);
  processor.process([], output);
  assert.equal(processor.started, true);
});

test("audio worklet accepts a direct transport message port", () => {
  const processor = new ProcessorClass();
  const port = {
    startCalled: false,
    start() {
      this.startCalled = true;
    }
  };

  processor.attachTransport(port);
  port.onmessage({
    data: {
      type: "push",
      samples: stereoFrames(128),
      sampleRate: 24000
    }
  });

  assert.equal(port.startCalled, true);
  assert.equal(processor.available, 128);
});

test("worker audio gently steers its queue toward the measured target", () => {
  const processor = new ProcessorClass();
  const baseRatio = processor.sourceRate / sampleRate;

  processor.available = 100;
  assert.ok(processor.resamplingRatio() < baseRatio);

  processor.available = processor.targetBufferedFrames;
  assert.equal(processor.resamplingRatio(), baseRatio);

  processor.available = processor.maximumBufferedFrames;
  assert.ok(processor.resamplingRatio() > baseRatio);
  assert.ok(processor.resamplingRatio() <= baseRatio * 1.02);
});
