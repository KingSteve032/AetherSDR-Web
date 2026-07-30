const minimumMeterDb = -60;

export function rmsToDb(rms) {
  if (!Number.isFinite(rms) || rms <= 0) {
    return -96;
  }
  return Math.max(-96, Math.min(0, 20 * Math.log10(rms)));
}

export function dbToMeterPercent(db) {
  if (!Number.isFinite(db)) {
    return 0;
  }
  return Math.round(
    Math.max(0, Math.min(1, (db - minimumMeterDb) / -minimumMeterDb)) *
    100);
}

export class LocalMicrophoneMonitor {
  constructor(onLevel) {
    this.onLevel = onLevel;
    this.context = null;
    this.stream = null;
    this.source = null;
    this.analyser = null;
    this.samples = null;
    this.animationFrame = 0;
    this.enabled = false;
  }

  async setEnabled(enabled) {
    if (!enabled) {
      await this.stop();
      return;
    }
    if (this.enabled) {
      return;
    }
    if (!navigator.mediaDevices?.getUserMedia) {
      throw new Error("This browser does not support microphone input.");
    }

    const AudioContextClass =
      window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) {
      throw new Error("This browser does not support Web Audio.");
    }

    try {
      this.stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          autoGainControl: false,
          echoCancellation: false,
          noiseSuppression: false
        },
        video: false
      });
      this.context = new AudioContextClass({ latencyHint: "interactive" });
      this.source = this.context.createMediaStreamSource(this.stream);
      this.analyser = this.context.createAnalyser();
      this.analyser.fftSize = 1024;
      this.analyser.smoothingTimeConstant = .55;
      this.samples = new Float32Array(this.analyser.fftSize);

      // This graph intentionally ends at the analyser. It is never connected
      // to speakers, a WebSocket, or the radio transmit path.
      this.source.connect(this.analyser);
      await this.context.resume();
      this.enabled = true;
      this.measure();
    } catch (error) {
      await this.stop();
      throw error;
    }
  }

  async stop() {
    this.enabled = false;
    if (this.animationFrame) {
      window.cancelAnimationFrame(this.animationFrame);
      this.animationFrame = 0;
    }
    this.source?.disconnect();
    this.analyser?.disconnect();
    this.stream?.getTracks().forEach(track => track.stop());
    if (this.context && this.context.state !== "closed") {
      await this.context.close();
    }
    this.context = null;
    this.stream = null;
    this.source = null;
    this.analyser = null;
    this.samples = null;
    this.onLevel?.(0, -96);
  }

  measure() {
    if (!this.enabled || !this.analyser || !this.samples) {
      return;
    }

    this.analyser.getFloatTimeDomainData(this.samples);
    let squareSum = 0;
    for (const sample of this.samples) {
      squareSum += sample * sample;
    }
    const db = rmsToDb(Math.sqrt(squareSum / this.samples.length));
    this.onLevel?.(dbToMeterPercent(db), db);
    this.animationFrame = window.requestAnimationFrame(() => this.measure());
  }
}
