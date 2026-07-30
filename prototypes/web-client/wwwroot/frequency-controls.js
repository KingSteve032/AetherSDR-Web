const MIN_FREQUENCY_HZ = 100_000;
const MAX_FREQUENCY_HZ = 60_000_000;

export function formatFrequency(frequencyHz) {
  const padded = Math.round(frequencyHz).toString().padStart(9, "0");
  return `${Number(padded.slice(0, -6))}.${padded.slice(-6, -3)}.${padded.slice(-3)}`;
}

export function parseFrequency(value) {
  const text = String(value ?? "").trim().toLowerCase();
  if (!text) {
    return null;
  }

  let frequencyHz = null;
  const suffixMatch = text.match(/^(\d+(?:[.,]\d+)?)\s*(mhz|khz|hz)$/);
  if (suffixMatch) {
    const amount = Number(suffixMatch[1].replace(",", "."));
    const multiplier =
      suffixMatch[2] === "mhz" ? 1_000_000 :
      suffixMatch[2] === "khz" ? 1_000 : 1;
    frequencyHz = Math.round(amount * multiplier);
  } else {
    const separators = (text.match(/[.,]/g) ?? []).length;
    if (separators === 1 && /^\d+[.,]\d+$/.test(text)) {
      frequencyHz =
        Math.round(Number(text.replace(",", ".")) * 1_000_000);
    } else if (/^[\d.,\s]+$/.test(text)) {
      const digits = text.replace(/[^\d]/g, "");
      if (!digits) {
        return null;
      }
      const amount = Number(digits);
      if (separators > 1 || amount >= MIN_FREQUENCY_HZ) {
        frequencyHz = amount;
      } else if (amount <= 60 && digits.length <= 2) {
        frequencyHz = amount * 1_000_000;
      } else {
        frequencyHz = amount * 1_000;
      }
    }
  }

  return Number.isSafeInteger(frequencyHz) &&
    frequencyHz >= MIN_FREQUENCY_HZ &&
    frequencyHz <= MAX_FREQUENCY_HZ
    ? frequencyHz
    : null;
}

export function resolveFrequencySliceId(
  requestedSliceId,
  slices,
  radioActiveSliceId = "") {
  const available = Array.isArray(slices) ? slices : [];
  return available.find(slice => slice.id === requestedSliceId)?.id ??
    available.find(slice => slice.id === radioActiveSliceId)?.id ??
    available.find(slice => slice.isActive)?.id ??
    available[0]?.id ??
    "";
}

export function isFrequencyVisible(frequencyHz, panadapter) {
  if (!panadapter) {
    return false;
  }
  const halfBandwidth = panadapter.bandwidthHz / 2;
  return frequencyHz >= panadapter.centerFrequencyHz - halfBandwidth &&
    frequencyHz <= panadapter.centerFrequencyHz + halfBandwidth;
}

export function clampPanCenter(centerFrequencyHz, bandwidthHz) {
  const halfBandwidth = Math.max(0, Number(bandwidthHz) || 0) / 2;
  return Math.round(Math.max(
    Math.max(MIN_FREQUENCY_HZ, halfBandwidth),
    Math.min(MAX_FREQUENCY_HZ - halfBandwidth, centerFrequencyHz)));
}
