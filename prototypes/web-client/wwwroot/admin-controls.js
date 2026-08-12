export function normalizeAdminMode(value) {
  return String(value || "").trim().toLowerCase() === "exclusive"
    ? "exclusive"
    : "shared";
}

export function normalizeReservation(value) {
  const normalized = String(value || "").trim();
  return normalized || null;
}

export function formatClientCapacity(available, licensed) {
  const availableCount = Number(available);
  const licensedCount = Number(licensed);
  if (availableCount < 0 || licensedCount < 0) {
    return "Client capacity unavailable";
  }
  return `${availableCount} of ${licensedCount} client slots available`;
}

export function buildPolicyRequest(mode, reservedUserId) {
  return {
    mode: normalizeAdminMode(mode),
    reservedUserId: normalizeReservation(reservedUserId)
  };
}

export function normalizeRadioLabel(value) {
  return String(value || "").trim();
}

export function normalizeTransmitPolicyState(value) {
  const normalized = String(value || "").trim().toLowerCase();
  return new Set([
    "receive-only",
    "tx-eligible",
    "temporarily-disabled",
    "prerequisites-failed"
  ]).has(normalized)
    ? normalized
    : "receive-only";
}

export function buildTransmitPolicyRequest(state) {
  return { state: normalizeTransmitPolicyState(state) };
}

export function formatRadioOwnership(radio) {
  const source = String(radio?.source || "local").trim().toLowerCase();
  const sourceRadioId =
    String(radio?.sourceRadioId || radio?.radioId || "unknown").trim();
  if (source === "remote") {
    const stationId = String(radio?.stationId || "unknown").trim();
    return `Remote station ${stationId} · source radio ${sourceRadioId}`;
  }
  return `Local gateway · source radio ${sourceRadioId}`;
}

export function normalizeStationId(value) {
  return String(value || "").trim();
}

export function stationIdValid(value) {
  return /^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$/.test(
    normalizeStationId(value));
}

export function formatStationCredentialSource(source) {
  return String(source || "").trim().toLowerCase() === "imported"
    ? "Imported from existing setup"
    : "Enrolled with one-time code";
}

export function formatEnrollmentPurpose(purpose) {
  switch (String(purpose || "").trim().toLowerCase()) {
    case "rotate":
      return "credential rotation";
    case "reenroll":
      return "re-enrollment";
    default:
      return "new enrollment";
  }
}

export function formatAuditAction(action) {
  switch (String(action || "").trim().toLowerCase()) {
    case "radio.policy.update":
      return "Radio access policy changed";
    case "radio.identity.update":
      return "Radio identity updated";
    case "radio.transmit_policy.update":
      return "Radio transmit policy changed";
    case "radio.operator.force_disconnect":
      return "Operator released";
    case "station.enrollment_code.create":
      return "Station enrollment code created";
    case "station.credential.enable":
      return "Station enabled";
    case "station.credential.disable":
      return "Station disabled";
    case "station.credential.revoke":
      return "Station credential revoked";
    default:
      return "Administrative action";
  }
}

export function formatAuditResult(result) {
  return String(result || "").trim().toLowerCase() === "succeeded"
    ? "SUCCEEDED"
    : "FAILED";
}
