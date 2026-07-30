const audioMagic = 0x41544541;
const audioHeaderBytes = 16;

export class ReconnectBackoff {
  constructor(
    setTimer = (callback, delay) =>
      globalThis.setTimeout(callback, delay),
    clearTimer = timer => globalThis.clearTimeout(timer),
    baseDelayMilliseconds = 750,
    maximumDelayMilliseconds = 15_000
  ) {
    this.setTimer = setTimer;
    this.clearTimer = clearTimer;
    this.baseDelayMilliseconds = baseDelayMilliseconds;
    this.maximumDelayMilliseconds = maximumDelayMilliseconds;
    this.attempt = 0;
    this.timer = null;
  }

  schedule(callback) {
    if (this.timer !== null) {
      return null;
    }

    const delay = Math.min(
      this.maximumDelayMilliseconds,
      this.baseDelayMilliseconds * (2 ** this.attempt));
    this.attempt += 1;
    this.timer = this.setTimer(() => {
      this.timer = null;
      callback();
    }, delay);
    return delay;
  }

  cancel() {
    if (this.timer === null) {
      return;
    }
    this.clearTimer(this.timer);
    this.timer = null;
  }

  reset() {
    this.cancel();
    this.attempt = 0;
  }
}

export function decodeRadioAudioFrame(buffer) {
  if (!(buffer instanceof ArrayBuffer) ||
      buffer.byteLength < audioHeaderBytes) {
    return null;
  }

  const view = new DataView(buffer);
  if (view.getUint32(0, true) !== audioMagic) {
    return null;
  }

  const version = view.getUint8(4);
  const channels = view.getUint8(5);
  const sampleRate = view.getUint16(6, true);
  const sequence = view.getUint32(8, true);
  const frameCount = view.getUint32(12, true);
  const sampleCount = frameCount * channels;
  const expectedBytes = audioHeaderBytes + (sampleCount * 2);
  const valid =
    version === 0 &&
    channels === 2 &&
    sampleRate >= 8000 &&
    expectedBytes === buffer.byteLength;

  return {
    valid,
    sequence,
    sampleRate,
    frameCount,
    samples: valid
      ? new Int16Array(buffer, audioHeaderBytes, sampleCount)
      : null
  };
}

export class AudioDeliveryTracker {
  constructor(deliveryExpected = true) {
    this.deliveryExpected = deliveryExpected !== false;
    this.reset();
  }

  reset() {
    this.receivedPackets = 0;
    this.receivedFrames = 0;
    this.malformedPackets = 0;
    this.missingPackets = 0;
    this.maximumPacketGapMilliseconds = 0;
    this.lastSequence = null;
    this.lastPacketAt = null;
    this.lastReportAt = 0;
  }

  setDeliveryExpected(expected) {
    const nextExpected = expected === true;
    if (this.deliveryExpected === nextExpected) {
      return;
    }

    this.deliveryExpected = nextExpected;
    this.lastSequence = null;
    this.lastPacketAt = null;
  }

  observe(frame, receivedAt = monotonicMilliseconds()) {
    this.receivedPackets += 1;
    if (!frame.valid) {
      this.malformedPackets += 1;
      return;
    }

    this.receivedFrames += frame.frameCount;
    if (!this.deliveryExpected) {
      return;
    }

    if (this.lastPacketAt !== null) {
      this.maximumPacketGapMilliseconds = Math.max(
        this.maximumPacketGapMilliseconds,
        receivedAt - this.lastPacketAt);
    }
    if (this.lastSequence !== null) {
      const expected = (this.lastSequence + 1) >>> 0;
      const missing = (frame.sequence - expected) >>> 0;
      if (missing > 0 && missing < 1_000_000) {
        this.missingPackets += missing;
      }
    }

    this.lastPacketAt = receivedAt;
    this.lastSequence = frame.sequence;
  }

  shouldReport(now = monotonicMilliseconds()) {
    if (now - this.lastReportAt < 1000) {
      return false;
    }
    this.lastReportAt = now;
    return true;
  }

  snapshot() {
    return {
      receivedPackets: this.receivedPackets,
      receivedFrames: this.receivedFrames,
      malformedPackets: this.malformedPackets,
      missingPackets: this.missingPackets,
      maximumPacketGapMilliseconds:
        this.maximumPacketGapMilliseconds
    };
  }
}

function monotonicMilliseconds() {
  return globalThis.performance?.now?.() ?? Date.now();
}
