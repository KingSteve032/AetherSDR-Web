const audioMagic = 0x41544541;
const audioHeaderBytes = 16;
const foregroundSettleMilliseconds = 250;
const contextProgressProbeMilliseconds = 120;

export class RadioAudioPlayer {
  constructor(delay = waitMilliseconds) {
    this.context = null;
    this.node = null;
    this.gain = null;
    this.enabled = false;
    this.sliceAvailable = false;
    this.masterVolume = 1;
    this.headphoneVolume = .9;
    this.receivedPackets = 0;
    this.receivedFrames = 0;
    this.malformedPackets = 0;
    this.missingPackets = 0;
    this.maximumPacketGapMilliseconds = 0;
    this.lastSequence = null;
    this.lastPacketAt = null;
    this.workletDiagnostics = null;
    this.workletReportedAt = 0;
    this.transportDiagnostics = null;
    this.transportPortHandler = null;
    this.playbackStateHandler = null;
    this.deliveryPath = "legacy-main-thread";
    this.pageVisible = true;
    this.recoveryPending = false;
    this.backgroundTransitions = 0;
    this.foregroundRecoveries = 0;
    this.lifecycleGeneration = 0;
    this.delay = delay;
  }

  async setEnabled(enabled) {
    if (!enabled) {
      this.lifecycleGeneration += 1;
      this.enabled = false;
      this.recoveryPending = false;
      this.notifyPlaybackState();
      this.node?.port.postMessage({ type: "clear" });
      if (this.context?.state === "running") {
        await this.context.suspend();
      }
      return;
    }

    if (!this.context) {
      const AudioContextClass =
        window.AudioContext || window.webkitAudioContext;
      if (!AudioContextClass) {
        throw new Error("This browser does not support Web Audio.");
      }

      this.context = new AudioContextClass({ latencyHint: "interactive" });
      const context = this.context;
      context.onstatechange = () => {
        if (context !== this.context ||
            !this.enabled ||
            !this.pageVisible ||
            context.state === "running") {
          return;
        }
        this.recoveryPending = true;
        this.notifyPlaybackState();
      };
      await this.context.audioWorklet.addModule(
        "/audio-worklet.js?v=audio-worker-tune-1");
      this.node = new AudioWorkletNode(
        this.context,
        "aether-radio-audio",
        {
          numberOfInputs: 0,
          numberOfOutputs: 1,
          outputChannelCount: [2]
        });
      this.node.port.onmessage = event => {
        if (event.data?.type !== "diagnostics") {
          return;
        }
        this.workletDiagnostics = event.data;
        this.workletReportedAt = Date.now();
      };
      this.attachTransportPort();
      this.gain = this.context.createGain();
      this.node.connect(this.gain);
      this.gain.connect(this.context.destination);
      this.applyVolume();
    }

    this.enabled = true;
    this.recoveryPending = !this.pageVisible;
    if (this.pageVisible) {
      try {
        await this.context.resume();
      } catch (error) {
        this.enabled = false;
        this.recoveryPending = false;
        this.notifyPlaybackState();
        throw error;
      }
    }
    this.notifyPlaybackState();
  }

  async setPageVisible(visible) {
    const nextVisible = visible !== false;
    const contextNeedsResume =
      nextVisible &&
      this.enabled &&
      this.context &&
      this.context.state !== "running";
    if (this.pageVisible === nextVisible &&
        (!nextVisible ||
         (!this.recoveryPending && !contextNeedsResume))) {
      return false;
    }

    const generation = ++this.lifecycleGeneration;
    if (!nextVisible) {
      if (this.pageVisible) {
        this.backgroundTransitions += 1;
      }
      this.pageVisible = false;
      this.recoveryPending = this.enabled;
      this.notifyPlaybackState();
      this.reset();
      if (this.enabled && this.context?.state === "running") {
        await this.context.suspend();
      }
      return true;
    }

    this.pageVisible = true;
    if (!this.enabled) {
      this.recoveryPending = false;
      this.notifyPlaybackState();
      return true;
    }

    this.recoveryPending = true;
    this.notifyPlaybackState();
    this.reset();
    return this.recoverForeground(generation, false);
  }

  async resumeFromUserGesture() {
    if (!this.enabled ||
        !this.pageVisible ||
        !this.recoveryPending ||
        !this.context) {
      return false;
    }

    const generation = ++this.lifecycleGeneration;
    this.notifyPlaybackState();
    this.reset();
    return this.recoverForeground(generation, true);
  }

  setVolume(masterPercent, headphonePercent) {
    this.masterVolume = Math.max(0, Math.min(1, masterPercent / 100));
    this.headphoneVolume =
      Math.max(0, Math.min(1, headphonePercent / 100));
    this.applyVolume();
  }

  reset() {
    this.node?.port.postMessage({ type: "clear" });
  }

  setTransportHandlers(portHandler, playbackStateHandler) {
    this.transportPortHandler = portHandler;
    this.playbackStateHandler = playbackStateHandler;
    this.attachTransportPort();
    this.notifyPlaybackState();
  }

  updateTransportDiagnostics(diagnostics) {
    this.transportDiagnostics = diagnostics || null;
  }

  setDeliveryPath(deliveryPath) {
    this.deliveryPath = String(deliveryPath || "legacy-main-thread");
  }

  setSliceAvailable(available) {
    const nextAvailable = Boolean(available);
    if (this.sliceAvailable === nextAvailable) {
      return;
    }
    this.sliceAvailable = nextAvailable;
    this.notifyPlaybackState();
    if (!nextAvailable) {
      this.reset();
    }
  }

  acceptFrame(buffer) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < audioHeaderBytes) {
      return false;
    }

    const view = new DataView(buffer);
    if (view.getUint32(0, true) !== audioMagic) {
      return false;
    }

    this.receivedPackets += 1;
    const version = view.getUint8(4);
    const channels = view.getUint8(5);
    const sampleRate = view.getUint16(6, true);
    const sequence = view.getUint32(8, true);
    const frameCount = view.getUint32(12, true);
    const sampleCount = frameCount * channels;
    const expectedBytes = audioHeaderBytes + (sampleCount * 2);
    if (version !== 0 ||
        channels !== 2 ||
        sampleRate < 8000 ||
        expectedBytes !== buffer.byteLength) {
      this.malformedPackets += 1;
      return true;
    }

    const receivedAt = monotonicMilliseconds();
    if (this.lastPacketAt !== null) {
      this.maximumPacketGapMilliseconds = Math.max(
        this.maximumPacketGapMilliseconds,
        receivedAt - this.lastPacketAt);
    }
    if (this.lastSequence !== null) {
      const expected = (this.lastSequence + 1) >>> 0;
      const missing = (sequence - expected) >>> 0;
      if (missing > 0 && missing < 1_000_000) {
        this.missingPackets += missing;
      }
    }
    this.lastPacketAt = receivedAt;
    this.lastSequence = sequence;
    this.receivedFrames += frameCount;
    if (!this.enabled || !this.node || !this.sliceAvailable) {
      return true;
    }

    const samples = new Int16Array(
      buffer,
      audioHeaderBytes,
      sampleCount);
    this.node.port.postMessage(
      { type: "push", samples, sampleRate },
      [buffer]);
    return true;
  }

  getDiagnostics(activeSliceId = "") {
    const worklet = this.workletDiagnostics || {};
    const transport = this.transportDiagnostics || {};
    const sourceSampleRate = finiteNumber(worklet.sourceSampleRate);
    const outputSampleRate =
      finiteNumber(worklet.outputSampleRate) ||
      finiteNumber(this.context?.sampleRate);
    const queueFrames = finiteNumber(worklet.queueFrames);
    const queueMilliseconds = finiteNumber(worklet.queueMilliseconds);
    const baseLatencyMilliseconds =
      secondsToMilliseconds(this.context?.baseLatency);
    const outputLatencyMilliseconds =
      secondsToMilliseconds(this.context?.outputLatency);

    return {
      enabled: this.enabled,
      contextState: this.context?.state || "none",
      deliveryPath: this.deliveryPath,
      pageVisible: this.pageVisible,
      playbackSuppressed:
        this.enabled && (!this.pageVisible || this.recoveryPending),
      recoveryPending: this.recoveryPending,
      backgroundTransitions: this.backgroundTransitions,
      foregroundRecoveries: this.foregroundRecoveries,
      sliceAvailable: this.sliceAvailable,
      activeSliceId: String(activeSliceId || "").slice(0, 16),
      sourceSampleRate,
      outputSampleRate,
      receivedPackets: this.transportDiagnostics
        ? finiteNumber(transport.receivedPackets)
        : this.receivedPackets,
      receivedFrames: this.transportDiagnostics
        ? finiteNumber(transport.receivedFrames)
        : this.receivedFrames,
      malformedPackets: this.transportDiagnostics
        ? finiteNumber(transport.malformedPackets)
        : this.malformedPackets,
      missingPackets: this.transportDiagnostics
        ? finiteNumber(transport.missingPackets)
        : this.missingPackets,
      maximumPacketGapMilliseconds:
        this.transportDiagnostics
          ? finiteNumber(transport.maximumPacketGapMilliseconds)
          : this.maximumPacketGapMilliseconds,
      playedFrames: finiteNumber(worklet.playedFrames),
      queueFrames,
      queueMilliseconds,
      started: Boolean(worklet.started),
      underruns: finiteNumber(worklet.underruns),
      trimmedFrames: finiteNumber(worklet.trimmedFrames),
      clearedFrames: finiteNumber(worklet.clearedFrames),
      baseLatencyMilliseconds,
      outputLatencyMilliseconds,
      estimatedLatencyMilliseconds:
        queueMilliseconds +
        baseLatencyMilliseconds +
        outputLatencyMilliseconds,
      workletReportAgeMilliseconds: this.workletReportedAt > 0
        ? Math.min(60_000, Date.now() - this.workletReportedAt)
        : null
    };
  }

  applyVolume() {
    if (!this.gain || !this.context) {
      return;
    }

    const level = this.masterVolume * this.headphoneVolume;
    this.gain.gain.setTargetAtTime(
      level,
      this.context.currentTime,
      .015);
  }

  attachTransportPort() {
    if (!this.node || !this.transportPortHandler) {
      return;
    }

    const channel = new MessageChannel();
    this.node.port.postMessage(
      {
        type: "attachTransport",
        port: channel.port1
      },
      [channel.port1]);
    this.transportPortHandler(channel.port2);
  }

  notifyPlaybackState() {
    this.playbackStateHandler?.(
      this.enabled && this.pageVisible && !this.recoveryPending,
      this.sliceAvailable);
  }

  async recoverForeground(generation, userGesture) {
    const context = this.context;
    if (!context || context.state === "closed") {
      throw new Error("The browser audio engine is no longer available.");
    }

    if (context.state === "running") {
      await context.suspend();
    }
    if (!userGesture) {
      await this.delay(foregroundSettleMilliseconds);
    }
    if (!this.recoveryIsCurrent(generation)) {
      return false;
    }

    await context.resume();
    let advancing = await this.contextClockIsAdvancing(context, generation);
    if (!advancing && !userGesture) {
      if (context.state === "running") {
        await context.suspend();
      }
      await this.delay(foregroundSettleMilliseconds);
      if (!this.recoveryIsCurrent(generation)) {
        return false;
      }
      await context.resume();
      advancing = await this.contextClockIsAdvancing(context, generation);
    }
    if (!advancing) {
      this.recoveryPending = true;
      this.notifyPlaybackState();
      throw new Error(
        "PC audio is waiting for a browser interaction to resume.");
    }
    if (!this.recoveryIsCurrent(generation)) {
      return false;
    }

    this.recoveryPending = false;
    this.foregroundRecoveries += 1;
    this.notifyPlaybackState();
    return true;
  }

  async contextClockIsAdvancing(context, generation) {
    if (context.state !== "running") {
      return false;
    }

    const startedAt = Number(context.currentTime);
    await this.delay(contextProgressProbeMilliseconds);
    if (!this.recoveryIsCurrent(generation) ||
        context.state !== "running") {
      return false;
    }

    const currentTime = Number(context.currentTime);
    return Number.isFinite(startedAt) &&
      Number.isFinite(currentTime) &&
      currentTime > startedAt;
  }

  recoveryIsCurrent(generation) {
    return generation === this.lifecycleGeneration &&
      this.pageVisible &&
      this.enabled;
  }
}

function finiteNumber(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric >= 0 ? numeric : 0;
}

function secondsToMilliseconds(value) {
  return finiteNumber(value) * 1000;
}

function monotonicMilliseconds() {
  return globalThis.performance?.now?.() ?? Date.now();
}

function waitMilliseconds(milliseconds) {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds));
}
