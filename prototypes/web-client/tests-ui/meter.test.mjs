import assert from "node:assert/strict";
import test from "node:test";

import { AetherSMeter } from "../wwwroot/meter.js";

test("S meter parks at minimum when no slice is selected", () => {
  let cancelledFrame = 0;
  let drawCount = 0;
  const attributes = new Map();
  globalThis.cancelAnimationFrame = frame => {
    cancelledFrame = frame;
  };
  const meter = Object.assign(
    Object.create(AetherSMeter.prototype),
    {
      geometry: { rx_scale: { minimum_dbm: -127 } },
      animationFrame: 42,
      dbm: -80,
      displayedFraction: .7,
      targetFraction: .7,
      peakDbm: -75,
      canvas: {
        setAttribute: (name, value) => attributes.set(name, value)
      },
      draw: () => {
        drawCount++;
      }
    });

  meter.setIdle();

  assert.equal(cancelledFrame, 42);
  assert.equal(meter.animationFrame, null);
  assert.equal(meter.dbm, -127);
  assert.equal(meter.displayedFraction, 0);
  assert.equal(meter.targetFraction, 0);
  assert.equal(meter.peakDbm, -127);
  assert.equal(attributes.get("aria-valuetext"), "Idle, no slice selected");
  assert.equal(drawCount, 1);
});

test("S meter needle advances even when animation frame id is zero", () => {
  let requestedFrames = 0;
  globalThis.requestAnimationFrame = () => {
    requestedFrames++;
    return 0;
  };
  const meter = Object.assign(
    Object.create(AetherSMeter.prototype),
    {
      geometry: {
        rx_scale: {
          minimum_dbm: -127,
          s9_dbm: -73,
          maximum_dbm: -13,
          db_per_s_unit: 6,
          s9_fraction: .6
        }
      },
      animationFrame: null,
      dbm: -127,
      displayedFraction: 0,
      targetFraction: 0,
      peakDbm: -127,
      lastFrameTime: performance.now(),
      lastInputTime: performance.now() - 100,
      canvas: { setAttribute: () => {} },
      draw: () => {}
    });

  meter.setDbm(-87);
  const firstFraction = meter.displayedFraction;
  meter.setDbm(-85);

  assert.ok(firstFraction > 0);
  assert.ok(meter.displayedFraction > firstFraction);
  assert.equal(requestedFrames, 1);
  assert.equal(meter.animationFrame, 0);
});

test("S meter skips drawing while its applet is collapsed", () => {
  let contextTouched = false;
  const meter = Object.assign(
    Object.create(AetherSMeter.prototype),
    {
      canvas: {
        getBoundingClientRect: () => ({
          width: 0,
          height: 140
        })
      },
      context: {
        setTransform: () => {
          contextTouched = true;
        }
      }
    });

  meter.draw();

  assert.equal(contextTouched, false);
});

test("S meter ignores non-positive arc radii", () => {
  let arcCalls = 0;
  const context = {
    beginPath: () => {},
    arc: () => {
      arcCalls++;
    },
    stroke: () => {}
  };
  const meter = Object.create(AetherSMeter.prototype);

  meter.strokeFractionArc(
    context,
    { centerX: 0, centerY: 0 },
    -5.15,
    0,
    1,
    "#fff");

  assert.equal(arcCalls, 0);
});
