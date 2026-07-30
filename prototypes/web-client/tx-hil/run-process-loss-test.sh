#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

ROOT="/mnt/devspace-projects/aethersdr-web/prototypes/web-client"
HIL_PROJECT="$ROOT/tx-hil/AetherSDR.TxHil.csproj"
HIL="$ROOT/tx-hil/bin/Release/net10.0/AetherSDR.TxHil.dll"
FREQUENCY_HZ="${1:-14262000}"
EXPECTED_SERIAL="1121-1104-6700-2912"
EXPECTED_RADIO="FLEX:1121-1104-6700-2912"
ON_AIR_CONFIRM="KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF"
ARM="/run/user/$UID/aethersdr-tx-process-loss.json"
CHILD_PLAN="/run/user/$UID/aethersdr-tx-process-child.json"
STATE_ROOT="${XDG_STATE_HOME:-$HOME/.local/state}/aethersdr-web/tx-hil-logs"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
REPORT_DIR="$STATE_ROOT/process-loss-$STAMP"
FULL_LOG="$REPORT_DIR/FULL.log"
SUMMARY="$REPORT_DIR/SUMMARY.txt"

if [[ ! "$FREQUENCY_HZ" =~ ^[0-9]+$ ]] ||
   (( FREQUENCY_HZ < 14225000 || FREQUENCY_HZ > 14350000 )); then
    echo "STOP: frequency must be an integer from 14225000 through 14350000 Hz." >&2
    exit 2
fi

if [[ -z "${TMUX:-}" ]]; then
    echo "STOP: run this script inside tmux so an SSH interruption cannot kill the safety parent." >&2
    echo "Example:" >&2
    echo "  tmux new -s psoc2-process-loss 'bash $ROOT/tx-hil/run-process-loss-test.sh $FREQUENCY_HZ'" >&2
    exit 2
fi

for required in dotnet python3 jq tee; do
    if ! command -v "$required" >/dev/null 2>&1; then
        echo "STOP: required command is missing: $required" >&2
        exit 2
    fi
done

if [[ ! -d "/run/user/$UID" ]]; then
    echo "STOP: /run/user/$UID does not exist." >&2
    exit 2
fi

mkdir -p "$REPORT_DIR"
touch "$FULL_LOG"

FREQUENCY_MHZ="$(python3 - "$FREQUENCY_HZ" <<'PY'
import sys
print(f"{int(sys.argv[1]) / 1_000_000:.3f}")
PY
)"
FREQUENCY_RADIO="$(python3 - "$FREQUENCY_HZ" <<'PY'
import sys
print(f"{int(sys.argv[1]) / 1_000_000:.6f}")
PY
)"
HUMAN_CONFIRM="$FREQUENCY_MHZ still clear; camera and remote power ready"

log() {
    printf '%s\n' "$*" | tee -a "$FULL_LOG"
}

section() {
    log ""
    log "================================================================"
    log "$*"
    log "================================================================"
}

extract_largest_json() {
    local input_file="$1"
    local output_file="$2"
    python3 - "$input_file" "$output_file" <<'PY'
import json
import pathlib
import sys

source = pathlib.Path(sys.argv[1])
target = pathlib.Path(sys.argv[2])
text = source.read_text(errors="replace")
decoder = json.JSONDecoder()
candidates = []
for index, character in enumerate(text):
    if character != "{":
        continue
    try:
        value, consumed = decoder.raw_decode(text[index:])
    except json.JSONDecodeError:
        continue
    if isinstance(value, dict):
        candidates.append((consumed, index, value))
if not candidates:
    raise SystemExit("no JSON object found")
value = max(candidates, key=lambda item: item[0])[2]
target.write_text(json.dumps(value, indent=2) + "\n")
PY
}

run_capture() {
    local label="$1"
    local output_file="$2"
    shift 2

    section "$label"
    set +e
    "$@" 2>&1 | tee "$output_file" | tee -a "$FULL_LOG"
    local command_status=${PIPESTATUS[0]}
    set -e
    if (( command_status != 0 )); then
        log "COMMAND FAILED: $label (exit $command_status)"
        return "$command_status"
    fi
    return 0
}

inspect_with_retries() {
    local label="$1"
    local final_output="$2"
    local final_json="$3"
    local attempt

    for attempt in 1 2 3; do
        local attempt_output="$REPORT_DIR/${label// /_}-attempt-$attempt.log"
        if run_capture "$label — attempt $attempt of 3" \
            "$attempt_output" \
            dotnet "$HIL" inspect; then
            if extract_largest_json "$attempt_output" "$final_json"; then
                cp "$attempt_output" "$final_output"
                return 0
            fi
            log "Inspection returned success but no usable JSON object."
        fi
        if (( attempt < 3 )); then
            log "Retrying read-only FLEX inspection in 3 seconds..."
            sleep 3
        fi
    done
    return 1
}

redact_prepare_output() {
    local input_file="$1"
    local output_file="$2"
    local token="$3"
    python3 - "$input_file" "$output_file" "$token" <<'PY'
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1]).read_text(errors="replace")
token = sys.argv[3]
if token:
    source = source.replace(token, "<REDACTED-ONE-TIME-TOKEN>")
source = re.sub(
    r'("oneTimeToken"\s*:\s*")[^"]*(")',
    r'\1<REDACTED-ONE-TIME-TOKEN>\2',
    source,
)
pathlib.Path(sys.argv[2]).write_text(source)
PY
}

write_summary() {
    local test_json="$1"
    local final_json="$2"
    {
        echo "AetherSDR PSOC2 true engine-process/TCP-loss HIL report"
        echo "Generated (UTC): $(date -u --iso-8601=seconds)"
        echo "Frequency: $FREQUENCY_MHZ MHz ($FREQUENCY_HZ Hz)"
        echo "Report directory: $REPORT_DIR"
        echo
        jq -r '
            "RESULT: " + (if .passed then "PASS" else "FAIL" end),
            "Test: " + .test,
            "Radio: " + .radio,
            "Serial: " + .serial,
            "RF emitted: " + (.rfEmitted|tostring),
            "Child PID: " + (.childProcess.ProcessId|tostring),
            "Child FLEX handle: " + .childProcess.clientHandle,
            "Child killed: " + (.childProcess.killed|tostring),
            "Entire process tree killed: " + (.childProcess.entireProcessTree|tostring),
            "Child exit code: " + (.childProcess.exitCode|tostring),
            "Graceful child cleanup ran: " + (.childProcess.gracefulCleanupRan|tostring),
            "Child resources gone: " + (.childProcess.childResourcesGoneBeforeCleanup|tostring),
            "Child commands before kill: key=" + (.childCommandsBeforeKill.key|tostring) + " unkey=" + (.childCommandsBeforeKill.unkey|tostring),
            "Replacement PID: " + (.replacementEngine.ProcessId|tostring),
            "Replacement FLEX handle: " + .replacementEngine.clientHandle,
            "Replacement exit code: " + (.replacementEngine.ExitCode|tostring),
            "Replacement old handle absent: " + (.replacementEngine.OldHandleAbsent|tostring),
            "Replacement identities fresh: " + (.replacementEngine.AllIdentitiesFresh|tostring),
            "Replacement resources gone: " + (.replacementEngine.ResourcesGone|tostring),
            "Replacement baseline restored: " + (.replacementEngine.BaselineRestored|tostring),
            "Replacement commands: key=" + (.replacementEngine.commands.key|tostring) + " unkey=" + (.replacementEngine.commands.unkey|tostring),
            "Replacement started: " + .replacementEngine.StartedAt,
            "Replacement ready: " + .replacementEngine.ReadyAt,
            "Replacement reconciled: " + .replacementEngine.ReconciledAt,
            "Replacement exited: " + .replacementEngine.ExitedAt,
            "Observer FLEX handle: " + .independentObserver.clientHandle,
            "Observer unkeys: " + (.independentObserver.unkeyCommands|tostring),
            "Observer key capability: " + (.independentObserver.keyCapability|tostring),
            "Unkey mechanism: " + .independentObserver.mechanism,
            "Process kill-to-exit ms: " + (.timing.processKillToExitMilliseconds|tostring),
            "Process exit-to-safety signal ms: " + (.timing.processExitToSafetySignalMilliseconds|tostring),
            "Safety signal-to-unkey dispatch ms: " + (.timing.safetySignalToUnkeyDispatchMilliseconds|tostring),
            "Unkey dispatch-to-completion ms: " + (.timing.unkeyDispatchToCompletionMilliseconds|tostring),
            "Unkey completion-to-idle ms: " + (.timing.unkeyCompletionToIdleMilliseconds|tostring),
            "Process exit-to-safety action completion ms: " + (.timing.processExitToSafetyActionMilliseconds|tostring),
            "Safety action completion-to-idle ms: " + (.timing.safetyActionToIdleMilliseconds|tostring),
            "Process exit-to-roster loss ms: " + (.timing.processExitToRosterLossMilliseconds|tostring),
            "Idle-to-roster loss ms: " + (.timing.idleToRosterLossMilliseconds|tostring),
            "Keyed-to-idle ms: " + (.timing.keyedToIdleMilliseconds|tostring),
            "CW ID: " + .identification.Callsign + " at " + (.identification.Wpm|tostring) + " WPM",
            "CW exact-owned TX observed: " + (.identification.SawExactOwnedTransmit|tostring)
        ' "$test_json"
        echo
        jq -r '
            "Final TX state: " + .tx.state,
            "Final TX occupants: " + (.tx.occupants|length|tostring),
            "Final RF power: " + (.radio.RfPower|tostring) + " W",
            "Final DAX enabled: " + (.radio.TransmitSettings.DaxEnabled|tostring),
            "Final microphone: " + .radio.TransmitSettings.MicSelection,
            "Final VOX enabled: " + (.radio.TransmitSettings.VoxEnabled|tostring),
            "Final CWX: " + (.radio.cwx.Wpm|tostring) + " WPM, QSK=" + (.radio.cwx.QskEnabled|tostring) + ", delay=" + (.radio.cwx.BreakInDelayMilliseconds|tostring) + " ms"
        ' "$final_json"
        echo
        if [[ ! -e "$ARM" ]]; then
            echo "Outer manifest: consumed/absent"
        else
            echo "Outer manifest: WARNING — still present at $ARM"
        fi
        if [[ ! -e "$CHILD_PLAN" ]]; then
            echo "Child plan: consumed/absent"
        else
            echo "Child plan: WARNING — still present at $CHILD_PLAN"
        fi
    } > "$SUMMARY"
}

finish() {
    local status=$?
    echo
    echo "Report directory: $REPORT_DIR"
    echo "Summary:          $SUMMARY"
    echo "Complete log:     $FULL_LOG"
    if (( status != 0 )); then
        echo "SCRIPT RESULT: FAILED (exit $status)"
    fi
}
trap finish EXIT

section "PSOC2 TRUE ENGINE-PROCESS/TCP-LOSS HIL"
log "Frequency: $FREQUENCY_MHZ MHz ($FREQUENCY_HZ Hz)"
log "Radio: $EXPECTED_RADIO"
log "Expected serial: $EXPECTED_SERIAL"
log "Report directory: $REPORT_DIR"
log "No one-time token will be written to this report."

section "STALE ONE-TIME FILE CHECK"
for protected_file in "$ARM" "$CHILD_PLAN"; do
    if [[ -e "$protected_file" ]]; then
        log "STOP: stale protected file exists: $protected_file"
        log "Inspect it manually. This script will not delete an unexpected safety file."
        exit 3
    fi
    log "Clear: $protected_file"
done

BUILD_LOG="$REPORT_DIR/00-build.log"
run_capture "Build standalone HIL executable" \
    "$BUILD_LOG" \
    dotnet build "$HIL_PROJECT" -c Release

if [[ ! -f "$HIL" ]]; then
    log "STOP: expected HIL executable was not created: $HIL"
    exit 4
fi

PRE_INSPECT_LOG="$REPORT_DIR/10-pre-inspect.log"
PRE_INSPECT_JSON="$REPORT_DIR/10-pre-inspect.json"
if ! inspect_with_retries \
    "Pre-test inspection" \
    "$PRE_INSPECT_LOG" \
    "$PRE_INSPECT_JSON"; then
    log "STOP: PSOC2 could not be inspected after three read-only attempts."
    exit 5
fi

if ! jq -e \
    --arg serial "$EXPECTED_SERIAL" \
    '
      .operation == "inspect" and
      .radio.Serial == $serial and
      .tx.state == "idle" and
      (.tx.occupants | length) == 0 and
      .radio.RfPower == 100 and
      .radio.TransmitSettings.RfPower == 100 and
      .radio.TransmitSettings.DaxEnabled == true and
      .radio.TransmitSettings.MicSelection == "PC" and
      .radio.TransmitSettings.VoxEnabled == false and
      .radio.cwx.Wpm == 30 and
      .radio.cwx.QskEnabled == true and
      .radio.cwx.BreakInDelayMilliseconds == 5 and
      ([.guiClients[]? | select(.IsThisSession != true)] | length) == 0
    ' "$PRE_INSPECT_JSON" >/dev/null; then
    log "STOP: pre-test inspection did not satisfy the exact PSOC2 idle/restoration requirements."
    jq . "$PRE_INSPECT_JSON" | tee -a "$FULL_LOG"
    exit 6
fi
log "Pre-test safety validation: PASS"

NO_RF_LOG="$REPORT_DIR/15-no-rf-process-restart-preflight.log"
NO_RF_JSON="$REPORT_DIR/15-no-rf-process-restart-preflight.json"
section "NO-RF PROCESS/RESTART PREFLIGHT"
log "This stage kills and replaces an idle engine child but cannot key PSOC2."
set +e
dotnet "$HIL" verify-safety-process-loss-preflight \
    --frequency-hz "$FREQUENCY_HZ" \
    --on-air-confirm "$ON_AIR_CONFIRM" \
    2>&1 | tee "$NO_RF_LOG" | tee -a "$FULL_LOG"
NO_RF_STATUS=${PIPESTATUS[0]}
set -e

NO_RF_JSON_OK=false
if extract_largest_json "$NO_RF_LOG" "$NO_RF_JSON"; then
    NO_RF_JSON_OK=true
fi

if (( NO_RF_STATUS != 0 )) || [[ "$NO_RF_JSON_OK" != true ]]; then
    log "STOP: the no-RF process/restart preflight failed or emitted no usable JSON."
    log "Attempting the idle-only station-default recovery; it will fail closed unless PSOC2 is freshly idle."
    set +e
    dotnet "$HIL" restore-idle-defaults \
        2>&1 | tee "$REPORT_DIR/16-no-rf-failure-recovery.log" | tee -a "$FULL_LOG"
    PREFLIGHT_RECOVERY_STATUS=${PIPESTATUS[0]}
    set -e
    if (( PREFLIGHT_RECOVERY_STATUS != 0 )); then
        log "WARNING: no-RF failure recovery did not complete. Verify PSOC2 manually."
    fi
    exit 16
fi

if ! jq -e '
      .test == "independent-engine-process-loss-no-rf-preflight" and
      .passed == true and
      .rfEmitted == false and
      .childProcess.killed == true and
      .childProcess.entireProcessTree == true and
      .childProcess.exitCode == 137 and
      .childProcess.gracefulCleanupRan == false and
      .childProcess.childResourcesGoneBeforeCleanup == true and
      .childCommandsBeforeKill.key == 0 and
      .childCommandsBeforeKill.unkey == 0 and
      .independentObserver.unkeyCommands == 0 and
      .independentObserver.keyCapability == false and
      .replacementEngine.ExitCode == 0 and
      .replacementEngine.OldHandleAbsent == true and
      .replacementEngine.AllIdentitiesFresh == true and
      .replacementEngine.ResourcesGone == true and
      .replacementEngine.BaselineRestored == true and
      .replacementEngine.commands.key == 0 and
      .replacementEngine.commands.unkey == 0 and
      (.replacementEngine.ProcessId != .childProcess.ProcessId) and
      (.replacementEngine.clientHandle != .childProcess.clientHandle)
    ' "$NO_RF_JSON" >/dev/null; then
    log "STOP: the no-RF process/restart evidence did not meet the exact zero-command acceptance criteria."
    jq . "$NO_RF_JSON" | tee -a "$FULL_LOG"
    exit 17
fi

sleep 2
NO_RF_INSPECT_LOG="$REPORT_DIR/17-post-no-rf-inspect.log"
NO_RF_INSPECT_JSON="$REPORT_DIR/17-post-no-rf-inspect.json"
if ! inspect_with_retries \
    "Post-no-RF-preflight inspection" \
    "$NO_RF_INSPECT_LOG" \
    "$NO_RF_INSPECT_JSON"; then
    log "STOP: PSOC2 could not be inspected after the no-RF preflight."
    exit 18
fi

if ! jq -e \
    --arg serial "$EXPECTED_SERIAL" \
    --arg test_frequency "$FREQUENCY_RADIO" \
    '
      .operation == "inspect" and
      .radio.Serial == $serial and
      .tx.state == "idle" and
      (.tx.occupants | length) == 0 and
      .radio.RfPower == 100 and
      .radio.TransmitSettings.RfPower == 100 and
      .radio.TransmitSettings.DaxEnabled == true and
      .radio.TransmitSettings.MicSelection == "PC" and
      .radio.TransmitSettings.VoxEnabled == false and
      .radio.cwx.Wpm == 30 and
      .radio.cwx.QskEnabled == true and
      .radio.cwx.BreakInDelayMilliseconds == 5 and
      ([.guiClients[]? | select(.IsThisSession != true)] | length) == 0 and
      ([.slices[]? | select(.fields.RF_frequency == $test_frequency)] | length) == 0
    ' "$NO_RF_INSPECT_JSON" >/dev/null; then
    log "STOP: PSOC2 did not remain at the full idle baseline after the no-RF restart preflight."
    jq . "$NO_RF_INSPECT_JSON" | tee -a "$FULL_LOG"
    set +e
    dotnet "$HIL" restore-idle-defaults \
        2>&1 | tee "$REPORT_DIR/18-no-rf-baseline-recovery.log" | tee -a "$FULL_LOG"
    set -e
    exit 19
fi
log "No-RF process/restart preflight and delayed baseline validation: PASS"

section "FRESH OPERATOR CONFIRMATION"
log "Listen again on exactly $FREQUENCY_MHZ MHz."
log "Keep the PSOC2 camera visible and remote power-off immediately available."
log "Type this exact line to continue:"
log "  $HUMAN_CONFIRM"
printf '> '
IFS= read -r typed_confirmation
printf 'Operator entered: %s\n' "$typed_confirmation" >> "$FULL_LOG"
if [[ "$typed_confirmation" != "$HUMAN_CONFIRM" ]]; then
    log "STOP: the fresh operator confirmation did not match exactly."
    exit 7
fi
log "Fresh operator confirmation: accepted"

PREPARE_RAW="$(mktemp "/run/user/$UID/aethersdr-process-prepare.XXXXXX")"
PREPARE_REDACTED="$REPORT_DIR/20-prepare-redacted.log"
PREPARE_JSON="$REPORT_DIR/20-prepare-redacted.json"

section "CREATE FIVE-MINUTE PROCESS-LOSS MANIFEST"
set +e
dotnet "$HIL" prepare-safety-process-loss \
    --arm-file "$ARM" \
    --frequency-hz "$FREQUENCY_HZ" \
    --on-air-confirm "$ON_AIR_CONFIRM" \
    >"$PREPARE_RAW" 2>&1
PREPARE_STATUS=$?
set -e

if ! extract_largest_json "$PREPARE_RAW" "$PREPARE_JSON"; then
    redact_prepare_output "$PREPARE_RAW" "$PREPARE_REDACTED" ""
    cat "$PREPARE_REDACTED" | tee -a "$FULL_LOG"
    rm -f "$PREPARE_RAW"
    log "STOP: prepare output contained no usable JSON object."
    exit 8
fi

TOKEN="$(jq -r '.oneTimeToken // empty' "$PREPARE_JSON")"
redact_prepare_output "$PREPARE_RAW" "$PREPARE_REDACTED" "$TOKEN"
rm -f "$PREPARE_RAW"
# Remove the token from the structured copy as well.
jq '.oneTimeToken = "<REDACTED-ONE-TIME-TOKEN>"' \
    "$PREPARE_JSON" > "$PREPARE_JSON.tmp"
mv "$PREPARE_JSON.tmp" "$PREPARE_JSON"
cat "$PREPARE_REDACTED" | tee -a "$FULL_LOG"

if (( PREPARE_STATUS != 0 )); then
    log "STOP: manifest preparation failed with exit $PREPARE_STATUS."
    unset TOKEN
    exit "$PREPARE_STATUS"
fi

if [[ -z "$TOKEN" ]]; then
    log "STOP: prepare output did not contain the one-time token."
    exit 9
fi

if ! jq -e \
    --argjson frequency "$FREQUENCY_HZ" \
    '
      .prepared == true and
      .purpose == "independent-engine-process-loss" and
      .radio.Serial == "1121-1104-6700-2912" and
      .radio.FrequencyHz == $frequency and
      .radio.TxAntenna == "ANT1" and
      .radio.Mode == "USB" and
      .radio.RfPower == 1 and
      .radio.KeyMilliseconds == 100 and
      .safetyProcessLoss.injectedBoundary == "engine-process-and-flex-tcp" and
      .safetyProcessLoss.childPlanLifetimeSeconds == 30 and
      .safetyProcessLoss.exactRosterConnectedToAbsentTransition == true and
      .safetyProcessLoss.engineExplicitUnkey == false and
      .safetyProcessLoss.independentObserverUnkeyOnly == true and
      .safetyProcessLoss.processKillEntireTree == true and
      .safetyProcessLoss.gracefulChildCleanupExpected == false and
      (.externalGuiClients | length) == 0
    ' "$PREPARE_JSON" >/dev/null; then
    log "STOP: the redacted process-loss manifest evidence did not match the exact safe operation."
    unset TOKEN
    exit 10
fi
log "Purpose-bound process-loss manifest validation: PASS"

TEST_LOG="$REPORT_DIR/30-process-loss-test.log"
TEST_JSON="$REPORT_DIR/30-process-loss-test.json"
section "LIVE 1 W ENGINE-PROCESS/TCP-LOSS TEST"
log "WATCH THE CAMERA NOW."
log "Use remote power-off immediately if PSOC2 remains keyed for about two seconds or any critical ownership/unkey error appears."

set +e
dotnet "$HIL" safety-process-loss \
    --arm-file "$ARM" \
    --token "$TOKEN" \
    2>&1 | tee "$TEST_LOG" | tee -a "$FULL_LOG"
TEST_STATUS=${PIPESTATUS[0]}
set -e
unset TOKEN

if (( TEST_STATUS != 0 )); then
    log "LIVE TEST COMMAND FAILED (exit $TEST_STATUS)."
    log "If the camera shows PSOC2 still keyed, use remote power-off immediately."
fi

TEST_JSON_OK=false
if extract_largest_json "$TEST_LOG" "$TEST_JSON"; then
    TEST_JSON_OK=true
fi

sleep 2
POST_INSPECT_LOG="$REPORT_DIR/40-post-inspect.log"
POST_INSPECT_JSON="$REPORT_DIR/40-post-inspect.json"
if ! inspect_with_retries \
    "Post-test inspection" \
    "$POST_INSPECT_LOG" \
    "$POST_INSPECT_JSON"; then
    log "STOP: final radio inspection failed after three attempts."
    log "Verify PSOC2 manually and use remote power-off if TX is not visibly idle."
    exit 11
fi

if (( TEST_STATUS != 0 )) || [[ "$TEST_JSON_OK" != true ]]; then
    if jq -e '
          .tx.state == "idle" and
          (.tx.occupants | length) == 0
        ' "$POST_INSPECT_JSON" >/dev/null; then
        section "SAFE IDLE-DEFAULT RECOVERY AFTER FAILED HIL RESULT"
        RECOVERY_LOG="$REPORT_DIR/45-idle-default-recovery.log"
        set +e
        dotnet "$HIL" restore-idle-defaults \
            2>&1 | tee "$RECOVERY_LOG" | tee -a "$FULL_LOG"
        RECOVERY_STATUS=${PIPESTATUS[0]}
        set -e
        if (( RECOVERY_STATUS == 0 )); then
            RECOVERY_INSPECT_LOG="$REPORT_DIR/46-post-recovery-inspect.log"
            RECOVERY_INSPECT_JSON="$REPORT_DIR/46-post-recovery-inspect.json"
            if inspect_with_retries \
                "Post-failure recovery inspection" \
                "$RECOVERY_INSPECT_LOG" \
                "$RECOVERY_INSPECT_JSON"; then
                if jq -e '
                      .tx.state == "idle" and
                      (.tx.occupants | length) == 0 and
                      .radio.RfPower == 100 and
                      .radio.TransmitSettings.RfPower == 100 and
                      .radio.TransmitSettings.DaxEnabled == true and
                      .radio.TransmitSettings.MicSelection == "PC" and
                      .radio.TransmitSettings.VoxEnabled == false
                    ' "$RECOVERY_INSPECT_JSON" >/dev/null; then
                    log "Safe recovery confirmed: PSOC2 idle at the 100 W station baseline."
                else
                    log "WARNING: the recovery command completed, but final baseline validation failed."
                fi
            else
                log "WARNING: the recovery command completed, but the follow-up inspection failed."
            fi
        else
            log "WARNING: idle-default recovery failed closed with exit $RECOVERY_STATUS."
        fi
    else
        log "No automatic setting recovery attempted because radio-confirmed idle was unavailable."
    fi
fi

if [[ "$TEST_JSON_OK" != true ]]; then
    log "STOP: live test output contained no usable result JSON."
    exit 12
fi

if ! jq -e '
      .test == "independent-engine-process-loss-unkey" and
      .passed == true and
      .rfEmitted == true and
      .childProcess.killed == true and
      .childProcess.entireProcessTree == true and
      .childProcess.gracefulCleanupRan == false and
      .childProcess.childResourcesGoneBeforeCleanup == true and
      .childCommandsBeforeKill.key == 1 and
      .childCommandsBeforeKill.unkey == 0 and
      .replacementEngine.ExitCode == 0 and
      .replacementEngine.OldHandleAbsent == true and
      .replacementEngine.AllIdentitiesFresh == true and
      .replacementEngine.ResourcesGone == true and
      .replacementEngine.BaselineRestored == true and
      .replacementEngine.commands.key == 0 and
      .replacementEngine.commands.unkey == 0 and
      (.replacementEngine.ProcessId != .childProcess.ProcessId) and
      (.replacementEngine.clientHandle != .childProcess.clientHandle) and
      (.replacementEngine.EngineInstanceId != .childProcess.EngineInstanceId) and
      (.replacementEngine.SessionId != .childProcess.SessionId) and
      (.replacementEngine.BrowserClientId != .childProcess.BrowserClientId) and
      (.replacementEngine.LeaseId != .childProcess.LeaseId) and
      (.independentObserver.unkeyCommands == 0 or
       .independentObserver.unkeyCommands == 1) and
      .independentObserver.keyCapability == false and
      (.independentObserver.mechanism == "radio-auto-unkey-on-engine-tcp-close" or
       .independentObserver.mechanism == "independent-observer-unkey") and
      (.timing.processKillToExitMilliseconds >= 0) and
      (.timing.processExitToSafetySignalMilliseconds >= 0) and
      (if .independentObserver.unkeyCommands == 1 then
         (.timing.safetySignalToUnkeyDispatchMilliseconds >= 0) and
         (.timing.unkeyDispatchToCompletionMilliseconds >= 0) and
         (.timing.unkeyCompletionToIdleMilliseconds >= 0)
       else
         .timing.safetySignalToUnkeyDispatchMilliseconds == null and
         .timing.unkeyDispatchToCompletionMilliseconds == null and
         .timing.unkeyCompletionToIdleMilliseconds == null
       end) and
      (.timing.processExitToSafetyActionMilliseconds >= 0) and
      (.timing.safetyActionToIdleMilliseconds >= 0) and
      (.timing.processExitToRosterLossMilliseconds >= 0) and
      (.timing.idleToRosterLossMilliseconds >= 0) and
      (.timing.keyedToIdleMilliseconds >= 0) and
      .identification.Callsign == "KC4CAW" and
      .identification.Wpm == 20 and
      .identification.SawExactOwnedTransmit == true
    ' "$TEST_JSON" >/dev/null; then
    log "STOP: live result JSON did not satisfy the exact process-loss acceptance criteria."
    jq . "$TEST_JSON" | tee -a "$FULL_LOG"
    exit 13
fi

if ! jq -e \
    --arg serial "$EXPECTED_SERIAL" \
    --arg test_frequency "$FREQUENCY_RADIO" \
    '
      .operation == "inspect" and
      .radio.Serial == $serial and
      .tx.state == "idle" and
      (.tx.occupants | length) == 0 and
      .radio.RfPower == 100 and
      .radio.TransmitSettings.RfPower == 100 and
      .radio.TransmitSettings.DaxEnabled == true and
      .radio.TransmitSettings.MicSelection == "PC" and
      .radio.TransmitSettings.VoxEnabled == false and
      .radio.cwx.Wpm == 30 and
      .radio.cwx.QskEnabled == true and
      .radio.cwx.BreakInDelayMilliseconds == 5 and
      ([.guiClients[]? | select(.IsThisSession != true)] | length) == 0 and
      ([.slices[]? | select(.fields.RF_frequency == $test_frequency)] | length) == 0
    ' "$POST_INSPECT_JSON" >/dev/null; then
    log "STOP: final PSOC2 restoration/leak validation failed."
    jq . "$POST_INSPECT_JSON" | tee -a "$FULL_LOG"
    exit 14
fi

if [[ -e "$ARM" || -e "$CHILD_PLAN" ]]; then
    log "STOP: a one-time safety file remains after the operation."
    [[ -e "$ARM" ]] && log "Remaining outer manifest: $ARM"
    [[ -e "$CHILD_PLAN" ]] && log "Remaining child plan: $CHILD_PLAN"
    exit 15
fi

if (( TEST_STATUS != 0 )); then
    exit "$TEST_STATUS"
fi

write_summary "$TEST_JSON" "$POST_INSPECT_JSON"
section "FINAL RESULT"
cat "$SUMMARY" | tee -a "$FULL_LOG"
log "PASS: true engine-process/TCP-loss HIL completed and PSOC2 restoration was verified."
