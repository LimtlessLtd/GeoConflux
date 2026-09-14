#!/usr/bin/env bash
#
# One collection round: every committed brief, collected under the access policy, kept only if it
# found something.
#
# This is the file .github/workflows/collect.yml runs, for the same reason ci.yml calls
# tools/verify.sh rather than repeating its checks — a scheduled job whose logic exists only inside
# a workflow can only be debugged by pushing and waiting.
#
#   bash tools/collect.sh                  # every brief in data/osint/briefs
#   bash tools/collect.sh armed-conflict   # one of them, by id
#
# It collects; it does not commit. Deciding what reaches the repository is the workflow's job, and
# keeping that out of here means running it locally can never push anything.
#
# Exit status is 0 when every brief either collected something or read its channels and found a
# quiet week. It is non-zero only when a brief could not read anything at all, which is a statement
# about this collector rather than about the world and is the one case worth waking someone for.

set -euo pipefail

cd "$(dirname "$0")/.."

BRIEFS="data/osint/briefs"
BUNDLES="data/osint"
KEEP_DAYS="${KEEP_DAYS:-28}"

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
warn() { printf '\033[33m%s\033[0m\n' "$1"; }
fail() { printf '\n\033[31merror: %s\033[0m\n' "$1" >&2; exit 1; }

PYTHON=$(command -v python3 || command -v python) || fail "No Python interpreter was found."

# Before anything reaches the network. The access rules are the part of this that a run could get
# quietly wrong — a refusal read as permission looks exactly like a successful collection — so a
# broken policy must stop the round rather than be discovered in what it collected.
step "Check the access policy before collecting anything"
"$PYTHON" --version
"$PYTHON" tools/collect/access.py --self-test >/dev/null
"$PYTHON" tools/collect/collect.py --self-test >/dev/null
"$PYTHON" tools/collect/schedule.py --self-test >/dev/null
printf 'Access policy, collector and run decisions all pass their offline checks.\n'

today=$(date -u +%Y-%m-%d)
staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

wanted=("$@")
collected=0
quiet=0
alarms=()

for plan in "$BRIEFS"/*.run.json; do
    [ -e "$plan" ] || fail "No run files found in $BRIEFS."

    brief=$(basename "$plan" .run.json)

    if [ "${#wanted[@]}" -gt 0 ] && [[ ! " ${wanted[*]} " == *" $brief "* ]]; then
        continue
    fi

    step "Collect: $brief"

    # Into staging first. A bundle that turns out not to be worth keeping must never have existed in
    # data/osint, because the pipeline reads that directory by glob and a half-written or empty file
    # sitting there is a bundle as far as it is concerned.
    bundle="$staging/$today-$brief.json"

    # A brief that throws costs its own bundle and not the round. The reason is printed rather than
    # swallowed, because a collector that fails silently is the failure this whole script is against.
    if ! "$PYTHON" tools/collect/collect.py --run "$plan" --out "$bundle"; then
        alarms+=("$brief: the collector exited non-zero; no bundle was produced")
        continue
    fi

    verdict=0
    reason=$("$PYTHON" tools/collect/schedule.py --verdict "$bundle") || verdict=$?

    case "$verdict" in
        0)
            mv "$bundle" "$BUNDLES/$today-$brief.json"
            printf '\033[32mkept\033[0m   %s — %s\n' "$brief" "$reason"
            collected=$((collected + 1))
            ;;
        3)
            warn "quiet  $brief — $reason"
            quiet=$((quiet + 1))
            ;;
        *)
            # Not committed, and loud. An empty bundle because nothing was published and an empty
            # bundle because nothing answered are indistinguishable in the file and opposite in
            # meaning; this is the only place that difference is acted on.
            alarms+=("$brief: $reason")
            ;;
    esac
done

# Bundles the pipeline stopped reading a fortnight ago. They are removed from the working tree and
# not from the repository: every one of them stays in history, which is where an archive belongs.
# See tools/collect/schedule.py for why the horizon is twice the runtime's.
step "Prune bundles past the working-set horizon"

pruned=0

while IFS= read -r stale; do
    [ -n "$stale" ] || continue
    rm -f "$stale"
    printf 'pruned %s\n' "$stale"
    pruned=$((pruned + 1))
done < <("$PYTHON" tools/collect/schedule.py --prunable "$BUNDLES" --keep-days "$KEEP_DAYS")

if [ "$pruned" -eq 0 ]; then
    printf 'Nothing is older than %s days.\n' "$KEEP_DAYS"
fi

step "Round complete"
printf '%s brief(s) collected, %s quiet, %s alarming, %s pruned.\n' \
    "$collected" "$quiet" "${#alarms[@]}" "$pruned"

if [ "${#alarms[@]}" -gt 0 ]; then
    printf '\n'
    for alarm in "${alarms[@]}"; do
        printf '\033[31mALARM\033[0m  %s\n' "$alarm"
    done

    fail "A brief reached nothing at all. That is this collector failing, not the world going quiet."
fi
