const fallbackGeometry = {
  format_version: 1,
  design_version: 5,
  sizing: {
    preferred: [280, 140],
    minimum_aspect_ratio: 1.75,
    maximum_aspect_ratio: 4
  },
  arc: {
    center_x_width_factor: .5,
    radius_width_factor: .85,
    center_y_height_factor: .35,
    start_degrees: 55,
    end_degrees: 125,
    inner_gap_pixels: 6,
    line_width_pixels: 3
  },
  tick_style: {
    start_offset_pixels: 2,
    end_offset_pixels: 14,
    label_offset_pixels: 26,
    line_width_pixels: 1.5,
    font_minimum_pixels: 10,
    font_height_factor: .1,
    bold: true
  },
  rx_scale: {
    minimum_dbm: -127,
    s9_dbm: -73,
    maximum_dbm: -13,
    db_per_s_unit: 6,
    s9_fraction: .6,
    ticks: [
      { value: -121, label: "1" },
      { value: -109, label: "3" },
      { value: -97, label: "5" },
      { value: -85, label: "7" },
      { value: -73, label: "9" },
      { value: -53, label: "+20" },
      { value: -33, label: "+40" }
    ]
  },
  needle: {
    pivot_y_below_widget_pixels: 6,
    tip_extension_pixels: 14,
    line_width_pixels: 2,
    shadow_width_pixels: 3,
    shadow_offset: [1, 1]
  },
  pivot: {
    minimum_radius_pixels: 13.5,
    radius_width_factor: .0975,
    glow_radius_factor: 3.4,
    glow_middle_factor: .45,
    glow_center_alpha: 80,
    glow_middle_alpha: 28,
    rim_width_pixels: 1
  },
  peak_marker: {
    radius_inset_pixels: 2,
    length_pixels: 6,
    half_width_pixels: 3,
    minimum_lead_db: 1
  },
  readout: {
    source_font_minimum_pixels: 9,
    source_font_height_divisor: 14,
    value_font_minimum_pixels: 13,
    value_font_height_divisor: 8,
    top_extra_pixels: 4,
    side_margin_pixels: 6
  }
};

const colors = {
  background: "#0f0f1a",
  rx: "#c8d8e8",
  tx: "#0080d0",
  warning: "#ff4444",
  accent: "#00b4d8",
  secondary: "#8090a0",
  pivot: "#050509",
  pivotRim: "#3a3e48",
  pivotGlow: [255, 176, 96]
};

export class AetherSMeter {
  constructor(canvas) {
    this.canvas = canvas;
    this.context = canvas.getContext("2d", { alpha: false });
    this.geometry = fallbackGeometry;
    this.dbm = -95;
    this.displayedFraction = this.dbmToFraction(this.dbm);
    this.targetFraction = this.displayedFraction;
    this.peakDbm = this.dbm;
    this.lastFrameTime = performance.now();
    this.lastInputTime = this.lastFrameTime;
    this.animationFrame = null;
    this.resizeObserver = new ResizeObserver(() => this.draw());
    this.resizeObserver.observe(canvas);
    this.draw();
  }

  async loadGeometry(url) {
    try {
      const response = await fetch(url, {
        credentials: "same-origin",
        headers: { Accept: "application/json" }
      });
      if (!response.ok) {
        return false;
      }
      const geometry = await response.json();
      if (!this.isValidGeometry(geometry)) {
        return false;
      }
      this.geometry = geometry;
      this.targetFraction = this.dbmToFraction(this.dbm);
      this.displayedFraction = this.targetFraction;
      this.draw();
      return true;
    } catch {
      return false;
    }
  }

  setDbm(dbm) {
    const scale = this.geometry.rx_scale;
    const now = performance.now();
    this.dbm = Math.max(scale.minimum_dbm, Math.min(scale.maximum_dbm, dbm));
    this.targetFraction = this.dbmToFraction(this.dbm);
    const elapsed = Math.min(
      250,
      Math.max(16, now - this.lastInputTime));
    this.lastInputTime = now;
    const inputAlpha = Math.max(.12, 1 - Math.exp(-elapsed / 120));
    this.displayedFraction +=
      (this.targetFraction - this.displayedFraction) * inputAlpha;
    this.peakDbm = Math.max(this.peakDbm, this.dbm);
    this.canvas.setAttribute(
      "aria-label",
      `S meter ${this.sUnitsText(this.dbm)}, ${Math.round(this.dbm)} dBm`);
    this.canvas.setAttribute("aria-valuemin", String(scale.minimum_dbm));
    this.canvas.setAttribute("aria-valuemax", String(scale.maximum_dbm));
    this.canvas.setAttribute("aria-valuenow", String(Math.round(this.dbm)));
    this.canvas.setAttribute(
      "aria-valuetext",
      `${this.sUnitsText(this.dbm)}, ${Math.round(this.dbm)} dBm`);
    this.draw();
    this.scheduleAnimation();
  }

  setIdle() {
    const minimumDbm = this.geometry.rx_scale.minimum_dbm;
    if (this.animationFrame !== null) {
      cancelAnimationFrame(this.animationFrame);
      this.animationFrame = null;
    }
    this.dbm = minimumDbm;
    this.displayedFraction = 0;
    this.targetFraction = 0;
    this.peakDbm = minimumDbm;
    this.lastInputTime = performance.now();
    this.canvas.setAttribute("aria-label", "S meter idle, no slice selected");
    this.canvas.setAttribute("aria-valuenow", String(minimumDbm));
    this.canvas.setAttribute("aria-valuetext", "Idle, no slice selected");
    this.draw();
  }

  animate(time) {
    const elapsed = Math.min(100, Math.max(1, time - this.lastFrameTime));
    this.lastFrameTime = time;
    const alpha = 1 - Math.exp(-elapsed / 85);
    this.displayedFraction +=
      (this.targetFraction - this.displayedFraction) * alpha;
    if (this.peakDbm > this.dbm) {
      this.peakDbm = Math.max(this.dbm, this.peakDbm - ((elapsed / 1000) * 8));
    }
    this.draw();

    if (Math.abs(this.displayedFraction - this.targetFraction) > .001 ||
        this.peakDbm > this.dbm + .1) {
      this.scheduleAnimation();
    } else {
      this.displayedFraction = this.targetFraction;
      this.draw();
    }
  }

  scheduleAnimation() {
    if (this.animationFrame !== null) {
      return;
    }
    this.lastFrameTime = performance.now();
    this.animationFrame = requestAnimationFrame(time => {
      this.animationFrame = null;
      this.animate(time);
    });
  }

  draw() {
    const rect = this.canvas.getBoundingClientRect();
    if (!Number.isFinite(rect.width) ||
        !Number.isFinite(rect.height) ||
        rect.width < 16 ||
        rect.height < 16) {
      return;
    }
    const width = rect.width;
    const height = rect.height;
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    const pixelWidth = Math.round(width * ratio);
    const pixelHeight = Math.round(height * ratio);
    if (this.canvas.width !== pixelWidth || this.canvas.height !== pixelHeight) {
      this.canvas.width = pixelWidth;
      this.canvas.height = pixelHeight;
    }

    const context = this.context;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.fillStyle = colors.background;
    context.fillRect(0, 0, width, height);

    const layout = this.layoutFor(width, height);
    context.save();
    context.beginPath();
    context.rect(
      layout.left,
      layout.top,
      layout.width,
      layout.height);
    context.clip();

    this.drawReadout(context, layout);
    this.drawArcs(context, layout);
    this.drawTicks(context, layout);
    this.drawPivotGlow(context, layout);
    this.drawNeedle(context, layout);
    this.drawPivotCover(context, layout);
    this.drawPeak(context, layout);
    context.restore();
  }

  drawReadout(context, layout) {
    const geometry = this.geometry.readout;
    const sourceSize = Math.max(
      geometry.source_font_minimum_pixels,
      Math.floor(layout.height / geometry.source_font_height_divisor));
    const valueSize = Math.max(
      geometry.value_font_minimum_pixels,
      Math.floor(layout.height / geometry.value_font_height_divisor));
    const baseline =
      layout.top + Math.max(sourceSize, valueSize) + geometry.top_extra_pixels;

    context.textBaseline = "alphabetic";
    context.font = `${valueSize}px Inter, "Segoe UI", sans-serif`;
    context.fillStyle = colors.accent;
    context.textAlign = "left";
    context.fillText(
      this.sUnitsText(this.dbm),
      layout.left + geometry.side_margin_pixels,
      baseline);
    context.fillStyle = colors.rx;
    context.textAlign = "right";
    context.fillText(
      `${Math.round(this.dbm)} dBm`,
      layout.right - geometry.side_margin_pixels,
      baseline);

    context.font = `${sourceSize}px Inter, "Segoe UI", sans-serif`;
    context.fillStyle = colors.secondary;
    context.textAlign = "center";
    context.fillText("S-Meter", layout.centerX, baseline - (valueSize - sourceSize));
  }

  drawArcs(context, layout) {
    const geometry = this.geometry;
    context.lineCap = "butt";
    context.lineWidth = geometry.arc.line_width_pixels;
    this.strokeFractionArc(
      context,
      layout,
      layout.radius,
      0,
      geometry.rx_scale.s9_fraction,
      colors.rx);
    this.strokeFractionArc(
      context,
      layout,
      layout.radius,
      geometry.rx_scale.s9_fraction,
      1,
      colors.warning);

    const redStart = 100 / 120;
    this.strokeFractionArc(
      context,
      layout,
      layout.innerRadius,
      0,
      redStart,
      colors.tx);
    this.strokeFractionArc(
      context,
      layout,
      layout.innerRadius,
      redStart,
      1,
      colors.warning);
  }

  drawTicks(context, layout) {
    const geometry = this.geometry;
    const tick = geometry.tick_style;
    const fontSize = Math.max(
      tick.font_minimum_pixels,
      Math.floor(layout.height * tick.font_height_factor));
    context.font =
      `${tick.bold ? "700 " : ""}${fontSize}px Inter, "Segoe UI", sans-serif`;
    context.textAlign = "center";
    context.textBaseline = "middle";

    for (const item of geometry.rx_scale.ticks) {
      const fraction = this.dbmToFraction(item.value);
      const ray = this.movementRay(layout, fraction);
      const warning = item.value > geometry.rx_scale.s9_dbm;
      this.drawRayTick(
        context,
        ray.scalePoint,
        ray.direction,
        tick.start_offset_pixels,
        tick.end_offset_pixels,
        tick.label_offset_pixels,
        item.label,
        warning ? colors.warning : colors.rx,
        1);
    }

    for (let watts = 0; watts <= 120; watts += 10) {
      const fraction = watts / 120;
      const ray = this.movementRay(layout, fraction);
      const point = this.rayCircleIntersection(
        layout,
        ray,
        layout.innerRadius);
      if (!point) {
        continue;
      }
      const showLabel =
        watts % 40 === 0 || watts === 100 || watts === 120;
      const warning = watts >= 100;
      this.drawRayTick(
        context,
        point,
        ray.direction,
        -tick.start_offset_pixels,
        -tick.end_offset_pixels,
        -tick.label_offset_pixels,
        showLabel ? String(watts) : "",
        warning ? colors.warning : colors.tx,
        1);
    }
  }

  drawRayTick(
    context,
    point,
    direction,
    startOffset,
    endOffset,
    labelOffset,
    label,
    color,
    opacity) {
    context.globalAlpha = opacity;
    context.strokeStyle = color;
    context.lineWidth = this.geometry.tick_style.line_width_pixels;
    context.beginPath();
    context.moveTo(
      point.x + (direction.x * startOffset),
      point.y + (direction.y * startOffset));
    context.lineTo(
      point.x + (direction.x * endOffset),
      point.y + (direction.y * endOffset));
    context.stroke();
    if (label) {
      context.fillStyle = color;
      context.fillText(
        label,
        point.x + (direction.x * labelOffset),
        point.y + (direction.y * labelOffset));
    }
    context.globalAlpha = 1;
  }

  drawPivotGlow(context, layout) {
    const pivot = this.geometry.pivot;
    const glowRadius = layout.pivotRadius * pivot.glow_radius_factor;
    const gradient = context.createRadialGradient(
      layout.centerX,
      layout.bottom,
      layout.pivotRadius,
      layout.centerX,
      layout.bottom,
      glowRadius);
    const [red, green, blue] = colors.pivotGlow;
    gradient.addColorStop(
      0,
      `rgba(${red},${green},${blue},${pivot.glow_center_alpha / 255})`);
    gradient.addColorStop(
      pivot.glow_middle_factor,
      `rgba(${red},${green},${blue},${pivot.glow_middle_alpha / 255})`);
    gradient.addColorStop(1, `rgba(${red},${green},${blue},0)`);
    context.fillStyle = gradient;
    context.beginPath();
    context.arc(
      layout.centerX,
      layout.bottom,
      glowRadius,
      Math.PI,
      Math.PI * 2);
    context.closePath();
    context.fill();
  }

  drawNeedle(context, layout) {
    const ray = this.movementRay(layout, this.displayedFraction);
    const needle = this.geometry.needle;
    const tip = {
      x: ray.scalePoint.x + (ray.direction.x * needle.tip_extension_pixels),
      y: ray.scalePoint.y + (ray.direction.y * needle.tip_extension_pixels)
    };
    const pivot = { x: layout.centerX, y: layout.needlePivotY };

    context.lineCap = "round";
    context.strokeStyle = "rgba(0,0,0,.32)";
    context.lineWidth = needle.shadow_width_pixels;
    context.beginPath();
    context.moveTo(
      pivot.x + needle.shadow_offset[0],
      pivot.y + needle.shadow_offset[1]);
    context.lineTo(
      tip.x + needle.shadow_offset[0],
      tip.y + needle.shadow_offset[1]);
    context.stroke();

    context.strokeStyle = "#fff";
    context.lineWidth = needle.line_width_pixels;
    context.beginPath();
    context.moveTo(pivot.x, pivot.y);
    context.lineTo(tip.x, tip.y);
    context.stroke();
  }

  drawPivotCover(context, layout) {
    context.fillStyle = colors.pivot;
    context.beginPath();
    context.arc(
      layout.centerX,
      layout.bottom,
      layout.pivotRadius,
      Math.PI,
      Math.PI * 2);
    context.closePath();
    context.fill();
    context.strokeStyle = colors.pivotRim;
    context.lineWidth = this.geometry.pivot.rim_width_pixels;
    context.beginPath();
    context.arc(
      layout.centerX,
      layout.bottom,
      layout.pivotRadius,
      Math.PI,
      Math.PI * 2);
    context.stroke();
  }

  drawPeak(context, layout) {
    if (this.peakDbm <= this.dbm + this.geometry.peak_marker.minimum_lead_db) {
      return;
    }
    const fraction = this.dbmToFraction(this.peakDbm);
    const ray = this.movementRay(layout, fraction);
    const point = this.rayCircleIntersection(
      layout,
      ray,
      layout.radius - this.geometry.peak_marker.radius_inset_pixels);
    if (!point) {
      return;
    }
    const marker = this.geometry.peak_marker;
    const perpendicular = { x: -ray.direction.y, y: ray.direction.x };
    context.fillStyle = "#ffaa00";
    context.beginPath();
    context.moveTo(point.x, point.y);
    context.lineTo(
      point.x - (marker.length_pixels * ray.direction.x) +
        (marker.half_width_pixels * perpendicular.x),
      point.y - (marker.length_pixels * ray.direction.y) +
        (marker.half_width_pixels * perpendicular.y));
    context.lineTo(
      point.x - (marker.length_pixels * ray.direction.x) -
        (marker.half_width_pixels * perpendicular.x),
      point.y - (marker.length_pixels * ray.direction.y) -
        (marker.half_width_pixels * perpendicular.y));
    context.closePath();
    context.fill();
  }

  strokeFractionArc(context, layout, radius, start, end, color) {
    if (!Number.isFinite(radius) || radius <= 0) {
      return;
    }
    context.strokeStyle = color;
    context.beginPath();
    context.arc(
      layout.centerX,
      layout.centerY,
      radius,
      -this.fractionToRadians(start),
      -this.fractionToRadians(end));
    context.stroke();
  }

  layoutFor(width, height) {
    const sizing = this.geometry.sizing;
    const aspect = width / height;
    let left = 0;
    let top = 0;
    let viewportWidth = width;
    let viewportHeight = height;
    if (aspect < sizing.minimum_aspect_ratio) {
      viewportHeight = width / sizing.minimum_aspect_ratio;
      top = (height - viewportHeight) / 2;
    } else if (aspect > sizing.maximum_aspect_ratio) {
      viewportWidth = height * sizing.maximum_aspect_ratio;
      left = (width - viewportWidth) / 2;
    }

    const arc = this.geometry.arc;
    const centerX = left + (viewportWidth * arc.center_x_width_factor);
    const radius = viewportWidth * arc.radius_width_factor;
    const centerY =
      top + radius + (viewportHeight * arc.center_y_height_factor);
    const preferredAspect = sizing.preferred[0] / sizing.preferred[1];
    const pivotScaleWidth =
      Math.min(viewportWidth, viewportHeight * preferredAspect);
    return {
      left,
      top,
      right: left + viewportWidth,
      bottom: top + viewportHeight,
      width: viewportWidth,
      height: viewportHeight,
      centerX,
      centerY,
      radius,
      innerRadius: radius - arc.inner_gap_pixels,
      needlePivotY:
        top + viewportHeight + this.geometry.needle.pivot_y_below_widget_pixels,
      pivotRadius: Math.max(
        this.geometry.pivot.minimum_radius_pixels,
        pivotScaleWidth * this.geometry.pivot.radius_width_factor)
    };
  }

  movementRay(layout, fraction) {
    const radians = this.fractionToRadians(fraction);
    const scalePoint = {
      x: layout.centerX + (layout.radius * Math.cos(radians)),
      y: layout.centerY - (layout.radius * Math.sin(radians))
    };
    const delta = {
      x: scalePoint.x - layout.centerX,
      y: scalePoint.y - layout.needlePivotY
    };
    const length = Math.hypot(delta.x, delta.y) || 1;
    return {
      pivot: { x: layout.centerX, y: layout.needlePivotY },
      scalePoint,
      direction: { x: delta.x / length, y: delta.y / length }
    };
  }

  rayCircleIntersection(layout, ray, radius) {
    const relativePivot = {
      x: ray.pivot.x - layout.centerX,
      y: ray.pivot.y - layout.centerY
    };
    const projection =
      (relativePivot.x * ray.direction.x) +
      (relativePivot.y * ray.direction.y);
    const constant =
      (relativePivot.x * relativePivot.x) +
      (relativePivot.y * relativePivot.y) -
      (radius * radius);
    const discriminant = (projection * projection) - constant;
    if (discriminant < 0) {
      return null;
    }
    const distance = -projection + Math.sqrt(discriminant);
    return {
      x: ray.pivot.x + (distance * ray.direction.x),
      y: ray.pivot.y + (distance * ray.direction.y)
    };
  }

  fractionToRadians(fraction) {
    const arc = this.geometry.arc;
    const clamped = Math.max(0, Math.min(1, fraction));
    const degrees =
      arc.end_degrees - (clamped * (arc.end_degrees - arc.start_degrees));
    return degrees * (Math.PI / 180);
  }

  dbmToFraction(dbm) {
    const scale = this.geometry.rx_scale;
    const clamped = Math.max(scale.minimum_dbm, Math.min(scale.maximum_dbm, dbm));
    if (clamped <= scale.s9_dbm) {
      return scale.s9_fraction *
        ((clamped - scale.minimum_dbm) /
          (scale.s9_dbm - scale.minimum_dbm));
    }
    return scale.s9_fraction +
      ((1 - scale.s9_fraction) *
        ((clamped - scale.s9_dbm) /
          (scale.maximum_dbm - scale.s9_dbm)));
  }

  sUnitsText(dbm) {
    const scale = this.geometry.rx_scale;
    if (dbm <= scale.minimum_dbm) {
      return "S0";
    }
    if (dbm <= scale.s9_dbm) {
      const unit = Math.round(
        (dbm - scale.minimum_dbm) / scale.db_per_s_unit);
      return `S${Math.max(0, Math.min(9, unit))}`;
    }
    return `S9+${Math.round(dbm - scale.s9_dbm)}`;
  }

  isValidGeometry(geometry) {
    const finite = value => Number.isFinite(Number(value));
    return geometry?.format_version === 1 &&
      geometry.design_version > 0 &&
      Array.isArray(geometry.sizing?.preferred) &&
      finite(geometry.arc?.radius_width_factor) &&
      finite(geometry.arc?.start_degrees) &&
      finite(geometry.arc?.end_degrees) &&
      geometry.arc.start_degrees < geometry.arc.end_degrees &&
      Array.isArray(geometry.rx_scale?.ticks) &&
      geometry.rx_scale.ticks.length === 7 &&
      finite(geometry.rx_scale.minimum_dbm) &&
      finite(geometry.rx_scale.s9_dbm) &&
      finite(geometry.rx_scale.maximum_dbm) &&
      finite(geometry.needle?.tip_extension_pixels) &&
      finite(geometry.pivot?.radius_width_factor);
  }
}
