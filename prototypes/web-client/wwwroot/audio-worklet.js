class AetherRadioAudioProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.capacity = 4800;
    this.startThresholdFrames = 1080;
    this.targetBufferedFrames = 1080;
    this.maximumBufferedFrames = 2880;
    this.left = new Float32Array(this.capacity);
    this.right = new Float32Array(this.capacity);
    this.readIndex = 0;
    this.writeIndex = 0;
    this.available = 0;
    this.sourceRate = 24000;
    this.sourcePosition = 0;
    this.started = false;
    this.receivedFrames = 0;
    this.playedFrames = 0;
    this.underruns = 0;
    this.trimmedFrames = 0;
    this.clearedFrames = 0;
    this.outputFramesSinceReport = 0;
    this.transportPort = null;

    this.port.onmessage = event => {
      if (event.data?.type === "attachTransport" && event.data.port) {
        this.attachTransport(event.data.port);
        return;
      }
      this.handleMessage(event.data);
    };
  }

  attachTransport(port) {
    this.transportPort?.close?.();
    this.transportPort = port;
    this.transportPort.onmessage = event => {
      this.handleMessage(event.data);
    };
    this.transportPort.start?.();
  }

  handleMessage(message) {
    if (message?.type === "clear") {
      this.clear();
    } else if (message?.type === "push" &&
               message.samples instanceof Int16Array) {
      this.push(message.samples, message.sampleRate);
    }
  }

  clear() {
    this.clearedFrames += this.available;
    this.resetQueue();
    this.report();
  }

  resetQueue() {
    this.readIndex = 0;
    this.writeIndex = 0;
    this.available = 0;
    this.sourcePosition = 0;
    this.started = false;
  }

  push(samples, sourceRate) {
    if (Number.isFinite(sourceRate) &&
        sourceRate >= 8000 &&
        sourceRate !== this.sourceRate) {
      this.clearedFrames += this.available;
      this.resetQueue();
      this.sourceRate = sourceRate;
    }

    const frameCount = Math.floor(samples.length / 2);
    this.receivedFrames += frameCount;
    let firstFrame = 0;
    if (frameCount >= this.maximumBufferedFrames) {
      this.trimmedFrames +=
        this.available + (frameCount - this.maximumBufferedFrames);
      this.resetQueue();
      firstFrame = frameCount - this.maximumBufferedFrames;
    } else if (
      this.available + frameCount > this.maximumBufferedFrames
    ) {
      this.discard(
        Math.max(0, this.available - this.targetBufferedFrames),
        true);
    }

    for (let frame = firstFrame; frame < frameCount; frame += 1) {
      if (this.available === this.capacity) {
        this.readIndex = (this.readIndex + 1) % this.capacity;
        this.available -= 1;
        this.trimmedFrames += 1;
      }

      this.left[this.writeIndex] = samples[frame * 2] / 32768;
      this.right[this.writeIndex] = samples[(frame * 2) + 1] / 32768;
      this.writeIndex = (this.writeIndex + 1) % this.capacity;
      this.available += 1;
    }
  }

  discard(frameCount, trimmed = false) {
    const discarded = Math.min(
      Math.max(0, Math.floor(frameCount)),
      this.available);
    this.readIndex = (this.readIndex + discarded) % this.capacity;
    this.available -= discarded;
    this.sourcePosition = 0;
    if (trimmed) {
      this.trimmedFrames += discarded;
    }
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    if (!output?.length) {
      return true;
    }

    const leftOutput = output[0];
    const rightOutput = output[1] || output[0];
    this.outputFramesSinceReport += leftOutput.length;
    if (!this.started && this.available >= this.startThresholdFrames) {
      this.started = true;
    }
    if (!this.started) {
      leftOutput.fill(0);
      rightOutput.fill(0);
      this.reportWhenDue();
      return true;
    }

    const ratio = this.resamplingRatio();
    let outputIndex = 0;
    for (; outputIndex < leftOutput.length; outputIndex += 1) {
      if (this.available < 2) {
        this.started = false;
        this.sourcePosition = 0;
        this.underruns += 1;
        break;
      }

      const nextIndex = (this.readIndex + 1) % this.capacity;
      const mix = this.sourcePosition;
      leftOutput[outputIndex] =
        this.left[this.readIndex] +
        ((this.left[nextIndex] - this.left[this.readIndex]) * mix);
      rightOutput[outputIndex] =
        this.right[this.readIndex] +
        ((this.right[nextIndex] - this.right[this.readIndex]) * mix);

      this.sourcePosition += ratio;
      const consumed = Math.floor(this.sourcePosition);
      if (consumed > 0) {
        const bounded = Math.min(consumed, this.available);
        this.readIndex = (this.readIndex + bounded) % this.capacity;
        this.available -= bounded;
        this.sourcePosition -= bounded;
        this.playedFrames += bounded;
      }
    }

    if (outputIndex < leftOutput.length) {
      leftOutput.fill(0, outputIndex);
      rightOutput.fill(0, outputIndex);
    }
    this.reportWhenDue();
    return true;
  }

  resamplingRatio() {
    const baseRatio = this.sourceRate / sampleRate;
    if (this.targetBufferedFrames <= 0) {
      return baseRatio;
    }

    const queueError =
      (this.available - this.targetBufferedFrames) /
      this.targetBufferedFrames;
    const adjustment = Math.max(
      -.02,
      Math.min(.02, queueError * .02));
    return baseRatio * (1 + adjustment);
  }

  reportWhenDue() {
    if (this.outputFramesSinceReport < sampleRate / 2) {
      return;
    }
    this.outputFramesSinceReport = 0;
    this.report();
  }

  report() {
    this.port.postMessage?.({
      type: "diagnostics",
      sourceSampleRate: this.sourceRate,
      outputSampleRate: sampleRate,
      queueFrames: this.available,
      queueMilliseconds:
        this.sourceRate > 0
          ? (this.available * 1000) / this.sourceRate
          : 0,
      started: this.started,
      receivedFrames: this.receivedFrames,
      playedFrames: this.playedFrames,
      underruns: this.underruns,
      trimmedFrames: this.trimmedFrames,
      clearedFrames: this.clearedFrames
    });
  }
}

registerProcessor("aether-radio-audio", AetherRadioAudioProcessor);
