export function normalizeBandPlan(source) {
  if (!source || !Array.isArray(source.segments)) {
    return [];
  }

  return source.segments.flatMap(segment => {
    const lowMhz = Number(segment?.low);
    const highMhz = Number(segment?.high);
    const label = String(segment?.label ?? "").trim();
    const license = String(segment?.license ?? "").trim();
    const color = String(segment?.color ?? "").trim();
    if (!Number.isFinite(lowMhz) ||
        !Number.isFinite(highMhz) ||
        lowMhz < 0 ||
        highMhz <= lowMhz ||
        !label ||
        !/^#[0-9a-f]{6}$/i.test(color)) {
      return [];
    }

    return [{
      lowHz: Math.round(lowMhz * 1_000_000),
      highHz: Math.round(highMhz * 1_000_000),
      label,
      license,
      color
    }];
  });
}

export function visibleBandSegments(
  segments,
  centerFrequencyHz,
  bandwidthHz,
  displayWidth = 0) {
  const centerHz = Number(centerFrequencyHz);
  const spanHz = Number(bandwidthHz);
  if (!Array.isArray(segments) ||
      !Number.isFinite(centerHz) ||
      !Number.isFinite(spanHz) ||
      spanHz <= 0) {
    return [];
  }

  const startHz = centerHz - (spanHz / 2);
  const endHz = centerHz + (spanHz / 2);
  return segments.flatMap(segment => {
    const visibleLowHz = Math.max(startHz, segment.lowHz);
    const visibleHighHz = Math.min(endHz, segment.highHz);
    if (visibleHighHz <= visibleLowHz) {
      return [];
    }

    const left = (visibleLowHz - startHz) / spanHz;
    const width = (visibleHighHz - visibleLowHz) / spanHz;
    const widthPixels = width * Math.max(0, Number(displayWidth) || 0);
    return [{
      ...segment,
      left,
      width,
      showLabel: widthPixels >= 24,
      showLicense: widthPixels >= 58 && Boolean(segment.license)
    }];
  });
}
