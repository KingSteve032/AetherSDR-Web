export function rangeFillPercent(minimum, maximum, value) {
  const min = Number(minimum);
  const max = Number(maximum);
  const current = Number(value);
  if (!Number.isFinite(min) ||
      !Number.isFinite(max) ||
      !Number.isFinite(current) ||
      max <= min) {
    return 0;
  }

  return Math.max(0, Math.min(100, ((current - min) / (max - min)) * 100));
}

export function sliderToAudioPan(value) {
  return Math.round(
    Math.max(-50, Math.min(50, Number(value) || 0)) + 50);
}

export function audioPanToSlider(value) {
  return Math.round(
    Math.max(0, Math.min(100, Number(value) || 0)) - 50);
}
