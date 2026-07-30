import assert from "node:assert/strict";
import test from "node:test";

globalThis.WebSocket = {
  CONNECTING: 0,
  OPEN: 1,
  CLOSING: 2,
  CLOSED: 3
};

const { RadioTransportClient } =
  await import("../wwwroot/radio-transport.js");

class FakeWorker {
  constructor() {
    this.messages = [];
    this.listeners = new Map();
    this.terminated = false;
  }

  addEventListener(type, listener) {
    this.listeners.set(type, listener);
  }

  postMessage(message, transfer = []) {
    this.messages.push({ message, transfer });
  }

  emit(message) {
    this.listeners.get("message")?.({ data: message });
  }

  emitError() {
    this.listeners.get("error")?.({});
  }

  terminate() {
    this.terminated = true;
  }
}

class FakeSocket {
  constructor() {
    this.readyState = WebSocket.CONNECTING;
    this.sent = [];
  }

  open() {
    this.readyState = WebSocket.OPEN;
    this.onopen?.();
  }

  send(data) {
    this.sent.push(data);
  }

  close(code, reason) {
    this.readyState = WebSocket.CLOSED;
    this.onclose?.({ code, reason, wasClean: true });
  }
}

test("radio transport owns connection state and ignores stale worker events", () => {
  const worker = new FakeWorker();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker);
  let opens = 0;
  transport.onopen = () => {
    opens += 1;
  };

  transport.connect("wss://example.test/ws/radio", "aether.v0");
  assert.equal(transport.readyState, WebSocket.CONNECTING);
  assert.equal(worker.messages[0].message.type, "connect");

  worker.emit({ type: "open", connectionId: 99 });
  assert.equal(opens, 0);

  worker.emit({ type: "open", connectionId: 1 });
  assert.equal(opens, 1);
  assert.equal(transport.readyState, WebSocket.OPEN);

  assert.equal(transport.send("{\"cmd\":\"hello\"}"), true);
  assert.equal(worker.messages.at(-1).message.type, "send");
});

test("radio transport transfers the direct audio port to its worker", () => {
  const worker = new FakeWorker();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker);
  const port = {};

  transport.attachAudioPort(port);

  const sent = worker.messages.at(-1);
  assert.equal(sent.message.type, "audio.attach");
  assert.equal(sent.message.port, port);
  assert.deepEqual(sent.transfer, [port]);
});

test("radio transport requests and forwards network traffic diagnostics", () => {
  const worker = new FakeWorker();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker);
  let received = null;
  transport.onnetworkdiagnostics = diagnostics => {
    received = diagnostics;
  };

  transport.requestNetworkDiagnostics();
  assert.equal(
    worker.messages.at(-1).message.type,
    "network.diagnostics.request");

  transport.connect("wss://example.test/ws/radio", "aether.v0");
  worker.emit({
    type: "network.diagnostics",
    connectionId: 1,
    diagnostics: { bitsPerSecond: 128000 }
  });
  assert.deepEqual(received, { bitsPerSecond: 128000 });
});

test("radio transport forwards only the active worker connection", () => {
  const worker = new FakeWorker();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker);
  const binaries = [];
  transport.onbinary = buffer => binaries.push(buffer);

  transport.connect("wss://example.test/one", "aether.v0");
  transport.connect("wss://example.test/two", "aether.v0");
  worker.emit({
    type: "binary",
    connectionId: 1,
    data: new ArrayBuffer(1)
  });
  worker.emit({
    type: "binary",
    connectionId: 2,
    data: new ArrayBuffer(2)
  });

  assert.equal(binaries.length, 1);
  assert.equal(binaries[0].byteLength, 2);
});

test("radio transport falls back safely when its worker cannot start", () => {
  const socket = new FakeSocket();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => {
      throw new Error("Workers unavailable");
    },
    () => socket);
  let opened = false;
  transport.onopen = () => {
    opened = true;
  };

  transport.connect("wss://example.test/ws/radio", "aether.v0");
  socket.open();

  assert.equal(transport.mode, "main-thread-fallback");
  assert.equal(opened, true);
  assert.equal(transport.readyState, WebSocket.OPEN);
  assert.equal(transport.send("hello"), true);
  assert.deepEqual(socket.sent, ["hello"]);
});

test("radio transport falls back when a module worker fails to load", () => {
  const worker = new FakeWorker();
  const socket = new FakeSocket();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker,
    () => socket);

  transport.connect("wss://example.test/ws/radio", "aether.v0");
  worker.emitError();
  socket.open();

  assert.equal(worker.terminated, true);
  assert.equal(transport.mode, "main-thread-fallback");
  assert.equal(transport.readyState, WebSocket.OPEN);
});

test("radio transport reconnects through its fallback after a live worker fails", () => {
  const worker = new FakeWorker();
  const socket = new FakeSocket();
  const transport = new RadioTransportClient(
    "/radio-transport-worker.js",
    () => worker,
    () => socket);
  let opens = 0;
  transport.onopen = () => {
    opens += 1;
  };

  transport.connect("wss://example.test/ws/radio", "aether.v0");
  worker.emit({ type: "open", connectionId: 1 });
  assert.equal(opens, 1);

  worker.emitError();
  assert.equal(worker.terminated, true);
  assert.equal(transport.mode, "main-thread-fallback");
  assert.equal(transport.readyState, WebSocket.CONNECTING);

  socket.open();
  assert.equal(opens, 2);
  assert.equal(transport.readyState, WebSocket.OPEN);
});
