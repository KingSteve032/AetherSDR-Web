const poorGapMilliseconds = 300;
const healthyGapMilliseconds = 150;

export class TransportTrafficTracker {
  constructor(now = monotonicMilliseconds) {
    this.now = now;
    this.reset();
  }

  reset(at = this.now()) {
    this.sampleStartedAt = at;
    this.lastMessageAt = null;
    this.clearSample();
  }

  observe(kind, byteLength, at = this.now()) {
    const bytes = Number(byteLength);
    if (!Number.isFinite(bytes) || bytes < 0) {
      return;
    }

    if (this.lastMessageAt !== null) {
      this.maximumGapMilliseconds = Math.max(
        this.maximumGapMilliseconds,
        Math.max(0, at - this.lastMessageAt));
    }
    this.lastMessageAt = at;
    this.receivedBytes += bytes;
    this.receivedMessages += 1;

    if (kind === "audio") {
      this.audioBytes += bytes;
      this.audioPackets += 1;
    } else if (kind === "spectrum") {
      this.spectrumBytes += bytes;
      this.spectrumFrames += 1;
    } else if (kind === "text") {
      this.textBytes += bytes;
      this.textMessages += 1;
    }
  }

  takeSnapshot(at = this.now()) {
    const sampleMilliseconds = Math.max(0, at - this.sampleStartedAt);
    const rateScale = sampleMilliseconds > 0
      ? 1000 / sampleMilliseconds
      : 0;
    const snapshot = {
      sampleMilliseconds,
      receivedBytes: this.receivedBytes,
      receivedMessages: this.receivedMessages,
      bytesPerSecond: this.receivedBytes * rateScale,
      bitsPerSecond: this.receivedBytes * rateScale * 8,
      audioBytesPerSecond: this.audioBytes * rateScale,
      spectrumBytesPerSecond: this.spectrumBytes * rateScale,
      textBytesPerSecond: this.textBytes * rateScale,
      messagesPerSecond: this.receivedMessages * rateScale,
      maximumGapMilliseconds: this.maximumGapMilliseconds,
      audioPackets: this.audioPackets,
      spectrumFrames: this.spectrumFrames,
      textMessages: this.textMessages
    };

    this.sampleStartedAt = at;
    this.clearSample();
    return snapshot;
  }

  clearSample() {
    this.receivedBytes = 0;
    this.receivedMessages = 0;
    this.audioBytes = 0;
    this.spectrumBytes = 0;
    this.textBytes = 0;
    this.audioPackets = 0;
    this.spectrumFrames = 0;
    this.textMessages = 0;
    this.maximumGapMilliseconds = 0;
  }
}

export class AdaptiveBandwidthController {
  constructor({
    poorSamplesRequired = 3,
    healthySamplesRequired = 30,
    minimumLowDurationMilliseconds = 120_000,
    normalCooldownMilliseconds = 60_000
  } = {}) {
    this.poorSamplesRequired = poorSamplesRequired;
    this.healthySamplesRequired = healthySamplesRequired;
    this.minimumLowDurationMilliseconds =
      minimumLowDurationMilliseconds;
    this.normalCooldownMilliseconds = normalCooldownMilliseconds;
    this.reset();
  }

  reset() {
    this.poorSamples = 0;
    this.healthySamples = 0;
    this.lastMissingPackets = null;
    this.automaticLowBandwidth = false;
    this.manualLowBandwidth = false;
    this.lowBandwidthStartedAt = 0;
    this.normalSuppressedUntil = 0;
  }

  noteManualSelection(enabled, now = Date.now()) {
    this.poorSamples = 0;
    this.healthySamples = 0;
    this.lastMissingPackets = null;
    this.automaticLowBandwidth = false;
    this.manualLowBandwidth = enabled === true;
    this.lowBandwidthStartedAt = enabled ? now : 0;
    this.normalSuppressedUntil = enabled
      ? Number.POSITIVE_INFINITY
      : now + this.normalCooldownMilliseconds;
  }

  noteAutomaticSelection(enabled, now = Date.now()) {
    this.poorSamples = 0;
    this.healthySamples = 0;
    this.automaticLowBandwidth = enabled === true;
    this.manualLowBandwidth = false;
    this.lowBandwidthStartedAt = enabled ? now : 0;
    this.normalSuppressedUntil = enabled
      ? 0
      : now + this.normalCooldownMilliseconds;
  }

  observe({
    traffic,
    missingPackets,
    lowBandwidth,
    connected,
    pageVisible
  }, now = Date.now()) {
    const missing = Math.max(0, Number(missingPackets) || 0);
    const missingDelta = this.lastMissingPackets === null
      ? 0
      : Math.max(0, missing - this.lastMissingPackets);
    this.lastMissingPackets = missing;

    if (!connected ||
        !pageVisible ||
        !traffic ||
        Number(traffic.sampleMilliseconds) < 1000 ||
        Number(traffic.receivedMessages) <= 0) {
      this.poorSamples = 0;
      this.healthySamples = 0;
      return null;
    }

    const maximumGap = Number(traffic.maximumGapMilliseconds) || 0;
    const poor = maximumGap >= poorGapMilliseconds || missingDelta > 0;
    const healthy =
      maximumGap <= healthyGapMilliseconds && missingDelta === 0;

    if (!lowBandwidth) {
      this.healthySamples = 0;
      this.poorSamples = poor ? this.poorSamples + 1 : 0;
      if (now < this.normalSuppressedUntil ||
          this.poorSamples < this.poorSamplesRequired) {
        return null;
      }

      this.noteAutomaticSelection(true, now);
      return {
        enabled: true,
        reason: missingDelta > 0
          ? `${missingDelta} missing audio packet` +
            `${missingDelta === 1 ? "" : "s"}`
          : `${Math.round(maximumGap)} ms delivery gap`
      };
    }

    this.poorSamples = 0;
    if (!this.automaticLowBandwidth || this.manualLowBandwidth) {
      this.healthySamples = 0;
      return null;
    }

    this.healthySamples = healthy ? this.healthySamples + 1 : 0;
    if (now - this.lowBandwidthStartedAt <
          this.minimumLowDurationMilliseconds ||
        this.healthySamples < this.healthySamplesRequired) {
      return null;
    }

    this.noteAutomaticSelection(false, now);
    return {
      enabled: false,
      reason: "sustained healthy delivery"
    };
  }
}

export function formatTrafficRate(bitsPerSecond) {
  const value = Math.max(0, Number(bitsPerSecond) || 0);
  if (value >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(2)} Mb/s`;
  }
  if (value >= 1000) {
    return `${Math.round(value / 1000)} kb/s`;
  }
  return `${Math.round(value)} b/s`;
}

function monotonicMilliseconds() {
  return globalThis.performance?.now?.() ?? Date.now();
}
