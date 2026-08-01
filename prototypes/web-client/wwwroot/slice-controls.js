const lowerFilterModes = new Set(["LSB", "DIGL", "CWR"]);
const carrierSpanningModes = new Set(["AM", "SAM", "FM", "NFM"]);
const sidebandLowModes = new Set(["LSB", "DIGL"]);
const sidebandHighModes = new Set(["USB", "DIGU"]);
const filterMinimumWidthHz = 50;
const filterEdgeLimitHz = 12_000;

export function rxControlAvailability(mode, radioModel = "") {
  const normalizedMode = String(mode).toUpperCase();
  const isDigital =
    ["DIGL", "DIGU", "NT", "RTTY"].includes(normalizedMode) ||
    normalizedMode.startsWith("FDV");
  const isCw = ["CW", "CWL", "CWR"].includes(normalizedMode);
  const isFm = ["FM", "NFM", "DFM", "WFM"].includes(normalizedMode);
  const isVoice = !isDigital && !isCw && !isFm;
  const hasExtendedDsp = /\bFLEX-8\d{3}\b/i.test(String(radioModel));

  return {
    squelch: !isDigital && !isCw,
    nb: !isFm,
    nr: !isFm,
    anf: isVoice,
    nrl: !isFm,
    nrs: hasExtendedDsp && !isFm,
    rnn: hasExtendedDsp && !isFm && !isCw,
    nrf: hasExtendedDsp && !isFm,
    anfl: isVoice,
    anft: isVoice
  };
}

export function filterEdgesForMode(mode, widthHz) {
  const normalizedMode = String(mode).toUpperCase();
  const limits = filterLimitsForMode(normalizedMode);
  const width = Math.max(
    filterMinimumWidthHz,
    Math.min(limits.maximumHz - limits.minimumHz, Math.round(widthHz)));
  if (sidebandLowModes.has(normalizedMode)) {
    const high = -300;
    return {
      filterLowHz: Math.max(limits.minimumHz, high - width),
      filterHighHz: high
    };
  }
  if (sidebandHighModes.has(normalizedMode)) {
    const low = 300;
    return {
      filterLowHz: low,
      filterHighHz: Math.min(limits.maximumHz, low + width)
    };
  }
  if (lowerFilterModes.has(normalizedMode)) {
    return {
      filterLowHz: Math.max(limits.minimumHz, -width),
      filterHighHz: 0
    };
  }
  if (!carrierSpanningModes.has(normalizedMode)) {
    return {
      filterLowHz: 0,
      filterHighHz: Math.min(limits.maximumHz, width)
    };
  }
  const lowerHalf = Math.floor(width / 2);
  return clampFilterEdgesForMode(normalizedMode, {
    filterLowHz: -lowerHalf,
    filterHighHz: width - lowerHalf
  });
}

export function filterLimitsForMode(mode) {
  const normalizedMode = String(mode).toUpperCase();
  if (lowerFilterModes.has(normalizedMode)) {
    return {
      minimumHz: -filterEdgeLimitHz,
      maximumHz: 0,
      minimumWidthHz: filterMinimumWidthHz
    };
  }
  if (carrierSpanningModes.has(normalizedMode)) {
    return {
      minimumHz: -filterEdgeLimitHz,
      maximumHz: filterEdgeLimitHz,
      minimumWidthHz: filterMinimumWidthHz
    };
  }
  return {
    minimumHz: 0,
    maximumHz: filterEdgeLimitHz,
    minimumWidthHz: filterMinimumWidthHz
  };
}

export function clampFilterEdgesForMode(
  mode,
  edges,
  draggedEdge = "both") {
  const limits = filterLimitsForMode(mode);
  const clamp = (value, minimum, maximum) =>
    Math.max(minimum, Math.min(maximum, value));
  let filterLowHz = Math.round(Number(edges?.filterLowHz));
  let filterHighHz = Math.round(Number(edges?.filterHighHz));

  if (!Number.isFinite(filterLowHz)) {
    filterLowHz = limits.minimumHz;
  }
  if (!Number.isFinite(filterHighHz)) {
    filterHighHz = limits.maximumHz;
  }

  filterLowHz = clamp(
    filterLowHz,
    limits.minimumHz,
    limits.maximumHz - limits.minimumWidthHz);
  filterHighHz = clamp(
    filterHighHz,
    limits.minimumHz + limits.minimumWidthHz,
    limits.maximumHz);

  if (filterHighHz - filterLowHz < limits.minimumWidthHz) {
    if (draggedEdge === "low") {
      filterLowHz = filterHighHz - limits.minimumWidthHz;
    } else {
      filterHighHz = filterLowHz + limits.minimumWidthHz;
    }
  }

  return { filterLowHz, filterHighHz };
}

export function formatFilterWidth(widthHz) {
  const width = Math.max(0, Number(widthHz) || 0);
  if (width >= 1000) {
    return `${(width / 1000).toFixed(width % 1000 === 0 ? 0 : 1)}K`;
  }
  return `${Math.round(width)}`;
}

export function sliceFlagDirection(normalizedPosition) {
  const position = Number(normalizedPosition);
  if (position <= .2) {
    return "right";
  }
  if (position >= .8) {
    return "left";
  }
  return position <= .5 ? "left" : "right";
}

export function sliceFlagDirections(normalizedPositions) {
  const positions = Array.isArray(normalizedPositions)
    ? normalizedPositions.map(position => Number(position))
    : [];
  const directions = [];

  for (const [index, position] of positions.entries()) {
    const preferred = sliceFlagDirection(position);
    const collidesWithPreferred = positions
      .slice(0, index)
      .some((previousPosition, previousIndex) =>
        Math.abs(previousPosition - position) <= .02 &&
        directions[previousIndex] === preferred);
    directions.push(
      collidesWithPreferred
        ? (preferred === "left" ? "right" : "left")
        : preferred);
  }

  return directions;
}

export function signalDbmToMeterFraction(dbm) {
  const value = Number(dbm);
  if (!Number.isFinite(value) || value <= -127) {
    return 0;
  }
  if (value <= -73) {
    return ((value + 127) / 54) * .6;
  }
  if (value <= -13) {
    return .6 + (((value + 73) / 60) * .4);
  }
  return 1;
}

export function sliceSignalDbm(slice, panadapter, bins) {
  const binCount = Number(bins?.length) || 0;
  const bandwidthHz = Number(panadapter?.bandwidthHz);
  const centerFrequencyHz = Number(panadapter?.centerFrequencyHz);
  const frequencyHz = Number(slice?.frequencyHz);
  const filterLowHz = Number(slice?.filterLowHz);
  const filterHighHz = Number(slice?.filterHighHz);
  if (binCount === 0 ||
      !Number.isFinite(bandwidthHz) ||
      bandwidthHz <= 0 ||
      !Number.isFinite(centerFrequencyHz) ||
      !Number.isFinite(frequencyHz) ||
      !Number.isFinite(filterLowHz) ||
      !Number.isFinite(filterHighHz)) {
    return null;
  }

  const panStartHz = centerFrequencyHz - (bandwidthHz / 2);
  const panEndHz = panStartHz + bandwidthHz;
  const filterStartHz = frequencyHz + Math.min(filterLowHz, filterHighHz);
  const filterEndHz = frequencyHz + Math.max(filterLowHz, filterHighHz);
  if (filterEndHz < panStartHz || filterStartHz > panEndHz) {
    return null;
  }

  const firstBin = Math.max(
    0,
    Math.floor(((filterStartHz - panStartHz) / bandwidthHz) * binCount));
  const lastBin = Math.min(
    binCount - 1,
    Math.ceil(((filterEndHz - panStartHz) / bandwidthHz) * binCount));
  if (lastBin < firstBin) {
    return null;
  }

  let peakDbm = Number.NEGATIVE_INFINITY;
  for (let index = firstBin; index <= lastBin; index += 1) {
    const candidate = Number(bins[index]);
    if (Number.isFinite(candidate)) {
      peakDbm = Math.max(peakDbm, candidate);
    }
  }
  return Number.isFinite(peakDbm) ? peakDbm : null;
}
