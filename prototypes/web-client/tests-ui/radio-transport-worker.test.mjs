import assert from "node:assert/strict";
import test from "node:test";

const workerMessages = [];
const sockets = [];
globalThis.self = {
  onmessage: null,
  postMessage(message, transfer = []) {
    workerMessages.push({ message, transfer });
  }
};

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  constructor(url, subprotocol) {
    this.url = url;
    this.subprotocol = subprotocol;
    this.readyState = FakeWebSocket.CONNECTING;
    this.sent = [];
    sockets.push(this);
  }

  send(data) {
    this.sent.push(data);
  }

  close(code, reason) {
    this.readyState = FakeWebSocket.CLOSED;
    this.onclose?.({ code, reason, wasClean: true });
  }

  open() {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
  }

  message(data) {
    this.onmessage?.({ data });
  }
}

globalThis.WebSocket = FakeWebSocket;
await import("../wwwroot/radio-transport-worker.js");

test("transport worker routes audio directly to the worklet port", () => {
  const audioMessages = [];
  const audioPort = {
    start() {},
    postMessage(message, transfer) {
      audioMessages.push({ message, transfer });
    }
  };

  self.onmessage({
    data: {
      type: "audio.attach",
      port: audioPort
    }
  });
  self.onmessage({
    data: {
      type: "audio.state",
      enabled: true,
      sliceAvailable: true
    }
  });
  self.onmessage({
    data: {
      type: "connect",
      connectionId: 1,
      url: "wss://example.test/ws/radio",
      subprotocol: "aether.v0"
    }
  });
  const socket = sockets.at(-1);
  socket.open();
  socket.message(audioFrame(1));

  assert.equal(audioMessages.length, 1);
  assert.equal(audioMessages[0].message.type, "push");
  assert.equal(audioMessages[0].message.sampleRate, 24000);
  assert.equal(audioMessages[0].message.samples.length, 4);
  assert.equal(
    workerMessages.some(entry => entry.message.type === "binary"),
    false);
});

test("transport worker transfers spectrum frames back to the page", () => {
  const socket = sockets.at(-1);
  const spectrum = new ArrayBuffer(20);
  new Uint8Array(spectrum).set([0x41, 0x45, 0x54, 0x46], 0);

  socket.message(spectrum);

  const routed = workerMessages.find(
    entry => entry.message.type === "binary");
  assert.notEqual(routed, undefined);
  assert.equal(routed.message.data, spectrum);
  assert.deepEqual(routed.transfer, [spectrum]);
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
