export const minimumAppletRailWidth = 220;
export const maximumAppletRailWidth = 480;

export function shouldCloseToolPanel(
  toolOpen,
  activeTool,
  requestedTool) {
  return Boolean(
    toolOpen &&
    requestedTool &&
    activeTool === requestedTool);
}

export function clampAppletRailWidth(width, viewportWidth) {
  const availableWidth = Number.isFinite(Number(viewportWidth))
    ? Math.floor(Number(viewportWidth) * .48)
    : maximumAppletRailWidth;
  const maximumWidth = Math.max(
    minimumAppletRailWidth,
    Math.min(maximumAppletRailWidth, availableWidth));
  const requestedWidth = Number(width);
  if (!Number.isFinite(requestedWidth)) {
    return Math.min(300, maximumWidth);
  }
  return Math.round(
    Math.max(
      minimumAppletRailWidth,
      Math.min(maximumWidth, requestedWidth)));
}
