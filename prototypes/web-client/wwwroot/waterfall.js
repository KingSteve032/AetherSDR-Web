import {
  clampFilterEdgesForMode
} from "./slice-controls.js?v=2d-only-1";

const V1_HEADER_SIZE = 20;
const V2_HEADER_SIZE = 24;
const V3_HEADER_SIZE = 28;
const PAN_DRAG_FRAME_MS = 33;
const SLICE_EDGE_ZONE_FRACTION = .1;
const SLICE_EDGE_MAX_BANDWIDTHS_PER_SECOND = 1.2;
const SLICE_EDGE_RAMP_MS = 600;

export class WaterfallRenderer {
  constructor(spectrumCanvas, waterfallCanvas) {
    this.spectrum = spectrumCanvas;
    this.waterfall = waterfallCanvas;
    this.spectrumContext = spectrumCanvas.getContext("2d", { alpha: false });
    this.waterfallContext = waterfallCanvas.getContext("2d", { alpha: false });
    this.bins = new Float32Array(1024).fill(-120);
    this.centerHz = 14_280_000;
    this.frameCenterHz = this.centerHz;
    this.frameBandwidthHz = 200_000;
    this.pendingCenterHz = null;
    this.pendingBandwidthHz = null;
    this.pendingSourceCenterHz = this.centerHz;
    this.pendingSourceBandwidthHz = this.frameBandwidthHz;
    this.acceptNextConfiguredCenter = false;
    this.bandwidthHz = 200_000;
    this.minDbm = -120;
    this.maxDbm = -40;
    this.sequence = 0;
    this.streamId = 0;
    this.fillEnabled = true;
    this.peakEnabled = false;
    this.waterfallEnabled = true;
    this.smoothedBins = new Float32Array(this.bins);
    this.peakBins = new Float32Array(this.bins);
    this.spectrumFrameIntervalMs = 1000 / 12;
    this.waterfallFrameIntervalMs = 1000 / 15;
    this.lastSpectrumDrawAt = 0;
    this.lastWaterfallDrawAt = 0;
    this.waterfallRow = null;
    this.colormapLut = this.createColormapLut();
    this.slices = [];
    this.activeSliceId = "A";
    this.onTune = null;
    this.onStep = null;
    this.onSliceActivate = null;
    this.onSliceTune = null;
    this.onSlicePreview = null;
    this.onFilterChange = null;
    this.onPanPreview = null;
    this.onPanCommit = null;
    this.onPanConfirmed = null;
    this.onZoom = null;
    this.onTuneBlocked = null;
    this.isSliceLocked = null;
    this.pointerState = null;
    this.dragPreview = null;
    this.lastDragEmitAt = 0;
    this.lastWheelAt = Number.NEGATIVE_INFINITY;
    this.sliceEdgePanTimer = 0;
    this.waterfallPanPreview = document.createElement("canvas");
    this.waterfallPanPreviewContext =
      this.waterfallPanPreview.getContext("2d", { alpha: false });
    this.waterfallPanPreviewActive = false;
    this.waterfallPanPreviewCenterHz = this.centerHz;
    this.waterfallPanPreviewBandwidthHz = this.bandwidthHz;

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(spectrumCanvas.parentElement);
    this.resizeObserver.observe(waterfallCanvas.parentElement);
    this.resize();

    spectrumCanvas.addEventListener(
      "pointerdown",
      event => this.handlePointerDown(event));
    spectrumCanvas.addEventListener(
      "pointermove",
      event => this.handlePointerMove(event));
    spectrumCanvas.addEventListener(
      "pointerup",
      event => this.handlePointerUp(event));
    spectrumCanvas.addEventListener(
      "pointercancel",
      event => this.handlePointerUp(event, true));
    waterfallCanvas.addEventListener(
      "pointerdown",
      event => this.handlePanPointerDown(event));
    waterfallCanvas.addEventListener(
      "pointermove",
      event => this.handlePanPointerMove(event));
    waterfallCanvas.addEventListener(
      "pointerup",
      event => this.handlePointerUp(event));
    waterfallCanvas.addEventListener(
      "pointercancel",
      event => this.handlePointerUp(event, true));
    const handleWheel = event => {
      event.preventDefault();
      const now = performance.now();
      if (now - this.lastWheelAt < 50) {
        return;
      }
      this.lastWheelAt = now;
      if (event.ctrlKey) {
        const point = this.canvasPoint(event, event.currentTarget);
        const anchorFraction = Math.max(
          0,
          Math.min(1, point.x / Math.max(1, this.spectrum.width)));
        this.onZoom?.(
          event.deltaY < 0 ? (1 / 1.5) : 1.5,
          anchorFraction);
      } else {
        this.onStep?.(event.deltaY < 0 ? 1 : -1);
      }
    };
    spectrumCanvas.addEventListener(
      "wheel",
      handleWheel,
      { passive: false });
    waterfallCanvas.addEventListener(
      "wheel",
      handleWheel,
      { passive: false });
    spectrumCanvas.addEventListener("keydown", event => {
      if (event.key === "ArrowUp" || event.key === "ArrowRight") {
        event.preventDefault();
        this.onStep?.(1);
      } else if (event.key === "ArrowDown" || event.key === "ArrowLeft") {
        event.preventDefault();
        this.onStep?.(-1);
      }
    });
  }

  configure(panadapter) {
    const configuredCenterHz = Number(panadapter.centerFrequencyHz);
    const configuredBandwidthHz = Number(panadapter.bandwidthHz);
    this.minDbm = panadapter.minDbm;
    this.maxDbm = panadapter.maxDbm;
    const frequencyGestureActive =
      (this.pointerState?.action === "background" &&
       this.pointerState.moved) ||
      Boolean(this.pointerState?.edgePanMoved);
    if (!frequencyGestureActive && this.pendingCenterHz === null) {
      this.centerHz = configuredCenterHz;
      this.bandwidthHz = configuredBandwidthHz;
    } else if (!frequencyGestureActive &&
               this.acceptNextConfiguredCenter &&
               !this.frequencyFramesMatch(
                 configuredCenterHz,
                 configuredBandwidthHz,
                 this.pendingSourceCenterHz,
                 this.pendingSourceBandwidthHz)) {
      this.centerHz = configuredCenterHz;
      this.bandwidthHz = configuredBandwidthHz;
      this.pendingCenterHz = configuredCenterHz;
      this.pendingBandwidthHz = configuredBandwidthHz;
      this.applyWaterfallFrequencyPreview(
        configuredCenterHz,
        configuredBandwidthHz);
    }
    const framesPerSecond = Math.max(
      1,
      Math.min(30, Number(panadapter.framesPerSecond) || 15));
    this.spectrumFrameIntervalMs = 1000 / framesPerSecond;
    this.waterfallFrameIntervalMs = 1000 / framesPerSecond;
  }

  setStreamId(streamId) {
    const nextStreamId = Number(streamId) >>> 0;
    if (this.streamId === nextStreamId) {
      return;
    }
    this.streamId = nextStreamId;
    this.sequence = 0;
    this.frameCenterHz = this.centerHz;
    this.frameBandwidthHz = this.bandwidthHz;
    this.pendingCenterHz = null;
    this.pendingBandwidthHz = null;
    this.pendingSourceCenterHz = this.centerHz;
    this.pendingSourceBandwidthHz = this.bandwidthHz;
    this.acceptNextConfiguredCenter = false;
    this.waterfallPanPreviewActive = false;
    this.smoothedBins.fill(this.minDbm);
    this.peakBins.fill(this.minDbm);
    this.waterfallContext.fillStyle = "#000010";
    this.waterfallContext.fillRect(
      0,
      0,
      this.waterfall.width,
      this.waterfall.height);
  }

  setFillEnabled(enabled) {
    this.fillEnabled = Boolean(enabled);
    this.drawSpectrum();
  }

  setPeakEnabled(enabled) {
    this.peakEnabled = Boolean(enabled);
    if (this.peakEnabled) {
      this.peakBins = Float32Array.from(this.smoothedBins);
    }
    this.drawSpectrum();
  }

  setWaterfallEnabled(enabled) {
    this.waterfallEnabled = Boolean(enabled);
  }

  setSlices(slices, activeSliceId) {
    this.slices = Array.isArray(slices) ? slices : [];
    this.activeSliceId = activeSliceId;
    this.drawSpectrum();
  }

  acceptFrame(arrayBuffer) {
    const view = new DataView(arrayBuffer);
    if (view.byteLength < V1_HEADER_SIZE ||
        view.getUint8(0) !== 65 ||
        view.getUint8(1) !== 69 ||
        view.getUint8(2) !== 84 ||
        view.getUint8(3) !== 70 ||
        view.getUint8(4) !== 0) {
      return false;
    }

    const version = view.getUint8(5);
    const headerSize =
      version === 1 ? V1_HEADER_SIZE :
      version === 2 ? V2_HEADER_SIZE :
      version === 3 ? V3_HEADER_SIZE :
      0;
    if (!headerSize || view.byteLength < headerSize) {
      return false;
    }
    if (version >= 2) {
      const frameStreamId = view.getUint32(20, true);
      if (this.streamId !== 0 && frameStreamId !== this.streamId) {
        return false;
      }
    }

    const binCount = view.getUint16(6, true);
    if (binCount < 64 ||
        binCount > 8192 ||
        view.byteLength !== headerSize + (binCount * 2)) {
      return false;
    }

    const previousSequence = this.sequence;
    this.sequence = view.getUint32(8, true);
    this.frameCenterHz = Number(view.getBigInt64(12, true));
    this.frameBandwidthHz =
      version >= 3
        ? view.getInt32(24, true)
        : this.bandwidthHz;
    let confirmedPendingPan = false;
    let authoritativeFrameChanged = false;
    const frequencyGestureActive =
      (this.pointerState?.action === "background" &&
       this.pointerState.moved) ||
      Boolean(this.pointerState?.edgePanMoved);
    if (!frequencyGestureActive) {
      if (this.pendingCenterHz !== null &&
          this.acceptNextConfiguredCenter &&
          !this.frequencyFramesMatch(
            this.frameCenterHz,
            this.frameBandwidthHz,
            this.pendingSourceCenterHz,
            this.pendingSourceBandwidthHz)) {
        this.centerHz = this.frameCenterHz;
        this.bandwidthHz = this.frameBandwidthHz;
        this.pendingCenterHz = null;
        this.pendingBandwidthHz = null;
        this.acceptNextConfiguredCenter = false;
        this.waterfallPanPreviewActive = false;
        confirmedPendingPan = true;
      } else if (this.pendingCenterHz !== null &&
          this.frequencyFramesMatch(
            this.frameCenterHz,
            this.frameBandwidthHz,
            this.pendingCenterHz,
            this.pendingBandwidthHz)) {
        this.centerHz = this.frameCenterHz;
        this.bandwidthHz = this.frameBandwidthHz;
        this.pendingCenterHz = null;
        this.pendingBandwidthHz = null;
        this.acceptNextConfiguredCenter = false;
        this.waterfallPanPreviewActive = false;
        confirmedPendingPan = true;
      } else if (this.pendingCenterHz === null) {
        authoritativeFrameChanged =
          !this.frequencyFramesMatch(
            this.centerHz,
            this.bandwidthHz,
            this.frameCenterHz,
            this.frameBandwidthHz);
        this.centerHz = this.frameCenterHz;
        this.bandwidthHz = this.frameBandwidthHz;
      }
    }
    const resetSmoothing =
      this.bins.length !== binCount ||
      previousSequence === 0 ||
      this.sequence <= previousSequence;
    if (this.bins.length !== binCount) {
      this.bins = new Float32Array(binCount);
      this.smoothedBins = new Float32Array(binCount);
      this.peakBins = new Float32Array(binCount);
    }

    for (let index = 0; index < binCount; index += 1) {
      const value =
        view.getInt16(headerSize + (index * 2), true) / 10;
      this.bins[index] = value;
      this.smoothedBins[index] =
        resetSmoothing
          ? value
          : (.35 * value) + (.65 * this.smoothedBins[index]);
      if (this.peakEnabled) {
        const previousPeak = this.peakBins[index];
        this.peakBins[index] =
          resetSmoothing || value >= previousPeak
            ? value
            : Math.max(value, previousPeak - .22);
      }
    }

    if (confirmedPendingPan && this.waterfallEnabled) {
      this.primeWaterfallFromCurrentFrame();
    }
    if (confirmedPendingPan || authoritativeFrameChanged) {
      this.onPanConfirmed?.(
        this.frameCenterHz,
        this.frameBandwidthHz);
    }

    if (document.hidden) {
      return false;
    }

    const now = performance.now();
    if (now - this.lastSpectrumDrawAt >= this.spectrumFrameIntervalMs) {
      this.lastSpectrumDrawAt = now;
      this.drawSpectrum();
    }
    if (this.waterfallEnabled &&
        now - this.lastWaterfallDrawAt >= this.waterfallFrameIntervalMs) {
      this.lastWaterfallDrawAt = now;
      this.pushWaterfallRow();
    }
    return true;
  }

  resize() {
    this.resizeCanvas(this.spectrum);
    this.resizeCanvas(this.waterfall);
    this.drawSpectrum();
    this.waterfallContext.fillStyle = "#000010";
    this.waterfallContext.fillRect(0, 0, this.waterfall.width, this.waterfall.height);
    if (this.waterfallPanPreviewActive) {
      this.beginWaterfallPanPreview();
    }
  }

  resizeCanvas(canvas) {
    const maximumRatio = canvas === this.waterfall ? 1 : 1.5;
    const ratio = Math.min(window.devicePixelRatio || 1, maximumRatio);
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(1, Math.floor(rect.width * ratio));
    const height = Math.max(1, Math.floor(rect.height * ratio));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
  }

  drawSpectrum() {
    const context = this.spectrumContext;
    const width = this.spectrum.width;
    const height = this.spectrum.height;
    if (!width || !height) {
      return;
    }

    context.fillStyle = "#000";
    context.fillRect(0, 0, width, height);

    context.lineWidth = 1;
    context.strokeStyle = "#152130";
    context.fillStyle = "#62788d";
    context.font = `${Math.max(9, Math.round(width / 170))}px Consolas, monospace`;

    for (let line = 0; line <= 8; line += 1) {
      const x = (line / 8) * width;
      context.beginPath();
      context.moveTo(x, 0);
      context.lineTo(x, height);
      context.stroke();

      const frequency =
        this.effectiveCenterHz() - (this.bandwidthHz / 2) +
        ((line / 8) * this.bandwidthHz);
      context.fillText((frequency / 1e6).toFixed(3), x + 4, height - 6);
    }

    for (let line = 0; line <= 4; line += 1) {
      const y = (line / 4) * height;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(width, y);
      context.stroke();
      const dbm = this.maxDbm - ((line / 4) * (this.maxDbm - this.minDbm));
      context.fillText(`${Math.round(dbm)}`, 4, y + 12);
    }

    this.draw2DSpectrum(context, width, height);
    if (this.peakEnabled) {
      this.drawPeakSpectrum(context, width, height);
    }
    this.drawSliceOverlays(context, width, height);
  }

  draw2DSpectrum(context, width, height) {
    const bins = this.smoothedBins.length > 0
      ? this.smoothedBins
      : this.bins;
    const points = this.frequencyProjectedPoints(bins, width);
    if (points.length === 0) {
      return;
    }

    const plotBottom = height * .9;
    const plotTop = height * .05;
    context.beginPath();
    context.moveTo(0, plotBottom);
    for (let index = 0; index < points.length; index += 1) {
      const normalized = Math.max(
        0,
        Math.min(1, (points[index] - this.minDbm) /
          (this.maxDbm - this.minDbm)));
      const x =
        points.length > 1 ? (index / (points.length - 1)) * width : 0;
      const y = plotBottom - (normalized * (plotBottom - plotTop));
      context.lineTo(x, y);
    }
    context.lineTo(width, plotBottom);
    context.closePath();

    if (this.fillEnabled) {
      const fill = context.createLinearGradient(0, plotTop, 0, plotBottom);
      fill.addColorStop(0, "#00dfff99");
      fill.addColorStop(.42, "#007da866");
      fill.addColorStop(1, "#06263a22");
      context.fillStyle = fill;
      context.fill();
    }

    context.beginPath();
    for (let index = 0; index < points.length; index += 1) {
      const normalized = Math.max(
        0,
        Math.min(1, (points[index] - this.minDbm) /
          (this.maxDbm - this.minDbm)));
      const x =
        points.length > 1 ? (index / (points.length - 1)) * width : 0;
      const y = plotBottom - (normalized * (plotBottom - plotTop));
      if (index === 0) {
        context.moveTo(x, y);
      } else {
        context.lineTo(x, y);
      }
    }
    context.strokeStyle = "#00dfff";
    context.lineWidth = Math.max(
      1,
      Math.min(window.devicePixelRatio || 1, 1.5));
    context.stroke();
  }

  drawPeakSpectrum(context, width, height) {
    const points = this.frequencyProjectedPoints(this.peakBins, width);
    if (points.length === 0) {
      return;
    }

    const plotBottom = height * .9;
    const plotTop = height * .05;
    context.beginPath();
    for (let index = 0; index < points.length; index += 1) {
      const normalized = Math.max(
        0,
        Math.min(1, (points[index] - this.minDbm) /
          (this.maxDbm - this.minDbm)));
      const x =
        points.length > 1 ? (index / (points.length - 1)) * width : 0;
      const y = plotBottom - (normalized * (plotBottom - plotTop));
      if (index === 0) {
        context.moveTo(x, y);
      } else {
        context.lineTo(x, y);
      }
    }
    context.strokeStyle = "#ffe45c";
    context.lineWidth = Math.max(
      1,
      Math.min(window.devicePixelRatio || 1, 1.5));
    context.stroke();
  }

  peakPreservingPoints(bins, targetCount) {
    if (!bins?.length || targetCount <= 0) {
      return new Float32Array();
    }
    const count = Math.max(2, Math.min(bins.length, Math.floor(targetCount)));
    if (count === bins.length) {
      return bins;
    }
    const points = new Float32Array(count);
    const step = bins.length / count;
    for (let point = 0; point < count; point += 1) {
      const first = Math.min(
        bins.length - 1,
        Math.floor(point * step));
      const afterLast = Math.min(
        bins.length,
        Math.max(first + 1, Math.ceil((point + 1) * step)));
      let peak = Number.NEGATIVE_INFINITY;
      for (let index = first; index < afterLast; index += 1) {
        peak = Math.max(peak, bins[index]);
      }
      points[point] = Number.isFinite(peak) ? peak : this.minDbm;
    }
    return points;
  }

  frequencyProjectedPoints(bins, targetCount) {
    if (!bins?.length || targetCount <= 0) {
      return new Float32Array();
    }

    const count = Math.max(2, Math.floor(targetCount));
    const viewCenterHz = this.effectiveCenterHz();
    const viewBandwidthHz = Number(this.bandwidthHz);
    const sourceCenterHz = Number(this.frameCenterHz);
    const sourceBandwidthHz = Number(this.frameBandwidthHz);
    if (!Number.isFinite(viewCenterHz) ||
        !Number.isFinite(viewBandwidthHz) ||
        !Number.isFinite(sourceCenterHz) ||
        !Number.isFinite(sourceBandwidthHz) ||
        viewBandwidthHz <= 0 ||
        sourceBandwidthHz <= 0) {
      return this.peakPreservingPoints(bins, count);
    }

    const points = new Float32Array(count).fill(this.minDbm);
    const viewStartHz = viewCenterHz - (viewBandwidthHz / 2);
    const sourceStartHz = sourceCenterHz - (sourceBandwidthHz / 2);
    const sourceLength = bins.length;

    for (let point = 0; point < count; point += 1) {
      const destinationLowHz =
        viewStartHz + ((point / count) * viewBandwidthHz);
      const destinationHighHz =
        viewStartHz + (((point + 1) / count) * viewBandwidthHz);
      const sourceLow =
        ((destinationLowHz - sourceStartHz) / sourceBandwidthHz) *
        sourceLength;
      const sourceHigh =
        ((destinationHighHz - sourceStartHz) / sourceBandwidthHz) *
        sourceLength;
      const clippedLow = Math.max(0, sourceLow);
      const clippedHigh = Math.min(sourceLength, sourceHigh);
      if (clippedHigh <= clippedLow) {
        continue;
      }

      if (clippedHigh - clippedLow < 1) {
        const centerIndex = Math.max(
          0,
          Math.min(
            sourceLength - 1,
            (((clippedLow + clippedHigh) / 2) /
              Math.max(1, sourceLength)) *
              Math.max(0, sourceLength - 1)));
        const lowerIndex = Math.floor(centerIndex);
        const upperIndex = Math.min(sourceLength - 1, lowerIndex + 1);
        const fraction = centerIndex - lowerIndex;
        const interpolated =
          (bins[lowerIndex] * (1 - fraction)) +
          (bins[upperIndex] * fraction);
        points[point] = Math.max(
          interpolated,
          bins[lowerIndex],
          bins[upperIndex]);
        continue;
      }

      const first = Math.max(
        0,
        Math.min(sourceLength - 1, Math.floor(clippedLow)));
      const afterLast = Math.max(
        first + 1,
        Math.min(sourceLength, Math.ceil(clippedHigh)));
      let peak = Number.NEGATIVE_INFINITY;
      for (let index = first; index < afterLast; index += 1) {
        peak = Math.max(peak, bins[index]);
      }
      points[point] = Number.isFinite(peak) ? peak : this.minDbm;
    }
    return points;
  }

  drawSliceOverlays(context, width, height) {
    const startHz = this.effectiveCenterHz() - (this.bandwidthHz / 2);
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    for (const slice of this.slices) {
      const preview =
        this.dragPreview?.sliceId === slice.id
          ? this.dragPreview
          : slice;
      const frequencyHz =
        preview.frequencyHz ?? slice.frequencyHz;
      const filterLowHz =
        preview.filterLowHz ?? slice.filterLowHz;
      const filterHighHz =
        preview.filterHighHz ?? slice.filterHighHz;
      const normalized = (frequencyHz - startHz) / this.bandwidthHz;
      if (normalized < 0 || normalized > 1) {
        continue;
      }

      const color = slice.id === "B" ? "#ff40ff" : "#00d4ff";
      const lowX =
        ((frequencyHz + filterLowHz - startHz) / this.bandwidthHz) *
        width;
      const highX =
        ((frequencyHz + filterHighHz - startHz) / this.bandwidthHz) *
        width;
      const x = normalized * width;
      const active = slice.id === this.activeSliceId;

      context.fillStyle = `${color}${active ? "22" : "10"}`;
      context.fillRect(lowX, 0, Math.max(1, highX - lowX), height);

      context.strokeStyle = `${color}${active ? "e6" : "7a"}`;
      context.lineWidth = active ? Math.max(1.5, ratio) : 1;
      context.beginPath();
      context.moveTo(x, 0);
      context.lineTo(x, height);
      context.stroke();

      context.fillStyle = color;
      context.beginPath();
      context.moveTo(x, 2);
      context.lineTo(x - (5 * ratio), 10 * ratio);
      context.lineTo(x + (5 * ratio), 10 * ratio);
      context.closePath();
      context.fill();

      context.fillStyle = "#061018de";
      context.fillRect(x + (5 * ratio), 3 * ratio, 15 * ratio, 13 * ratio);
      context.fillStyle = color;
      context.font = `bold ${Math.max(9, 9 * ratio)}px Inter, sans-serif`;
      context.fillText(slice.id, x + (8 * ratio), 13 * ratio);
    }
  }

  pushWaterfallRow() {
    const context = this.waterfallContext;
    const width = this.waterfall.width;
    const height = this.waterfall.height;
    if (!width || !height) {
      return;
    }

    context.drawImage(this.waterfall, 0, 0, width, height - 1, 0, 1, width, height - 1);
    if (!this.waterfallRow || this.waterfallRow.width !== width) {
      this.waterfallRow = context.createImageData(width, 1);
    }
    const image = this.waterfallRow;
    const lut = this.colormapLut;
    const viewCenterHz = this.effectiveCenterHz();
    const viewStartHz = viewCenterHz - (this.bandwidthHz / 2);
    const frameStartHz =
      this.frameCenterHz - (this.frameBandwidthHz / 2);

    for (let x = 0; x < width; x += 1) {
      const frequencyHz =
        viewStartHz + ((x / Math.max(1, width - 1)) * this.bandwidthHz);
      const sourceFraction =
        (frequencyHz - frameStartHz) /
        Math.max(1, this.frameBandwidthHz);
      const pixel = x * 4;
      if (sourceFraction < 0 || sourceFraction > 1) {
        image.data[pixel] = 0;
        image.data[pixel + 1] = 0;
        image.data[pixel + 2] = 16;
        image.data[pixel + 3] = 255;
        continue;
      }
      const binIndex = Math.min(
        this.bins.length - 1,
        Math.floor(sourceFraction * this.bins.length));
      const normalized = Math.max(
        0,
        Math.min(1, (this.bins[binIndex] - this.minDbm) /
          (this.maxDbm - this.minDbm)));
      const color = Math.round(normalized * 255) * 3;
      image.data[pixel] = lut[color];
      image.data[pixel + 1] = lut[color + 1];
      image.data[pixel + 2] = lut[color + 2];
      image.data[pixel + 3] = 255;
    }

    context.putImageData(image, 0, 0);
  }

  primeWaterfallFromCurrentFrame() {
    const width = this.waterfall.width;
    const height = this.waterfall.height;
    if (!width ||
        !height ||
        !this.waterfallPanPreviewContext) {
      return;
    }

    this.pushWaterfallRow();
    this.waterfallPanPreview.width = width;
    this.waterfallPanPreview.height = 1;
    this.waterfallPanPreviewContext.drawImage(
      this.waterfall,
      0,
      0,
      width,
      1,
      0,
      0,
      width,
      1);
    this.waterfallContext.imageSmoothingEnabled = false;
    this.waterfallContext.drawImage(
      this.waterfallPanPreview,
      0,
      0,
      width,
      1,
      0,
      0,
      width,
      height);
  }

  handlePointerDown(event) {
    if (event.button !== 0) {
      return;
    }
    const point = this.canvasPoint(event);
    const hit = this.hitTestSlice(point.x);
    if (hit) {
      if (hit.slice.id !== this.activeSliceId) {
        this.onSliceActivate?.(hit.slice.id);
      }
      const locked = Boolean(this.isSliceLocked?.(hit.slice.id));
      if (hit.action === "slice" && locked) {
        this.onTuneBlocked?.(hit.slice.id);
        event.preventDefault();
        return;
      }
      this.pointerState = {
        action: hit.action,
        surface: this.spectrum,
        pointerId: event.pointerId,
        sliceId: hit.slice.id,
        startX: point.x,
        lastX: point.x,
        moved: false,
        frequencyHz: hit.slice.frequencyHz,
        mode: hit.slice.mode,
        filterLowHz: hit.slice.filterLowHz,
        filterHighHz: hit.slice.filterHighHz,
        frequencyOffsetHz:
          this.xToFrequency(point.x) - hit.slice.frequencyHz
      };
    } else {
      this.beginWaterfallPanPreview();
      this.pointerState = {
        action: "background",
        surface: this.spectrum,
        clickTunes: true,
        pointerId: event.pointerId,
        startX: point.x,
        lastX: point.x,
        moved: false,
        centerHz: this.effectiveCenterHz(),
        previewCenterHz: this.effectiveCenterHz()
      };
    }
    this.spectrum.setPointerCapture(event.pointerId);
    this.updatePointerCursor(this.pointerState.action);
    event.preventDefault();
  }

  handlePanPointerDown(event) {
    if (event.button !== 0) {
      return;
    }
    const point = this.canvasPoint(event, this.waterfall);
    this.pointerState = {
      action: "background",
      surface: this.waterfall,
      clickTunes: false,
      pointerId: event.pointerId,
      startX: point.x,
      lastX: point.x,
      moved: false,
      centerHz: this.effectiveCenterHz(),
      previewCenterHz: this.effectiveCenterHz()
    };
    this.beginWaterfallPanPreview();
    this.waterfall.setPointerCapture(event.pointerId);
    this.waterfall.style.cursor = "grabbing";
    event.preventDefault();
  }

  handlePanPointerMove(event) {
    const drag = this.pointerState;
    if (!drag ||
        drag.surface !== this.waterfall ||
        event.pointerId !== drag.pointerId) {
      return;
    }

    const point = this.canvasPoint(event, this.waterfall);
    drag.lastX = point.x;
    drag.moved ||= Math.abs(point.x - drag.startX) >= 3;
    const now = performance.now();
    if (now - this.lastDragEmitAt < PAN_DRAG_FRAME_MS) {
      return;
    }
    this.lastDragEmitAt = now;
    this.emitPointerChange(drag, point.x, false);
    event.preventDefault();
  }

  handlePointerMove(event) {
    const point = this.canvasPoint(event);
    if (!this.pointerState) {
      const hit = this.hitTestSlice(point.x);
      this.updatePointerCursor(hit?.action);
      return;
    }
    if (event.pointerId !== this.pointerState.pointerId) {
      return;
    }

    const drag = this.pointerState;
    drag.lastX = point.x;
    drag.moved ||= Math.abs(point.x - drag.startX) >= 3;
    if (drag.action === "slice") {
      if (this.updateSliceEdgePan(drag, point.x)) {
        event.preventDefault();
        return;
      }
      this.stopSliceEdgePan();
    }
    const now = performance.now();
    if (now - this.lastDragEmitAt < PAN_DRAG_FRAME_MS) {
      return;
    }
    this.lastDragEmitAt = now;
    this.emitPointerChange(drag, point.x, false);
    event.preventDefault();
  }

  handlePointerUp(event, cancelled = false) {
    const drag = this.pointerState;
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }
    const surface = drag.surface ?? this.spectrum;
    const point = this.canvasPoint(event, surface);
    this.stopSliceEdgePan();
    if (!cancelled) {
      if (drag.action === "background" &&
          !drag.moved &&
          drag.clickTunes !== false) {
        this.onTune?.(Math.round(this.xToFrequency(point.x)));
      } else if (drag.moved || drag.action !== "background") {
        if (drag.edgePanMoved) {
          this.pendingCenterHz = this.centerHz;
          this.pendingBandwidthHz = this.bandwidthHz;
          this.pendingSourceCenterHz = this.frameCenterHz;
          this.pendingSourceBandwidthHz = this.frameBandwidthHz;
          this.acceptNextConfiguredCenter = true;
          this.applyWaterfallFrequencyPreview(
            this.centerHz,
            this.bandwidthHz);
          this.onPanCommit?.(this.centerHz);
        }
        this.emitPointerChange(drag, point.x, true);
      }
    } else if (drag.action === "background" || drag.edgePanMoved) {
      this.restoreWaterfallPanPreview();
    }
    if (surface.hasPointerCapture(event.pointerId)) {
      surface.releasePointerCapture(event.pointerId);
    }
    this.dragPreview = null;
    this.pointerState = null;
    this.updatePointerCursor();
    this.waterfall.style.cursor = "grab";
    if (drag.action === "background" && !drag.moved) {
      this.waterfallPanPreviewActive = false;
    }
    this.drawSpectrum();
    event.preventDefault();
  }

  emitPointerChange(drag, x, final) {
    const hzPerPixel = this.bandwidthHz / Math.max(1, this.spectrum.width);
    if (drag.action === "background") {
      const halfBandwidth = this.bandwidthHz / 2;
      const centerHz = Math.round(Math.max(
        Math.max(100_000, halfBandwidth),
        Math.min(
          60_000_000 - halfBandwidth,
          drag.centerHz - ((x - drag.startX) * hzPerPixel))));
      drag.previewCenterHz = centerHz;
      if (final) {
        this.centerHz = centerHz;
        this.pendingCenterHz = centerHz;
        this.pendingBandwidthHz = this.bandwidthHz;
        this.pendingSourceCenterHz = this.frameCenterHz;
        this.pendingSourceBandwidthHz = this.frameBandwidthHz;
        this.acceptNextConfiguredCenter = true;
        this.applyWaterfallFrequencyPreview(
          centerHz,
          this.bandwidthHz);
        this.onPanCommit?.(centerHz);
      } else {
        this.applyWaterfallFrequencyPreview(
          centerHz,
          this.bandwidthHz);
        this.onPanPreview?.(centerHz);
        this.drawSpectrum();
      }
      return;
    }

    if (drag.action === "slice") {
      const frequencyHz =
        final &&
        drag.edgePanMoved &&
        Number.isFinite(drag.edgeSliceFrequencyHz)
          ? Math.round(drag.edgeSliceFrequencyHz)
          : Math.round(
            this.xToFrequency(x) - drag.frequencyOffsetHz);
      if (final) {
        this.onSliceTune?.(drag.sliceId, frequencyHz, true);
      } else {
        this.dragPreview = {
          sliceId: drag.sliceId,
          frequencyHz
        };
        this.onSlicePreview?.(drag.sliceId, frequencyHz);
        this.onSliceTune?.(drag.sliceId, frequencyHz, false);
        this.drawSpectrum();
      }
      return;
    }
    const deltaHz = Math.round((x - drag.startX) * hzPerPixel);
    if (drag.action === "filter-low") {
      const edges = clampFilterEdgesForMode(
        drag.mode,
        {
          filterLowHz: drag.filterLowHz + deltaHz,
          filterHighHz: drag.filterHighHz
        },
        "low");
      if (final) {
        this.onFilterChange?.(
          drag.sliceId,
          edges.filterLowHz,
          edges.filterHighHz);
      } else {
        this.dragPreview = {
          sliceId: drag.sliceId,
          ...edges
        };
        this.drawSpectrum();
      }
    } else if (drag.action === "filter-high") {
      const edges = clampFilterEdgesForMode(
        drag.mode,
        {
          filterLowHz: drag.filterLowHz,
          filterHighHz: drag.filterHighHz + deltaHz
        },
        "high");
      if (final) {
        this.onFilterChange?.(
          drag.sliceId,
          edges.filterLowHz,
          edges.filterHighHz);
      } else {
        this.dragPreview = {
          sliceId: drag.sliceId,
          ...edges
        };
        this.drawSpectrum();
      }
    }
  }

  updateSliceEdgePan(drag, x) {
    const width = Math.max(1, this.spectrum.width);
    const zonePixels = Math.max(
      1,
      width * SLICE_EDGE_ZONE_FRACTION);
    const inEdgeZone =
      x <= zonePixels ||
      x >= width - zonePixels;
    if (!inEdgeZone) {
      drag.edgePanStartedAt = 0;
      return false;
    }

    drag.lastX = x;
    if (!this.sliceEdgePanTimer) {
      drag.edgePanStartedAt = performance.now();
      this.beginWaterfallPanPreview();
      this.sliceEdgePanTimer = window.setInterval(
        () => this.stepSliceEdgePan(drag),
        PAN_DRAG_FRAME_MS);
      this.stepSliceEdgePan(drag);
    }
    return true;
  }

  stopSliceEdgePan() {
    if (this.sliceEdgePanTimer) {
      window.clearInterval(this.sliceEdgePanTimer);
      this.sliceEdgePanTimer = 0;
    }
  }

  stepSliceEdgePan(drag) {
    if (this.pointerState !== drag ||
        drag.action !== "slice") {
      this.stopSliceEdgePan();
      return;
    }

    const width = Math.max(1, this.spectrum.width);
    const zonePixels = Math.max(
      1,
      width * SLICE_EDGE_ZONE_FRACTION);
    let direction = 0;
    let borderDistance = 0;
    if (drag.lastX <= zonePixels) {
      direction = -1;
      borderDistance = drag.lastX;
    } else if (drag.lastX >= width - zonePixels) {
      direction = 1;
      borderDistance = width - drag.lastX;
    } else {
      this.stopSliceEdgePan();
      return;
    }

    const depth = Math.max(
      0,
      Math.min(1, (zonePixels - borderDistance) / zonePixels));
    const heldMilliseconds =
      performance.now() - (drag.edgePanStartedAt || performance.now());
    const ramp = Math.min(1, heldMilliseconds / SLICE_EDGE_RAMP_MS);
    const deltaHz =
      direction *
      depth *
      Math.max(.05, ramp) *
      SLICE_EDGE_MAX_BANDWIDTHS_PER_SECOND *
      this.bandwidthHz *
      (PAN_DRAG_FRAME_MS / 1000);
    if (Math.abs(deltaHz) < 1) {
      return;
    }

    const halfBandwidth = this.bandwidthHz / 2;
    const nextCenterHz = Math.round(Math.max(
      Math.max(100_000, halfBandwidth),
      Math.min(
        60_000_000 - halfBandwidth,
        this.centerHz + deltaHz)));
    if (nextCenterHz === this.centerHz) {
      return;
    }

    this.centerHz = nextCenterHz;
    this.pendingCenterHz = nextCenterHz;
    this.pendingBandwidthHz = this.bandwidthHz;
    drag.edgePanMoved = true;
    const sliceX = Math.max(
      zonePixels,
      Math.min(width - zonePixels, drag.lastX));
    const sliceFrequencyHz = Math.round(
      this.xToFrequency(sliceX) - drag.frequencyOffsetHz);
    drag.edgeSliceFrequencyHz = sliceFrequencyHz;

    this.applyWaterfallFrequencyPreview(
      nextCenterHz,
      this.bandwidthHz);
    this.onPanPreview?.(nextCenterHz);
    this.dragPreview = {
      sliceId: drag.sliceId,
      frequencyHz: sliceFrequencyHz
    };
    this.onSlicePreview?.(
      drag.sliceId,
      sliceFrequencyHz);
    this.onSliceTune?.(
      drag.sliceId,
      sliceFrequencyHz,
      false);
    this.drawSpectrum();
  }

  hitTestSlice(x) {
    const startHz = this.effectiveCenterHz() - (this.bandwidthHz / 2);
    const pixelsPerHz =
      this.spectrum.width / Math.max(1, this.bandwidthHz);
    const ratio = Math.min(window.devicePixelRatio || 1, 1.5);
    const centerGrab = Math.max(7, ratio * 7);
    const edgeGrab = Math.max(4, ratio * 4);
    const ordered = [...this.slices].sort(
      (left, right) =>
        Number(left.isActive) - Number(right.isActive));

    for (const slice of ordered) {
      const center =
        (slice.frequencyHz - startHz) * pixelsPerHz;
      const low =
        (slice.frequencyHz + slice.filterLowHz - startHz) * pixelsPerHz;
      const high =
        (slice.frequencyHz + slice.filterHighHz - startHz) * pixelsPerHz;
      const left = Math.min(low, high);
      const right = Math.max(low, high);
      const centerDistance = Math.abs(x - center);
      const lowDistance = Math.abs(x - low);
      const highDistance = Math.abs(x - high);
      if (centerDistance <= centerGrab) {
        return { slice, action: "slice" };
      }
      if (lowDistance <= edgeGrab) {
        return { slice, action: "filter-low" };
      }
      if (highDistance <= edgeGrab) {
        return { slice, action: "filter-high" };
      }
      if (x >= left && x <= right) {
        return { slice, action: "slice" };
      }
    }
    return null;
  }

  canvasPoint(event, surface = this.spectrum) {
    const rect = surface.getBoundingClientRect();
    return {
      x:
        ((event.clientX - rect.left) / Math.max(1, rect.width)) *
        this.spectrum.width,
      y:
        ((event.clientY - rect.top) / Math.max(1, rect.height)) *
        this.spectrum.height
    };
  }

  xToFrequency(x) {
    const normalized =
      Math.max(0, Math.min(1, x / Math.max(1, this.spectrum.width)));
    return (
      this.effectiveCenterHz() -
      (this.bandwidthHz / 2) +
      (normalized * this.bandwidthHz)
    );
  }

  effectiveCenterHz() {
    return this.pointerState?.action === "background" &&
      this.pointerState.moved
      ? this.pointerState.previewCenterHz
      : this.centerHz;
  }

  isPanGestureActive() {
    return Boolean(
      (this.pointerState?.action === "background" &&
       this.pointerState.moved) ||
      this.pointerState?.edgePanMoved);
  }

  hasPendingPan() {
    return this.pendingCenterHz !== null;
  }

  cancelUserInteraction() {
    const drag = this.pointerState;
    const surface = drag?.surface ?? this.spectrum;
    this.stopSliceEdgePan();
    if (drag &&
        surface?.hasPointerCapture?.(drag.pointerId)) {
      surface.releasePointerCapture(drag.pointerId);
    }
    this.dragPreview = null;
    this.pointerState = null;
    this.pendingCenterHz = null;
    this.pendingBandwidthHz = null;
    this.pendingSourceCenterHz = this.centerHz;
    this.pendingSourceBandwidthHz = this.bandwidthHz;
    this.acceptNextConfiguredCenter = false;
    this.restoreWaterfallPanPreview();
    this.updatePointerCursor();
    if (this.waterfall?.style) {
      this.waterfall.style.cursor = "grab";
    }
    this.drawSpectrum();
  }

  previewExternalPanCenter(centerHz, acceptRadioCenter = false) {
    this.previewExternalFrequencyRange(
      centerHz,
      this.bandwidthHz,
      acceptRadioCenter);
  }

  previewExternalFrequencyRange(
    centerHz,
    bandwidthHz,
    acceptRadioCenter = false) {
    const nextCenterHz = Number(centerHz);
    const nextBandwidthHz = Number(bandwidthHz);
    if (!Number.isFinite(nextCenterHz) ||
        !Number.isFinite(nextBandwidthHz) ||
        nextBandwidthHz <= 0) {
      return;
    }
    this.beginWaterfallPanPreview();
    this.centerHz = nextCenterHz;
    this.bandwidthHz = nextBandwidthHz;
    this.pendingCenterHz = nextCenterHz;
    this.pendingBandwidthHz = nextBandwidthHz;
    this.pendingSourceCenterHz = this.frameCenterHz;
    this.pendingSourceBandwidthHz = this.frameBandwidthHz;
    this.acceptNextConfiguredCenter = Boolean(acceptRadioCenter);
    this.applyWaterfallFrequencyPreview(
      nextCenterHz,
      nextBandwidthHz);
    this.drawSpectrum();
  }

  cancelPendingPan(centerHz, bandwidthHz = this.bandwidthHz) {
    const restoredCenterHz = Number(centerHz);
    const restoredBandwidthHz = Number(bandwidthHz);
    if (Number.isFinite(restoredCenterHz)) {
      this.centerHz = restoredCenterHz;
    }
    if (Number.isFinite(restoredBandwidthHz) &&
        restoredBandwidthHz > 0) {
      this.bandwidthHz = restoredBandwidthHz;
    }
    this.pendingCenterHz = null;
    this.pendingBandwidthHz = null;
    this.pendingSourceCenterHz = this.centerHz;
    this.pendingSourceBandwidthHz = this.bandwidthHz;
    this.acceptNextConfiguredCenter = false;
    this.restoreWaterfallPanPreview();
    this.drawSpectrum();
  }

  centersMatch(leftHz, rightHz) {
    const toleranceHz = Math.max(
      25,
      this.bandwidthHz / Math.max(1, this.spectrum.width));
    return Math.abs(Number(leftHz) - Number(rightHz)) <= toleranceHz;
  }

  frequencyFramesMatch(
    leftCenterHz,
    leftBandwidthHz,
    rightCenterHz,
    rightBandwidthHz) {
    const bandwidthToleranceHz = Math.max(
      25,
      Number(leftBandwidthHz) /
        Math.max(1, this.spectrum.width));
    return this.centersMatch(leftCenterHz, rightCenterHz) &&
      Math.abs(
        Number(leftBandwidthHz) -
        Number(rightBandwidthHz)) <= bandwidthToleranceHz;
  }

  beginWaterfallPanPreview() {
    const width = this.waterfall.width;
    const height = this.waterfall.height;
    if (!width ||
        !height ||
        !this.waterfallPanPreviewContext) {
      return;
    }
    this.waterfallPanPreview.width = width;
    this.waterfallPanPreview.height = height;
    this.waterfallPanPreviewContext.drawImage(
      this.waterfall,
      0,
      0,
      width,
      height);
    this.waterfallPanPreviewCenterHz = this.effectiveCenterHz();
    this.waterfallPanPreviewBandwidthHz = this.bandwidthHz;
    this.waterfallPanPreviewActive = true;
  }

  applyWaterfallFrequencyPreview(
    centerHz,
    bandwidthHz = this.bandwidthHz) {
    if (!this.waterfallPanPreviewActive) {
      return;
    }
    const width = this.waterfall.width;
    const height = this.waterfall.height;
    const sourceBandwidthHz =
      Math.max(1, this.waterfallPanPreviewBandwidthHz);
    const destinationBandwidthHz = Math.max(1, bandwidthHz);
    const sourceStartHz =
      this.waterfallPanPreviewCenterHz - (sourceBandwidthHz / 2);
    const sourceEndHz = sourceStartHz + sourceBandwidthHz;
    const destinationStartHz =
      centerHz - (destinationBandwidthHz / 2);
    const destinationEndHz =
      destinationStartHz + destinationBandwidthHz;
    const overlapStartHz = Math.max(sourceStartHz, destinationStartHz);
    const overlapEndHz = Math.min(sourceEndHz, destinationEndHz);
    this.waterfallContext.fillStyle = "#000010";
    this.waterfallContext.fillRect(0, 0, width, height);
    if (overlapEndHz <= overlapStartHz) {
      return;
    }
    const sourceX =
      ((overlapStartHz - sourceStartHz) / sourceBandwidthHz) * width;
    const sourceWidth =
      ((overlapEndHz - overlapStartHz) / sourceBandwidthHz) * width;
    const destinationX =
      ((overlapStartHz - destinationStartHz) /
        destinationBandwidthHz) * width;
    const destinationWidth =
      ((overlapEndHz - overlapStartHz) /
        destinationBandwidthHz) * width;
    this.waterfallContext.drawImage(
      this.waterfallPanPreview,
      sourceX,
      0,
      sourceWidth,
      height,
      destinationX,
      0,
      destinationWidth,
      height);
  }

  restoreWaterfallPanPreview() {
    if (!this.waterfallPanPreviewActive) {
      return;
    }
    this.waterfallContext.drawImage(
      this.waterfallPanPreview,
      0,
      0);
    this.waterfallPanPreviewActive = false;
  }

  updatePointerCursor(action) {
    this.spectrum.style.cursor =
      action === "filter-low" || action === "filter-high"
        ? "ew-resize"
        : action === "slice"
          ? "grab"
          : this.pointerState?.action === "background"
            ? "grabbing"
            : "grab";
  }

  createColormapLut() {
    const lut = new Uint8ClampedArray(256 * 3);
    for (let index = 0; index < 256; index += 1) {
      const [red, green, blue] = this.colormap(index / 255);
      const offset = index * 3;
      lut[offset] = red;
      lut[offset + 1] = green;
      lut[offset + 2] = blue;
    }
    return lut;
  }

  colormap(value) {
    const stops = [
      [0.00, [0, 0, 0]],
      [0.15, [0, 0, 128]],
      [0.30, [0, 64, 255]],
      [0.45, [0, 200, 255]],
      [0.60, [0, 220, 0]],
      [0.80, [255, 255, 0]],
      [1.00, [255, 0, 0]]
    ];

    for (let index = 1; index < stops.length; index += 1) {
      if (value <= stops[index][0]) {
        const previous = stops[index - 1];
        const next = stops[index];
        const amount = (value - previous[0]) / (next[0] - previous[0]);
        return previous[1].map(
          (component, componentIndex) =>
            Math.round(component + ((next[1][componentIndex] - component) * amount)));
      }
    }

    return stops.at(-1)[1];
  }
}
