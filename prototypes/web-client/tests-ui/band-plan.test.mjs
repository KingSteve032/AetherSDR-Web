import assert from "node:assert/strict";
import test from "node:test";

import {
  normalizeBandPlan,
  visibleBandSegments
} from "../wwwroot/band-plan.js";

test("band plan validation drops malformed boundary data", () => {
  const segments = normalizeBandPlan({
    segments: [
      {
        low: 14.0,
        high: 14.07,
        label: "CW",
        license: "E,G",
        color: "#3060ff"
      },
      {
        low: 14.2,
        high: 14.1,
        label: "broken",
        license: "",
        color: "red"
      }
    ]
  });

  assert.deepEqual(segments, [{
    lowHz: 14_000_000,
    highHz: 14_070_000,
    label: "CW",
    license: "E,G",
    color: "#3060ff"
  }]);
});

test("visible band plan segments clip and move with the pan view", () => {
  const segments = normalizeBandPlan({
    segments: [
      {
        low: 14.0,
        high: 14.07,
        label: "CW",
        license: "E,G",
        color: "#3060ff"
      },
      {
        low: 14.15,
        high: 14.35,
        label: "PHONE",
        license: "E,G",
        color: "#ff8000"
      }
    ]
  });

  const visible = visibleBandSegments(
    segments,
    14_175_000,
    200_000,
    1_000);

  assert.equal(visible.length, 1);
  assert.equal(visible[0].label, "PHONE");
  assert.equal(visible[0].left, .375);
  assert.equal(visible[0].width, .625);
  assert.equal(visible[0].showLabel, true);
  assert.equal(visible[0].showLicense, true);
});
