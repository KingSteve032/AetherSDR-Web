import {
  AudioDeliveryTracker,
  decodeRadioAudioFrame
} from "./radio-transport-core.js?v=background-delivery-1";
import {
  TransportTrafficTracker
} from "./network-profile.js?v=network-profile-1";

export class RadioTransportClient {
  constructor(
    workerUrl,
    workerFactory = url =>
      new Worker(url, {
        type: "module",
        name: "aether-radio-transport"
      }),
    socketFactory = (url, subprotocol) =>
      new WebSocket(url, subprotocol)) {
    this.workerUrl = workerUrl;
    this.workerFactory = workerFactory;
    this.socketFactory = socketFactory;
    this.worker = null;
    this.direct = null;
    this.readyState = WebSocket.CLOSED;
    this.connectionId = 0;
    this.lastUrl = "";
    this.lastSubprotocol = "";
    this.opened = false;
    this.audioEnabled = false;
    this.sliceAvailable = false;
    this.onopen = null;
    this.ontext = null;
    this.onbinary = null;
    this.onclose = null;
    this.onerror = null;
    this.onaudiodiagnostics = null;
    this.onnetworkdiagnostics = null;
    this.startWorker();
  }

  get mode() {
    return this.direct ? "main-thread-fallback" : "worker";
  }

  connect(url, subprotocol) {
    this.lastUrl = url;
    this.lastSubprotocol = subprotocol;
    this.opened = false;
    if (this.direct) {
      this.direct.connect(url, subprotocol);
      this.readyState = this.direct.readyState;
      return;
    }

    this.connectionId += 1;
    this.readyState = WebSocket.CONNECTING;
    this.worker.postMessage({
      type: "connect",
      connectionId: this.connectionId,
      url,
      subprotocol
    });
  }

  send(data) {
    if (this.direct) {
      return this.direct.send(data);
    }
    if (this.readyState !== WebSocket.OPEN) {
      return false;
    }
    this.worker.postMessage({
      type: "send",
      connectionId: this.connectionId,
      data
    });
    return true;
  }

  close(code = 1000, reason = "") {
    if (this.direct) {
      this.direct.close(code, reason);
      this.readyState = this.direct.readyState;
      return;
    }
    if (this.readyState > WebSocket.OPEN) {
      return;
    }
    this.readyState = WebSocket.CLOSING;
    this.worker.postMessage({
      type: "close",
      connectionId: this.connectionId,
      code,
      reason
    });
  }

  attachAudioPort(port) {
    if (this.direct) {
      this.direct.attachAudioPort(port);
      return;
    }
    this.worker.postMessage(
      { type: "audio.attach", port },
      [port]);
  }

  setAudioState(enabled, sliceAvailable) {
    this.audioEnabled = enabled === true;
    this.sliceAvailable = sliceAvailable === true;
    if (this.direct) {
      this.direct.setAudioState(
        this.audioEnabled,
        this.sliceAvailable);
      return;
    }
    this.worker.postMessage({
      type: "audio.state",
      enabled: this.audioEnabled,
      sliceAvailable: this.sliceAvailable
    });
  }

  requestAudioDiagnostics() {
    if (this.direct) {
      this.direct.requestAudioDiagnostics();
      return;
    }
    this.worker.postMessage({ type: "audio.diagnostics.request" });
  }

  requestNetworkDiagnostics() {
    if (this.direct) {
      this.direct.requestNetworkDiagnostics();
      return;
    }
    this.worker.postMessage({ type: "network.diagnostics.request" });
  }

  startWorker() {
    try {
      this.worker = this.workerFactory(this.workerUrl);
      this.worker.addEventListener(
        "message",
        event => this.handleWorkerMessage(event.data));
      this.worker.addEventListener("error", () => {
        this.activateDirectFallback(
          this.readyState === WebSocket.CONNECTING ||
          this.readyState === WebSocket.OPEN);
      });
    } catch {
      this.activateDirectFallback(false);
    }
  }

  activateDirectFallback(reconnect) {
    this.worker?.terminate?.();
    this.worker = null;
    const direct = new DirectRadioTransportClient(this.socketFactory);
    direct.onopen = () => {
      this.readyState = WebSocket.OPEN;
      this.opened = true;
      this.onopen?.();
    };
    direct.ontext = data => this.ontext?.(data);
    direct.onbinary = data => this.onbinary?.(data);
    direct.onclose = event => {
      this.readyState = WebSocket.CLOSED;
      this.onclose?.(event);
    };
    direct.onerror = () => this.onerror?.();
    direct.onaudiodiagnostics =
      diagnostics => this.onaudiodiagnostics?.(diagnostics);
    direct.onnetworkdiagnostics =
      diagnostics => this.onnetworkdiagnostics?.(diagnostics);
    direct.setAudioState(this.audioEnabled, this.sliceAvailable);
    this.direct = direct;

    if (reconnect && this.lastUrl) {
      direct.connect(this.lastUrl, this.lastSubprotocol);
      this.readyState = direct.readyState;
    }
  }

  handleWorkerMessage(message) {
    if (message?.connectionId !== this.connectionId) {
      return;
    }

    switch (message.type) {
      case "open":
        this.readyState = WebSocket.OPEN;
        this.opened = true;
        this.onopen?.();
        break;
      case "text":
        this.ontext?.(message.data);
        break;
      case "binary":
        this.onbinary?.(message.data);
        break;
      case "close":
        this.readyState = WebSocket.CLOSED;
        this.onclose?.(message);
        break;
      case "error":
        this.onerror?.();
        break;
      case "audio.diagnostics":
        this.onaudiodiagnostics?.(message.diagnostics);
        break;
      case "network.diagnostics":
        this.onnetworkdiagnostics?.(message.diagnostics);
        break;
    }
  }
}

class DirectRadioTransportClient {
  constructor(socketFactory) {
    this.socketFactory = socketFactory;
    this.socket = null;
    this.readyState = WebSocket.CLOSED;
    this.connectionId = 0;
    this.audioPort = null;
    this.audioEnabled = false;
    this.sliceAvailable = false;
    this.audioTracker = new AudioDeliveryTracker(false);
    this.trafficTracker = new TransportTrafficTracker();
    this.onopen = null;
    this.ontext = null;
    this.onbinary = null;
    this.onclose = null;
    this.onerror = null;
    this.onaudiodiagnostics = null;
    this.onnetworkdiagnostics = null;
  }

  connect(url, subprotocol) {
    const previous = this.socket;
    if (previous?.readyState <= WebSocket.OPEN) {
      previous.close(1000, "Radio connection replaced.");
    }

    this.connectionId += 1;
    const connectionId = this.connectionId;
    const socket = this.socketFactory(url, subprotocol);
    socket.binaryType = "arraybuffer";
    this.socket = socket;
    this.readyState = WebSocket.CONNECTING;
    this.audioTracker.reset();
    this.trafficTracker.reset();

    socket.onopen = () => {
      if (this.isCurrent(connectionId, socket)) {
        this.readyState = WebSocket.OPEN;
        this.onopen?.();
      }
    };
    socket.onmessage = event => {
      if (!this.isCurrent(connectionId, socket)) {
        return;
      }
      if (typeof event.data === "string") {
        this.trafficTracker.observe(
          "text",
          utf8ByteLength(event.data));
        this.ontext?.(event.data);
        return;
      }
      if (!(event.data instanceof ArrayBuffer)) {
        return;
      }

      const audioFrame = decodeRadioAudioFrame(event.data);
      if (!audioFrame) {
        this.trafficTracker.observe("spectrum", event.data.byteLength);
        this.onbinary?.(event.data);
        return;
      }

      const receivedAt = monotonicMilliseconds();
      this.trafficTracker.observe(
        "audio",
        event.data.byteLength,
        receivedAt);
      this.audioTracker.observe(audioFrame, receivedAt);
      if (audioFrame.valid &&
          this.audioPort &&
          this.audioEnabled &&
          this.sliceAvailable) {
        this.audioPort.postMessage(
          {
            type: "push",
            samples: audioFrame.samples,
            sampleRate: audioFrame.sampleRate
          },
          [event.data]);
      }
      if (this.audioTracker.shouldReport(receivedAt)) {
        this.requestAudioDiagnostics();
      }
    };
    socket.onclose = event => {
      if (this.isCurrent(connectionId, socket)) {
        this.socket = null;
        this.readyState = WebSocket.CLOSED;
        this.onclose?.(event);
      }
    };
    socket.onerror = () => {
      if (this.isCurrent(connectionId, socket)) {
        this.onerror?.();
      }
    };
  }

  send(data) {
    if (this.socket?.readyState !== WebSocket.OPEN) {
      return false;
    }
    this.socket.send(data);
    return true;
  }

  close(code, reason) {
    if (this.socket?.readyState > WebSocket.OPEN) {
      return;
    }
    this.readyState = WebSocket.CLOSING;
    this.socket?.close(code, reason);
  }

  attachAudioPort(port) {
    this.audioPort?.close?.();
    this.audioPort = port;
    this.audioPort?.start?.();
  }

  setAudioState(enabled, sliceAvailable) {
    this.audioEnabled = enabled === true;
    this.sliceAvailable = sliceAvailable === true;
    this.audioTracker.setDeliveryExpected(
      this.audioEnabled && this.sliceAvailable);
  }

  requestAudioDiagnostics() {
    this.onaudiodiagnostics?.(this.audioTracker.snapshot());
  }

  requestNetworkDiagnostics() {
    this.onnetworkdiagnostics?.(this.trafficTracker.takeSnapshot());
  }

  isCurrent(connectionId, socket) {
    return connectionId === this.connectionId && socket === this.socket;
  }
}

function monotonicMilliseconds() {
  return globalThis.performance?.now?.() ?? Date.now();
}

function utf8ByteLength(value) {
  return new TextEncoder().encode(String(value)).byteLength;
}
