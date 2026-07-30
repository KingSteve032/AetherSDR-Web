import {
  AudioDeliveryTracker,
  decodeRadioAudioFrame
} from "./radio-transport-core.js?v=background-delivery-1";
import {
  TransportTrafficTracker
} from "./network-profile.js?v=network-profile-1";

const audioTracker = new AudioDeliveryTracker(false);
const trafficTracker = new TransportTrafficTracker();
let activeConnectionId = 0;
let socket = null;
let audioPort = null;
let audioEnabled = false;
let sliceAvailable = false;

self.onmessage = event => {
  const message = event.data || {};
  switch (message.type) {
    case "connect":
      connect(message);
      break;
    case "send":
      if (message.connectionId === activeConnectionId &&
          socket?.readyState === WebSocket.OPEN) {
        socket.send(message.data);
      }
      break;
    case "close":
      if (message.connectionId === activeConnectionId &&
          socket?.readyState <= WebSocket.OPEN) {
        socket.close(message.code || 1000, message.reason || "");
      }
      break;
    case "audio.attach":
      audioPort?.close?.();
      audioPort = message.port || null;
      audioPort?.start?.();
      break;
    case "audio.state":
      audioEnabled = message.enabled === true;
      sliceAvailable = message.sliceAvailable === true;
      audioTracker.setDeliveryExpected(audioEnabled && sliceAvailable);
      break;
    case "audio.diagnostics.request":
      postAudioDiagnostics();
      break;
    case "network.diagnostics.request":
      postNetworkDiagnostics();
      break;
  }
};

function connect(message) {
  activeConnectionId = message.connectionId;
  const connectionId = activeConnectionId;
  const previousSocket = socket;
  socket = null;
  if (previousSocket?.readyState <= WebSocket.OPEN) {
    previousSocket.close(1000, "Radio connection replaced.");
  }

  audioTracker.reset();
  trafficTracker.reset();
  const nextSocket = new WebSocket(message.url, message.subprotocol);
  nextSocket.binaryType = "arraybuffer";
  socket = nextSocket;

  nextSocket.onopen = () => {
    if (isCurrent(connectionId, nextSocket)) {
      self.postMessage({ type: "open", connectionId });
    }
  };
  nextSocket.onmessage = event => {
    if (!isCurrent(connectionId, nextSocket)) {
      return;
    }
    if (typeof event.data === "string") {
      trafficTracker.observe(
        "text",
        utf8ByteLength(event.data));
      self.postMessage({
        type: "text",
        connectionId,
        data: event.data
      });
      return;
    }
    if (!(event.data instanceof ArrayBuffer)) {
      return;
    }

    const audioFrame = decodeRadioAudioFrame(event.data);
    if (audioFrame) {
      const receivedAt = monotonicMilliseconds();
      trafficTracker.observe(
        "audio",
        event.data.byteLength,
        receivedAt);
      audioTracker.observe(audioFrame, receivedAt);
      if (audioFrame.valid &&
          audioPort &&
          audioEnabled &&
          sliceAvailable) {
        audioPort.postMessage(
          {
            type: "push",
            samples: audioFrame.samples,
            sampleRate: audioFrame.sampleRate
          },
          [event.data]);
      }
      if (audioTracker.shouldReport(receivedAt)) {
        postAudioDiagnostics();
      }
      return;
    }

    trafficTracker.observe("spectrum", event.data.byteLength);
    self.postMessage(
      {
        type: "binary",
        connectionId,
        data: event.data
      },
      [event.data]);
  };
  nextSocket.onclose = event => {
    if (isCurrent(connectionId, nextSocket)) {
      socket = null;
      self.postMessage({
        type: "close",
        connectionId,
        code: event.code,
        reason: event.reason || "",
        wasClean: event.wasClean === true
      });
    }
  };
  nextSocket.onerror = () => {
    if (isCurrent(connectionId, nextSocket)) {
      self.postMessage({ type: "error", connectionId });
    }
  };
}

function isCurrent(connectionId, candidate) {
  return connectionId === activeConnectionId && candidate === socket;
}

function postAudioDiagnostics() {
  self.postMessage({
    type: "audio.diagnostics",
    connectionId: activeConnectionId,
    diagnostics: audioTracker.snapshot()
  });
}

function postNetworkDiagnostics() {
  self.postMessage({
    type: "network.diagnostics",
    connectionId: activeConnectionId,
    diagnostics: trafficTracker.takeSnapshot()
  });
}

function monotonicMilliseconds() {
  return globalThis.performance?.now?.() ?? Date.now();
}

function utf8ByteLength(value) {
  return new TextEncoder().encode(String(value)).byteLength;
}
